using System;
using System.Collections.Generic;
using Sandbox.ModAPI;
using VRage.Game.Components;
using VRageMath;

namespace MagRail
{
    // Entry point of the mod: owns the systems and the order they run in each tick.
    // Priority 500: session components run by ascending priority (ties by type name), MySector (800) runs all block
    // logic and other mods default to 1000. The rail hold writes grid velocities first, so whatever runs after us
    // (coupling scripts, other mods, block logic) and the physics step's constraints have the last word: the rail
    // is a force, not the master of the train.
    [MySessionComponentDescriptor(MyUpdateOrder.BeforeSimulation | MyUpdateOrder.AfterSimulation, 500)]
    public class MagRailSession : MySessionComponentBase
    {
        public const string Version = "V0.10.49";

        public static MagRailSession Instance { get; private set; }

        public TrackNetwork Network { get; private set; }
        public TrackCollision Collision { get; private set; }
        public TrackPieces Pieces { get; private set; }     // null on a dedicated server
        public readonly List<HoverEngineLogic> Engines = new List<HoverEngineLogic>();
        public readonly List<TrainControlLogic> ControlUnits = new List<TrainControlLogic>();
        public readonly Rides Rides = new Rides();
        public readonly PerfCounter UpdateTime = new PerfCounter(), DrawTime = new PerfCounter(), RideTime = new PerfCounter();

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
            TrackStyle.Check(ModContext.ModItem);
            Net.Load();
            SpanDamage.Load();
            if (client)
            {
                linker = new RailLinker();
                Pieces = new TrackPieces();
                MyAPIGateway.Utilities.MessageEntered += Commands.OnMessage;
            }
            Log.Info(Version + " loaded (" + (MyAPIGateway.Session.IsServer ? "server" : "client") + ")");
        }

        // The world is loaded: blocks initialised from now on are new (built, pasted, streamed in).
        public bool Started { get; private set; }

        public override void BeforeStart()
        {
            Started = true;
            SpanDamage.Start();
        }

        protected override void UnloadData()
        {
            Net.Unload();
            SpanDamage.Unload();
            if (client)
            {
                MyAPIGateway.Utilities.MessageEntered -= Commands.OnMessage;
            }
            Network.Clear();
            Collision.Clear();
            Engines.Clear();
            ControlUnits.Clear();
            AutopilotLogic.Clear();
            Rides.Clear();
            TrackDebug.Clear();
            SpanWelding.Clear();
            RideLog.Stop();
            if (Pieces != null)
                Pieces.Clear();
            Log.Clear();
            Instance = null;
        }

        // Trains on the rail: before the physics step (the hold cancels gravity up front).
        public override void UpdateBeforeSimulation()
        {
            try
            {
                RideTime.Start();
                AutopilotLogic.UpdateAll();
                Rides.Update(Engines);
                RideTime.Stop();
            }
            catch (Exception e)
            {
                Log.Error("Session.UpdateBeforeSimulation", e);
            }
        }

        public override void UpdateAfterSimulation()
        {
            try
            {
                UpdateTime.Start();
                tick++;
                Network.Update();
                Collision.Update(Network);
                if (MyAPIGateway.Session.IsServer)
                    SpanWelding.ServerUpdate(Network, tick);
                SpanDamage.Update(Network, tick);
                if (client)
                {
                    Pieces.Update(Network);
                    SpanWelding.ClientUpdate(Network);
                    SpanDamage.ClientUpdate(Network, tick);
                    UpdateEngineParts();
                    TrainControlLogic.UpdateLights(ControlUnits, tick);
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

        void UpdateEngineParts()
        {
            if (MyAPIGateway.Session.Camera == null)
                return;
            Vector3D camera = MyAPIGateway.Session.Camera.WorldMatrix.Translation;
            for (int i = 0; i < Engines.Count; i++)
                if (Engines[i].Platform != null)
                    Engines[i].Platform.Update(camera, tick);
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
                MagRail.Draw.Begin(linker.Held);
                SpanLines.DrawAll(Network, Pieces, linker.AimedSpan, linker.MarkedSpan);
                linker.DrawFrame();
                LinkerDisplay.DrawOthers(MagRail.Draw.Camera);
                if (Hud.Begin())
                    linker.DrawHud();
                SpanWelding.DrawHud(Network);
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
