using System;
using System.Collections.Generic;
using System.Text;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Definitions;
using Sandbox.Game.EntityComponents;
using Sandbox.ModAPI;
using VRage.Game;
using VRage.Game.Components;
using VRage.Game.Entity;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRage.ObjectBuilders;
using VRageMath;

namespace MagRail
{
    public enum RailState
    {
        Off,        // switched off or damaged
        NoRail,     // no rail within the search distance
        Far,        // rail found, the hover point too far from its place over it
        Tilted,     // rail found, the engine not upright to it
        TrackEnd,   // the engine is past the end of the track
        Held,       // the engine holds its point over the rail
    }

    // A hover engine: a wheel suspension without a wheel (RotorPart names no block group, so the game spawns
    // none). A sensor of its grid's RailBody: its guide point (block centre + offsets), the cursor on the rail
    // under it and its settings; and the drive's power consumer. Numbers are the old Maglev's, so the ported physics
    // keeps them.
    [MyEntityComponentDescriptor(typeof(MyObjectBuilder_MotorSuspension), false,
        "MagRail_EngineLarge", "MagRail_EngineSmall", "MagRail_EngineMini")]
    public class HoverEngineLogic : MyGameLogicComponent
    {
        // Must be listed in Data/EntityComponents_MagRail.sbc, or the game drops it on save.
        static readonly Guid SettingsGuid = new Guid("680eadb9-ba93-4634-accb-e32545f16dc7");

        // The rail is captured within this distance of the hover point and let go beyond BreakawayDistance.
        public const double CaptureDistance = 4.0, BreakawayDistance = 6.0;
        const double SearchMargin = 12.0, MinSearch = 14.0;

        public IMyMotorSuspension Block { get; private set; }
        public EngineSettings Settings { get; private set; }
        // Server: the physics' cursor and capture state (RailBody). DebugCursor: /magrail debug and the
        // status of the terminal info on clients, so they do not move the physics' cursor.
        public readonly TrackCursor Cursor = new TrackCursor(), DebugCursor = new TrackCursor();
        public RailState Status = RailState.NoRail;
        public bool Held;
        public double LastError;
        public Vector3D LastOffset;
        public bool HasLastOffset;
        public int JumpTicks, LostTicks, NextSearchTick;
        // Server: the power the drive asks for this tick, MW (Drive; Rides hands it to the network).
        public float PowerWanted;
        // The model has a platform (EnginePlatform): the player sets the train height (the base's top over the rail's
        // top) and the platform's gap over the rail. The platform hangs under the base within the model's range of
        // depths (restMin .. restMax, m under the block centre; one depth without an offset range), so the train
        // height's range is the model's too. Clients: the platform itself (cosmetic).
        public bool HasPlatform { get; private set; }
        public double TrainHeightMin { get; private set; }
        public double TrainHeightMax { get; private set; }
        public EnginePlatform Platform;
        // baseTop: the base's top over the block centre (the model's top), m.
        double restMin, restMax, baseTop, defaultTrainHeight, defaultGap;
        MyResourceSinkComponent sink;
        float demand;
        bool registered;

        public override void Init(MyObjectBuilder_EntityBase objectBuilder)
        {
            Block = (IMyMotorSuspension)Entity;
            NeedsUpdate |= MyEntityUpdateEnum.BEFORE_NEXT_FRAME;
        }

        public override void UpdateOnceBeforeFrame()
        {
            try
            {
                // Projections and placement previews have no physics: no logic for them.
                if (Block.CubeGrid.Physics == null || MagRailSession.Instance == null)
                    return;
                ReadPlatform();
                Settings = LoadSettings() ?? new EngineSettings
                {
                    HoverHeight = Block.CubeGrid.GridSizeEnum == MyCubeSize.Small ? EngineSettings.SmallHeight : EngineSettings.LargeHeight,
                };
                if (HasPlatform && Settings.TrainHeight <= 0)
                {
                    Settings.TrainHeight = (float)defaultTrainHeight;
                    Settings.PlatformGap = (float)defaultGap;
                    FitGap(Settings);
                }
                MagRailSession.Instance.Engines.Add(this);
                registered = true;
                HookPower();
                Block.AppendingCustomInfo += EngineInfo.Append;
                EngineTerminal.Create();
                if (!MyAPIGateway.Utilities.IsDedicated && HasPlatform)
                    Platform = new EnginePlatform(this);
            }
            catch (Exception e)
            {
                Log.Error("HoverEngineLogic.UpdateOnceBeforeFrame", e);
            }
        }

