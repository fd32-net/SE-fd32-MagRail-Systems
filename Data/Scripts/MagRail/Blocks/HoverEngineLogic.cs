using System;
using System.Text;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Game.EntityComponents;
using Sandbox.ModAPI;
using VRage.Game;
using VRage.Game.Components;
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
    // under it and its settings. Numbers are the old Maglev's, so the ported physics keeps them.
    [MyEntityComponentDescriptor(typeof(MyObjectBuilder_MotorSuspension), false,
        "MagRail_HoverLarge", "MagRail_HoverSmall", "MagRail_HoverMini")]
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
                Settings = LoadSettings() ?? new EngineSettings
                {
                    HoverHeight = Block.CubeGrid.GridSizeEnum == MyCubeSize.Small ? EngineSettings.SmallHeight : EngineSettings.LargeHeight,
                };
                MagRailSession.Instance.Engines.Add(this);
                registered = true;
                Block.AppendingCustomInfo += EngineInfo.Append;
                EngineTerminal.Create();
            }
            catch (Exception e)
            {
                Log.Error("HoverEngineLogic.UpdateOnceBeforeFrame", e);
            }
        }

        public override void Close()
        {
            if (registered && MagRailSession.Instance != null)
                MagRailSession.Instance.Engines.Remove(this);
            if (registered)
                Block.AppendingCustomInfo -= EngineInfo.Append;
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

        public double HoverHeight
        {
            get { return Settings.HoverHeight; }
        }

        public double SearchDistance
        {
            get { return Math.Max(MinSearch, HoverHeight + SearchMargin); }
        }

        // The point held over the rail.
        public Vector3D GuidePoint
        {
            get
            {
                MatrixD m = Block.WorldMatrix;
                return m.Translation + m.Right * Settings.OffsetRight + m.Forward * Settings.OffsetForward + m.Up * Settings.OffsetUp;
            }
        }

        // Hold force by engine type, N: the rail cannot hold the grid harder than its engines.
        public double HoldForce
        {
            get
            {
                switch (Block.BlockDefinition.SubtypeId)
                {
                    case "MagRail_HoverLarge": return 4000000.0;
                    case "MagRail_HoverSmall": return 2000000.0;
                    default: return 300000.0;
                }
            }
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
            // Mid-track the rail point is square under the guide point; past an end it stays at the end.
            if (Math.Abs(along) > TrackCursor.PastEnd)
                return RailState.TrackEnd;
            if (Vector3D.Dot(Block.WorldMatrix.Up, sample.Up) < 0.5)
                return RailState.Tilted;
            return error <= CaptureDistance ? RailState.Held : RailState.Far;
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
