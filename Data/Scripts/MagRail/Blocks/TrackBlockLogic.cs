using System;
using System.Collections.Generic;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Engine.Physics;
using Sandbox.Game.EntityComponents;
using Sandbox.ModAPI;
using VRage.Game.Components;
using VRage.Game.Entity;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRage.ObjectBuilders;
using VRageMath;

namespace MagRail
{
    // A track block (Rail Block, Free Rail Block, Rail Switch, Embedded Rail): its settings, its rail part and its ports.
    // Rail Switch / Wide Rail Switch (blocks/Switch1.py): a straight rail part whose three branch ports are
    // one point at the front end, like the old Maglev junction; the spans laid from them are the branches.
    // The rail part is the subpart the model's "subpart_" dummy names; the dummy's matrix is the standard placement.
    // The script moves the part by the height setting (and turns the Free Rail head about the rail point = the
    // part's origin), gives it physics (the game gives subparts none) and lights its copper (material "Emissive").
    // Ports are the dummies of the part's model: position = rail point, Forward = outwards, Up = rail up.
    // The descriptor lists the subtypes of TrackStyle's registry (an attribute takes constants only): keep both alike.
    [MyEntityComponentDescriptor(typeof(MyObjectBuilder_UpgradeModule), false,
        "MagRail_RailBlock1", "MagRail_FreeRail",
        "MagRail_Switch1", "MagRail_WideRailBlock1", "MagRail_WideFreeRail", "MagRail_WideSwitch1",
        "MagRail_EmbeddedRail", "MagRail_EmbeddedRailHalf", "MagRail_WideEmbeddedRail", "MagRail_WideEmbeddedRailHalf")]
    public class TrackBlockLogic : MyGameLogicComponent
    {
        // Branch port names by SwitchBranch.
        public static readonly string[] BranchPorts = { "branch_c", "branch_l", "branch_r" };
        // Must be listed in Data/EntityComponents_MagRail.sbc, or the game drops them on save.
        static readonly Guid SettingsGuid = new Guid("444da85b-4de9-4530-8b2a-10d8f75d9c76");
        static readonly Guid SpansGuid = new Guid("b0b3f1c2-6f7e-4d84-9a51-2c8e7d3f4a60");

        class PortDef
        {
            public string Name;
            public Matrix Local;    // in the rail part's coordinates
            public Vector3 Aim;     // L/R branch in the centre's point: its side (unit, same coordinates), see PortAim
        }

        class PartInfo
        {
            public string Name;
            public Matrix Standard;
            // Free Rail on the lift: the base's turning upper half (subpart TurnName), its standard placement.
            public Matrix? Turn;
            public List<PortDef> Ports;     // null until the part's model was seen
            public bool Fan;                // Rail Switch: the branch ports share one point
        }

        const string TurnName = "Turn";

        // Per subtype, read from the finished model once.
        static readonly Dictionary<string, PartInfo> partInfos = new Dictionary<string, PartInfo>();
        static readonly Dictionary<string, IMyModelDummy> dummies = new Dictionary<string, IMyModelDummy>();

        public IMyUpgradeModule Block { get; private set; }
        public TrackKind Kind { get; private set; }
        public TrackStyle Style { get; private set; }
        public TrackSettings Settings { get; private set; }
        public SpanStore SpanStore { get; private set; }
        // The rail stays at its standard height (TrackStyle.FixedHeight): a stored height offset is ignored.
        public bool FixedHeight { get; private set; }
        // Changes whenever the rail part moves (spans rebuild their curves).
        public int Revision { get; private set; }

        bool registered;
        // Initialised with the world (before MagRailSession.Started): a block without settings is from an older version.
        bool fromWorld;
        // Where the track index last saw the block: a grid split/merge or a grid turning static moves it.
        IMyCubeGrid indexedGrid;
        bool indexedStatic;
        List<TrackLane> lanes;
        MyEntitySubpart part;
        bool partOnStatic;
        bool partDirty;
        // The part was there at the last UpdatePart (partKnown: there was one).
        bool partShown;
        bool partKnown;
        // The struts of a block on the lift (TrackStyle.Lifts), else null.
        RailLift lift;

