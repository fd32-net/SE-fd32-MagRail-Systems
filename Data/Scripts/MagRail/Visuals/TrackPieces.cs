using System.Collections.Generic;
using System.Diagnostics;
using Sandbox.ModAPI;
using VRage.Game.Entity;
using VRageMath;

namespace MagRail
{
    // Track pieces of the spans in rail mode within view (clients): placed nearest span first, a few
    // per tick, removed once a span is out of range (with a margin, so they do not blink at the edge).
    // Until a span's pieces are first all placed, SpanLines draws it as a steel line. A span being built (SpanWeld) shows construction
    // stage pieces (stage 0 where nothing is welded in yet), the finished pieces on its finished segments.
    public class TrackPieces
    {
        const double EnterRange = 2000, ExitRange = 2300;
        const int RefreshTicks = 10;
        public static int PerTick = 20;     // TEMPORARY settable (/magrail rate), a const before it

        readonly Dictionary<Span, PlacedPieces> placed = new Dictionary<Span, PlacedPieces>();
        readonly List<PlacedPieces> queue = new List<PlacedPieces>();   // unfinished, nearest first
        readonly List<Span> gone = new List<Span>();
        int tick, stamp;
        readonly Stopwatch repave = new Stopwatch();    // TEMPORARY (/magrail repave)

        public int EntityCount { get; private set; }

        public bool Complete(Span s)
        {
            PlacedPieces p;
            return placed.TryGetValue(s, out p) && p.Layout == s.Layout && p.Shown;
        }

        public void Update(TrackNetwork network)
        {
            if (++tick % RefreshTicks == 0)
            {
                Refresh(network);
                if (repave.IsRunning && queue.Count == 0)
                {
                    repave.Stop();
                    MyAPIGateway.Utilities.ShowMessage("MagRail", EntityCount + " pieces in " +
                        repave.Elapsed.TotalSeconds.ToString("0.0") + " s, " + PerTick + " per tick");
                }
            }
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
                p.Take(layout, PieceEntities.ParentOf(s), frame, layout.Pieces.Count, true);
                // The pieces before go a refresh after the new ones are all placed: a new entity shows a frame late.
                if (p.Done && p.Leaving.Count > 0)
                    p.Release();
                Vector3 color = s.Color;
                string skin = s.Skin;
                if (color != p.Color || skin != p.Skin)
                    p.Paint(color, skin);
                int selectedStart = s.Owner.SelectedInstead(s.OwnerPort), selectedEnd = s.Partner.SelectedInstead(s.PartnerPort);
                if (selectedStart != p.SelectedStart || selectedEnd != p.SelectedEnd)
                    p.Light(selectedStart, selectedEnd);
                float[] weld = s.Weld(layout);
                if (p.WeldRevision != s.WeldRevision || p.Weld != weld)
                    p.ApplyWeld(weld, s.WeldRevision);
                p.Stamp = stamp;
                p.Distance = d;
                if (!p.Done)
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
                    if (p.Entities[i] != null || !p.Wanted(i))
                        continue;
                    // The bare body first, then the piece over it (Show).
                    if (layout.Bare[i] != null)
                    {
                        p.Bares[i] = PieceEntities.Visual(PieceEntities.Model(layout.Bare[i]), layout.Pieces[i], p.Frame, p.Parent, p.Color, p.Skin, 1);
                        budget--;
                    }
                    p.Entities[i] = PieceEntities.Visual(PieceEntities.Model(layout.Models[i]), layout.Pieces[i], p.Frame, p.Parent, p.Color, p.Skin,
                        layout.Glow(i, p.SelectedStart, p.SelectedEnd));
                    p.Show(i);
                    budget--;
                }
                for (int j = 0; j < p.Stages.Length && budget > 0; j++)
                {
                    int stage = p.StageWanted(j);
                    if (p.Stages[j] != null || stage < 0)
                        continue;
                    p.Stages[j] = PieceEntities.Visual(PieceEntities.Model(layout.Style.Stage(stage, layout.StageFull[j])), layout.Stages[j],
                        p.Frame, p.Parent, p.Color, p.Skin, 1);
                    p.StageShown[j] = stage;
                    budget--;
                }
                // Gone through it all with budget to spare: nothing is missing.
                if (budget > 0)
                    p.Done = p.Shown = true;
            }
        }

        // The shown piece of span s nearest to a world point (a bullet's mark goes on it, SpanPart), or null: none placed.
        public MyEntity PieceNear(Span s, Vector3D point)
        {
            PlacedPieces p;
            if (!placed.TryGetValue(s, out p) || p.Entities == null)
                return null;
            MyEntity best = null;
            double bestDistance = double.MaxValue;
            Nearest(p.Entities, point, ref best, ref bestDistance);
            Nearest(p.Bares, point, ref best, ref bestDistance);
            Nearest(p.Stages, point, ref best, ref bestDistance);
            return best;
        }

        static void Nearest(MyEntity[] entities, Vector3D point, ref MyEntity best, ref double bestDistance)
        {
            for (int i = 0; i < entities.Length; i++)
            {
                MyEntity e = entities[i];
                if (e == null || e.MarkedForClose || !e.InScene || !e.Render.Visible)
                    continue;
                double d = e.PositionComp.WorldAABB.Distance(point);
                if (d < bestDistance)
                {
                    best = e;
                    bestDistance = d;
                }
            }
        }

        // TEMPORARY (/magrail repave): every piece removed and placed again; the time it takes in chat.
        public void Repave()
        {
            foreach (PlacedPieces p in placed.Values)
                p.Clear();
            placed.Clear();
            queue.Clear();
            EntityCount = 0;
            repave.Restart();
        }

        // Unload: the game closes every entity itself.
        public void Clear()
        {
            placed.Clear();
            queue.Clear();
        }
    }
}
