using System;
using System.Collections.Generic;
using Sandbox.ModAPI;
using Sandbox.ModAPI.Interfaces;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRageMath;

namespace MagRail
{
    public enum DriveState
    {
        Idle,
        Driving,
        Braking,        // Space with Braking on
        Parked,         // the grid's hand brake
        Dampers,        // inertia dampeners (Z) on and nothing pushes: the engines' thrust stops the train
        NoController,   // the engines are asked to push but no controller gives the direction
        Sideways,       // the controller faces across the rail
        Cruise,         // the train's cruise control holds the speed
        Autopilot,      // the train's autopilot drives
    }

    // The drive of one grid on the rail (part of its RailBody), ported from the old Maglev (MaglevDrive,
    // MaglevPower, MaglevParking). Driven like wheels from a cockpit, seat or remote control (W/S, Space brake,
    // the controller's "Control wheels", main cockpit) with the native suspension controls: Propulsion, Power,
    // Speed Limit, Propulsion override, Braking, Parking.
    // - Thrust is game-like, as a thruster's: along the rail at the centre of mass, so an engine at one end never
    //   turns the grid. An engine has no direction of its own: the controller's axis nearest the rail drives, so a
    //   cockpit facing along the rail drives with W/S, one turned sideways with D/A, one facing up with Space/C
    //   (then Space is no brake). The axis is kept until it is more than 60 degrees off the rail. In a curve the
    //   push comes with the curve's turn (RailBody.AlongResponse), so the grid's ends stay on the rail.
    // - The controller is looked up on the grids mechanically joined to this one (rotor, hinge, piston), as the
    //   game's wheel control reaches. Every grid of a train pushes itself with its own engines: couplers carry
    //   little.
    // - Power is spent on thrust only, in proportion to the force (full thrust = the engine's max power), from the
    //   engine's own grid network; the thrust is scaled by the share the network supplied. Holding the rail,
    //   coasting, Space and parking are free.
    // - Parking is a brake along the rail, not a lock: a share of each parked engine's force. A grid nearly
    //   standing with nothing pushing it is held by a weak brake (rest hold), so free wagons do not roll into a
    //   braked locomotive.
    // - Inertia dampeners (the vanilla Z of the controller, on a grid with a Train Control Unit): with nothing
    //   pushing, the engines push against the grid's motion as W/S would (Propulsion, Power, power drawn) and
    //   stop it and hold it, as a ship's dampeners use its thrusters; off, the train coasts. Every grid of the
    //   train reads the controller's flag, so the train stops together.
    // - The numbers are the defaults of DriveTuning (temporary: tuned from the cockpit terminal).
    public class Drive
    {
        const double Step = 1.0 / 60.0;
        // Shares of the engine's force (HoverEngineLogic.Force, PLAN 10.1): thrust at 100% Power, the most of the
        // Space brake, parking. The Space brake of an engine is BrakeFactor times its thrust (Power slider), up to
        // BrakeShare: Power 10% brakes with 20% of the force, 50% and more with 100% (user decision 2026-10-11).
        public const double ThrustShare = 1.0, BrakeShare = 1.0, ParkShare = 1.5, BrakeFactor = 2.0;
        // Controller forward vs rail: |cos| below this counts as sideways (60 degrees).
        const double AlignMin = 0.5;
        // Cruise brake (downhill, over the target) as a share of the engine's thrust: gentle, so pushing wagons do
        // not fold up the train; a slope steeper than that speeds the train up (the old Maglev's number).
        const double CruiseBrakeShare = 0.3;
        // Thrust fades out over the last m/s below the speed limit.
        const double SpeedLimitBand = 1.0;
        const int ControllerRefreshTicks = 30, HandBrakeRefreshTicks = 10;

        readonly IMyCubeGrid grid;
        static readonly List<IMyShipController> scratch = new List<IMyShipController>();
        static readonly List<double> forces = new List<double>();
        static readonly List<IMyCubeGrid> groupScratch = new List<IMyCubeGrid>();

