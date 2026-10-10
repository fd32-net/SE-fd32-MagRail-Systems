using System.Collections.Generic;
using Sandbox.Game.Entities;
using Sandbox.ModAPI;
using VRage.Game.ModAPI;
using VRageMath;

namespace MagRail
{
    // The rail bodies: one per grid with hover engines, regrouped from the engine list every tick (a grid split
    // or merge just moves engines to another body). Runs before the physics step: the hold cancels gravity up
    // front. The server writes the physics.
    // Clients keep the game's prediction off on those grids, or the pilot's predicted copy fights the server's.
    // The game resets that flag on every sync update of a grid it does not predict
    // (MyEntityPhysicsStateGroup.ClientUpdate), so it is set again every tick. A train held by a rail on a
    // moving ship: the server sends the train's place in world coordinates (it ties a grid to another only
    // through a physical connection), so a predicted ship and the train shown from server snapshots drift
    // apart on the client and the train goes through the hull. The ship (with everything connected to it) is
    // then shown from snapshots too, both from the same server moments; its pilot steers it with the network
    // delay while a train rides it.
    public class Rides
    {
        const int InfoTicks = 60;
        // Clients: how often the engines' rail (and the ship it is on) is looked up.
        const int ClientProbeTicks = 10;

        readonly Dictionary<IMyCubeGrid, RailBody> bodies = new Dictionary<IMyCubeGrid, RailBody>();
        readonly List<IMyCubeGrid> gone = new List<IMyCubeGrid>();
        // Clients: grids shown from server snapshots only (bodies and the ships their trains ride on).
        readonly HashSet<IMyCubeGrid> animated = new HashSet<IMyCubeGrid>();
        readonly List<IMyCubeGrid> group = new List<IMyCubeGrid>();
        int tick;

        public int Count
        {
            get { return bodies.Count; }
        }

        public void Update(List<HoverEngineLogic> engines)
        {
            tick++;
            foreach (RailBody body in bodies.Values)
                body.Engines.Clear();
            for (int i = 0; i < engines.Count; i++)
            {
                HoverEngineLogic e = engines[i];
                RailBody body;
                if (!bodies.TryGetValue(e.Block.CubeGrid, out body))
                    bodies[e.Block.CubeGrid] = body = new RailBody(e.Block.CubeGrid);
                body.Engines.Add(e);
            }
            gone.Clear();
            foreach (KeyValuePair<IMyCubeGrid, RailBody> pair in bodies)
                if (pair.Value.Engines.Count == 0)
                    gone.Add(pair.Key);
            for (int i = 0; i < gone.Count; i++)
                bodies.Remove(gone[i]);

            if (Net.IsServer)
            {
                foreach (RailBody body in bodies.Values)
                    body.Update(tick);
                if (RideLog.Active)
                    RideLog.Record(bodies.Values);
            }
            else
            {
                if (tick % ClientProbeTicks == 1)
                    ProbeOnClient(engines);
                foreach (IMyCubeGrid grid in animated)
                    DisablePrediction(grid);
            }
            if (tick % InfoTicks == 0)
                for (int i = 0; i < engines.Count; i++)
                    engines[i].Block.RefreshCustomInfo();
        }

        // Clients do not run the physics: the engines' status (terminal info) is the same check without its
        // hysteresis, and the ships under held trains join the animated grids.
        void ProbeOnClient(List<HoverEngineLogic> engines)
        {
            animated.Clear();
            foreach (IMyCubeGrid grid in bodies.Keys)
                animated.Add(grid);
            for (int i = 0; i < engines.Count; i++)
            {
                HoverEngineLogic e = engines[i];
                TrackSample s;
                double error;
                e.Status = e.Probe(e.DebugCursor, e.Block.WorldMatrix.Forward, out s, out error);
                // As on the server: a cursor not over the engine's rail is not kept (it could stay on another one).
                if (e.Status != RailState.Held)
                    e.DebugCursor.Reset();
                if (e.Status != RailState.Held || s.Carrier == null || animated.Contains(s.Carrier))
                    continue;
                group.Clear();
                MyAPIGateway.GridGroups.GetGroup(s.Carrier, GridLinkTypeEnum.Physical, group);
                foreach (IMyCubeGrid g in group)
                    animated.Add(g);
                animated.Add(s.Carrier);
            }
            group.Clear();
        }

        static void DisablePrediction(IMyCubeGrid grid)
        {
            MyCubeGrid g = grid as MyCubeGrid;
            if (g != null && !g.Closed && !g.ForceDisablePrediction)
                g.ForceDisablePrediction = true;
        }

        // The body holding the rail nearest to a point (/magrail debug).
        public RailBody Nearest(Vector3D point)
        {
            RailBody best = null;
            double bestDistance = double.MaxValue;
            foreach (RailBody body in bodies.Values)
            {
                if (!body.Holding)
                    continue;
                double d = Vector3D.DistanceSquared(body.Grid.WorldAABB.Center, point);
                if (d < bestDistance)
                {
                    bestDistance = d;
                    best = body;
                }
            }
            return best;
        }

        public void Clear()
        {
            bodies.Clear();
            animated.Clear();
        }
    }
}
