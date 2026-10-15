using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Game.EntityComponents;
using Sandbox.ModAPI;
using SpaceEngineers.Game.ModAPI;
using VRage.Game.Components;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRage.ObjectBuilders;
using VRageMath;

namespace MagRail
{
    // The autopilot block, ported from the old Maglev (MaglevAutopilot, user decision 2026-10-15: stations as
    // before). It keeps up to 10 stations; a station is the rail point under the block where the train stood when
    // it was remembered. "Go" drives the train there along the track with the switches as they are (Route: no way
    // there = error, the train does not start) until the block is over the point again; then the station's timer
    // is triggered (and the train parked, if asked). On the way the route is walked again every tick: a switch
    // thrown, a rail turned prototype or a block lost and the train brakes to a stop with that error. Stop brakes
    // it to a stop as well; switching the block off lets go at once.
    // Speed: the max speed (m/s), speeding up at the set acceleration, a braking curve to the station at the set
    // braking, and slower before curves, so the side acceleration stays within the set one until the whole train has
    // left the curve (user decisions 2026-10-15; the rail itself takes any curve at any speed: RailBody). Every grid of the train holds the speed asked with its own engines (Drive.AutopilotThrust).
    // Only one autopilot drives a train at a time; the pilot's W/S/Space and the cruise do nothing meanwhile.
    // Toolbar actions: go to station 1..10, go to the next station (round trips with timers), stop.
    // The server decides; terminal and toolbar send requests, the server sends settings and status to everyone.
    [MyEntityComponentDescriptor(typeof(MyObjectBuilder_UpgradeModule), false, "MagRail_AutopilotLarge", "MagRail_AutopilotSmall")]
    public partial class AutopilotLogic : MyGameLogicComponent
    {
        // Commands (value = station index, -1 = the selected one).
        public const int CmdStart = 1, CmdStop = 2, CmdSelect = 3, CmdRemember = 4, CmdRemove = 5, CmdRename = 6,
            CmdMaxSpeed = 7, CmdTimer = 8, CmdNext = 9, CmdPark = 10, CmdShowStations = 11,
            // value = tenths of m/s^2 (CmdLateral: of g, 0 = no limit)
            CmdAccel = 12, CmdBrake = 13, CmdLateral = 14;

        // The speed asked for runs at most this far ahead of the train's own while it speeds up (m/s).
        const double RampLead = 1.0, Step = 1.0 / 60.0;
        // Near the station: the speed falls off linearly (ApproachGain per metre), never below CreepSpeed until
        // within StopZone; overshot, the train backs up at most at BackupSpeed.
        const double ApproachGain = 1.0, CreepSpeed = 0.15, StopZone = 0.1, BackupSpeed = 1.0;
        // Overshot this far at most (the station is looked for behind the train within this).
        const double OvershootSearch = 50;
        // Arrived: within ArriveDistance and moved less than ArriveStill over ArriveTicks, or SettleTicks after first
        // reaching it (parking waits for the stop however long it takes).
        const double ArriveDistance = 0.2, ArriveStill = 0.02;
        const int ArriveTicks = 30, SettleTicks = 180;
        // Halting on an error: stopped below HaltSpeed for HaltTicks, or given up after SettleTicks.
        const double HaltSpeed = 0.1;
        const int HaltTicks = 10;
        const double RailDistance = 5;      // the block's drop point is at most this far from the rail
        const int EngineRefreshTicks = 60, StationRefreshTicks = 60, ScanTicks = 15, StatusTicks = 60, GroupRefreshTicks = 60;

        static readonly Guid StorageGuid = new Guid("7c1e4a90-3d2b-4f86-a5e1-9b0c6d2f8e47");

        enum Phase { Idle, Driving, Halting }

        // The order for the grids of a train this tick (Drive.ReadInput): the speed along Direction (minus = back).
        public struct Command
        {
            public int Tick;
            public double Speed;
            public Vector3D Direction;
        }

        static readonly List<AutopilotLogic> all = new List<AutopilotLogic>();
        static readonly Dictionary<IMyCubeGrid, Command> commands = new Dictionary<IMyCubeGrid, Command>();
        static readonly List<IMyCubeGrid> groupScratch = new List<IMyCubeGrid>();
        static readonly List<IMyShipController> controllerScratch = new List<IMyShipController>();
        static int tick;

        public IMyUpgradeModule Block { get; private set; }
        public AutopilotConfig Config = new AutopilotConfig();
        // Status line as sent over the network (AutopilotStatus).
        public string Status = AutopilotStatus.Make("MagRail_Ap_Idle");