        // Controller a player drives the grid from (or null) and the one whose facing sets the direction: the
        // pilot, else the main cockpit, else any controller that controls wheels (Propulsion override without a
        // pilot).
        IMyShipController pilot, reference;
        int controllerTick = -999;
        // The reference controller's axis that drives along the rail (0 Forward: W/S, 1 Right: D/A, 2 Up: Space/C;
        // -1 = not picked) and the controller it was picked for.
        int driveAxis = -1;
        IMyShipController axisController;
        // The reference controller's grid has a Train Control Unit (else it has no dampeners to read); the train's
        // units, and the one whose cruise is on this tick.
        bool unit;
        readonly List<TrainControlLogic> trainUnits = new List<TrainControlLogic>();
        TrainControlLogic trainUnit;
        // This tick: the cruise drives, and the pilot's W/S along the cruise direction (+1 = the way it pulls).
        bool cruising;
        double cruiseMove;
        // This tick: the autopilot's order (AutopilotLogic), if it drives the train.
        bool autopilot;
        AutopilotLogic.Command order;
        DriveTuning tuning = DriveTuning.Default;
        bool handBrake;
        int handBrakeTick = -999;
        // This tick's input.
        double move;
        bool brakeKey;
        // This tick's brakes are the parking brake (else Space); ticks the Space brake has been on.
        bool parked;
        int brakeTicks;
        // Along-rail speed written last tick while braking: the difference to this tick's speed is what gravity,
        // the carrier's acceleration and couplers added in between, and the brake cancels it up front (never more
        // than its force: static friction, not a lock).
        bool brakeHistory;
        double brakeWrittenAlong;

        // Last tick's state (terminal info, /magrail log): thrust given along the rail (N, plus = pathForward),
        // brake force (N), the network's share of the thrust asked (1 = full).
        public DriveState State;
        public double Force, BrakeForce, PowerShare = 1;
        // The brake force was cut to the acceleration limit (DriveTuningHud).
        public bool BrakeCapped;

        public IMyShipController Reference
        {
            get { return reference; }
        }

        // Terminal info: the train has a controller but no Train Control Unit on its grid (Z does nothing).
        public bool NoControlUnit
        {
            get { return reference != null && !unit; }
        }

        public Drive(IMyCubeGrid grid)
        {
            this.grid = grid;
        }

        public void ReadInput(int tick, Vector3D pathForward)
        {
            RefreshController(tick);
            PickAxis(pathForward);
            move = 0;
            brakeKey = false;
            if (pilot != null)
            {
                Vector3 m = pilot.MoveIndicator;
                double input = driveAxis == 1 ? m.X : driveAxis == 2 ? m.Y : -m.Z;
                move = MathHelper.Clamp(input, -1.0, 1.0);
                brakeKey = driveAxis != 2 && m.Y > 0.5f;
            }
            bool trainParked = reference != null && !reference.MarkedForClose && reference.HandBrake;
            // Parked train: W/S pushes no grid of it (else the wagons push the parked locomotive along).
            if (move != 0 && tuning.ParkStopsDrive && trainParked)
                move = 0;
            // The autopilot drives: the pilot's keys do nothing, and the cruise would drive on after it.
            trainUnit = CruisingUnit();
            autopilot = AutopilotLogic.TryGetCommand(grid, out order);
            if (autopilot)
            {
                move = 0;
                brakeKey = false;
                cruising = false;
                if (trainUnit != null)
                    Cruise.CancelTrain(grid, null);
                return;
            }
            ReadCruise(trainParked);
        }

        // ---- autopilot ----

        // The autopilot's speed along its direction (minus = back), held as the cruise holds its own: pushing the
        // way the grid moves takes the engines' thrust (Power, Speed Limit), against it their full force. A grid
        // square to the direction (a far wagon in a tight curve) goes the way it moves.
        double AutopilotThrust(List<HoverEngineLogic> held, Vector3D pathForward, double along, double mass, double ramp, int tick)
        {
            State = DriveState.Autopilot;
            double facing = Vector3D.Dot(order.Direction, pathForward);
            double sign = Math.Abs(facing) >= 0.3 ? Math.Sign(facing) : Math.Abs(along) > 0.1 ? Math.Sign(along) * Math.Sign(order.Speed) : 0;
            if (sign == 0)
                return 0;
            double speed = along * sign;
            double wanted = (order.Speed - speed) * mass / Step;
            bool pushing = Math.Abs(speed) < 0.05 || (wanted > 0) == (speed > 0);
            forces.Clear();
            double limit = 0;
            for (int i = 0; i < held.Count; i++)
            {
                HoverEngineLogic e = held[i];
                double force = 0;
                if (e.Block.Propulsion && e.Block.PropulsionOverride == 0)
                {
                    if (!pushing)
                        force = e.Force * tuning.ThrustShare;
                    else
                    {
                        force = e.Force * tuning.ThrustShare * PowerSlider(e);
                        double speedLimit = SpeedLimit(e);
                        if (Math.Abs(speed) > 0 && speedLimit < double.MaxValue)
                            force *= MathHelper.Clamp((speedLimit - Math.Abs(speed)) / SpeedLimitBand, 0.0, 1.0);
                    }
                }
                forces.Add(force * (wanted > 0 ? sign : -sign));
                limit += force;
            }
            double cap = Math.Min(Math.Abs(wanted), (pushing ? tuning.MaxThrustAccel : tuning.MaxBrakeAccel) * mass);
            double share = limit > 0 ? Math.Min(cap / limit, 1.0) : 0;
            return Push(held, share, mass, ramp);
        }

