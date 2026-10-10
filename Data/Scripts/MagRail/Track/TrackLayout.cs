using System;
using System.Collections.Generic;
using VRageMath;

namespace MagRail
{
    // How a span in rail mode is laid from track piece models (ModelSource/blocks/TrackPieces.py):
    // straight pieces along chords of the curve, as long as the curve allows (short in tight curves, so the
    // bends at the joints stay small enough for the collars to hide), stretched together to fill
    // the span exactly; and for collision, a run of unscaled 2.5 m pieces. Matrices are in the span's
    // frame (see SpanCurve): origin = rail point in the middle of the piece, Forward along the rail, Up.
    public class TrackLayout
    {
        // Piece model lengths, m (model MagRail_Track_<mm>).
        public static readonly double[] Lengths = { 0.625, 1.25, 2.5, 5, 10, 20, 40 };
        public const double CollisionLength = 2.5;
        // A chord stays this close to the curve (sag L^2 / 8R), m.
        const double SagTolerance = 0.04;
        // The rail turns at most this much over one piece, both its direction (= the bend at a joint) and its
        // up (twist): the collars of the piece models hide about 4 degrees between neighbours. Tighter than
        // about 12 m radius even the shortest piece bends more.
        static readonly double MaxTurnCos = Math.Cos(MathHelper.ToRadians(3));

        public readonly SpanCurve Curve;
        public readonly List<int> Models = new List<int>();             // index into Lengths
        public readonly List<MatrixD> Pieces = new List<MatrixD>();     // stretched along Forward
        public readonly List<MatrixD> Collision = new List<MatrixD>();

        TrackLayout(SpanCurve curve)
        {
            Curve = curve;
        }

        public static TrackLayout Build(SpanCurve c)
        {
            TrackLayout layout = new TrackLayout(c);
            int n = c.Pos.Count;
            double[] allowed = new double[n];
            for (int i = 0; i < n; i++)
                allowed[i] = c.Curvature[i] > 1e-9 ? Math.Sqrt(8 * SagTolerance / c.Curvature[i]) : double.PositiveInfinity;

            // Longest piece that fits from s on, while more than half the shortest piece is left; the
            // pieces are stretched to the span length afterwards (0.75-1.5 on short spans, ~1 on long).
            double length = c.Length, s = 0, half = Lengths[0] / 2;
            while (length - s > half || layout.Models.Count == 0)
            {
                int pick = 0;
                for (int m = Lengths.Length - 1; m > 0; m--)
                {
                    if (Lengths[m] <= length - s + half && Fits(c, allowed, s, Lengths[m]))
                    {
                        pick = m;
                        break;
                    }
                }
                layout.Models.Add(pick);
                s += Lengths[pick];
            }
            double stretch = length / s, a = 0;
            for (int i = 0; i < layout.Models.Count; i++)
            {
                double l = Lengths[layout.Models[i]];
                layout.Pieces.Add(Chord(c, a, a + l * stretch, l));
                a += l * stretch;
            }

            int count = Math.Max(1, (int)Math.Ceiling(length / CollisionLength - 1e-6));
            for (int i = 0; i < count; i++)
                layout.Collision.Add(Chord(c, length * i / count, length * (i + 1) / count, 0));
            return layout;
        }

        static bool Fits(SpanCurve c, double[] allowed, double s0, double length)
        {
            double s1 = Math.Min(s0 + length, c.Length);
            for (int i = c.Index(s0), last = Math.Min(c.Index(s1) + 1, c.Pos.Count - 1); i <= last; i++)
                if (allowed[i] < length)
                    return false;
            return Vector3D.Dot(c.TanAt(s0), c.TanAt(s1)) >= MaxTurnCos && Vector3D.Dot(c.UpAt(s0), c.UpAt(s1)) >= MaxTurnCos;
        }

        // Piece from arc length a to b: on the chord, up = the mean of the ends' ups; modelLength > 0
        // stretches the model to the chord.
        static MatrixD Chord(SpanCurve c, double a, double b, double modelLength)
        {
            Vector3D p0 = c.PosAt(a), p1 = c.PosAt(b);
            Vector3D forward = p1 - p0;
            double chord = forward.Length();
            forward = chord > 1e-6 ? forward / chord : c.Tan[c.Index(a)];
            Vector3D up = c.UpAt(a) + c.UpAt(b);
            up -= forward * Vector3D.Dot(up, forward);
            up = up.LengthSquared() > 1e-10 ? Vector3D.Normalize(up) : Vector3D.CalculatePerpendicularVector(forward);
            MatrixD m = MatrixD.CreateWorld((p0 + p1) * 0.5, forward, up);
            if (modelLength > 0)
                m.Forward = forward * (chord / modelLength);
            return m;
        }
    }
}