        Phase phase;
        bool registered;
        // Server run.
        string targetArg = "";
        long arrivalTimer;
        readonly TrackCursor cursor = new TrackCursor(), station = new TrackCursor(), back = new TrackCursor();
        HoverEngineLogic engine;
        int engineTick = -999, stationTick = -999, scanTick = -999, statusTick = -999, groupTick = -999;
        readonly List<Route.Limit> limits = new List<Route.Limit>();
        double scanDistance, trainLength, distance, lastSpeed;
        Vector3D direction;
        int arriveTicks, arriveStart = -1, haltStart, haltTicks;
        double arriveRef;
        string haltReason;
        readonly List<IMyCubeGrid> train = new List<IMyCubeGrid>();

        public override void Init(MyObjectBuilder_EntityBase objectBuilder)
        {
            Block = (IMyUpgradeModule)Entity;
            string text;
            if (Block.Storage != null && Block.Storage.TryGetValue(StorageGuid, out text))
                Config = AutopilotConfig.Load(text);
            NeedsUpdate |= MyEntityUpdateEnum.BEFORE_NEXT_FRAME;
        }

        public override void UpdateOnceBeforeFrame()
        {
            try
            {
                if (Block.CubeGrid.Physics == null || MagRailSession.Instance == null)
                    return;
                all.Add(this);
                registered = true;
                Block.AppendingCustomInfo += AppendInfo;
                CreateTerminal();
                // Loaded (save, blueprint) as active: nothing drives yet.
                if (Net.IsServer && Config.Active)
                    Publish(Status, false);
            }
            catch (Exception e)
            {
                Log.Error("AutopilotLogic.UpdateOnceBeforeFrame", e);
            }
        }

        public override void Close()
        {
            if (!registered)
                return;
            registered = false;
            all.Remove(this);
            Block.AppendingCustomInfo -= AppendInfo;
            RemoveGps();
        }

        public static void Clear()
        {
            all.Clear();
            commands.Clear();
            hud = null;
            hudText = null;
        }

        public static AutopilotLogic Get(IMyTerminalBlock b)
        {
            return b != null && b.GameLogic != null ? b.GameLogic.GetAs<AutopilotLogic>() : null;
        }

        // ---- the tick (MagRailSession, before the rides) ----

        public static void UpdateAll()
        {
            tick++;
            for (int i = 0; i < all.Count; i++)
            {
                AutopilotLogic a = all[i];
                try
                {
                    a.Update();
                }
                catch (Exception e)
                {
                    Log.Error("AutopilotLogic.Update", e);
                    if (Net.IsServer && a.phase != Phase.Idle)
                        a.Finish(AutopilotStatus.Make("MagRail_Ap_InternalError"), true, false);
                }
            }
        }

        // The order for a grid this tick, if an autopilot drives its train.
        public static bool TryGetCommand(IMyCubeGrid grid, out Command command)
        {
            return commands.TryGetValue(grid, out command) && command.Tick == tick;
        }

        void Update()
        {
            if (Block.MarkedForClose)
                return;
            if (!MyAPIGateway.Utilities.IsDedicated)
            {
                UpdateHud();
                UpdateGps();
                UpdateLight();
            }
            if (!Net.IsServer || (phase == Phase.Idle && !Config.Running))
                return;
            if (!Block.IsFunctional || !Block.Enabled)
            {
                // Off: let go at once.
                Finish(AutopilotStatus.Make(Block.IsFunctional ? "MagRail_Ap_Off" : "MagRail_Ap_Damaged"), true, false);
                return;
            }
            if (phase == Phase.Idle)
                Start();
            else if (phase == Phase.Driving)
                DriveTick();
            else
                HaltTick();
        }

        // ---- server run ----