        // ---- cruise ----

        // The train's unit with its cruise on (two after a merge of trains: the lowest id), or null.
        TrainControlLogic CruisingUnit()
        {
            TrainControlLogic best = null;
            for (int i = 0; i < trainUnits.Count; i++)
            {
                TrainControlLogic u = trainUnits[i];
                if (u.Cruise.Engaged && !u.Block.MarkedForClose && u.Block.IsFunctional && (best == null || u.Block.EntityId < best.Block.EntityId))
                    best = u;
            }
            return best;
        }

        // The train's cruise (on its Train Control Unit, Cruise): S, Space and the train's hand brake cancel it, the
        // target is kept for Resume.
        void ReadCruise(bool trainParked)
        {
            cruising = false;
            cruiseMove = 0;
            if (trainUnit == null)
                return;
            // W/S are the cockpit's; the cruise pulls the way its unit pushes. A cockpit facing the other way: S is
            // gas over the target and W cancels; one facing across it: either cancels.
            if (pilot != null && move != 0)
            {
                double facing = Vector3D.Dot(Axis(pilot, driveAxis), Cruise.Direction(trainUnit));
                cruiseMove = Math.Abs(facing) < AlignMin ? -Math.Abs(move) : move * Math.Sign(facing);
            }
            int cancel = Cruise.NoteNone;
            if (cruiseMove < -0.1 || (pilot != null && brakeKey))
                cancel = Cruise.NoteBrake;
            else if (trainParked)
                cancel = Cruise.NoteParking;
            if (cancel != Cruise.NoteNone)
            {
                Cruise s = trainUnit.Cruise;
                s.Engaged = false;
                Cruise.Publish(trainUnit, s, cancel);
                return;
            }
            cruising = true;
        }

        // Cruise thrust: every tick the engines put this grid back on the target speed, as far as their thrust (or
        // the gentle cruise brake) reaches on this grid's own mass; whatever the wagons, slopes and drag take away is
        // made up on the next tick, so nothing is learned and nothing swings (the old Maglev's cruise). Engines with
        // Propulsion off or an override do not cruise.
        double CruiseThrust(List<HoverEngineLogic> held, Vector3D pathForward, double along, double mass, double ramp, int tick)
        {
            State = DriveState.Cruise;
            double facing = Vector3D.Dot(Cruise.Direction(trainUnit), pathForward);
            if (Math.Abs(facing) < AlignMin)
            {
                State = DriveState.Sideways;
                return 0;
            }
            double sign = facing > 0 ? 1 : -1;
            double speed = along * sign;
            double wanted = (trainUnit.Cruise.Target - speed) * mass / Step;
            bool pushing = wanted >= 0;
            forces.Clear();
            double limit = 0;
            for (int i = 0; i < held.Count; i++)
            {
                HoverEngineLogic e = held[i];
                double force = 0;
                if (e.Block.Propulsion && e.Block.PropulsionOverride == 0 && !Braking(e, tick))
                {
                    if (!pushing)
                        force = e.Force * tuning.ThrustShare * CruiseBrakeShare;
                    else
                    {
                        force = e.Force * tuning.ThrustShare * PowerSlider(e);
                        double speedLimit = SpeedLimit(e);
                        if (speed > 0 && speedLimit < double.MaxValue)
                            force *= MathHelper.Clamp((speedLimit - speed) / SpeedLimitBand, 0.0, 1.0);
                    }
                }
                forces.Add(force * (pushing ? sign : -sign));
                limit += force;
            }
            double cap = Math.Min(Math.Abs(wanted), (pushing ? tuning.MaxThrustAccel : tuning.MaxBrakeAccel) * mass);
            double share = limit > 0 ? Math.Min(cap / limit, 1.0) : 0;
            return Push(held, share, mass, ramp);
        }

