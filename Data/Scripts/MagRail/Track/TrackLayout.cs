using System;
using System.Collections.Generic;
using VRageMath;

namespace MagRail
{
    // How a span in rail mode is laid from track piece models (ModelSource/blocks/TrackPieces.py, TrackBends.py).
    // The rail is a row of 0.625 m modules (the span length rounded to whole modules, all stretched alike), so
    // the copper plates and textures of the models run on across the joints. Its centre line is a polyline
    // through the curve: vertices in the middles of modules, legs as long as the curve allows. Every vertex
    // module is a bend model that turns the rail from one leg to the next (also from the port's direction into
    // the first leg and out of the last), the modules between are straight pieces. So the rail has no kinks
    // beyond the bends' rounding (a quarter step); where a vertex is too steep for a bend (tight curves, fast
    // twist), a joint with pyramid ends shows a chamfer instead. For collision, a run of unscaled 2.5 m pieces.
    // Matrices are in the span's frame (see SpanCurve): origin = rail point in the middle of the piece's chord,
    // Forward along the chord, Up.
    public class TrackLayout
    {
        public const double Module = 0.625;
        public const double CollisionLength = 2.5;
        // Straight pieces, in modules (model MagRail_Track_<mm>); a straight run takes the longest first.
        static readonly int[] StraightModules = { 64, 32, 16, 8, 4, 2, 1 };
        // Bend models turn by yaw and pitch in steps of BendStep, at most BendSteps each (TrackBends.py).
        const int BendSteps = 6;
        static readonly double BendStep = MathHelper.ToRadians(0.5);
        // A leg stays this close to the curve (sag L^2 / 8R), m.
        const double SagTolerance = 0.04;
        // Over one leg the curve turns at most as much as a bend can take (the bend at a vertex is about half
        // of each neighbouring leg's turn); tighter than about 12 m radius even one module turns more. Twist (the
        // up turning about the rail) has no bend models: it stays small enough to see no step at a joint.
        public static readonly double MaxBend = BendSteps * BendStep;
        public static readonly double MaxTwist = MathHelper.ToRadians(1);
        // Where a vertex turns more than a bend model can take, or the up twists more than this between the
        // neighbouring legs, a joint (MagRail_Track_Joint) takes the bend's place: its pyramid ends turn the
        // kink into a chamfer.
        static readonly double JointTwist = MathHelper.ToRadians(2.5);
        const string JointModel = "MagRail_Track_Joint";
        static readonly double MaxTurnCos = Math.Cos(MaxBend);

        public readonly SpanCurve Curve;
        public readonly List<string> Models = new List<string>();       // model names
        public readonly List<MatrixD> Pieces = new List<MatrixD>();     // stretched along Forward
        public readonly List<MatrixD> Collision = new List<MatrixD>();

        TrackLayout(SpanCurve curve)
        {
            Curve = curve;
        }

        public static string StraightModel(int modules)
        {
            return "MagRail_Track_" + (modules * 625);
        }

        static string BendModel(int yaw, int pitch)
        {
            return "MagRail_Bend_Y" + yaw + "_P" + (pitch < 0 ? "m" : "") + Math.Abs(pitch);
        }

        public static TrackLayout Build(SpanCurve c)
        {
            TrackLayout layout = new TrackLayout(c);
            double length = c.Length;
            int n = Math.Max(1, (int)Math.Round(length / Module));
            double m = length / n;      // stretched module

            if (n == 1)
                layout.Add(StraightModel(1), Chord(c.PosAt(0), c.PosAt(length), c.UpAt(0) + c.UpAt(length), m));
            else
                layout.Rail(n, m);

            int count = Math.Max(1, (int)Math.Ceiling(length / CollisionLength - 1e-6));
            for (int i = 0; i < count; i++)
            {
                double a = length * i / count, b = length * (i + 1) / count;
                layout.Collision.Add(Chord(c.PosAt(a), c.PosAt(b), c.UpAt(a) + c.UpAt(b), 0));
            }
            return layout;
        }

