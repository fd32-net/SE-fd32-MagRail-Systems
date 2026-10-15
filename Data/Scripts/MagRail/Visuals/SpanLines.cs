using System;
using VRageMath;

namespace MagRail
{
    public enum LineStyle
    {
        Rail,       // steel (a rail whose track pieces are not all placed yet)
        Prototype,  // laid span, coloured by shape (Draw.ByShape)
        Preview,    // Rail Linker while laying: coloured by shape, a thicker centre line
    }

    // Spans as overlays, seen by everyone (clients): prototypes, and rails until their track pieces are placed.
    // A centre line through the rail points; near the camera also a translucent ribbon over the crests (it shows the
    // bank) and the rail's section every few modules, where the track pieces will stand. While the Rail Linker is held
    // the line and the near sections are also seen through models (Draw.SeeThrough). A span that should be a rail
    // but cannot be one gets a red cross in the middle; the span the Linker aims at pulses.
    public static class SpanLines
    {
        const double DrawRange = 3000;
        const double DetailRange = 120;         // ribbon and sections
        const double ThroughRange = 200;        // centre line through models
        const double SectionThroughRange = 60;
        const int SectionModules = 4;           // a section every 2.5 m
        const double FadeFrom = 500;            // the line fades to FarAlpha at DrawRange
        const float FarAlpha = 0.35f;
        const float RibbonAlpha = 0.12f;
        public const float FillAlpha = 0.2f;    // end faces: the inside under the outline
        static readonly Color Steel = new Color(175, 185, 200);

        // `aimed` (under the Linker's crosshair) pulses white, `marked` (a click on the aimed port takes it away) pulses
        // red; a built rail is drawn only then (over its track pieces).
        public static void DrawAll(TrackNetwork network, TrackPieces pieces, Span aimed, Span marked)
        {
            for (int i = 0; i < network.Spans.Count; i++)
            {
                Span s = network.Spans[i];
                if (!s.Bound || (s.IsRail && pieces.Complete(s) && s != aimed && s != marked))
                    continue;
                SpanCurve c = network.CurveOf(s);
                MatrixD frame = TrackNetwork.FrameMatrix(s);
                if (Vector3D.Distance(Vector3D.Transform(c.Bounds.Center, frame), Draw.Camera) - c.Bounds.Radius > DrawRange)
                    continue;
                DrawCurve(c, frame, s.Owner.Style, s.IsRail ? LineStyle.Rail : LineStyle.Prototype, s == aimed, s == marked);
                if (s.Record.WantRail && s.Problem != null)
                    DrawCross(c, frame, c.Pos.Count / 2, Draw.Red);
            }
        }

        public static void DrawCurve(SpanCurve c, MatrixD frame, TrackStyle style, LineStyle look, bool aimed = false,
            bool marked = false)
        {
            int last = c.Pos.Count - 1;
            float glow = marked ? 0.3f + 0.6f * Draw.Pulse : aimed ? 0.05f + 0.25f * Draw.Pulse : 0;
            Color tint = marked ? Draw.Red : Color.White;
            aimed |= marked;
            // Aimed or marked: brighter too, pulsing (the colour alone hardly shows on a steel rail).
            float bright = aimed ? 2 + 2 * Draw.Pulse : 1;
            double halfWidth = style.Width / 2;
            Vector3D a = Vector3D.Transform(c.Pos[0], frame);
            int i = 0;
            while (i < last)
            {
                double distance = Vector3D.Distance(a, Draw.Camera);
                int step = distance < 60 ? 2 : distance < 400 ? 4 : 8;
                int j = Math.Min(i + step, last);
                Vector3D b = Vector3D.Transform(c.Pos[j], frame);
                Color color = ColorOf(c, i, j, look, glow, tint);

                if (distance < DetailRange)
                {
                    Vector3D upA = Vector3D.TransformNormal(c.Up[i], frame), upB = Vector3D.TransformNormal(c.Up[j], frame);
                    Vector3D sideA = Vector3D.TransformNormal(Vector3D.Cross(c.Tan[i], c.Up[i]), frame) * halfWidth;
                    Vector3D sideB = Vector3D.TransformNormal(Vector3D.Cross(c.Tan[j], c.Up[j]), frame) * halfWidth;
                    Vector3D crestA = a + upA * style.CrestHeight, crestB = b + upB * style.CrestHeight;
                    Draw.Quad(crestA - sideA, crestA + sideA, crestB + sideB, crestB - sideB, color * RibbonAlpha);
                }

                float fade = distance < FadeFrom ? 1 : (float)(1 - (1 - FarAlpha) * Math.Min(1, (distance - FadeFrom) / (DrawRange - FadeFrom)));
                float near = look == LineStyle.Rail ? 0.12f : look == LineStyle.Preview ? 0.09f : 0.07f;
                Draw.Line(a, b, color * fade, Draw.Width(distance, aimed ? near * 1.4f : near), distance < ThroughRange, bright);
                a = b;
                i = j;
            }
            DrawSections(c, frame, style, look, glow, tint, bright);
        }

        static Color ColorOf(SpanCurve c, int i, int j, LineStyle look, float glow, Color tint)
        {
            // A rail is steel; aimed at, it shows its shape like a prototype.
            Color color;
            if (look == LineStyle.Rail && glow <= 0)
                color = Steel;
            else
            {
                double k = 0;
                for (int n = i; n <= j; n++)
                    k = Math.Max(k, c.Curvature[n]);
                color = Draw.ByShape(k, c.TwistRate);
            }
            return glow > 0 ? Color.Lerp(color, tint, glow) : color;
        }

        // The end face texture (Draw.Face) every SectionModules modules, not on the ends (the ports are there): on the
        // module boundaries of a laid span (TrackLayout: whole modules, all stretched alike); while laying (Preview)
        // as far apart from the start, so they stand still while the free end moves.
        static void DrawSections(SpanCurve c, MatrixD frame, TrackStyle style, LineStyle look, float glow, Color tint,
            float bright)
        {
            int modules = Math.Max(1, (int)Math.Round(c.Length / TrackLayout.Module));
            double step = SectionModules * (look == LineStyle.Preview ? TrackLayout.Module : c.Length / modules);
            for (double s = step; s < c.Length - step * 0.5; s += step)
            {
                Vector3D p = Vector3D.Transform(c.PosAt(s), frame);
                double distance = Vector3D.Distance(p, Draw.Camera);
                if (distance > DetailRange)
                    continue;
                int i = c.Index(s);
                Vector3D tan = c.TanAt(s), up = c.UpAt(s);
                Vector3D right = Vector3D.TransformNormal(Vector3D.Cross(tan, up), frame);
                Vector3D upward = Vector3D.TransformNormal(up, frame);
                Color color = ColorOf(c, i, i + 1, look, glow, tint) * 0.85f;
                bool through = distance < SectionThroughRange;
                Draw.Face(style, -1, true, p, right, upward, color * FillAlpha, 1, through, bright);
                Draw.Face(style, -1, false, p, right, upward, color, 1, through, bright);
            }
        }

        static void DrawCross(SpanCurve c, MatrixD frame, int i, Color color)
        {
            Vector3D p = Vector3D.Transform(c.Pos[i], frame);
            Vector3D up = Vector3D.TransformNormal(c.Up[i], frame);
            Vector3D side = Vector3D.TransformNormal(Vector3D.Cross(c.Tan[i], c.Up[i]), frame);
            Draw.Line(p - (up + side) * 0.5, p + (up + side) * 0.5, color, 0.1f, true);
            Draw.Line(p - (up - side) * 0.5, p + (up - side) * 0.5, color, 0.1f, true);
        }
    }
}