        // Native ShouldBrake: Braking on, and Space held or the engine parked.
        bool Braking(HoverEngineLogic e, int tick)
        {
            return e.Block.Brake && (brakeKey || (e.Block.IsParkingEnabled && HandBrake(tick)));
        }

        // Along-rail velocity change of the engines' thrust this tick. along: the along speed about to be written
        // (relative to the rail, sign of pathForward); drift: what gravity and the rail's own acceleration change it
        // by after the write (the dampers cancel it up front). Sets every holding engine's power demand.
        public double Thrust(List<HoverEngineLogic> held, Vector3D pathForward, double along, double drift, double mass, double ramp, int tick)
        {
            State = DriveState.Idle;
            Force = 0;
            PowerShare = 1;
            BrakeForce = 0;
            bool anyInput = false;
            for (int i = 0; i < held.Count; i++)
            {
                HoverEngineLogic e = held[i];
                if (Braking(e, tick))
                {
                    BrakeForce += e.Force * (brakeKey
                        ? Math.Min(tuning.BrakeFactor * tuning.ThrustShare * PowerSlider(e), tuning.BrakeShare)
                        : tuning.ParkShare);
                    continue;
                }
                anyInput |= Input(e) != 0;
            }
            parked = BrakeForce > 0 && !brakeKey;
            if (BrakeForce > 0)
                State = parked ? DriveState.Parked : DriveState.Braking;
            if (autopilot && BrakeForce <= 0)
                return AutopilotThrust(held, pathForward, along, mass, ramp, tick);
            // W over the cruise is gas over the target: the pilot's thrust instead.
            if (cruising && cruiseMove <= 0.1 && BrakeForce <= 0)
                return CruiseThrust(held, pathForward, along, mass, ramp, tick);
            if (!anyInput)
                return BrakeForce <= 0 && Dampers() ? Damp(held, along + drift, mass, ramp) : 0;
            if (reference == null || reference.MarkedForClose)
            {
                State = DriveState.NoController;
                return 0;
            }
            double facing = Vector3D.Dot(Axis(reference, driveAxis), pathForward);
            if (Math.Abs(facing) < AlignMin)
            {
                State = DriveState.Sideways;
                return 0;
            }
            double sign = facing > 0 ? 1 : -1;

            forces.Clear();
            double sum = 0;
            for (int i = 0; i < held.Count; i++)
            {
                HoverEngineLogic e = held[i];
                double force = 0;
                if (!Braking(e, tick))
                {
                    force = e.Force * tuning.ThrustShare * MathHelper.Clamp(Input(e), -1.0, 1.0) * sign;
                    // The speed limit only holds back thrust that speeds the grid up, never braking.
                    double speedWithThrust = along * Math.Sign(force);
                    double limit = SpeedLimit(e);
                    if (force != 0 && speedWithThrust > 0 && limit < double.MaxValue)
                        force *= MathHelper.Clamp((limit - speedWithThrust) / SpeedLimitBand, 0.0, 1.0);
                }
                forces.Add(force);
                sum += force;
            }
            double max = tuning.MaxThrustAccel * mass;
            double scale = mass > 0 && Math.Abs(sum) > max ? max / Math.Abs(sum) : 1;
            if (State == DriveState.Idle)
                State = DriveState.Driving;
            return Push(held, scale, mass, ramp);
        }

        // Inertia dampeners: every engine with Propulsion on pushes against the grid's motion along the rail as W/S
        // would (its Power slider, the same power and limit), only as hard as it takes to stop the grid this tick,
        // so a stopped train stays held on a slope (and draws power for it). along: the along speed the grid would
        // have after this tick's physics (gravity added), so a lift does not creep down at g/60 m/s.
        double Damp(List<HoverEngineLogic> held, double along, double mass, double ramp)
        {
            State = DriveState.Dampers;
            forces.Clear();
            double sum = 0;
            for (int i = 0; i < held.Count; i++)
            {
                HoverEngineLogic e = held[i];
                double force = e.Block.Propulsion ? e.Force * tuning.ThrustShare * PowerSlider(e) : 0;
                forces.Add(force);
                sum += force;
            }
            double needed = Math.Min(mass * Math.Abs(along) / (Step * ramp), tuning.MaxThrustAccel * mass);
            double scale = sum > needed ? needed / sum : 1;
            return Push(held, -Math.Sign(along) * scale, mass, ramp);
        }