        void ReadPlatform()
        {
            double rest, zero, min, max;
            IMyModel model = FinishedModel();
            HasPlatform = EnginePlatform.ReadRest(model, out rest, out zero, out min, out max);
            if (!HasPlatform)
                return;
            bool range = !double.IsNaN(zero) && min <= max;
            restMin = range ? zero + min : rest;
            restMax = range ? zero + max : rest;
            baseTop = model.BoundingBox.Max.Y;
            TrainHeightMin = baseTop + restMin;
            TrainHeightMax = baseTop + restMax + EngineSettings.MaxGap;
            // A new engine: the Mini 1.5 m high with a 0.5 m gap, the large engine (both grids) 2.5 m with 1 m.
            defaultTrainHeight = MathHelper.Clamp(IsMini ? 1.5 : 2.5, TrainHeightMin, TrainHeightMax);
            defaultGap = IsMini ? 0.5 : EngineSettings.DefaultGap;
        }

        // The block's finished model (its definition's), not the one it shows: a block being built or ground down shows a
        // construction stage, without the platform's dummy and with another top. Read through an entity that never
        // enters the scene (MyModels is not open to mods), once per model.
        static readonly Dictionary<string, IMyModel> finishedModels = new Dictionary<string, IMyModel>();

        IMyModel FinishedModel()
        {
            MyCubeBlockDefinition def = Block.SlimBlock.BlockDefinition as MyCubeBlockDefinition;
            if (def == null || string.IsNullOrEmpty(def.Model))
                return Block.Model;
            IMyModel model;
            if (!finishedModels.TryGetValue(def.Model, out model))
            {
                MyEntity e = new MyEntity();
                e.Init(null, def.Model, null, null, null);
                model = ((IMyEntity)e).Model;
                e.Close();
                finishedModels[def.Model] = model;
            }
            return model ?? Block.Model;
        }

        public override void Close()
        {
            if (registered && MagRailSession.Instance != null)
                MagRailSession.Instance.Engines.Remove(this);
            if (registered)
                Block.AppendingCustomInfo -= EngineInfo.Append;
            if (Platform != null)
                Platform.Remove();
            registered = false;
        }

        // ---- settings ----

        // A player (terminal, programmable block) asks for new settings, on any machine.
        public void Request(EngineSettings requested)
        {
            if (Net.IsServer)
            {
                ApplyRequest(requested);
                return;
            }
            // Shown at once; the server's answer overwrites it.
            SetSettings(requested.Clamped());
            Net.SendEngineSettingsRequest(this, requested);
        }

        // Server: a request passed the access check.
        public void ApplyRequest(EngineSettings requested)
        {
            SetSettings(requested.Clamped());
            Net.BroadcastEngineSettings(this);
        }

        public void SetSettings(EngineSettings settings)
        {
            Settings = settings;
            Block.Storage = Block.Storage ?? new MyModStorageComponent();
            Block.Storage[SettingsGuid] = Settings.Encode();
            EngineTerminal.Refresh();
        }

        EngineSettings LoadSettings()
        {
            string text;
            if (Block.Storage == null || !Block.Storage.TryGetValue(SettingsGuid, out text) || string.IsNullOrEmpty(text))
                return null;
            try
            {
                return EngineSettings.Decode(text).Clamped();
            }
            catch (Exception e)
            {
                Log.Error("HoverEngineLogic.LoadSettings", e);
                return null;
            }
        }

        // ---- the engine as a sensor ----

        // A suspension without a wheel never reports IsWorking: built far enough and switched on.
        public bool Active
        {
            get { return registered && Block.IsFunctional && Block.Enabled; }
        }