        void Start()
        {
            AutopilotConfig c = Config;
            if (!c.Has(c.Target))
            {
                Finish(Error("MagRail_Ap_ErrNoTarget"), true, false);
                return;
            }
            for (int i = 0; i < all.Count; i++)
            {
                AutopilotLogic other = all[i];
                if (other != this && (other.phase != Phase.Idle || other.Config.Running) && SameTrain(other.Block.CubeGrid, Block.CubeGrid))
                {
                    Finish(Error("MagRail_Ap_ErrOtherAutopilot"), true, false);
                    return;
                }
            }
            string error = FindPlace(true);
            if (error == null && !FindStation(true))
                error = "MagRail_Ap_ErrStationRail";
            if (error != null)
            {
                Finish(Error(error), true, false);
                return;
            }
            // The way there: ahead or behind the way the cursor faces, the shorter.
            double ahead = Route.Distance(cursor, station, Route.MaxLength);
            back.CopyFrom(cursor);
            back.Reverse();
            double behind = Route.Distance(back, station, ahead >= 0 ? ahead : Route.MaxLength);
            if (ahead < 0 && behind < 0)
            {
                Finish(Error("MagRail_Ap_ErrNoRoute"), true, false);
                return;
            }
            if (behind >= 0 && (ahead < 0 || behind < ahead))
                cursor.Reverse();
            c.Last = c.Target;
            Store();
            SetHandBrake(false);
            targetArg = c.Arg(c.Target);
            arrivalTimer = c.Stations[c.Target].TimerId;
            RefreshTrain(true);
            trainLength = TrainLength();
            scanTick = -999;
            statusTick = -999;
            arriveTicks = 0;
            arriveStart = -1;
            lastSpeed = 0;
            phase = Phase.Driving;
            Publish(AutopilotStatus.Make("MagRail_Ap_DrivingTo", targetArg), false);
        }

        void DriveTick()
        {
            AutopilotConfig c = Config;
            if (!c.Running)
            {
                Finish(AutopilotStatus.Make("MagRail_Ap_Stopped"), true, false);
                return;
            }
            string error = FindPlace(false);
            if (error != null)
            {
                Halt(error);
                return;
            }
            if (!FindStation(false))
            {
                Halt("MagRail_Ap_ErrStationRail");
                return;
            }
            if (HandBrakeOn())
            {
                Halt("MagRail_Ap_ErrParkingBrake");
                return;
            }
            // Distance to go: ahead; overshot (or a loop that leads round again), the station just behind.
            double ahead = Route.Distance(cursor, station, Route.MaxLength);
            back.CopyFrom(cursor);
            back.Reverse();
            double behind = Route.Distance(back, station, ahead >= 0 ? Math.Min(ahead, OvershootSearch) : OvershootSearch);
            if (ahead < 0 && behind < 0)
            {
                Halt("MagRail_Ap_ErrRouteLost");
                return;
            }
            distance = behind >= 0 && (ahead < 0 || behind < ahead) ? -behind : ahead;
            TrackSample s = cursor.Sample(Block.WorldMatrix.Translation, 0);
            direction = s.Tangent;

            if (Arrived(c))
                return;

            double maxSpeed = Math.Min(c.MaxSpeed, Cruise.MaxSpeed(Block.CubeGrid));
            if (tick - scanTick >= ScanTicks || distance > scanDistance + 1)
            {
                scanTick = tick;
                scanDistance = distance;
                double look = maxSpeed * maxSpeed / (2 * c.Brake) + trainLength + 10;
                if (c.Lateral > 0)
                    Route.CurveLimits(cursor, Math.Min(look, Math.Max(0, distance)), c.LateralAccel, maxSpeed, limits);
                else
                    limits.Clear();
            }
            // The speed asked for and what sets it (shown in the status: the player sees why it is not faster).
            double speed = StationSpeed(distance, maxSpeed, c.Brake), radius = 0;
            string why = speed >= maxSpeed ? "MagRail_Ap_DrivingMax" : "MagRail_Ap_DrivingStation";
            if (speed > 0)
            {
                double curve = Math.Max(CreepSpeed, Route.Allowed(limits, scanDistance - distance, c.Brake, trainLength, maxSpeed, out radius));
                if (curve < speed)
                {
                    speed = curve;
                    why = "MagRail_Ap_DrivingCurve";
                }
                // Speeding up: the speed asked grows at the set acceleration, never far ahead of the train's own.
                double own = Block.CubeGrid.Physics != null ? Vector3D.Dot(Block.CubeGrid.Physics.LinearVelocity, direction) : 0;
                double ramp = Math.Max(CreepSpeed, Math.Min(lastSpeed, Math.Max(0, own) + RampLead) + c.Accel * Step);
                if (ramp < speed)
                {
                    speed = ramp;
                    why = "MagRail_Ap_DrivingAccel";
                }
            }
            lastSpeed = speed;
            SendCommand(speed);
            if (tick - statusTick >= StatusTicks)
            {
                statusTick = tick;
                Publish(AutopilotStatus.Make(why, targetArg, FormatDistance(distance), Math.Abs(speed).ToString("0", CultureInfo.InvariantCulture),
                    radius.ToString("0", CultureInfo.InvariantCulture)), false);
            }
        }