        // Gives forces[i] * scale through the engines: each asks power for its force and gives the share its
        // network supplied. Returns the along-rail velocity change.
        double Push(List<HoverEngineLogic> held, double scale, double mass, double ramp)
        {
            double wanted = 0;
            for (int i = 0; i < held.Count; i++)
            {
                HoverEngineLogic e = held[i];
                double force = forces[i] * scale;
                if (force == 0)
                    continue;
                e.PowerWanted += (float)(Math.Abs(force) / (e.Force * tuning.ThrustShare) * e.MaxPower);
                wanted += Math.Abs(force);
                Force += force * e.PowerSupply;
            }
            PowerShare = wanted > 0 ? Math.Abs(Force) / wanted : 1;
            return mass > 0 ? Force / mass * Step * ramp : 0;
        }

        // Along-rail velocity change of the brakes this tick: the engines braking (Space, parking) or, on a grid
        // nearly standing that nothing pushes, the rest hold. carriedAlong: the along speed as it arrived this tick
        // (relative to the rail, turned with it); along: the along speed about to be written; continuous: the body
        // wrote last tick too.
        public double Brake(double carriedAlong, double along, double mass, double ramp, bool idle, bool continuous)
        {
            double force = BrakeForce;
            bool space = force > 0 && !parked;
            brakeTicks = space ? brakeTicks + 1 : 0;
            if (space && tuning.BrakeRise > 0)
                force *= Math.Min(1.0, DriveTuning.BrakeRiseStart + (1 - DriveTuning.BrakeRiseStart) * brakeTicks * Step / tuning.BrakeRise);
            if (force <= 0 && idle && Math.Abs(carriedAlong) < tuning.RestSpeed)
                force = mass * tuning.RestAccel;
            BrakeCapped = false;
            if (force <= 0 || mass <= 0)
            {
                brakeHistory = false;
                return 0;
            }
            double max = mass * (parked ? tuning.MaxParkAccel : tuning.MaxBrakeAccel);
            BrakeCapped = force > max;
            force = Math.Min(force, max);
            if (space && tuning.SoftStop > 0)
                force *= MathHelper.Clamp(Math.Abs(along) / tuning.SoftStop, DriveTuning.SoftStopFloor, 1.0);
            BrakeForce = Math.Min(BrakeForce, force);
            double maxChange = force / mass * Step * ramp;
            double disturbance = brakeHistory && continuous && tuning.Disturbance
                ? MathHelper.Clamp(carriedAlong - brakeWrittenAlong, -maxChange, maxChange) : 0;
            double change = MathHelper.Clamp(-(along + disturbance), -maxChange, maxChange);
            brakeWrittenAlong = along + change;
            brakeHistory = true;
            return change;
        }

        public void Release()
        {
            driveAxis = -1;
            axisController = null;
            cruising = false;
            cruiseMove = 0;
            autopilot = false;
            brakeHistory = false;
            brakeTicks = 0;
            BrakeCapped = false;
            State = DriveState.Idle;
            Force = 0;
            BrakeForce = 0;
            PowerShare = 1;
        }

        // The controller's inertia dampeners: the vanilla flag of its grid (it exists only with a thruster there, the
        // Train Control Unit). Read every tick, with a pilot or not, as a ship's dampeners work.
        bool Dampers()
        {
            return reference != null && !reference.MarkedForClose && unit && reference.DampenersOverride;
        }

        // An engine's push, -1..1: Propulsion override, else W/S times its Power slider; nothing with Propulsion off.
        double Input(HoverEngineLogic e)
        {
            IMyMotorSuspension s = e.Block;
            if (!s.Propulsion)
                return 0;
            double input = s.PropulsionOverride;
            if (input == 0)
                input = move * PowerSlider(e);
            return input;
        }

        static double PowerSlider(HoverEngineLogic e)
        {
            return MathHelper.Clamp(e.Block.Power / 100.0, 0.0, 1.0);      // the API gives the slider's 0..100
        }

        static double SpeedLimit(HoverEngineLogic e)
        {
            // The game shows the Speed Limit slider only with this world option; without it the slider cannot be
            // set, so it does not limit either. Not in the mod API: read through the terminal property (km/h).
            if (MyAPIGateway.Session.SessionSettings == null || !MyAPIGateway.Session.SessionSettings.AdjustableMaxVehicleSpeed)
                return double.MaxValue;
            float kmh = e.Block.GetValueFloat("Speed Limit");
            return kmh >= 360f ? double.MaxValue : kmh / 3.6;
        }