        static TrackNetwork Network
        {
            get { return MagRailSession.Instance.Network; }
        }

        public override void Init(MyObjectBuilder_EntityBase objectBuilder)
        {
            Block = (IMyUpgradeModule)Entity;
            TrackStyle style;
            TrackKind kind;
            if (!TrackStyle.TryGet(Block.BlockDefinition.SubtypeId, out style, out kind))
            {
                // In the descriptor but not in the registry: a block without logic, not a guessed style.
                Log.Error("TrackBlockLogic.Init", "track block " + Block.BlockDefinition.SubtypeId + " is not in TrackStyle");
                return;
            }
            Style = style;
            Kind = kind;
            if (TrackStyle.HasLift(Block.BlockDefinition.SubtypeId))
                lift = new RailLift(this);
            FixedHeight = TrackStyle.HasFixedHeight(Block.BlockDefinition.SubtypeId);
            fromWorld = MagRailSession.Instance != null && !MagRailSession.Instance.Started;
            NeedsUpdate |= MyEntityUpdateEnum.BEFORE_NEXT_FRAME;
        }

        public override void UpdateOnceBeforeFrame()
        {
            try
            {
                // A weld or grind changed the model (OnIntegrity): the part and the collision follow at once.
                if (registered)
                {
                    UpdatePart();
                    return;
                }
                // Projections and placement previews have no physics: no logic for them.
                if (Block.CubeGrid.Physics == null || MagRailSession.Instance == null)
                    return;
                Settings = LoadSettings() ?? NewSettings();
                SpanStore = LoadSpans() ?? new SpanStore { SelfId = Block.EntityId };
                UpdatePart();
                Network.Register(this);
                registered = true;
                indexedGrid = Block.CubeGrid;
                indexedStatic = Block.CubeGrid.IsStatic;
                indexedGrid.OnBlockIntegrityChanged += OnIntegrity;
                indexedGrid.OnBlockRemoved += OnRemoved;
                // An embedded rail has nothing to set: out of the terminal and the toolbar's block list (synced properties,
                // set by the server; the player may show it again, it changes nothing).
                if (FixedHeight && Net.IsServer)
                {
                    Block.ShowInTerminal = false;
                    Block.ShowInToolbarConfig = false;
                }
                TrackTerminal.Create();
                NeedsUpdate |= MyEntityUpdateEnum.EACH_100TH_FRAME;
            }
            catch (Exception e)
            {
                Log.Error("TrackBlockLogic.UpdateOnceBeforeFrame", e);
            }
        }

        // The part is recreated by the game when the block's model changes (picked up at once by OnIntegrity; here as
        // well, in case the game changed it another way); the grid can turn static or dynamic: both are picked up here.
        public override void UpdateAfterSimulation100()
        {
            try
            {
                UpdatePart();
                if (registered && (Block.CubeGrid != indexedGrid || Block.CubeGrid.IsStatic != indexedStatic))
                {
                    indexedGrid.OnBlockIntegrityChanged -= OnIntegrity;
                    indexedGrid.OnBlockRemoved -= OnRemoved;
                    Block.CubeGrid.OnBlockIntegrityChanged += OnIntegrity;
                    Block.CubeGrid.OnBlockRemoved += OnRemoved;
                    indexedGrid = Block.CubeGrid;
                    indexedStatic = Block.CubeGrid.IsStatic;
                    Network.Touch();
                }
            }
            catch (Exception e)
            {
                Log.Error("TrackBlockLogic.UpdateAfterSimulation100", e);
            }
        }

        // Fired right after a weld or grind step (the model already switched): UpdatePart on the next frame, not 100
        // frames later — a player ran into a stage's rail before its collision came back (2026-10-22). The grid's event
        // fires for all its blocks: only this block's counts.
        void OnIntegrity(IMySlimBlock slim)
        {
            if (slim == Block.SlimBlock)
                NeedsUpdate |= MyEntityUpdateEnum.BEFORE_NEXT_FRAME;
        }