        // Over the point and standing, or SettleTicks after first reaching it; parking waits for the stop.
        bool Arrived(AutopilotConfig c)
        {
            if (Math.Abs(distance) < ArriveDistance)
            {
                if (arriveStart < 0)
                    arriveStart = tick;
                if (arriveTicks == 0 || Math.Abs(distance - arriveRef) > ArriveStill)
                {
                    arriveRef = distance;
                    arriveTicks = 1;
                }
                else
                {
                    arriveTicks++;
                }
            }
            else
            {
                arriveTicks = 0;
            }
            if (c.Park)
            {
                if (arriveTicks < ArriveTicks)
                    return false;
                SetHandBrake(true);
                Finish(AutopilotStatus.Make("MagRail_Ap_ArrivedParked", targetArg), true, true);
                return true;
            }
            if (arriveTicks >= ArriveTicks || (arriveStart >= 0 && tick - arriveStart >= SettleTicks))
            {
                Finish(AutopilotStatus.Make("MagRail_Ap_Arrived", targetArg), true, true);
                return true;
            }
            return false;
        }

        // Speed asked for at `d` metres to go (minus = overshot): the max speed, a braking curve, a linear fall
        // near the station with a creep speed, nothing within the stop zone, backing up when overshot.
        static double StationSpeed(double d, double maxSpeed, double brake)
        {
            if (Math.Abs(d) < StopZone)
                return 0;
            if (d < 0)
                return -Math.Min(BackupSpeed, Math.Max(CreepSpeed, -d * ApproachGain));
            double v = Math.Min(Math.Sqrt(2 * brake * d), d * ApproachGain);
            return Math.Min(maxSpeed, Math.Max(CreepSpeed, v));
        }

        // Whole metres far off, centimetres near (minus = overshot); invariant digits, the unit is in the text.
        static string FormatDistance(double d)
        {
            return d.ToString(Math.Abs(d) >= 10 ? "0" : "0.00", CultureInfo.InvariantCulture);
        }

        // Brakes the train to a stop, then finishes with the error.
        void Halt(string errorKey)
        {
            if (Config.Running)
            {
                Config.Running = false;
                Store();
            }
            haltReason = Error(errorKey);
            haltTicks = 0;
            haltStart = tick;
            phase = Phase.Halting;
            Publish(AutopilotStatus.Make("MagRail_Ap_StoppingError", AutopilotStatus.KeyArg(errorKey)), true);
        }

        void HaltTick()
        {
            Vector3D v = Block.CubeGrid.Physics != null ? (Vector3D)Block.CubeGrid.Physics.LinearVelocity : Vector3D.Zero;
            double speed = direction.LengthSquared() > 0.5 ? Math.Abs(Vector3D.Dot(v, direction)) : v.Length();
            if (tick - haltStart >= SettleTicks)
            {
                Finish(haltReason, false, false);
                return;
            }
            if (speed < HaltSpeed)
            {
                if (++haltTicks >= HaltTicks)
                {
                    Finish(haltReason, false, false);
                    return;
                }
            }
            else
            {
                haltTicks = 0;
            }
            if (direction.LengthSquared() < 0.5)
                direction = Block.WorldMatrix.Forward;
            lastSpeed = 0;
            SendCommand(0);
        }

        void Finish(string status, bool notify, bool arrived)
        {
            if (Config.Running)
            {
                Config.Running = false;
                Store();
            }
            phase = Phase.Idle;
            engine = null;
            cursor.Reset();
            Publish(status, notify);
            if (arrived)
                TriggerTimer(arrivalTimer);
        }

        static string Error(string key)
        {
            return AutopilotStatus.Make("MagRail_Ap_Error", AutopilotStatus.KeyArg(key));
        }

        // ---- where the train is ----

        // The block's place on the rail (cursor): the block dropped onto the rail plane of the nearest engine of
        // the train that holds the rail (its height above the rail taken out). Returns an error key or null.
        string FindPlace(bool fresh)
        {
            if (fresh || engine == null || !engine.Held || engine.Block.MarkedForClose || tick - engineTick >= EngineRefreshTicks)
            {
                engineTick = tick;
                engine = NearestEngine();
            }
            if (engine == null)
                return "MagRail_Ap_ErrNoEngine";
            if (!engine.Cursor.Valid)
                return "MagRail_Ap_ErrNoRail";
            TrackSample rail = engine.Cursor.Sample(engine.GuidePoint, 0);
            Vector3D at = Block.WorldMatrix.Translation;
            Vector3D drop = at - rail.Up * Vector3D.Dot(at - rail.Point, rail.Up);
            if (fresh || !cursor.Valid)
                cursor.CopyFrom(engine.Cursor);
            TrackSample s;
            if (!cursor.TryProject(drop, rail.Tangent, RailDistance, 0, out s))
                return "MagRail_Ap_ErrNoRail";
            return null;
        }