        // Of the guide point over the rail point, as held now (it moves to a new setting at ShiftSpeed). With a platform:
        // the rail's top and the train height, less the base's top over the block centre (the gap moves only the
        // platform).
        public double HoverHeight
        {
            get
            {
                Shift();
                return heightNow;
            }
        }

        double SetHoverHeight
        {
            get { return HasPlatform ? EnginePlatform.RailTop + TrainHeight - baseTop : Settings.HoverHeight; }
        }

        // A new side offset or height moves the held point there at this speed, m/s, not in one tick: a point that
        // jumps metres reads as the rail torn away (RailBody's early break) and dropped the engine.
        const double ShiftSpeed = 2.0;
        double sideNow, heightNow;
        int shiftFrame = -1;

        // Once a frame, on every machine alike (the physics and the platform's look).
        void Shift()
        {
            int frame = MyAPIGateway.Session.GameplayFrameCounter;
            if (frame == shiftFrame)
                return;
            double step = shiftFrame < 0 ? double.MaxValue : ShiftSpeed * Math.Max(0, frame - shiftFrame) / 60.0;
            shiftFrame = frame;
            sideNow = Toward(sideNow, Settings.SideOffset, step);
            heightNow = Toward(heightNow, SetHoverHeight, step);
        }

        static double Toward(double value, double target, double step)
        {
            return Math.Abs(target - value) <= step ? target : value + Math.Sign(target - value) * step;
        }

        // The base's top over the rail's top, m, within the model's range.
        public double TrainHeight
        {
            get { return MathHelper.Clamp(Settings.TrainHeight, TrainHeightMin, TrainHeightMax); }
        }

        // The platform's depth under the block centre at rest (its bottom), m: where it keeps the player's gap over the
        // rail's top under the base as held now (while the height moves, too). The terminal keeps the pair within the
        // model's range (FitGap, FitHeight); settings that are not (a programmable block's) leave it at the end of the
        // range.
        public double PlatformRest
        {
            get { return MathHelper.Clamp(HoverHeight - EnginePlatform.RailTop - Settings.PlatformGap, restMin, restMax); }
        }

        // The train height changed: the gap follows as far as the platform must move to stay within the model's range.
        public void FitGap(EngineSettings s)
        {
            double depth = s.TrainHeight - baseTop;
            s.PlatformGap = (float)MathHelper.Clamp(s.PlatformGap,
                Math.Max(EngineSettings.MinGap, depth - restMax), Math.Min(EngineSettings.MaxGap, depth - restMin));
        }

        // The gap changed: the train height follows as far as the platform must move to stay within the model's range.
        public void FitHeight(EngineSettings s)
        {
            s.TrainHeight = (float)MathHelper.Clamp(s.TrainHeight,
                Math.Max(TrainHeightMin, baseTop + restMin + s.PlatformGap), Math.Min(TrainHeightMax, baseTop + restMax + s.PlatformGap));
        }

        public double SearchDistance
        {
            get { return Math.Max(MinSearch, HoverHeight + SearchMargin); }
        }

        // The point held over the rail (the side offset as held now, see Shift).
        public Vector3D GuidePoint
        {
            get
            {
                Shift();
                MatrixD m = Block.WorldMatrix;
                return m.Translation + m.Right * sideNow;
            }
        }

        // The small engine for small grid (its own force and defaults).
        bool IsMini
        {
            get { return Block.BlockDefinition.SubtypeId == "MagRail_EngineMini"; }
        }

        // The engine's force, N: its full thrust (100% Power); the small grid's 5x1x5 engine is the same engine, the Mini
        // has a quarter of it. Everything else is a share of it (PLAN 10.1): it holds the rail with HoldShare of it, the
        // brakes are Drive's shares.
        public double Force
        {
            get { return IsMini ? 250000.0 : 1000000.0; }
        }

        public const double HoldShare = 4.0;

        // Hold force, N: the rail cannot hold the grid harder than its engines.
        public double HoldForce
        {
            get { return Force * HoldShare; }
        }