        // The grid tells it right after the block was taken off: closed already when it is gone (ground off, destroyed,
        // removed), not when a split moves it to a new grid. Not told when the whole grid closes (deleted, unloaded).
        void OnRemoved(IMySlimBlock slim)
        {
            if (slim == Block.SlimBlock && Block.MarkedForClose && Net.IsServer && registered && MagRailSession.Instance != null)
                Network.Removed(this);
        }

        public override void Close()
        {
            if (registered && MagRailSession.Instance != null)
                Network.Unregister(this);
            if (registered)
            {
                indexedGrid.OnBlockIntegrityChanged -= OnIntegrity;
                indexedGrid.OnBlockRemoved -= OnRemoved;
            }
            registered = false;
            if (lift != null)
                lift.Remove();
        }

        // ---- settings ----

        // Rail point height above the bottom of the cell, m (NaN until the finished model is known).
        public float StandardHeight
        {
            get
            {
                PartInfo info = GetPartInfo();
                return info != null ? info.Standard.Translation.Y + Block.CubeGrid.GridSize / 2 : float.NaN;
            }
        }

        public float Height
        {
            get { return StandardHeight + HeightOffset; }
        }

        float HeightOffset
        {
            get { return FixedHeight ? 0 : Settings.HeightOffset; }
        }

        // Lowest rail point (terminal height), m: on the lift lower than over a platform.
        public float MinHeight
        {
            get { return lift != null ? RailLift.MinHeight : Style.MinHeight; }
        }

        // A span in rail mode touches the block: height and head stay as they are.
        public bool IsLocked
        {
            get { return registered && Network.IsLocked(this); }
        }

        // A player (terminal, toolbar, programmable block) asks for new settings, on any machine.
        public void Request(TrackSettings requested)
        {
            if (Net.IsServer)
            {
                ApplyRequest(requested);
                return;
            }
            // Shown at once; the server's answer overwrites it.
            SetSettings(TrackSettings.Merge(Settings, requested, IsLocked));
            Net.SendSettingsRequest(this, requested);
        }

        // Server: a request passed the access check.
        public void ApplyRequest(TrackSettings requested)
        {
            SetSettings(TrackSettings.Merge(Settings, requested, IsLocked));
            Net.BroadcastSettings(this);
        }

        // Only a change of the rail's place (height, head) moves the rail: new curves for its spans and lanes, the
        // part moved. A switch's branch is read by the route as it is (LaneFrom): turning it redraws nothing.
        public void SetSettings(TrackSettings settings)
        {
            bool moved = Settings == null || !Settings.SamePlace(settings);
            Settings = settings;
            Block.Storage = Block.Storage ?? new MyModStorageComponent();
            Block.Storage[SettingsGuid] = Settings.Encode();
            if (moved)
            {
                Revision++;
                if (registered)
                    Network.Touch();
                partDirty = true;
                UpdatePart();
            }
            TrackTerminal.Refresh();
        }

        // No settings stored: a new block stands at the standard height. One loaded with the world never had its
        // settings changed under an older version: it keeps the old standard height (TrackSettings version 3), where
        // its rails were built. Stored at once, so the next load knows.
        TrackSettings NewSettings()
        {
            TrackSettings s = new TrackSettings { HeightOffset = fromWorld ? TrackSettings.LegacyOffset : 0 };
            Block.Storage = Block.Storage ?? new MyModStorageComponent();
            Block.Storage[SettingsGuid] = s.Encode();
            return s;
        }

        TrackSettings LoadSettings()
        {
            string text;
            if (Block.Storage == null || !Block.Storage.TryGetValue(SettingsGuid, out text) || string.IsNullOrEmpty(text))
                return null;
            try
            {
                return TrackSettings.Decode(text);
            }
            catch (Exception e)
            {
                Log.Error("TrackBlockLogic.LoadSettings", e);
                return null;
            }
        }

