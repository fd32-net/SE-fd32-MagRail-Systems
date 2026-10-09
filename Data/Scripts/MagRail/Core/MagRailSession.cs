using System;
using Sandbox.ModAPI;
using VRage.Game.Components;

namespace MagRail
{
    // Entry point of the mod: owns the systems and the order they run in each tick.
    [MySessionComponentDescriptor(MyUpdateOrder.AfterSimulation)]
    public class MagRailSession : MySessionComponentBase
    {
        public const string Version = "V0.4.0";

        public static MagRailSession Instance { get; private set; }

        public TrackNetwork Network { get; private set; }

        int tick;
        bool client;
        RailLinker linker;

        public override void LoadData()
        {
            Instance = this;
            Network = new TrackNetwork();
            client = !MyAPIGateway.Utilities.IsDedicated;
            Loc.Load(ModContext != null ? ModContext.ModPath : null);
            Net.Load();
            if (client)
            {
                linker = new RailLinker();
                MyAPIGateway.Entities.OnEntityAdd += RailLinker.HideInHand;
                MyAPIGateway.Utilities.MessageEntered += Commands.OnMessage;
            }
            Log.Info(Version + " loaded (" + (MyAPIGateway.Session.IsServer ? "server" : "client") + ")");
        }

        protected override void UnloadData()
        {
            Net.Unload();
            if (client)
            {
                MyAPIGateway.Entities.OnEntityAdd -= RailLinker.HideInHand;
                MyAPIGateway.Utilities.MessageEntered -= Commands.OnMessage;
            }
            Network.Clear();
            Log.Clear();
            Instance = null;
        }

        public override void UpdateAfterSimulation()
        {
            try
            {
                tick++;
                Network.Update();
                if (client && tick % 60 == 0)
                    Loc.CheckReload();
            }
            catch (Exception e)
            {
                Log.Error("Session.UpdateAfterSimulation", e);
            }
        }

        public override void HandleInput()
        {
            if (!client)
                return;
            try
            {
                linker.HandleInput();
            }
            catch (Exception e)
            {
                Log.Error("Session.HandleInput", e);
            }
        }

        public override void Draw()
        {
            if (!client || MyAPIGateway.Session.Camera == null)
                return;
            try
            {
                SpanLines.DrawAll(Network);
                linker.DrawFrame();
            }
            catch (Exception e)
            {
                Log.Error("Session.Draw", e);
            }
        }
    }
}