        HoverEngineLogic NearestEngine()
        {
            List<HoverEngineLogic> engines = MagRailSession.Instance.Engines;
            HoverEngineLogic best = null;
            double bestDistance = double.MaxValue;
            Vector3D at = Block.WorldMatrix.Translation;
            for (int i = 0; i < engines.Count; i++)
            {
                HoverEngineLogic e = engines[i];
                if (!e.Held || !e.Cursor.Valid || !SameTrain(e.Block.CubeGrid, Block.CubeGrid))
                    continue;
                double d = Vector3D.DistanceSquared(e.Block.WorldMatrix.Translation, at);
                if (d < bestDistance)
                {
                    bestDistance = d;
                    best = e;
                }
            }
            return best;
        }

        // The target station's place on the track (re-found now and then: its rail may be on a ship).
        bool FindStation(bool fresh)
        {
            if (!fresh && station.Valid && station.Rideable && tick - stationTick < StationRefreshTicks)
                return true;
            stationTick = tick;
            if (!Config.Has(Config.Target))
                return false;
            Vector3D point = Config.Stations[Config.Target].Point;
            station.Reset();
            TrackSample s;
            return station.TryProject(point, Vector3D.Forward, 1.0, 0, out s);
        }

        // The grids of the train (mechanically joined to the block's grid).
        void RefreshTrain(bool force)
        {
            if (!force && tick - groupTick < GroupRefreshTicks && train.Count > 0)
                return;
            groupTick = tick;
            train.Clear();
            MyAPIGateway.GridGroups.GetGroup(Block.CubeGrid, GridLinkTypeEnum.Mechanical, train);
            if (train.Count == 0)
                train.Add(Block.CubeGrid);
        }

        // The train's extent along the route: its tail must leave a curve before it speeds up again.
        double TrainLength()
        {
            Vector3D d = cursor.Sample(Block.WorldMatrix.Translation, 0).Tangent;
            double min = double.MaxValue, max = double.MinValue;
            for (int i = 0; i < train.Count; i++)
            {
                BoundingBoxD box = train[i].WorldAABB;
                for (int k = 0; k < 8; k++)
                {
                    double p = Vector3D.Dot(box.GetCorner(k), d);
                    min = Math.Min(min, p);
                    max = Math.Max(max, p);
                }
            }
            return max > min ? max - min : 0;
        }

        void SendCommand(double speed)
        {
            RefreshTrain(false);
            Command c = new Command { Tick = tick, Speed = speed, Direction = direction };
            for (int i = 0; i < train.Count; i++)
                commands[train[i]] = c;
        }

        public static bool SameTrain(IMyCubeGrid a, IMyCubeGrid b)
        {
            return a == b || MyAPIGateway.GridGroups.HasConnection(a, b, GridLinkTypeEnum.Mechanical);
        }

        // The hand brake of the train's grids (every ship controller on them; the game keeps it per grid).
        void SetHandBrake(bool on)
        {
            CollectControllers();
            for (int i = 0; i < controllerScratch.Count; i++)
                controllerScratch[i].HandBrake = on;
            controllerScratch.Clear();
        }

        bool HandBrakeOn()
        {
            if (tick % 30 != 0)
                return false;
            CollectControllers();
            bool on = false;
            for (int i = 0; i < controllerScratch.Count && !on; i++)
                on = controllerScratch[i].HandBrake;
            controllerScratch.Clear();
            return on;
        }

        void CollectControllers()
        {
            RefreshTrain(false);
            controllerScratch.Clear();
            var terminal = MyAPIGateway.TerminalActionsHelper.GetTerminalSystemForGrid(Block.CubeGrid);
            if (terminal != null)
                terminal.GetBlocksOfType(controllerScratch, s => train.Contains(s.CubeGrid));
        }

        static void TriggerTimer(long id)
        {
            IMyEntity entity;
            IMyTimerBlock timer = id != 0 && MyAPIGateway.Entities.TryGetEntityById(id, out entity) ? entity as IMyTimerBlock : null;
            if (timer != null && !timer.MarkedForClose && timer.IsFunctional && timer.Enabled)
                timer.Trigger();
        }