        // ---- controllers ----

        // The reference controller's axis that drives: kept while it is within 60 degrees of the rail (a curve does
        // not swap the keys midway), else the one nearest the rail. One of three square axes is always within 55.
        void PickAxis(Vector3D pathForward)
        {
            if (reference == null || reference.MarkedForClose)
            {
                driveAxis = -1;
                axisController = null;
                return;
            }
            if (reference != axisController)
            {
                driveAxis = -1;
                axisController = reference;
            }
            if (driveAxis >= 0 && Math.Abs(Vector3D.Dot(Axis(reference, driveAxis), pathForward)) >= AlignMin)
                return;
            double best = -1;
            for (int i = 0; i < 3; i++)
            {
                double d = Math.Abs(Vector3D.Dot(Axis(reference, i), pathForward));
                if (d > best)
                {
                    best = d;
                    driveAxis = i;
                }
            }
        }

        static Vector3D Axis(IMyShipController c, int axis)
        {
            MatrixD m = c.WorldMatrix;
            return axis == 1 ? m.Right : axis == 2 ? m.Up : m.Forward;
        }

        void RefreshController(int tick)
        {
            if (tick - controllerTick < ControllerRefreshTicks && tick >= controllerTick
                && (pilot == null || (!pilot.MarkedForClose && pilot.IsUnderControl)))
                return;
            controllerTick = tick;
            FindControllers(grid, out pilot, out reference);
            unit = reference != null && TrainControlLogic.OnGrid(reference.CubeGrid);
            TrainControlLogic.CollectTrain(grid, trainUnits);
            tuning = DriveTuning.For(reference);
        }

        // The train's controllers (the grids mechanically joined to this one): the one a player drives from (or
        // null) and the one whose facing sets the direction: the pilot, else the main cockpit, else any controller
        // that controls wheels (the lowest id, so every grid of the train picks the same one).
        public static void FindControllers(IMyCubeGrid grid, out IMyShipController pilot, out IMyShipController reference)
        {
            IMyShipController main = null, any = null, piloted = null;
            CollectGroupControllers(grid);
            for (int i = 0; i < scratch.Count; i++)
            {
                IMyShipController s = scratch[i];
                if (s.MarkedForClose || !s.IsFunctional || !s.CanControlShip || !s.ControlWheels)
                    continue;
                if (s.IsMainCockpit)
                    main = s;
                if (any == null || s.EntityId < any.EntityId)
                    any = s;
                if (s.IsUnderControl && (piloted == null || s.IsMainCockpit))
                    piloted = s;
            }
            scratch.Clear();
            // With a main cockpit, only it drives (as the game's own ship control).
            if (main != null && piloted != main)
                piloted = null;
            pilot = piloted;
            reference = piloted ?? main ?? any;
        }

        // Every ship controller on the grids mechanically joined to this one (not over connectors).
        static void CollectGroupControllers(IMyCubeGrid grid)
        {
            scratch.Clear();
            groupScratch.Clear();
            MyAPIGateway.GridGroups.GetGroup(grid, GridLinkTypeEnum.Mechanical, groupScratch);
            var terminal = MyAPIGateway.TerminalActionsHelper.GetTerminalSystemForGrid(grid);
            if (terminal != null)
                terminal.GetBlocksOfType(scratch, s => groupScratch.Contains(s.CubeGrid));
            groupScratch.Clear();
        }

        // The grid's hand brake (P, the cockpit's Hand brake), read through any ship controller on it: the game
        // keeps it in the grid's wheel system, which the mod API only shows there. Only the grid's own counts.
        bool HandBrake(int tick)
        {
            if (tick - handBrakeTick < HandBrakeRefreshTicks && tick >= handBrakeTick)
                return handBrake;
            handBrakeTick = tick;
            handBrake = false;
            scratch.Clear();
            var terminal = MyAPIGateway.TerminalActionsHelper.GetTerminalSystemForGrid(grid);
            if (terminal != null)
                terminal.GetBlocksOfType(scratch, c => c.CubeGrid == grid);
            for (int i = 0; i < scratch.Count && !handBrake; i++)
                handBrake = scratch[i].HandBrake;
            scratch.Clear();
            return handBrake;
        }
    }
}