        // ---- spans owned by this block ----

        SpanStore LoadSpans()
        {
            string text;
            if (Block.Storage == null || !Block.Storage.TryGetValue(SpansGuid, out text) || string.IsNullOrEmpty(text))
                return null;
            try
            {
                return SpanStore.Decode(text);
            }
            catch (Exception e)
            {
                Log.Error("TrackBlockLogic.LoadSpans", e);
                return null;
            }
        }

        public void SaveSpans()
        {
            SpanStore.SelfId = Block.EntityId;
            Block.Storage = Block.Storage ?? new MyModStorageComponent();
            Block.Storage[SpansGuid] = SpanStore.Encode();
        }

        // Client: the server's version.
        public void SetSpanStore(SpanStore store)
        {
            SpanStore = store;
            SaveSpans();
        }

        // ---- ports ----

        public bool PortsKnown
        {
            get
            {
                PartInfo info = GetPartInfo();
                return info != null && info.Ports != null;
            }
        }

        public int PortCount
        {
            get
            {
                PartInfo info = GetPartInfo();
                return info != null && info.Ports != null ? info.Ports.Count : 0;
            }
        }

        public string PortName(int port)
        {
            return GetPartInfo().Ports[port].Name;
        }

        public int PortIndex(string name)
        {
            List<PortDef> ports = GetPartInfo().Ports;
            for (int i = 0; i < ports.Count; i++)
                if (ports[i].Name == name)
                    return i;
            return -1;
        }

        // Port matrix relative to the block's grid.
        public MatrixD PortGridLocal(int port)
        {
            PartInfo info = GetPartInfo();
            return (MatrixD)(info.Ports[port].Local * PartMatrix(info)) * Block.PositionComp.LocalMatrixRef;
        }

        public MatrixD PortWorld(int port)
        {
            return PortBlockLocal(port) * Block.WorldMatrix;
        }

        // Where the Rail Linker aims at the port and draws its dot: the port point, but L/R branches that share the
        // centre branch's point are moved to their side, to the middle of their third of the end face.
        public Vector3D PortAim(int port)
        {
            PartInfo info = GetPartInfo();
            return Vector3D.Transform(info.Ports[port].Local.Translation + info.Ports[port].Aim * (float)(Style.Width / 3),
                PartMatrix(info) * Block.WorldMatrix);
        }

        // A branch port of a switch whose branches share one point (FanLevel): its SwitchBranch, else -1.
        public int FanRank(int port)
        {
            PartInfo info = GetPartInfo();
            if (info == null || !info.Fan || port < 0)
                return -1;
            return Array.IndexOf(BranchPorts, info.Ports[port].Name);
        }

        // A branch port of a Rail Switch: the copper of its spans fades out towards it while it is not selected
        // (TrackLayout.Glow); SelectedInstead: the selected branch (SwitchBranch) then, else -1.
        public bool IsBranchPort(int port)
        {
            return Kind == TrackKind.Switch && port >= 0 && PortsKnown && Array.IndexOf(BranchPorts, PortName(port)) >= 0;
        }

        public int SelectedInstead(int port)
        {
            return IsBranchPort(port) && PortName(port) != BranchPorts[(int)Settings.Branch] ? (int)Settings.Branch : -1;
        }

        public MatrixD PortBlockLocal(int port)
        {
            PartInfo info = GetPartInfo();
            return (MatrixD)(info.Ports[port].Local * PartMatrix(info));
        }

        // Joints (fixed blocks end to end) only on ports in line with the grid: rail block ports, the switch
        // entry and its straight branch. The Free Rail head turns; the L/R branches leave at an angle.
        public bool IsJointPort(int port)
        {
            string name = PortName(port);
            return Kind == TrackKind.Rail || name == "entry" || name == BranchPorts[(int)SwitchBranch.Center];
        }

        // ---- lanes (the rail inside the block) ----

