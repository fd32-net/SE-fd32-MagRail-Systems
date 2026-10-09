using System;
using System.Collections.Generic;
using Sandbox.ModAPI;
using VRage.Game.Components;

namespace MagRail
{
    // Entry point of the mod: owns the systems and the order they run in each tick.
    [MySessionComponentDescriptor(MyUpdateOrder.AfterSimulation)]
    public class MagRailSession : MySessionComponentBase
    {
        public const string Version = "V0.3.0";

        public static MagRailSession Instance { get; private set; }

        int tick;
        readonly Dictionary<long, TrackBlockLogic> trackBlocks = new Dictionary<long, TrackBlockLogic>();

        public override void LoadData()
        {
            Instance = this;
            Loc.Load(ModContext != null ? ModContext.ModPath : null);
            Net.Load();
            Log.Info(Version + " loaded (" + (MyAPIGateway.Session.IsServer ? "server" : "client") + ")");
        }

        protected override void UnloadData()
        {
            Net.Unload();
            trackBlocks.Clear();
            Log.Clear();
            Instance = null;
        }

        public void Register(TrackBlockLogic logic)
        {
            trackBlocks[logic.Block.EntityId] = logic;
        }

        public void Unregister(TrackBlockLogic logic)
        {
            trackBlocks.Remove(logic.Block.EntityId);
        }

        public TrackBlockLogic FindTrackBlock(long entityId)
        {
            TrackBlockLogic logic;
            return trackBlocks.TryGetValue(entityId, out logic) ? logic : null;
        }

        public override void UpdateAfterSimulation()
        {
            try
            {
                tick++;
                if (tick % 60 == 0)
                    Loc.CheckReload();
            }
            catch (Exception e)
            {
                Log.Error("Session.UpdateAfterSimulation", e);
            }
        }
    }
}