        // ---- commands ----

        // Terminal / toolbar entry point, on whichever machine ran it.
        public void Request(int command, int value, long arg = 0, string text = null)
        {
            if (Net.IsServer)
            {
                ServerCommand(command, value, arg, text);
            }
            else
            {
                // Show a settings change at once; the server's answer confirms it.
                if (IsSetting(command))
                {
                    ApplySetting(command, value, arg, text);
                    Store();
                }
                Net.SendAutopilotRequest(this, command, value, arg, text);
            }
            // Renaming rebuilds the terminal on every key: the text box would lose focus.
            if (command != CmdRename)
                RefreshTerminal();
        }

        static bool IsSetting(int command)
        {
            return command == CmdSelect || command == CmdRename || command == CmdTimer || command == CmdRemove || command == CmdMaxSpeed
                || command == CmdPark || command == CmdShowStations || command == CmdAccel || command == CmdBrake || command == CmdLateral;
        }

        public void ServerCommand(int command, int value, long arg, string text)
        {
            AutopilotConfig c = Config;
            if (command == CmdNext)
            {
                command = CmdStart;
                value = Math.Max(0, c.NextIndex);
            }
            if (command == CmdStart)
            {
                int index = value < 0 ? c.Selected : value;
                if (!c.Has(index))
                {
                    Publish(c.Stations.Count == 0 ? Error("MagRail_Ap_ErrNoStations")
                        : AutopilotStatus.Make("MagRail_Ap_ErrNoStation", (index + 1).ToString(CultureInfo.InvariantCulture)), true);
                    return;
                }
                if (phase == Phase.Halting)
                    return;
                c.Target = index;
                c.Running = true;
                Store();
                // Driving elsewhere: plan anew from here on the next tick.
                if (phase == Phase.Driving)
                    phase = Phase.Idle;
                Publish(AutopilotStatus.Make("MagRail_Ap_Starting"), false);
                return;
            }
            if (command == CmdStop)
            {
                // Lets go at once: the pilot has the train again.
                c.Running = false;
                Store();
                if (phase != Phase.Idle)
                    Finish(AutopilotStatus.Make("MagRail_Ap_Stopped"), true, false);
                else
                    Publish(AutopilotStatus.Make("MagRail_Ap_Stopped"), false);
                return;
            }
            if (command == CmdRemember)
            {
                Remember(value, text);
                return;
            }
            bool wasRunning = c.Running;
            ApplySetting(command, value, arg, text);
            Store();
            if (wasRunning && !c.Running && phase != Phase.Idle)
                Finish(AutopilotStatus.Make("MagRail_Ap_StoppedDeleted"), true, false);
            else
                Publish(Status, false);
        }

        void ApplySetting(int command, int value, long arg, string text)
        {
            AutopilotConfig c = Config;
            switch (command)
            {
                case CmdSelect:
                    if (c.Has(value))
                        c.Selected = value;
                    break;
                case CmdRename:
                    if (c.Has(value))
                        c.Stations[value].Name = AutopilotConfig.CleanName(text);
                    break;
                case CmdTimer:
                    if (c.Has(value))
                        c.Stations[value].TimerId = arg;
                    break;
                case CmdRemove:
                    if (!c.Has(value))
                        break;
                    c.Stations.RemoveAt(value);
                    if (c.Target == value)
                    {
                        c.Target = -1;
                        c.Running = false;
                    }
                    else if (c.Target > value)
                    {
                        c.Target--;
                    }
                    if (c.Last == value)
                        c.Last = -1;
                    else if (c.Last > value)
                        c.Last--;
                    if (c.Selected > value || c.Selected >= c.Stations.Count)
                        c.Selected--;
                    c.ClampIndices();
                    break;
                case CmdPark:
                    c.Park = value < 0 ? !c.Park : value != 0;
                    break;
                case CmdShowStations:
                    c.ShowStations = value != 0;
                    break;
                case CmdMaxSpeed:
                    c.MaxSpeed = MathHelper.Clamp(value, 1, (int)Cruise.MaxSpeed(Block.CubeGrid));
                    break;
                case CmdAccel:
                    c.Accel = MathHelper.Clamp(value / 10.0, AutopilotConfig.MinAccel, AutopilotConfig.MaxAccel);
                    break;
                case CmdBrake:
                    c.Brake = MathHelper.Clamp(value / 10.0, AutopilotConfig.MinAccel, AutopilotConfig.MaxAccel);
                    break;
                case CmdLateral:
                    c.Lateral = AutopilotConfig.ClampLateral(value / 10.0);
                    break;
            }
        }

