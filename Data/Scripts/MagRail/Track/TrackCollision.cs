using System.Collections.Generic;
using Sandbox.Game.Entities;
using Sandbox.ModAPI;
using VRage.Game.Entity;
using VRage.Game.ModAPI;
using VRageMath;

namespace MagRail
{
    // Collision of the spans in rail mode near players, so they can walk on them (being built: stage 0 low, SpanWeld):
    // 2.5 m pieces within
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
                if (!layout.Style.Solid)
                    continue;
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
                // A span being built: its segments with nothing welded in have stage 0's low collision (the frame's
                // bottom), the others the finished rail's.
                float[] weld = s.Weld(layout);
                for (int j = 0; j < layout.Collision.Count; j++)
                {
                    double d = Nearest(Vector3D.Transform(layout.Collision[j].Translation, frame));
                    bool low = weld != null && weld[layout.CollisionSegment[j]] <= 0;
                    if (p.Entities[j] != null && p.Low[j] != low)
                    {
                        PieceEntities.Remove(p.Entities[j]);
                        p.Entities[j] = null;
                    }
                    if (p.Entities[j] == null)
                    {
                        if (d < EnterRange && budget > 0)
                        {
                            p.Entities[j] = PieceEntities.Collision(low ? layout.Style.Stage(0, true) : layout.CollisionModel,
                                layout.Collision[j], p.Frame, s);
                            p.Low[j] = low;
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

        // ---- clearance (Rail Linker, prototype -> rail) ----

        // A block a span's collision cuts into is pushed, deformed and blown up if its grid moves, and grinds the rail
        // down (SpanDamage): such a span is not switched to rail.
        const double ClearMargin = 0.1;
        static readonly List<MyEntity> nearby = new List<MyEntity>();
        static readonly HashSet<IMySlimBlock> tested = new HashSet<IMySlimBlock>();

        // The first block the collision chords' boxes of span s (the section's face outline, ClearMargin smaller all
        // round: a block the rail only touches is none) cut into, or null. Its two end blocks and projections are none.
        // A block is its cells' box, a block with a model its model's box.
        public static IMySlimBlock BlockInside(TrackNetwork network, Span s)
        {
            TrackLayout layout = network.LayoutOf(s);
            if (layout == null || !layout.Style.Solid)
                return null;
            TrackStyle style = layout.Style;
            MatrixD frame = TrackNetwork.FrameMatrix(s);
            try
            {
                for (int j = 0; j < layout.Collision.Count; j++)
                {
                    double half = layout.CollisionHalf[j] - ClearMargin;
                    if (half <= 0)
                        continue;
                    BoundingBoxD local = new BoundingBoxD(
                        new Vector3D(-style.FaceHalfWidth + ClearMargin, style.FaceBottom + ClearMargin, -half),
                        new Vector3D(style.FaceHalfWidth - ClearMargin, style.FaceTop - ClearMargin, half));
                    MatrixD m = layout.Collision[j] * frame;
                    MyOrientedBoundingBoxD box = new MyOrientedBoundingBoxD(local, m);
                    BoundingBoxD world = local.TransformFast(m);
                    nearby.Clear();
                    tested.Clear();
                    MyGamePruningStructure.GetTopMostEntitiesInBox(ref world, nearby);
                    for (int k = 0; k < nearby.Count; k++)
                    {
                        IMyCubeGrid grid = nearby[k] as IMyCubeGrid;
                        if (grid == null || grid.Physics == null || nearby[k].IsPreview)
                            continue;
                        IMySlimBlock hit = BlockInside(grid, ref box, world, s);
                        if (hit != null)
                            return hit;
                    }
                }
                return null;
            }
            finally
            {
                nearby.Clear();
                tested.Clear();
            }
        }

        static IMySlimBlock BlockInside(IMyCubeGrid grid, ref MyOrientedBoundingBoxD box, BoundingBoxD world, Span s)
        {
            double size = grid.GridSize;
            BoundingBoxD local = world.TransformFast(grid.WorldMatrixNormalizedInv);
            Vector3I min = Vector3I.Max(Vector3I.Floor(local.Min / size + 0.5), grid.Min);
            Vector3I max = Vector3I.Min(Vector3I.Floor(local.Max / size + 0.5), grid.Max);
            Vector3I c;
            for (c.X = min.X; c.X <= max.X; c.X++)
                for (c.Y = min.Y; c.Y <= max.Y; c.Y++)
                    for (c.Z = min.Z; c.Z <= max.Z; c.Z++)
                    {
                        IMySlimBlock b = grid.GetCubeBlock(c);
                        if (b == null || !tested.Add(b) || b.FatBlock == s.Owner.Block || b.FatBlock == s.Partner.Block)
                            continue;
                        MyOrientedBoundingBoxD other = BlockBox(b);
                        if (box.Intersects(ref other))
                            return b;
                    }
            return null;
        }

        // A block's box in the world (BlockInside).
        public static MyOrientedBoundingBoxD BlockBox(IMySlimBlock b)
        {
            if (b.FatBlock != null)
                return new MyOrientedBoundingBoxD(b.FatBlock.LocalAABB, b.FatBlock.WorldMatrix);
            double size = b.CubeGrid.GridSize;
            return new MyOrientedBoundingBoxD(new BoundingBoxD(((Vector3D)b.Min - 0.5) * size, ((Vector3D)b.Max + 0.5) * size),
                b.CubeGrid.WorldMatrix);
        }
    }
}
