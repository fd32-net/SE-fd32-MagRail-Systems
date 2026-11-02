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
        public double TwistRate;    // rad per metre: the up turning about the rail, even over the span
        public BoundingSphereD Bounds;
        // Cut: only a window of a longer curve is sampled (Build: maxLength around focus); FullLength is the whole
        // curve's (estimated), Length the window's, Offset where the window starts on the whole curve (S counts from
        // there).
        public bool Cut;
        public double FullLength, Offset;

        // a, b: port matrices in the span's frame (Translation = rail point, Forward = outwards, Up).
        // maxLength: of a longer curve sample only this much, centred on its point nearest to focus (the Rail Linker's
        // preview of a very long span: the part near the player).
        public static SpanCurve Build(MatrixD a, MatrixD b, double maxLength = double.PositiveInfinity,
            Vector3D focus = default(Vector3D))
        {
            return Build(a.Translation, a.Forward, a.Up, b.Translation, -b.Forward, b.Up, maxLength, focus, false);
        }

        // The curve through a bundle's middle (SpanRecord.InBundle): as Build, its twist eased (Spread). A rail beside
        // it turns round it as its up does; turning at the ends it would leave its port askew.
        public static SpanCurve Bundle(MatrixD a, MatrixD b, double maxLength = double.PositiveInfinity,
            Vector3D focus = default(Vector3D))
        {
            return Build(a.Translation, a.Forward, a.Up, b.Translation, -b.Forward, b.Up, maxLength, focus, true);
        }

        // A span laid beside another one (SpanRecord.BesideRight): the other span's ports are a and b turned and moved
        // back (Guide), its curve is built and moved across and turned again (Across): it ends exactly on a and b.
        public static SpanCurve Beside(MatrixD a, MatrixD b, double right, double up, double roll)
        {
            return Bundle(Guide(a, right, up, -roll, 1), Guide(b, right, up, roll, -1)).Across(right, up, roll);
        }

        // The other span's port from this span's: its up turned back about the port's way out (at b the curve runs into
        // the port: the turn about the curve is the other way round there), then moved back across (side -1 at b: the
        // curve's right is the port's left).
        static MatrixD Guide(MatrixD port, double right, double up, double roll, double side)
        {
            Vector3D u = Turn(port.Up, port.Forward, roll);
            Vector3D r = Vector3D.Cross(port.Forward, u);
            return MatrixD.CreateWorld(port.Translation - r * (right * side) - u * up, port.Forward, u);
        }

        // v (square to the axis) turned by angle about the axis, right-handed.
        public static Vector3D Turn(Vector3D v, Vector3D axis, double angle)
        {
            return v * Math.Cos(angle) + Vector3D.Cross(axis, v) * Math.Sin(angle);
        }

        // The same curve moved across in every cross-section: right along Tan x Up, up along Up; its up this curve's
        // turned by roll about the rail (a rail laid beside this one: every cross-section the same).
        public SpanCurve Across(double right, double up, double roll)
        {
            Vector3D[] offset = new Vector3D[Pos.Count];
            for (int i = 0; i < offset.Length; i++)
                offset[i] = Vector3D.Cross(Tan[i], Up[i]) * right + Up[i] * up;
            SpanCurve c = Moved(offset);
            for (int i = 0; i < Pos.Count; i++)
                c.Up.Add(Reject(Turn(Up[i], Tan[i], roll), c.Tan[i]));
            c.TwistRate = TwistRate;
            c.Measure();
            return c;
        }

        // Moved across on the inner side of a turn tighter than the distance across, the curve folds back on itself.
        public bool Folds(SpanCurve from)
        {
            for (int i = 0; i + 1 < Pos.Count; i++)
                if (Vector3D.Dot(Pos[i + 1] - Pos[i], from.Tan[i]) <= 0)
                    return true;
            return false;
        }

        // Rail inside a block from one of its ports to another (same port matrices): it starts inwards
        // and leaves through the second port.
        public static SpanCurve Lane(MatrixD from, MatrixD to)
        {
            return Build(from.Translation, -from.Forward, from.Up, to.Translation, to.Forward, to.Up);
        }

        // From p0 leaving along t0 to p3 arriving along t3; eased: the twist (Spread).
        static SpanCurve Build(Vector3D p0, Vector3D t0, Vector3D up0, Vector3D p3, Vector3D t3, Vector3D up3,
            double maxLength = double.PositiveInfinity, Vector3D focus = default(Vector3D), bool eased = false)
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
            if (estimate > maxLength)
                return Window(p0, c1, c2, p3, up0, up3, maxLength, focus, eased);
            int n = Math.Max(8, (int)Math.Ceiling(estimate / Step));
            c.Sample(p0, c1, c2, p3, 0, 1, n, t0);
            c.Finish(up0, up3, eased);
            return c;
        }

        // Samples n + 1 points evenly in t from ta to tb (S from the first).
        void Sample(Vector3D p0, Vector3D c1, Vector3D c2, Vector3D p3, double ta, double tb, int n, Vector3D t0)
        {
            SpanCurve c = this;
            Vector3D chordDir = p3 - p0;
            chordDir = chordDir.LengthSquared() > 1e-8 ? Vector3D.Normalize(chordDir) : t0;
            for (int i = 0; i <= n; i++)
            {
                double t = ta + (tb - ta) * i / n;
                Vector3D d1 = Derivative(p0, c1, c2, p3, t);
                Vector3D d2 = SecondDerivative(p0, c1, c2, p3, t);
                double speed = d1.Length();
                Vector3D p = Point(p0, c1, c2, p3, t);
                c.Pos.Add(p);
                c.Tan.Add(speed > 1e-8 ? d1 / speed : chordDir);
                c.Curvature.Add(speed > 1e-8 ? Vector3D.Cross(d1, d2).Length() / (speed * speed * speed) : 0);
                c.S.Add(i == 0 ? 0 : c.S[i - 1] + Vector3D.Distance(c.Pos[i - 1], p));
            }
        }

        // A window of maxLength of the whole Bezier around its point nearest to focus. A coarse pass over the whole
        // curve gives arc length against t, the nearest point, the transported up and the twist (spread over the whole
        // curve, as Finish does); the window is then sampled as usual.
        static SpanCurve Window(Vector3D p0, Vector3D c1, Vector3D c2, Vector3D p3, Vector3D up0, Vector3D up3,
            double maxLength, Vector3D focus, bool eased)
        {
            const int Coarse = 1024;
            double[] s = new double[Coarse + 1];
            Vector3D[] ups = new Vector3D[Coarse + 1];
            Vector3D tan = Vector3D.Normalize(Derivative(p0, c1, c2, p3, 0)), last = p0;
            Vector3D up = Reject(up0, tan);
            ups[0] = up;
            int nearest = 0;
            double nearestDistance = Vector3D.DistanceSquared(p0, focus);
            for (int k = 1; k <= Coarse; k++)
            {
                double t = k / (double)Coarse;
                Vector3D p = Point(p0, c1, c2, p3, t), d = Derivative(p0, c1, c2, p3, t);
                if (d.LengthSquared() > 1e-16)
                    tan = Vector3D.Normalize(d);
                up = Reject(up, tan);
                ups[k] = up;
                s[k] = s[k - 1] + Vector3D.Distance(last, p);
                last = p;
                double distance = Vector3D.DistanceSquared(p, focus);
                if (distance < nearestDistance)
                {
                    nearestDistance = distance;
                    nearest = k;
                }
            }
            double total = s[Coarse];
            Vector3D target = Reject(up3, tan);
            double twist = Math.Atan2(Vector3D.Dot(Vector3D.Cross(up, target), tan), Vector3D.Dot(up, target));

            double from = Math.Max(0, Math.Min(s[nearest] - maxLength * 0.5, total - maxLength));
            int ka, kb;
            double ta = TAt(s, from, out ka), tb = TAt(s, Math.Min(total, from + maxLength), out kb);
            SpanCurve c = new SpanCurve();
            c.Cut = true;
            c.FullLength = total;
            c.Offset = from;
            c.Sample(p0, c1, c2, p3, ta, tb, Math.Max(8, (int)Math.Ceiling(maxLength / Step)), tan);
            c.Finish(ups[ka], up3, eased, twist);
            return c;
        }

        // t at arc length x of a coarse table s (evenly in t), k: the table entry before it.
        static double TAt(double[] s, double x, out int k)
        {
            int last = s.Length - 1;
            k = 0;
            while (k < last - 1 && s[k + 1] <= x)
                k++;
            double d = s[k + 1] - s[k];
            double f = d > 1e-9 ? MathHelper.Clamp((x - s[k]) / d, 0, 1) : 0;
            return (k + f) / last;
        }

        // The same curve with every point moved by offset[i] (TerrainLift), the ups found again along it (Finish).
        public SpanCurve Shifted(Vector3D[] offset)
        {
            SpanCurve c = Moved(offset);
            c.Finish(Up[0], Up[Pos.Count - 1], false);
            return c;
        }

        // Points moved by offset[i], tangents and curvature from them, the ends keep theirs (the lift fades out there; a
        // rail beside leaves its ports the same way); no ups yet.
        SpanCurve Moved(Vector3D[] offset)
        {
            SpanCurve c = new SpanCurve();
            int n = Pos.Count - 1;
            for (int i = 0; i <= n; i++)
            {
                c.Pos.Add(Pos[i] + offset[i]);
                c.S.Add(i == 0 ? 0 : c.S[i - 1] + Vector3D.Distance(c.Pos[i - 1], c.Pos[i]));
            }
            for (int i = 0; i <= n; i++)
            {
                Vector3D d = i == 0 || i == n ? Vector3D.Zero : c.Pos[i + 1] - c.Pos[i - 1];
                c.Tan.Add(d.LengthSquared() > 1e-12 ? Vector3D.Normalize(d) : Tan[i]);
            }
            for (int i = 0; i <= n; i++)
            {
                int a = Math.Max(0, i - 1), b = Math.Min(n, i + 1);
                double ds = c.S[b] - c.S[a];
                double angle = Math.Acos(MathHelper.Clamp(Vector3D.Dot(c.Tan[a], c.Tan[b]), -1.0, 1.0));
                c.Curvature.Add(ds > 1e-9 ? angle / ds : 0);
            }
            c.Cut = Cut;
            c.FullLength = Cut ? FullLength : c.S[n];
            c.Offset = Offset;
            c.Length = c.S[n];
            return c;
        }

        // From Pos, Tan, S and Curvature: Length, the ups, MinRadius, Bounds. twist: given (a window of a longer curve:
        // the whole curve's, up0 is the untwisted up at the window's start; up3 is unused), not found from up3.
        void Finish(Vector3D up0, Vector3D up3, bool eased, double twist = double.NaN)
        {
            SpanCurve c = this;
            int n = Pos.Count - 1;
            c.Length = c.S[n];
            if (!c.Cut)
                c.FullLength = c.Length;

            // Parallel transport of A's up along the tangents, then the remaining twist to B's up
            // spread over the length (Spread).
            Vector3D up = Reject(up0, c.Tan[0]);
            for (int i = 0; i <= n; i++)
            {
                if (i > 0)
                    up = Reject(up, c.Tan[i]);
                c.Up.Add(up);
            }
            // Found here: over this curve; given: over the whole one, the window from Offset.
            double full = c.Length, start = 0;
            if (double.IsNaN(twist))
            {
                Vector3D target = Reject(up3, c.Tan[n]);
                twist = Math.Atan2(Vector3D.Dot(Vector3D.Cross(c.Up[n], target), c.Tan[n]), Vector3D.Dot(c.Up[n], target));
            }
            else
            {
                full = c.FullLength;
                start = c.Offset;
            }
            c.TwistRate = full > 1e-6 ? Math.Abs(twist) / full * (eased ? 1.5 : 1) : 0;
            for (int i = 0; i <= n; i++)
            {
                double angle = full > 1e-6 ? twist * Spread((start + c.S[i]) / full, eased) : 0;
                Vector3D u = c.Up[i], k = c.Tan[i];
                c.Up[i] = Vector3D.Normalize(u * Math.Cos(angle) + Vector3D.Cross(k, u) * Math.Sin(angle));
            }
            c.Measure();
        }

        // How much of the twist is done at x (0..1 of the length): evenly, or eased (smoothstep: still at both ends,
        // 1.5 times the even rate midway).
        static double Spread(double x, bool eased)
        {
            x = MathHelper.Clamp(x, 0, 1);
            return eased ? x * x * (3 - 2 * x) : x;
        }

        // MinRadius and Bounds from Pos and Curvature.
        void Measure()
        {
            SpanCurve c = this;
            int n = Pos.Count - 1;
            BoundingBoxD box = BoundingBoxD.CreateInvalid();
            for (int i = 0; i <= n; i++)
            {
                box.Include(c.Pos[i]);
                if (c.Curvature[i] > 1e-6)
                    c.MinRadius = Math.Min(c.MinRadius, 1 / c.Curvature[i]);
            }
            c.Bounds = new BoundingSphereD(box.Center, box.HalfExtents.Length());
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

        // The last sample at most `metres` of arc after sample i: a point that far from something is no nearer to it
        // anywhere up to there (S sums the chords). Skipping by sample count is not safe: samples are even in t, not
        // in length, and over tens of km the difference adds up to many metres.
        public int Ahead(int i, double metres)
        {
            return Math.Max(i, Index(S[i] + metres));
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
