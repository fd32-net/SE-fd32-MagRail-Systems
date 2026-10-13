using System.Collections.Generic;
using Sandbox.ModAPI;
using VRage.Game.ModAPI;
using VRageMath;

namespace MagRail
{
    // Collision of the spans in rail mode near players, so they can walk on them: 2.5 m pieces within
    // ~100 m of a player, removed past ~130 m. The server covers every player, a client only its own
    // (its character moves on the client). Static bodies; spans on a moving grid get none (PieceEntities).
    public class TrackCollision
    {
        const double EnterRange = 100, ExitRange = 130;
        const int RefreshTicks = 10;
        const int PerRefresh = 60;

        readonly Dictionary<Span, PlacedPieces> placed = new Dictionary<Span, PlacedPieces>();
        readonly List<Span> gone = new List<Span>();
        readonly List<Vector3D> origins = new List<Vector3D>();
        readonly List<IMyPlayer> players = new List<IMyPlayer>();
        int tick, stamp;

        public int EntityCount { get; private set; }

        public void Update(TrackNetwork network)
        {
            if (++tick % RefreshTicks != 0)
                return;
            GatherOrigins();
            stamp++;
            int budget = PerRefresh, count = 0;
            for (int i = 0; i < network.Spans.Count; i++)
            {
                Span s = network.Spans[i];
                if (!s.Bound || !s.IsRail)
                    continue;
                TrackLayout layout = network.LayoutOf(s);
                MatrixD frame = TrackNetwork.FrameMatrix(s);
                BoundingSphereD bounds = layout.Curve.Bounds;
                if (Nearest(Vector3D.Transform(bounds.Center, frame)) - bounds.Radius > ExitRange)
                    continue;
                if (PieceEntities.ParentOf(s) != null)
                    continue;
                PlacedPieces p;
                if (!placed.TryGetValue(s, out p))
                {
                    p = new PlacedPieces(s);
                    placed[s] = p;
                }
                p.Take(layout, null, frame, layout.Collision.Count);
                p.Stamp = stamp;
                for (int j = 0; j < layout.Collision.Count; j++)
                {
                    double d = Nearest(Vector3D.Transform(layout.Collision[j].Translation, frame));
                    if (p.Entities[j] == null)
                    {
                        if (d < EnterRange && budget > 0)
                        {
                            p.Entities[j] = PieceEntities.Collision(layout.CollisionModel, layout.Collision[j], p.Frame);
                            budget--;
                        }
                    }
                    else if (d > ExitRange)
                    {
                        PieceEntities.Remove(p.Entities[j]);
                        p.Entities[j] = null;
                    }
                }
                count += p.Count;
            }
            foreach (KeyValuePair<Span, PlacedPieces> pair in placed)
            {
                if (pair.Value.Stamp != stamp)
                {
                    pair.Value.Clear();
                    gone.Add(pair.Key);
                }
            }
            for (int i = 0; i < gone.Count; i++)
                placed.Remove(gone[i]);
            gone.Clear();
            EntityCount = count;
        }

        void GatherOrigins()
        {
            origins.Clear();
            if (MyAPIGateway.Session.IsServer)
            {
                players.Clear();
                MyAPIGateway.Players.GetPlayers(players);
                for (int i = 0; i < players.Count; i++)
                    AddOrigin(players[i]);
                players.Clear();
            }
            else
            {
                AddOrigin(MyAPIGateway.Session.Player);
            }
        }

        // A player in the game (not dead, not in the respawn screen).
        void AddOrigin(IMyPlayer player)
        {
            if (player != null && player.Controller != null && player.Controller.ControlledEntity != null)
                origins.Add(player.GetPosition());
        }

        double Nearest(Vector3D point)
        {
            double best = double.PositiveInfinity;
            for (int i = 0; i < origins.Count; i++)
            {
                double d = Vector3D.DistanceSquared(point, origins[i]);
                if (d < best)
                    best = d;
            }
            return System.Math.Sqrt(best);
        }

        // Unload: the game closes every entity itself.
        public void Clear()
        {
            placed.Clear();
        }
    }
}
