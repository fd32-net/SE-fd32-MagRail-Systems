using System;
using System.Collections.Generic;
using VRageMath;

namespace MagRail
{
    // Where the track is, for finding it near a point: a hash grid of 50 m cells over everything that stays
    // put (blocks on static grids, spans on stations; a long span in ~25 m chunks of its curve), plus a list
    // of what moves with a grid (checked by its current bounds on every query). Rebuilt when the network's
    // Version changes. Entries are candidates: the caller measures the exact distance.
    public class TrackIndex
    {
        const double CellSize = 50;
        const int ChunkSamples = 100;
        // A block's rail can stand out of its cells (height setting, turned head).
        const double BlockMargin = 3;

        public struct Entry
        {
            public TrackBlockLogic Block;   // the lanes of a block, or
            public Span Span;               // samples First..Last of a span's curve
            public int First, Last;
        }

        readonly Dictionary<long, List<Entry>> cells = new Dictionary<long, List<Entry>>();
        readonly List<Entry> moving = new List<Entry>();
        int builtVersion = -1;

        // Adds the entries that may hold track within radius of the point (an entry can come more than once).
        public void Query(TrackNetwork network, Vector3D point, double radius, List<Entry> result)
        {
            if (builtVersion != network.Version)
                Build(network);
            Vector3I lo = Cell(point - radius), hi = Cell(point + radius);
            for (int x = lo.X; x <= hi.X; x++)
                for (int y = lo.Y; y <= hi.Y; y++)
                    for (int z = lo.Z; z <= hi.Z; z++)
                    {
                        List<Entry> list;
                        if (cells.TryGetValue(Key(x, y, z), out list))
                            result.AddRange(list);
                    }
            for (int i = 0; i < moving.Count; i++)
            {
                Entry e = moving[i];
                if (e.Block != null)
                {
                    BoundingBoxD box = e.Block.Block.WorldAABB.Inflate(BlockMargin + radius);
                    if (box.Contains(point) != ContainmentType.Disjoint)
                        result.Add(e);
                    continue;
                }
                SpanCurve c = network.CurveOf(e.Span);
                if (c != null && Vector3D.Distance(Vector3D.Transform(c.Bounds.Center, TrackNetwork.FrameMatrix(e.Span)), point)
                    <= c.Bounds.Radius + radius)
                    result.Add(e);
            }
        }

        void Build(TrackNetwork network)
        {
            builtVersion = network.Version;
            cells.Clear();
            moving.Clear();
            foreach (TrackBlockLogic b in network.Blocks)
            {
                Entry e = new Entry { Block = b };
                if (b.Block.CubeGrid.IsStatic)
                    Insert(e, b.Block.WorldAABB.Inflate(BlockMargin));
                else
                    moving.Add(e);
            }
            for (int i = 0; i < network.Spans.Count; i++)
            {
                Span s = network.Spans[i];
                if (!s.Bound)
                    continue;
                bool inGrid = TrackNetwork.InGridFrame(s);
                bool fixedPlace = inGrid ? s.Owner.Block.CubeGrid.IsStatic : s.Owner.Block.CubeGrid.IsStatic && s.Partner.Block.CubeGrid.IsStatic;
                SpanCurve c = network.CurveOf(s);
                if (!fixedPlace)
                {
                    // Between two grids that are not both static a span is never a rail.
                    if (inGrid)
                        moving.Add(new Entry { Span = s, First = 0, Last = c.Pos.Count - 1 });
                    continue;
                }
                MatrixD frame = TrackNetwork.FrameMatrix(s);
                for (int first = 0; first < c.Pos.Count - 1; first += ChunkSamples)
                {
                    int last = Math.Min(first + ChunkSamples, c.Pos.Count - 1);
                    BoundingBoxD box = BoundingBoxD.CreateInvalid();
                    for (int k = first; k <= last; k++)
                        box.Include(Vector3D.Transform(c.Pos[k], frame));
                    Insert(new Entry { Span = s, First = first, Last = last }, box);
                }
            }
        }

        void Insert(Entry e, BoundingBoxD box)
        {
            Vector3I lo = Cell(box.Min), hi = Cell(box.Max);
            for (int x = lo.X; x <= hi.X; x++)
                for (int y = lo.Y; y <= hi.Y; y++)
                    for (int z = lo.Z; z <= hi.Z; z++)
                    {
                        long key = Key(x, y, z);
                        List<Entry> list;
                        if (!cells.TryGetValue(key, out list))
                            cells[key] = list = new List<Entry>();
                        list.Add(e);
                    }
        }

        static Vector3I Cell(Vector3D p)
        {
            return new Vector3I((int)Math.Floor(p.X / CellSize), (int)Math.Floor(p.Y / CellSize), (int)Math.Floor(p.Z / CellSize));
        }

        // 21 bits per axis: +-52 000 km.
        static long Key(int x, int y, int z)
        {
            return ((long)(x & 0x1FFFFF) << 42) | ((long)(y & 0x1FFFFF) << 21) | (long)(z & 0x1FFFFF);
        }

        public void Clear()
        {
            cells.Clear();
            moving.Clear();
            builtVersion = -1;
        }
    }
}
