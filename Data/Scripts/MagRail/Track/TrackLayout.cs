using System;
using System.Collections.Generic;
using VRageMath;

namespace MagRail
{
    // How a span in rail mode is laid from track piece models (ModelSource/blocks/TrackPieces.py):
    // straight pieces along chords of the curve, as long as the curve allows, stretched together to fill
    // the span exactly; and for collision, a run of unscaled 2.5 m pieces. Matrices are in the span's
    // frame (see SpanCurve): origin = rail point in the middle of the piece, Forward along the rail, Up.
    public class TrackLayout
    {
        // Piece model lengths, m (model MagRail_Track_<cm>).
        public static readonly double[] Lengths = { 2.5, 5, 10, 20, 40 };
        public const double CollisionLength = 2.5;
        // A chord stays this close to the curve (sag L^2 / 8R), m.
        const double SagTolerance = 0.04;
        // The rail's up turns at most this much over one piece (twist, vertical bends): the collars of the
        // piece models hide about 4 degrees between neighbours.
        static readonly double MaxUpTurnCos = Math.Cos(MathHelper.ToRadians(3));

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
            for (int i = Index(c, s0), last = Math.Min(Index(c, s1) + 1, c.Pos.Count - 1); i <= last; i++)
                if (allowed[i] < length)
                    return false;
            return Vector3D.Dot(UpAt(c, s0), UpAt(c, s1)) >= MaxUpTurnCos;
        }

        // Piece from arc length a to b: on the chord, up = the mean of the ends' ups; modelLength > 0
        // stretches the model to the chord.
        static MatrixD Chord(SpanCurve c, double a, double b, double modelLength)
        {
            Vector3D p0 = PosAt(c, a), p1 = PosAt(c, b);
            Vector3D forward = p1 - p0;
            double chord = forward.Length();
            forward = chord > 1e-6 ? forward / chord : c.Tan[Index(c, a)];
            Vector3D up = UpAt(c, a) + UpAt(c, b);
            up -= forward * Vector3D.Dot(up, forward);
            up = up.LengthSquared() > 1e-10 ? Vector3D.Normalize(up) : Vector3D.CalculatePerpendicularVector(forward);
            MatrixD m = MatrixD.CreateWorld((p0 + p1) * 0.5, forward, up);
            if (modelLength > 0)
                m.Forward = forward * (chord / modelLength);
            return m;
        }

        // Sample i with S[i] <= s < S[i + 1] (the last segment for s >= Length).
        static int Index(SpanCurve c, double s)
        {
            int lo = 0, hi = c.S.Count - 2;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) / 2;
                if (c.S[mid] <= s)
                    lo = mid;
                else
                    hi = mid - 1;
            }
            return lo;
        }

        static double Fraction(SpanCurve c, int i, double s)
        {
            double d = c.S[i + 1] - c.S[i];
            return d > 1e-9 ? MathHelper.Clamp((s - c.S[i]) / d, 0, 1) : 0;
        }

        static Vector3D PosAt(SpanCurve c, double s)
        {
            int i = Index(c, s);
            return Vector3D.Lerp(c.Pos[i], c.Pos[i + 1], Fraction(c, i, s));
        }

        static Vector3D UpAt(SpanCurve c, double s)
        {
            int i = Index(c, s);
            return Vector3D.Normalize(Vector3D.Lerp(c.Up[i], c.Up[i + 1], Fraction(c, i, s)));
        }
    }
}
