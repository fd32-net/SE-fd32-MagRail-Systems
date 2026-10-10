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
        public const string Version = "V0.7.0";

        public static MagRailSession Instance { get; private set; }

        public TrackNetwork Network { get; private set; }
        public TrackCollision Collision { get; private set; }
        public TrackPieces Pieces { get; private set; }     // null on a dedicated server
        public readonly List<HoverEngineLogic> Engines = new List<HoverEngineLogic>();
        public readonly PerfCounter UpdateTime = new PerfCounter(), DrawTime = new PerfCounter();

        int tick;
        bool client;
        RailLinker linker;

        public override void LoadData()
        {
            Instance = this;
            Network = new TrackNetwork();
            Collision = new TrackCollision();
            client = !MyAPIGateway.Utilities.IsDedicated;
            Loc.Load(ModContext != null ? ModContext.ModPath : null);
            PieceEntities.Load(ModContext.ModPath);
            Net.Load();
            if (client)
            {
                linker = new RailLinker();
                Pieces = new TrackPieces();
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
            Collision.Clear();
            Engines.Clear();
            TrackDebug.Clear();
            if (Pieces != null)
                Pieces.Clear();
            Log.Clear();
            Instance = null;
        }

        public override void UpdateAfterSimulation()
        {
            try
            {
                UpdateTime.Start();
                tick++;
                Network.Update();
                Collision.Update(Network);
                if (client)
                {
                    Pieces.Update(Network);
                    TrackDebug.Update();
                    if (tick % 60 == 0)
                        Loc.CheckReload();
                }
                UpdateTime.Stop();
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
                DrawTime.Start();
                SpanLines.DrawAll(Network, Pieces);
                linker.DrawFrame();
                TrackDebug.DrawFrame(this);
                DrawTime.Stop();
            }
            catch (Exception e)
            {
                Log.Error("Session.Draw", e);
            }
        }
    }
}