        // Server: the rail point under the block as a new station (index -1) or an existing one moved here.
        void Remember(int index, string name)
        {
            AutopilotConfig c = Config;
            string error = phase == Phase.Idle ? FindPlace(true) : null;
            if (error == null && !cursor.Valid)
                error = "MagRail_Ap_ErrNoRail";
            if (error != null)
            {
                if (phase == Phase.Idle)
                    cursor.Reset();
                Publish(Error(error), true);
                return;
            }
            if (!c.Has(index) && c.Stations.Count >= AutopilotConfig.MaxStations)
            {
                Publish(AutopilotStatus.Make("MagRail_Ap_ErrMaxStations", AutopilotConfig.MaxStations.ToString(CultureInfo.InvariantCulture)), true);
                return;
            }
            TrackSample s = cursor.Sample(Block.WorldMatrix.Translation, 0);
            IMyCubeGrid railGrid = cursor.Span != null ? cursor.Span.Owner.Block.CubeGrid : cursor.Lane.Block.Block.CubeGrid;
            AutopilotConfig.Station st = c.Has(index) ? c.Stations[index] : new AutopilotConfig.Station { Name = AutopilotConfig.CleanName(name) };
            st.World = s.Point;
            st.GridId = railGrid.EntityId;
            st.Local = Vector3D.Transform(s.Point, MatrixD.Invert(railGrid.WorldMatrix));
            if (!c.Has(index))
            {
                c.Stations.Add(st);
                index = c.Stations.Count - 1;
            }
            c.Selected = index;
            if (phase == Phase.Idle)
                cursor.Reset();
            Store();
            Publish(AutopilotStatus.Make("MagRail_Ap_Remembered", c.Arg(index)), true);
        }

        // ---- state ----

        void Store()
        {
            if (Block.Storage == null)
                Block.Storage = new MyModStorageComponent();
            Block.Storage[StorageGuid] = Config.Save();
        }

        // Server: stores the status and sends settings + status to everyone.
        void Publish(string status, bool notify)
        {
            Config.Active = phase != Phase.Idle;
            Store();
            Status = status;
            Block.RefreshCustomInfo();
            if (notify)
                ShowNote(status);
            Net.BroadcastAutopilot(this, notify);
        }

        // Client: the server's settings and status.
        public void Apply(string config, string status, bool notify)
        {
            Config = AutopilotConfig.Load(config);
            Store();
            Status = status ?? "";
            Block.RefreshCustomInfo();
            if (notify)
                ShowNote(Status);
        }

        // Makes an open terminal rebuild its controls (list contents, enabled state).
        void RefreshTerminal()
        {
            Block.RefreshCustomInfo();
            bool show = Block.ShowInToolbarConfig;
            Block.ShowInToolbarConfig = !show;
            Block.ShowInToolbarConfig = show;
        }

        // ---- what players see ----

        static IMyHudNotification hud;
        static string hudText;
        const int HudTicks = 20, GpsTicks = 60, LightTicks = 10, LightBlinkTicks = 30, LightRepaintTicks = 120;
        static readonly Color LightIdle = Color.Yellow, LightDriving = new Color(0, 160, 255), LightArrived = Color.Green, LightError = Color.Red;

        int hudTick = -999, gpsTick = -999, lightTick = -999, lightPaintTick = -999;
        readonly List<IMyGps> gpsMarks = new List<IMyGps>();
        string gpsKey = "";
        Color lightColor;
        float lightEmissivity = -1;

        void ShowNote(string status)
        {
            if (!MyAPIGateway.Utilities.IsDedicated && LocalPlayerInTrain())
                MyAPIGateway.Utilities.ShowNotification(Loc.F("MagRail_Ap_Hud", AutopilotStatus.Render(status)), 3000, "White");
        }

        bool LocalPlayerInTrain()
        {
            IMyShipController local = MyAPIGateway.Session.ControlledObject as IMyShipController;
            return local != null && SameTrain(local.CubeGrid, Block.CubeGrid);
        }

