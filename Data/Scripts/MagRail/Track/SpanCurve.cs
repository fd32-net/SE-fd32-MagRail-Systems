using System;
using System.Collections.Generic;
using VRageMath;

namespace MagRail
{
    // Geometry of a span: cubic Bezier from port A to port B with "arc" handles (a circular arc where
    // the two port directions allow one), sampled every ~Step metres: point, tangent, up, curvature.
    // "Up" goes from A's up to B's up by parallel transport plus an even twist, so it never flips.
    // Points are in the span's frame: the grid for a span on one grid, the world otherwise.
    public class SpanCurve
    {
        public const double Step = 0.25;

        public readonly List<Vector3D> Pos = new List<Vector3D>();
        public readonly List<Vector3D> Tan = new List<Vector3D>();
        public readonly List<Vector3D> Up = new List<Vector3D>();
        public readonly List<double> S = new List<double>();          // arc length from A
        public readonly List<double> Curvature = new List<double>();  // 1 / radius
        public double Length;
        public double MinRadius = double.PositiveInfinity;
        public BoundingSphereD Bounds;

        // a, b: port matrices in the span's frame (Translation = rail point, Forward = outwards, Up).
        public static SpanCurve Build(MatrixD a, MatrixD b)
        {
            return Build(a.Translation, a.Forward, a.Up, b.Translation, -b.Forward, b.Up);
        }

        // Rail inside a block from one of its ports to another (same port matrices): it starts inwards
        // and leaves through the second port.
        public static SpanCurve Lane(MatrixD from, MatrixD to)
        {
            return Build(from.Translation, -from.Forward, from.Up, to.Translation, to.Forward, to.Up);
        }

        // From p0 leaving along t0 to p3 arriving along t3.
        static SpanCurve Build(Vector3D p0, Vector3D t0, Vector3D up0, Vector3D p3, Vector3D t3, Vector3D up3)
        {
            SpanCurve c = new SpanCurve();
            t0 = Vector3D.Normalize(t0);
            t3 = Vector3D.Normalize(t3);
            double h = ArcHandle(Vector3D.Distance(p0, p3), t0, t3);
            Vector3D c1 = p0 + t0 * h, c2 = p3 - t3 * h;

            double estimate = 0;
            Vector3D last = p0;
            for (int i = 1; i <= 32; i++)
            {
                Vector3D next = Point(p0, c1, c2, p3, i / 32.0);
                estimate += Vector3D.Distance(last, next);
                last = next;
            }
            int n = Math.Max(8, (int)Math.Ceiling(estimate / Step));

            Vector3D chordDir = p3 - p0;
            chordDir = chordDir.LengthSquared() > 1e-8 ? Vector3D.Normalize(chordDir) : t0;
            for (int i = 0; i <= n; i++)
            {
                double t = i / (double)n;
                Vector3D d1 = Derivative(p0, c1, c2, p3, t);
                Vector3D d2 = SecondDerivative(p0, c1, c2, p3, t);
                double speed = d1.Length();
                Vector3D p = Point(p0, c1, c2, p3, t);
                c.Pos.Add(p);
                c.Tan.Add(speed > 1e-8 ? d1 / speed : chordDir);
                c.Curvature.Add(speed > 1e-8 ? Vector3D.Cross(d1, d2).Length() / (speed * speed * speed) : 0);
                c.S.Add(i == 0 ? 0 : c.S[i - 1] + Vector3D.Distance(c.Pos[i - 1], p));
            }
            c.Length = c.S[n];

            // Parallel transport of A's up along the tangents, then the remaining twist to B's up
            // spread evenly over the length.
            Vector3D up = Reject(up0, c.Tan[0]);
            for (int i = 0; i <= n; i++)
            {
                if (i > 0)
                    up = Reject(up, c.Tan[i]);
                c.Up.Add(up);
            }
            Vector3D target = Reject(up3, c.Tan[n]);
            double twist = Math.Atan2(Vector3D.Dot(Vector3D.Cross(c.Up[n], target), c.Tan[n]), Vector3D.Dot(c.Up[n], target));
            for (int i = 0; i <= n; i++)
            {
                double angle = c.Length > 1e-6 ? twist * c.S[i] / c.Length : 0;
                Vector3D u = c.Up[i], k = c.Tan[i];
                c.Up[i] = Vector3D.Normalize(u * Math.Cos(angle) + Vector3D.Cross(k, u) * Math.Sin(angle));
            }

            BoundingBoxD box = BoundingBoxD.CreateInvalid();
            for (int i = 0; i <= n; i++)
            {
                box.Include(c.Pos[i]);
                if (c.Curvature[i] > 1e-6)
                    c.MinRadius = Math.Min(c.MinRadius, 1 / c.Curvature[i]);
            }
            c.Bounds = new BoundingSphereD(box.Center, box.HalfExtents.Length());
            return c;
        }

