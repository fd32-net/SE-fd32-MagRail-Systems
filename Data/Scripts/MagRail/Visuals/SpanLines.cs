using Sandbox.ModAPI;
using VRageMath;

namespace MagRail
{
    public enum LineStyle
    {
        Rail,       // thick steel line (a rail whose track pieces are not all placed yet)
        Prototype,  // laid span: thin solid line coloured by shape (Draw.ByShape), ticks along the rail's up (bank)
        Preview,    // Rail Linker while laying: dashed, coloured by shape, no ticks
    }

    // Spans as lines, seen by everyone (clients): prototypes, and rails until their track pieces are
    // placed. A span that should be a rail but cannot be one gets a red cross in the middle.
    public static class SpanLines
    {
        const double DrawRange = 3000;
        const int SegmentSamples = 4;       // one billboard per ~1 m
        const int TickSamples = 10;         // an up tick every ~2.5 m
        static readonly Color Steel = new Color(175, 185, 200);

        public static void DrawAll(TrackNetwork network, TrackPieces pieces)
        {
            Vector3D camera = MyAPIGateway.Session.Camera.WorldMatrix.Translation;
            for (int i = 0; i < network.Spans.Count; i++)
            {
                Span s = network.Spans[i];
                if (!s.Bound || (s.IsRail && pieces.Complete(s)))
                    continue;
                SpanCurve c = network.CurveOf(s);
                MatrixD frame = TrackNetwork.FrameMatrix(s);
                if (Vector3D.Distance(Vector3D.Transform(c.Bounds.Center, frame), camera) - c.Bounds.Radius > DrawRange)
                    continue;
                DrawCurve(c, frame, s.IsRail ? LineStyle.Rail : LineStyle.Prototype);
                if (s.Record.WantRail && s.Problem != null)
                    DrawCross(c, frame, c.Pos.Count / 2, Draw.Red);
            }
        }

        public static void DrawCurve(SpanCurve c, MatrixD frame, LineStyle style)
        {
            int last = c.Pos.Count - 1;
            bool dash = true;
            for (int i = 0; i < last; i += SegmentSamples)
            {
                int j = i + SegmentSamples < last ? i + SegmentSamples : last;
                dash = !dash;
                if (style == LineStyle.Preview && dash)
                    continue;
                Vector3D a = Vector3D.Transform(c.Pos[i], frame), b = Vector3D.Transform(c.Pos[j], frame);
                if (style == LineStyle.Rail)
                {
                    Draw.Line(a, b, Steel, 0.15f);
                    continue;
                }
                double k = 0;
                for (int n = i; n <= j; n++)
                    k = k > c.Curvature[n] ? k : c.Curvature[n];
                Draw.Line(a, b, Draw.ByShape(k, c.TwistRate), style == LineStyle.Preview ? 0.09f : 0.06f);
            }
            if (style != LineStyle.Prototype)
                return;
            for (int i = 0; i <= last; i += TickSamples)
            {
                Vector3D p = Vector3D.Transform(c.Pos[i], frame);
                Draw.Line(p, p + Vector3D.TransformNormal(c.Up[i], frame) * 0.4, Color.White * 0.6f, 0.03f);
            }
        }

        static void DrawCross(SpanCurve c, MatrixD frame, int i, Color color)
        {
            Vector3D p = Vector3D.Transform(c.Pos[i], frame);
            Vector3D up = Vector3D.TransformNormal(c.Up[i], frame);
            Vector3D side = Vector3D.TransformNormal(Vector3D.Cross(c.Tan[i], c.Up[i]), frame);
            Draw.Line(p - (up + side) * 0.5, p + (up + side) * 0.5, color, 0.08f);
            Draw.Line(p - (up - side) * 0.5, p + (up - side) * 0.5, color, 0.08f);
        }
    }
}
