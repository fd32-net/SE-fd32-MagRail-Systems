using System;
using Sandbox.ModAPI;
using VRage.Game.Components;

namespace MagRail
{
    // Entry point of the mod: owns the systems and the order they run in each tick.
    [MySessionComponentDescriptor(MyUpdateOrder.AfterSimulation)]
    public class MagRailSession : MySessionComponentBase
    {
        public const string Version = "V0.1.0";

        public static MagRailSession Instance { get; private set; }

        int tick;

        public override void LoadData()
        {
            Instance = this;
            Loc.Load(ModContext != null ? ModContext.ModPath : null);
            Log.Info(Version + " loaded (" + (MyAPIGateway.Session.IsServer ? "server" : "client") + ")");
        }

        protected override void UnloadData()
        {
            Log.Clear();
            Instance = null;
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