        // ---- sampling by arc length (linear between the table's samples) ----

        // Sample i with S[i] <= s < S[i + 1] (the last segment for s >= Length).
        public int Index(double s)
        {
            int lo = 0, hi = S.Count - 2;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) / 2;
                if (S[mid] <= s)
                    lo = mid;
                else
                    hi = mid - 1;
            }
            return lo;
        }

        public double Fraction(int i, double s)
        {
            double d = S[i + 1] - S[i];
            return d > 1e-9 ? MathHelper.Clamp((s - S[i]) / d, 0, 1) : 0;
        }

        public Vector3D PosAt(double s)
        {
            int i = Index(s);
            return Vector3D.Lerp(Pos[i], Pos[i + 1], Fraction(i, s));
        }

        public Vector3D TanAt(double s)
        {
            int i = Index(s);
            return Vector3D.Normalize(Vector3D.Lerp(Tan[i], Tan[i + 1], Fraction(i, s)));
        }

        public Vector3D UpAt(double s)
        {
            int i = Index(s);
            return Vector3D.Normalize(Vector3D.Lerp(Up[i], Up[i + 1], Fraction(i, s)));
        }

        // Handle length that makes the Bezier follow a circular arc between the two tangents:
        // (2/3) tan(angle/4) / sin(angle/2) of the chord (1/3 for a straight run, ~0.39 at 90 degrees).
        static double ArcHandle(double chord, Vector3D t0, Vector3D t3)
        {
            double angle = Math.Acos(MathHelper.Clamp(Vector3D.Dot(t0, t3), -1.0, 1.0));
            double ratio = 1.0 / 3.0;
            double sinHalf = Math.Sin(angle * 0.5);
            if (sinHalf > 0.0001)
                ratio = (2.0 / 3.0) * Math.Tan(angle * 0.25) / sinHalf;
            return Math.Max(0.5, chord * MathHelper.Clamp(ratio, 0.30, 0.62));
        }

        static Vector3D Reject(Vector3D v, Vector3D axis)
        {
            Vector3D r = v - axis * Vector3D.Dot(v, axis);
            if (r.LengthSquared() < 1e-10)
                r = Vector3D.CalculatePerpendicularVector(axis);
            return Vector3D.Normalize(r);
        }

        static Vector3D Point(Vector3D p0, Vector3D p1, Vector3D p2, Vector3D p3, double t)
        {
            double u = 1 - t;
            return u * u * u * p0 + 3 * u * u * t * p1 + 3 * u * t * t * p2 + t * t * t * p3;
        }

        static Vector3D Derivative(Vector3D p0, Vector3D p1, Vector3D p2, Vector3D p3, double t)
        {
            double u = 1 - t;
            return 3 * (u * u * (p1 - p0) + 2 * u * t * (p2 - p1) + t * t * (p3 - p2));
        }

        static Vector3D SecondDerivative(Vector3D p0, Vector3D p1, Vector3D p2, Vector3D p3, double t)
        {
            return 6 * ((1 - t) * (p2 - 2 * p1 + p0) + t * (p3 - 2 * p2 + p1));
        }
    }
}