        // From the entry to each branch on a switch, else between the two ports; empty until the ports are known.
        public List<TrackLane> Lanes
        {
            get
            {
                if (lanes != null || !PortsKnown)
                    return lanes ?? noLanes;
                lanes = new List<TrackLane>();
                int entry = PortIndex("entry");
                if (entry >= 0)
                {
                    for (int i = 0; i < PortCount; i++)
                        if (i != entry)
                            lanes.Add(new TrackLane(this, entry, i));
                }
                else if (PortCount == 2)
                {
                    lanes.Add(new TrackLane(this, 0, 1));
                }
                return lanes;
            }
        }

        static readonly List<TrackLane> noLanes = new List<TrackLane>();

        // The lane a train takes coming in through a port: from the switch entry the selected branch, from a
        // branch back to the entry.
        public TrackLane LaneFrom(int port)
        {
            List<TrackLane> list = Lanes;
            int target = -1;
            if (Kind == TrackKind.Switch && port == PortIndex("entry"))
                target = PortIndex(BranchPorts[(int)Settings.Branch]);
            for (int i = 0; i < list.Count; i++)
            {
                TrackLane lane = list[i];
                if (target >= 0 ? lane.To == target : lane.From == port || lane.To == port)
                    return lane;
            }
            return null;
        }

        // Carries trains: built far enough to work, with its rail part known.
        public bool Rideable
        {
            get { return registered && Block.IsFunctional && PortsKnown; }
        }

        // ---- rail part ----

        PartInfo GetPartInfo()
        {
            string subtype = Block.BlockDefinition.SubtypeId;
            PartInfo info;
            if (partInfos.TryGetValue(subtype, out info))
                return info;
            if (Block.Model == null)
                return null;
            dummies.Clear();
            Block.Model.GetDummies(dummies);
            Matrix? turn = null;
            foreach (KeyValuePair<string, IMyModelDummy> d in dummies)
            {
                if (!d.Key.StartsWith("subpart_"))
                    continue;
                string name = d.Key.Substring("subpart_".Length);
                if (name == TurnName)
                    turn = Matrix.Normalize(d.Value.Matrix);
                else if (info == null)
                    info = new PartInfo { Name = name, Standard = Matrix.Normalize(d.Value.Matrix) };
            }
            dummies.Clear();
            if (info != null)
                info.Turn = turn;
            // A construction stage has no subpart: try again with the finished model.
            if (info != null)
                partInfos[subtype] = info;
            return info;
        }

        static void ReadPorts(PartInfo info, MyEntitySubpart sub)
        {
            IMyModel model = ((IMyEntity)sub).Model;
            if (info.Ports != null || model == null)
                return;
            List<PortDef> ports = new List<PortDef>();
            dummies.Clear();
            model.GetDummies(dummies);
            foreach (KeyValuePair<string, IMyModelDummy> d in dummies)
                ports.Add(new PortDef { Name = d.Key, Local = Matrix.Normalize(d.Value.Matrix) });
            dummies.Clear();
            ports.Sort((x, y) => string.CompareOrdinal(x.Name, y.Name));
            PortDef center = ports.Find(p => p.Name == BranchPorts[(int)SwitchBranch.Center]);
            foreach (PortDef p in ports)
            {
                if (center == null || p == center || !p.Name.StartsWith("branch_")
                    || Vector3.DistanceSquared(p.Local.Translation, center.Local.Translation) > 1e-4f)
                    continue;
                p.Aim = p.Name == BranchPorts[(int)SwitchBranch.Left] ? center.Local.Left : center.Local.Right;
                info.Fan = true;
            }
            info.Ports = ports;
        }

