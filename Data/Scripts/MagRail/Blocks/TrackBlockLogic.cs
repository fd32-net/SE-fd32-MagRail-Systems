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
    // A track block (Rail Block, Free Rail Block, Rail Switch): its settings, its rail part and its ports.
    // The rail part is the subpart the model's "subpart_" dummy names (the switch's "subpart_Beam<n>" are
    // its beam segments, SwitchBeam); the dummy's matrix is the standard placement. The script moves the part by the height setting (and turns the Free Rail
    // head about the rail point = the part's origin) and gives it physics: the game gives subparts none.
    // Ports are the dummies of the part's model: position = rail point, Forward = outwards, Up = rail up.
    [MyEntityComponentDescriptor(typeof(MyObjectBuilder_UpgradeModule), false,
        "MagRail_RailBlock1", "MagRail_RailBlock5", "MagRail_FreeRail", "MagRail_Switch")]
    public class TrackBlockLogic : MyGameLogicComponent
    {
        // Must be listed in Data/EntityComponents_MagRail.sbc, or the game drops them on save.
        static readonly Guid SettingsGuid = new Guid("444da85b-4de9-4530-8b2a-10d8f75d9c76");
        static readonly Guid SpansGuid = new Guid("b0b3f1c2-6f7e-4d84-9a51-2c8e7d3f4a60");

        class PortDef
        {
            public string Name;
            public Matrix Local;    // in the rail part's coordinates
        }

        class PartInfo
        {
            public string Name;
            public Matrix Standard;
            public List<PortDef> Ports;     // null until the part's model was seen
            public SwitchBeam.Shape Beam;   // Rail Switch
        }

        // Per subtype, read from the finished model once.
        static readonly Dictionary<string, PartInfo> partInfos = new Dictionary<string, PartInfo>();
        static readonly Dictionary<string, IMyModelDummy> dummies = new Dictionary<string, IMyModelDummy>();

        public IMyUpgradeModule Block { get; private set; }
        public TrackKind Kind { get; private set; }
        public TrackSettings Settings { get; private set; }
        public SpanStore SpanStore { get; private set; }
        // Changes whenever the rail part moves (spans rebuild their curves).
        public int Revision { get; private set; }

        bool registered;
        // Where the track index last saw the block: a grid split/merge or a grid turning static moves it.
        IMyCubeGrid indexedGrid;
        bool indexedStatic;
        List<TrackLane> lanes;
        MyEntitySubpart part;
        bool partOnStatic;
        bool partDirty;
        SwitchBeam beam;

        static TrackNetwork Network
        {
            get { return MagRailSession.Instance.Network; }
        }

        public static TrackKind KindOf(string subtype)
        {
            if (subtype == "MagRail_FreeRail")
                return TrackKind.FreeRail;
            if (subtype == "MagRail_Switch")
                return TrackKind.Switch;
            return TrackKind.Rail;
        }

        public override void Init(MyObjectBuilder_EntityBase objectBuilder)
        {
            Block = (IMyUpgradeModule)Entity;
            Kind = KindOf(Block.BlockDefinition.SubtypeId);
            NeedsUpdate |= MyEntityUpdateEnum.BEFORE_NEXT_FRAME;
        }

        public override void UpdateOnceBeforeFrame()
        {
            try
            {
                // Projections and placement previews have no physics: no logic for them.
                if (Block.CubeGrid.Physics == null || MagRailSession.Instance == null)
                    return;
                Settings = LoadSettings() ?? new TrackSettings();
                SpanStore = LoadSpans() ?? new SpanStore { SelfId = Block.EntityId };
                UpdatePart();
                Network.Register(this);
                registered = true;
                indexedGrid = Block.CubeGrid;
                indexedStatic = Block.CubeGrid.IsStatic;
                TrackTerminal.Create();
                NeedsUpdate |= MyEntityUpdateEnum.EACH_100TH_FRAME;
            }
            catch (Exception e)
            {
                Log.Error("TrackBlockLogic.UpdateOnceBeforeFrame", e);
            }
        }

        // The part is recreated by the game when the block's model changes; the grid can turn
        // static or dynamic: both are picked up here.
        public override void UpdateAfterSimulation100()
        {
            try
            {
                UpdatePart();
                if (beam != null)
                    beam.ShowLights(Block.IsFunctional);
                if (registered && (Block.CubeGrid != indexedGrid || Block.CubeGrid.IsStatic != indexedStatic))
                {
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

        // Only while the switch beam moves.
        public override void UpdateAfterSimulation()
        {
            try
            {
                if (beam != null && beam.Step())
                {
                    foreach (MyEntitySubpart segment in beam.Segments)
                        RebuildPhysics(segment);
                    beam.ShowLights(Block.IsFunctional);
                }
                if (beam == null || !beam.Moving)
                    NeedsUpdate &= ~MyEntityUpdateEnum.EACH_FRAME;
            }
            catch (Exception e)
            {
                Log.Error("TrackBlockLogic.UpdateAfterSimulation", e);
            }
        }

        public override void Close()
        {
            if (registered && MagRailSession.Instance != null)
                Network.Unregister(this);
            registered = false;
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
            get { return StandardHeight + Settings.HeightOffset; }
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

        public void SetSettings(TrackSettings settings)
        {
            Settings = settings;
            Block.Storage = Block.Storage ?? new MyModStorageComponent();
            Block.Storage[SettingsGuid] = Settings.Encode();
            Revision++;
            if (registered)
                Network.Touch();
            partDirty = true;
            UpdatePart();
            TrackTerminal.Refresh();
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
            return Kind == TrackKind.Rail || name == "entry" || name == SwitchBeam.BranchPorts[(int)SwitchBranch.Center];
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
                target = PortIndex(SwitchBeam.BranchPorts[(int)Settings.Branch]);
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
            foreach (KeyValuePair<string, IMyModelDummy> d in dummies)
            {
                if (!d.Key.StartsWith("subpart_") || d.Key.StartsWith("subpart_Beam"))
                    continue;
                info = new PartInfo { Name = d.Key.Substring("subpart_".Length), Standard = Matrix.Normalize(d.Value.Matrix) };
                break;
            }
            dummies.Clear();
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
            info.Ports = ports;
            info.Beam = BeamShape(ports);
        }

        // A switch: the entry and every branch port.
        static SwitchBeam.Shape BeamShape(List<PortDef> ports)
        {
            PortDef entry = ports.Find(p => p.Name == "entry");
            if (entry == null)
                return null;
            Matrix[] branches = new Matrix[SwitchBeam.BranchPorts.Length];
            for (int i = 0; i < branches.Length; i++)
            {
                PortDef branch = ports.Find(p => p.Name == SwitchBeam.BranchPorts[i]);
                if (branch == null)
                    return null;
                branches[i] = branch.Local;
            }
            return SwitchBeam.ShapeOf(entry.Local, branches);
        }

        void UpdatePart()
        {
            PartInfo info = GetPartInfo();
            MyEntitySubpart sub;
            if (info == null || !Block.TryGetSubpart(info.Name, out sub))
            {
                part = null;
                return;
            }
            ReadPorts(info, sub);
            bool onStatic = Block.CubeGrid.IsStatic;
            if (sub == part && onStatic == partOnStatic && !partDirty)
                return;
            bool samePart = sub == part;
            part = sub;
            partOnStatic = onStatic;
            partDirty = false;

            Matrix local = PartMatrix(info);
            part.PositionComp.SetLocalMatrix(ref local);
            RebuildPhysics(part);
            if (info.Beam == null)
                return;
            if (beam == null || !samePart)
            {
                beam = new SwitchBeam(info.Beam, (MyEntity)Block, part, local, Settings.Branch);
            }
            else
            {
                beam.Select(Settings.Branch);
                beam.Place(local);
            }
            beam.ShowLights(Block.IsFunctional);
            // A moving beam gets its collision when it arrives.
            if (beam.Moving)
                NeedsUpdate |= MyEntityUpdateEnum.EACH_FRAME;
            else
                foreach (MyEntitySubpart segment in beam.Segments)
                    RebuildPhysics(segment);
        }

        Matrix PartMatrix(PartInfo info)
        {
            Matrix m = info.Standard;
            Vector3 position = m.Translation + Vector3.Up * Settings.HeightOffset;
            m.Translation = Vector3.Zero;
            if (Kind == TrackKind.FreeRail)
                m *= Matrix.CreateFromYawPitchRoll(MathHelper.ToRadians(-Settings.Yaw), MathHelper.ToRadians(Settings.Pitch),
                    MathHelper.ToRadians(-Settings.Roll));
            m.Translation = position;
            return m;
        }

        // The rail part or a beam segment. Rebuilt on every move: moves are rare (settings changes) and a
        // static body does not follow its entity. On a moving grid the part has no collision (see
        // PieceEntities: it would hit the ship); it only keeps its world matrix up to date.
        void RebuildPhysics(MyEntity e)
        {
            if (e.Physics != null)
            {
                e.Physics.Close();
                e.Physics = null;
            }
            if (partOnStatic)
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