        void Rail(int n, double m)
        {
            SpanCurve c = Curve;
            int samples = c.Pos.Count;
            double[] allowed = new double[samples];
            for (int i = 0; i < samples; i++)
                allowed[i] = c.Curvature[i] > 1e-9 ? Math.Sqrt(8 * SagTolerance / c.Curvature[i]) : double.PositiveInfinity;

            // Vertices at module middles (in modules): the first and last module are bends too, so the rail
            // leaves and enters the ports along their direction. Longest leg that fits (binary search).
            List<double> at = new List<double> { 0.5 };
            double last = n - 0.5;
            while (at[at.Count - 1] < last)
            {
                double p = at[at.Count - 1];
                int lo = 1, hi = (int)Math.Round(last - p);
                while (lo < hi)
                {
                    int mid = (lo + hi + 1) / 2;
                    if (Fits(c, allowed, p * m, (p + mid) * m))
                        lo = mid;
                    else
                        hi = mid - 1;
                }
                at.Add(p + lo);
            }

            // The end vertices lie on the ports' lines, half a module in; the others on the curve.
            int v = at.Count;
            Vector3D[] pos = new Vector3D[v];
            for (int i = 0; i < v; i++)
                pos[i] = c.PosAt(at[i] * m);
            Vector3D startTan = c.TanAt(0), endTan = c.TanAt(c.Length);
            pos[0] = c.PosAt(0) + startTan * (m / 2);
            pos[v - 1] = c.PosAt(c.Length) - endTan * (m / 2);

            // Legs: direction and up (mean of the curve's ups at its ends).
            Vector3D[] dir = new Vector3D[v - 1], up = new Vector3D[v - 1];
            for (int i = 0; i < v - 1; i++)
            {
                dir[i] = Vector3D.Normalize(pos[i + 1] - pos[i]);
                up[i] = c.UpAt(at[i] * m) + c.UpAt(at[i + 1] * m);
            }

            for (int i = 0; i < v; i++)
            {
                Vector3D din = i == 0 ? startTan : dir[i - 1];
                Vector3D dout = i == v - 1 ? endTan : dir[i];
                Vector3D bendUp = (i == 0 ? c.UpAt(0) * 2 : up[i - 1]) + (i == v - 1 ? c.UpAt(c.Length) * 2 : up[i]);
                // Twist between the pieces on both sides: the legs' halves (a port side counts as nothing).
                double twist = c.TwistRate * m * ((i > 0 ? at[i] - at[i - 1] : 0) + (i < v - 1 ? at[i + 1] - at[i] : 0)) / 2;
                AddBend(pos[i] - din * (m / 2), pos[i] + dout * (m / 2), din, dout, bendUp, twist);
                if (i == v - 1)
                    break;
                // Straight run between this vertex's bend and the next one's.
                int modules = (int)Math.Round(at[i + 1] - at[i]) - 1;
                Vector3D a = pos[i] + dir[i] * (m / 2);
                while (modules > 0)
                {
                    int take = 1;
                    for (int j = 0; j < StraightModules.Length; j++)
                        if (StraightModules[j] <= modules)
                        {
                            take = StraightModules[j];
                            break;
                        }
                    Vector3D b = a + dir[i] * (take * m);
                    Add(StraightModel(take), Chord(a, b, up[i], take * Module));
                    a = b;
                    modules -= take;
                }
            }
        }

        // Bend module from p0 (direction din) to p1 (direction dout): placed by its chord, the nearest bend model
        // (a left bend is the right bend model turned around its up), or a joint where the kink is too steep.
        void AddBend(Vector3D p0, Vector3D p1, Vector3D din, Vector3D dout, Vector3D up, double twist)
        {
            MatrixD m = Chord(p0, p1, up, Module);
            Vector3D right = m.Right, u = m.Up;
            double yaw = Math.Asin(MathHelper.Clamp(Vector3D.Dot(dout, right), -1, 1)) - Math.Asin(MathHelper.Clamp(Vector3D.Dot(din, right), -1, 1));
            double pitch = Math.Asin(MathHelper.Clamp(Vector3D.Dot(dout, u), -1, 1)) - Math.Asin(MathHelper.Clamp(Vector3D.Dot(din, u), -1, 1));
            int ys = (int)Math.Round(yaw / BendStep), ps = (int)Math.Round(pitch / BendStep);
            if (Math.Abs(ys) > BendSteps || Math.Abs(ps) > BendSteps || twist > JointTwist)
            {
                Add(JointModel, m);
                return;
            }
            if (ys == 0 && ps == 0)
            {
                Add(StraightModel(1), m);
                return;
            }
            if (ys < 0)
            {
                m.Forward = -m.Forward;
                m.Right = -m.Right;
            }
            Add(BendModel(Math.Abs(ys), ps), m);
        }

        void Add(string model, MatrixD m)
        {
            Models.Add(model);
            Pieces.Add(m);
        }

        static bool Fits(SpanCurve c, double[] allowed, double s0, double s1)
        {
            s1 = Math.Min(s1, c.Length);
            double length = s1 - s0;
            for (int i = c.Index(s0), last = Math.Min(c.Index(s1) + 1, c.Pos.Count - 1); i <= last; i++)
                if (allowed[i] < length)
                    return false;
            return Vector3D.Dot(c.TanAt(s0), c.TanAt(s1)) >= MaxTurnCos && c.TwistRate * length <= MaxTwist;
        }

        // Piece on the chord p0 -> p1, up made square to it; modelLength > 0 stretches the model to the chord.
        static MatrixD Chord(Vector3D p0, Vector3D p1, Vector3D up, double modelLength)
        {
            Vector3D forward = p1 - p0;
            double chord = forward.Length();
            forward = chord > 1e-6 ? forward / chord : Vector3D.Forward;
            up -= forward * Vector3D.Dot(up, forward);
            up = up.LengthSquared() > 1e-10 ? Vector3D.Normalize(up) : Vector3D.CalculatePerpendicularVector(forward);
            MatrixD m = MatrixD.CreateWorld((p0 + p1) * 0.5, forward, up);
            if (modelLength > 0)
                m.Forward = forward * (chord / modelLength);
            return m;
        }
    }
}