        void UpdatePart()
        {
            PartInfo info = GetPartInfo();
            MyEntitySubpart sub = null;
            bool shown = info != null && Block.TryGetSubpart(info.Name, out sub);
            // Welding and grinding switch the block's model (finished / construction stage) without its grid
            // collision: the game keeps the shape of the model the grid had. A stage has the rail in its collision, the
            // finished model has not (the part's body holds it): refreshed here, or a block placed finished and ground
            // to a stage had no rail to stand on, and one welded up kept the stage's rail at the standard height.
            if (partKnown && shown != partShown)
                Block.SlimBlock.UpdateVisual();
            partKnown = true;
            partShown = shown;
            if (!shown)
            {
                part = null;
                if (lift != null)
                    lift.Remove();
                return;
            }
            ReadPorts(info, sub);
            bool onStatic = Block.CubeGrid.IsStatic;
            if (sub == part && onStatic == partOnStatic && !partDirty)
            {
                UpdateLift(info, false);
                return;
            }
            part = sub;
            partOnStatic = onStatic;
            partDirty = false;

            Matrix local = PartMatrix(info);
            part.PositionComp.SetLocalMatrix(ref local);
            RebuildPhysics(part);
            MyEntitySubpart turnPart;
            if (info.Turn.HasValue && Block.TryGetSubpart(TurnName, out turnPart))
            {
                Matrix turnLocal = info.Turn.Value;
                Vector3 at = turnLocal.Translation;
                turnLocal.Translation = Vector3.Zero;
                turnLocal *= TurnMatrix();
                turnLocal.Translation = at;
                turnPart.PositionComp.SetLocalMatrix(ref turnLocal);
            }
            if (!MyAPIGateway.Utilities.IsDedicated)
                PieceEntities.LightCopper(part);
            UpdateLift(info, true);
        }

        // The struts follow the rail part; the ears sit at its ends (the ports).
        void UpdateLift(PartInfo info, bool moved)
        {
            if (lift == null || info.Ports == null)
                return;
            float half = 0;
            foreach (PortDef p in info.Ports)
                half = Math.Max(half, Math.Abs(p.Local.Translation.Z));
            lift.Update(PartMatrix(info), half, info.Turn.HasValue ? TurnMatrix() : Matrix.Identity, moved);
        }

        // The Free Rail head's yaw, which the base's upper half follows.
        Matrix TurnMatrix()
        {
            return Matrix.CreateRotationY(MathHelper.ToRadians(-Settings.Yaw));
        }

        Matrix PartMatrix(PartInfo info)
        {
            Matrix m = info.Standard;
            Vector3 position = m.Translation + Vector3.Up * HeightOffset;
            m.Translation = Vector3.Zero;
            if (Kind == TrackKind.FreeRail)
            {
                Matrix standard = m;
                m *= Matrix.CreateFromYawPitchRoll(MathHelper.ToRadians(-Settings.Yaw), MathHelper.ToRadians(Settings.Pitch),
                    MathHelper.ToRadians(-Settings.Roll));
                // On the lift the head turns about the middle of its ear pivots (user, 2026-10-16), not the rail point:
                // that point stays where it is in the standard placement.
                if (lift != null && !Settings.RailPointPivot)
                    position += Vector3.Transform(RailLift.HeadPivot, standard) - Vector3.Transform(RailLift.HeadPivot, m);
            }
            m.Translation = position;
            return m;
        }

        // The rail part. Rebuilt on every move: moves are rare (settings changes) and a
        // static body does not follow its entity. On a moving grid the part has no collision (see
        // PieceEntities: it would hit the ship); it only keeps its world matrix up to date. A style that is not
        // Solid has none anywhere. An embedded rail (fixed height) has none either: its block's collision holds the rail,
        // and a body of the part's own is not the grid's (vanilla doors and gates over the block bumped into it).
        void RebuildPhysics(MyEntity e)
        {
            if (e.Physics != null)
            {
                e.Physics.Close();
                e.Physics = null;
            }
            if (partOnStatic && Style.Solid && !FixedHeight)
            {
                e.InitModelPhysics(RigidBodyFlag.RBF_STATIC, PieceEntities.StaticLayer);
            }
            else
            {
                e.NeedsWorldMatrix = true;
                Block.NeedsWorldMatrix = true;
            }
        }
    }
}
