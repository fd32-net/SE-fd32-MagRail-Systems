using System.Collections.Generic;
using Sandbox.ModAPI;
using VRage.Game.Entity;
using VRageMath;

namespace MagRail
{
    // Track pieces of the spans in rail mode within view (clients): placed nearest span first, a few
    // per tick, removed once a span is out of range (with a margin, so they do not blink at the edge).
    // Until a span is complete, SpanLines draws it as a steel line.
    public class TrackPieces
    {
        const double EnterRange = 2000, ExitRange = 2300;
        const int RefreshTicks = 10;
        const int PerTick = 20;

        readonly Dictionary<Span, PlacedPieces> placed = new Dictionary<Span, PlacedPieces>();
        readonly List<PlacedPieces> queue = new List<PlacedPieces>();   // unfinished, nearest first
        readonly List<Span> gone = new List<Span>();
        int tick, stamp;

        public int EntityCount { get; private set; }

        public bool Complete(Span s)
        {
            PlacedPieces p;
            return placed.TryGetValue(s, out p) && p.Layout == s.Layout && Done(p);
        }

        static bool Done(PlacedPieces p)
        {
            return p.Entities[p.Entities.Length - 1] != null;
        }

        public void Update(TrackNetwork network)
        {
            if (++tick % RefreshTicks == 0)
                Refresh(network);
            Build();
        }

        void Refresh(TrackNetwork network)
        {
            if (MyAPIGateway.Session.Camera == null)
                return;
            Vector3D camera = MyAPIGateway.Session.Camera.WorldMatrix.Translation;
            stamp++;
            queue.Clear();
            for (int i = 0; i < network.Spans.Count; i++)
            {
                Span s = network.Spans[i];
                if (!s.Bound || !s.IsRail)
                    continue;
                TrackLayout layout = network.LayoutOf(s);
                MatrixD frame = TrackNetwork.FrameMatrix(s);
                BoundingSphereD bounds = layout.Curve.Bounds;
                double d = Vector3D.Distance(Vector3D.Transform(bounds.Center, frame), camera) - bounds.Radius;
                PlacedPieces p;
                bool has = placed.TryGetValue(s, out p);
                if (d > (has ? ExitRange : EnterRange))
                    continue;
                if (!has)
                {
                    p = new PlacedPieces(s);
                    placed[s] = p;
                }
                p.Take(layout, PieceEntities.ParentOf(s), frame, layout.Pieces.Count);
                Vector3 color = s.Color;
                string skin = s.Skin;
                if (color != p.Color || skin != p.Skin)
                    p.Paint(color, skin);
                p.Stamp = stamp;
                p.Distance = d;
                if (!Done(p))
                    queue.Add(p);
            }
            int count = 0;
            foreach (KeyValuePair<Span, PlacedPieces> pair in placed)
            {
                if (pair.Value.Stamp != stamp)
                {
                    pair.Value.Clear();
                    gone.Add(pair.Key);
                }
                else
                    count += pair.Value.Count;
            }
            for (int i = 0; i < gone.Count; i++)
                placed.Remove(gone[i]);
            gone.Clear();
            EntityCount = count;
            queue.Sort((a, b) => a.Distance.CompareTo(b.Distance));
        }

        void Build()
        {
            int budget = PerTick;
            for (int q = 0; q < queue.Count && budget > 0; q++)
            {
                PlacedPieces p = queue[q];
                // The span may have lost a block since the last refresh.
                if (!p.Span.Bound || (p.Parent != null && p.Parent.MarkedForClose))
                    continue;
                TrackLayout layout = p.Layout;
                for (int i = 0; i < p.Entities.Length && budget > 0; i++)
                {
                    if (p.Entities[i] != null)
                        continue;
                    p.Entities[i] = PieceEntities.Visual(PieceEntities.Model(layout.Models[i]), layout.Pieces[i], p.Frame, p.Parent, p.Color, p.Skin);
                    budget--;
                }
            }
        }

        // Unload: the game closes every entity itself.
        public void Clear()
        {
            placed.Clear();
            queue.Clear();
        }
    }
}
