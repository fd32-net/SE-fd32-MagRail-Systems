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
    // A track block (Rail Block, Free Rail Block, Rail Switch): its settings and its rail part.
    // The rail part is the subpart the model's "subpart_" dummy names; the dummy's matrix is the
    // standard placement. The script moves the part by the height setting (and turns the Free Rail
    // head about the rail point = the part's origin) and gives it physics: the game gives subparts none.
    [MyEntityComponentDescriptor(typeof(MyObjectBuilder_UpgradeModule), false,
        "MagRail_RailBlock1", "MagRail_RailBlock5", "MagRail_FreeRail", "MagRail_Switch")]
    public class TrackBlockLogic : MyGameLogicComponent
    {
        // Must be listed in Data/EntityComponents_MagRail.sbc, or the game drops it on save.
        static readonly Guid SettingsGuid = new Guid("444da85b-4de9-4530-8b2a-10d8f75d9c76");

        // Collision layers of the rail part (MyPhysics.CollisionLayers): static body on stations,
        // kinematic body following the grid elsewhere.
        const int StaticLayer = 13;
        const int KinematicLayer = 15;

        class PartInfo
        {
            public string Name;
            public Matrix Standard;
        }

        // Per subtype, read from the finished model once.
        static readonly Dictionary<string, PartInfo> partInfos = new Dictionary<string, PartInfo>();
        static readonly Dictionary<string, IMyModelDummy> dummies = new Dictionary<string, IMyModelDummy>();

        public IMyUpgradeModule Block { get; private set; }
        public TrackKind Kind { get; private set; }
        public TrackSettings Settings { get; private set; }

        bool registered;
        MyEntitySubpart part;
        bool partOnStatic;
        bool partDirty;

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
                Settings = Load() ?? TrackSettings.Defaults(Kind);
                MagRailSession.Instance.Register(this);
                registered = true;
                TrackTerminal.Create();
                NeedsUpdate |= MyEntityUpdateEnum.EACH_100TH_FRAME;
                UpdatePart();
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
            }
            catch (Exception e)
            {
                Log.Error("TrackBlockLogic.UpdateAfterSimulation100", e);
            }
        }

        public override void Close()
        {
            if (registered && MagRailSession.Instance != null)
                MagRailSession.Instance.Unregister(this);
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

        public bool CanReshape
        {
            get { return !Settings.Locked; }
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
            SetSettings(TrackSettings.Merge(Settings, requested));
            Net.SendSettingsRequest(this, requested);
        }

        // Server: a request passed the access check.
        public void ApplyRequest(TrackSettings requested)
        {
            SetSettings(TrackSettings.Merge(Settings, requested));
            Net.BroadcastSettings(this);
        }

        public void SetSettings(TrackSettings settings)
        {
            Settings = settings;
            Save();
            partDirty = true;
            UpdatePart();
            TrackTerminal.Refresh();
        }

        TrackSettings Load()
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
                Log.Error("TrackBlockLogic.Load", e);
                return null;
            }
        }

        void Save()
        {
            if (Block.Storage == null)
                Block.Storage = new MyModStorageComponent();
            Block.Storage[SettingsGuid] = Settings.Encode();
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
                if (!d.Key.StartsWith("subpart_"))
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

        void UpdatePart()
        {
            PartInfo info = GetPartInfo();
            MyEntitySubpart sub;
            if (info == null || !Block.TryGetSubpart(info.Name, out sub))
            {
                part = null;
                return;
            }
            bool onStatic = Block.CubeGrid.IsStatic;
            if (sub == part && onStatic == partOnStatic && !partDirty)
                return;
            part = sub;
            partOnStatic = onStatic;
            partDirty = false;

            Matrix local = PartMatrix(info);
            part.PositionComp.SetLocalMatrix(ref local);
            RebuildPartPhysics();
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

        // Rebuilt on every move: moves are rare (settings changes) and a static body does not follow
        // its entity. On a moving grid the kinematic body follows the part's world matrix, which the
        // game then has to keep up to date.
        void RebuildPartPhysics()
        {
            if (part.Physics != null)
            {
                part.Physics.Close();
                part.Physics = null;
            }
            if (partOnStatic)
            {
                part.InitModelPhysics(RigidBodyFlag.RBF_STATIC, StaticLayer);
            }
            else
            {
                part.NeedsWorldMatrix = true;
                Block.NeedsWorldMatrix = true;
                part.InitModelPhysics(RigidBodyFlag.RBF_KINEMATIC, KinematicLayer);
            }
        }
    }
}