        // A constant HUD line with the status while the autopilot drives the local player's train, like the
        // vanilla AI blocks.
        void UpdateHud()
        {
            if (tick - hudTick < HudTicks || !Config.Active || !LocalPlayerInTrain())
                return;
            hudTick = tick;
            string text = Loc.F("MagRail_Ap_Hud", AutopilotStatus.Render(Status));
            if (hud == null || hudText != text)
            {
                if (hud != null)
                    hud.Hide();
                hud = MyAPIGateway.Utilities.CreateNotification(text, 1000, "White");
                hudText = text;
                hud.Show();
            }
            else
            {
                hud.ResetAliveTime();
            }
        }

        // Local GPS marks of the stations (not saved, this machine only) while Show stations is on and the local
        // player has access to the block.
        void UpdateGps()
        {
            if (tick - gpsTick < GpsTicks || MyAPIGateway.Session.GPS == null)
                return;
            gpsTick = tick;
            IMyPlayer player = MyAPIGateway.Session.Player;
            string key = Config.ShowStations && Config.Stations.Count > 0 && player != null && Block.IsFunctional && Block.HasPlayerAccess(player.IdentityId)
                ? Block.CustomName + "\n" + Config.Save() : "";
            if (key == gpsKey)
                return;
            RemoveGps();
            gpsKey = key;
            if (key.Length == 0)
                return;
            for (int i = 0; i < Config.Stations.Count; i++)
            {
                IMyGps gps = MyAPIGateway.Session.GPS.Create((i + 1) + ". " + Config.Name(i), Loc.F("MagRail_Ap_GpsDescription", Block.CustomName),
                    Config.Stations[i].Point, true, true);
                MyAPIGateway.Session.GPS.AddLocalGps(gps);
                gpsMarks.Add(gps);
            }
        }

        void RemoveGps()
        {
            if (MyAPIGateway.Session != null && MyAPIGateway.Session.GPS != null)
                for (int i = 0; i < gpsMarks.Count; i++)
                    MyAPIGateway.Session.GPS.RemoveLocalGps(gpsMarks[i]);
            gpsMarks.Clear();
            gpsKey = "";
        }

        // The model's 'Emissive' material shows the state, like a vanilla block: idle yellow, driving blue, arrived
        // green, error blinking red; off red, damaged dark. Painted again now and then: a build stage model swap
        // resets the material.
        void UpdateLight()
        {
            if (tick - lightTick < LightTicks)
                return;
            lightTick = tick;
            Color color;
            float emissivity = 1;
            if (!Block.IsFunctional)
            {
                color = Color.Black;
                emissivity = 0;
            }
            else if (!Block.IsWorking)
            {
                color = LightError;
            }
            else
            {
                string key = AutopilotStatus.Key(Status);
                if (key.StartsWith("MagRail_Ap_Err", StringComparison.Ordinal) || key == "MagRail_Ap_Error" || key == "MagRail_Ap_StoppingError"
                    || key == "MagRail_Ap_InternalError")
                {
                    color = LightError;
                    if ((tick / LightBlinkTicks) % 2 == 1)
                        emissivity = 0;
                }
                else if (Config.Active || Config.Running)
                {
                    color = LightDriving;
                }
                else if (key == "MagRail_Ap_Arrived" || key == "MagRail_Ap_ArrivedParked")
                {
                    color = LightArrived;
                }
                else
                {
                    color = LightIdle;
                }
            }
            if (color == lightColor && emissivity == lightEmissivity && tick - lightPaintTick < LightRepaintTicks)
                return;
            lightColor = color;
            lightEmissivity = emissivity;
            lightPaintTick = tick;
            Block.SetEmissiveParts("Emissive", color, emissivity);
        }

        void AppendInfo(IMyTerminalBlock b, StringBuilder sb)
        {
            try
            {
                AutopilotConfig c = Config;
                sb.Append(Loc.F("MagRail_Ap_Hud", AutopilotStatus.Render(Status))).Append('\n');
                sb.Append(Loc.F("MagRail_Ap_InfoStations", c.Stations.Count, c.Name(c.Selected))).Append('\n');
                sb.Append(Loc.F("MagRail_Ap_InfoPark", Loc.T(c.Park ? "MagRail_Yes" : "MagRail_No"))).Append('\n');
                sb.Append(Loc.F("MagRail_Ap_InfoMaxSpeed", Cruise.Speed(c.MaxSpeed))).Append('\n');
                if (phase == Phase.Driving)
                    sb.Append(Loc.F("MagRail_Ap_InfoRoute", FormatDistance(distance), Cruise.Speed(Math.Abs(lastSpeed)))).Append('\n');
            }
            catch (Exception ex)
            {
                Log.Error("AutopilotLogic.AppendInfo", ex);
            }
        }
    }
}