        // Where the rail is for this engine, without hysteresis: Held = it could capture the rail now.
        // error: distance across the rail from the guide point to its place over the rail.
        public RailState Probe(TrackCursor cursor, Vector3D searchForward, out TrackSample sample, out double error)
        {
            error = 0;
            sample = new TrackSample();
            if (!Active)
                return RailState.Off;
            Vector3D guide = GuidePoint;
            // A rail on the engine's own grid moves with it: it cannot carry the grid. Past the end of the track
            // the cursor goes over to another rail only if the engine could hold it (not a neighbouring track).
            if (!cursor.TryProject(guide, searchForward, SearchDistance, 0, out sample, Block.CubeGrid, HoverHeight + CaptureDistance))
                return RailState.NoRail;
            Vector3D e = sample.Point + sample.Up * HoverHeight - guide;
            double along = Vector3D.Dot(e, sample.Tangent);
            error = (e - sample.Tangent * along).Length();
            // Mid-track the rail point is square under the guide point; past an end it stays at the end. Up to
            // PastEnd beyond it the rail goes on straight along the end's tangent, so the point does not jump
            // when the next rail across a gap takes over.
            if (Math.Abs(along) > TrackCursor.PastEnd)
                return RailState.TrackEnd;
            if (cursor.DeadEnd)
            {
                sample.Point -= sample.Tangent * along;
                sample.Distance = Vector3D.Distance(guide, sample.Point);
            }
            if (Vector3D.Dot(Block.WorldMatrix.Up, sample.Up) < 0.5)
                return RailState.Tilted;
            return error <= CaptureDistance ? RailState.Held : RailState.Far;
        }

        // ---- power ----

        // Max power at full thrust, MW: RequiredPowerInput of the block definitions.
        public double MaxPower
        {
            get { return 5.0; }
        }

        // The engine joins its grid's power network through the suspension's own resource sink. The game gives a
        // suspension without a wheel no demand (and does not set the function again), so the sink's required-input
        // function is replaced by ours.
        void HookPower()
        {
            sink = Block.Components.Get<MyResourceSinkComponent>();
            if (sink == null)
                return;
            sink.SetMaxRequiredInputByType(MyResourceDistributorComponent.ElectricityId, (float)MaxPower);
            sink.SetRequiredInputFuncByType(MyResourceDistributorComponent.ElectricityId, () => demand);
            sink.Update();
        }

        // Server, every tick: the drive's demand to the network, in 1% steps of the max (rounded up: a small force
        // still asks for some power).
        public void ApplyPower()
        {
            float step = (float)(MaxPower * 0.01);
            SetDemand(Active ? (float)Math.Ceiling(PowerWanted / step - 0.001f) * step : 0f);
        }

        // The demand last set (MW) and the one last sent to clients (Net.BroadcastPower; -1 = never).
        public float Demand
        {
            get { return demand; }
        }

        public float SentDemand = -1f;

        // Server: the drive's demand; clients: the server's (Net), so their own copy of the grid's power network
        // (power screen, load, battery time) matches.
        public void SetDemand(float wanted)
        {
            if (sink == null || wanted == demand)
                return;
            demand = wanted;
            sink.SetRequiredInputByType(MyResourceDistributorComponent.ElectricityId, wanted);
        }

        // Share of its thrust the engine can give now: what the network supplied of its last demand. An engine
        // that asked for nothing yet gives nothing until the network answers (a tick).
        public double PowerSupply
        {
            get
            {
                if (sink == null)
                    return 1;
                if (sink.RequiredInputByType(MyResourceDistributorComponent.ElectricityId) <= 0f)
                    return 0;
                return MathHelper.Clamp(sink.SuppliedRatioByType(MyResourceDistributorComponent.ElectricityId), 0f, 1f);
            }
        }

        // Power drawn now, MW (0 on clients: they do not run the drive).
        public double PowerNow
        {
            get { return sink != null ? sink.CurrentInputByType(MyResourceDistributorComponent.ElectricityId) : 0; }
        }

        // The network gave less than the engine asked for.
        public bool PowerShort
        {
            get { return demand > 0f && sink != null && sink.SuppliedRatioByType(MyResourceDistributorComponent.ElectricityId) < 0.99f; }
        }

        // Physics state back to "no rail" (RailBody also resets the cursor).
        public void Release()
        {
            Held = false;
            HasLastOffset = false;
            JumpTicks = 0;
        }
    }
}
