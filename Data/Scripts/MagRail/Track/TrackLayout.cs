using System;
using System.Collections.Generic;
using VRageMath;

namespace MagRail
{
    // How a span in rail mode is laid from track piece models (ModelSource/blocks/TrackPieces.py, TrackBends.py,
    // WideTrack.py). The rail is a row of 0.625 m modules (the span length rounded to whole modules, all stretched
    // alike), so the copper plates and textures of the models run on across the joints. Every module lies on its chord
    // of the curve. On a curve every module is a bend model that turns the rail by its share of the curve, by yaw,
    // pitch or roll (the up twisting about the rail) only, in whole steps: what a module rounds away, or the axes that
    // wait, are carried on to the next module, so the rail follows the curve to a fraction of a step without models for
    // every mix of the three. Straight modules in a row become longer straight pieces where the curve is straight and
    // untwisted enough. Where a module would turn more than a bend model can (radius under about 12 m, a half roll in
    // under about 40 m), a joint with pyramid ends shows a chamfer instead. For collision, a run of unscaled 2.5 m
    // pieces.
    // Matrices are in the span's frame (see SpanCurve): origin = rail point in the middle of the piece's chord,
    // Forward along the chord, Up. A FanLevel shifts the drawn pieces (not the collision) near a Rail Switch 1; where
    // the span still overlaps another branch there, its modules are laid one by one with a continuous copper strip
    // instead of plates (TrackStyle: strip models), as the other branch's are.
    public class TrackLayout
    {
        public const double Module = 0.625;
        public const double CollisionLength = 2.5;
        // Straight pieces, in modules; a straight run takes the longest that fits first.
        static readonly int[] StraightModules = { 64, 32, 16, 8, 4, 2, 1 };
        // Bend models turn by yaw, pitch or roll in steps of BendStep, at most BendSteps (TrackBends.py, WideTrack.py)
        // per module: MaxBend (radius about 12 m), MaxTwist (half a roll in about 40 m); prototype colours (Draw).
        const int BendSteps = 6;
        static readonly double BendStep = MathHelper.ToRadians(0.5);
        public static readonly double MaxBend = BendSteps * BendStep;
        public static readonly double MaxTwist = BendSteps * BendStep;
        // Straight modules join into one piece while the curve points between them stay this close to its chord, m,
        // and its ends' ups within half a step.
        const double StraightTolerance = 0.003;

        public readonly SpanCurve Curve;
        public readonly TrackStyle Style;
        readonly FanLevel level;
        public readonly List<string> Models = new List<string>();       // model names
        public readonly List<MatrixD> Pieces = new List<MatrixD>();     // stretched along Forward
        public readonly List<MatrixD> Collision = new List<MatrixD>();

        TrackLayout(SpanCurve curve, TrackStyle style, FanLevel level)
        {
            Curve = curve;
            Style = style;
            this.level = level;
        }

        // Unscaled piece of CollisionLength, its collision used for the Collision matrices.
        public string CollisionModel
        {
            get { return Style.Straight((int)Math.Round(CollisionLength / Module)); }
        }

        public static TrackLayout Build(SpanCurve c, TrackStyle style, FanLevel level)
        {
            TrackLayout layout = new TrackLayout(c, style, level);
            double length = c.Length;
            int n = Math.Max(1, (int)Math.Round(length / Module));
            double m = length / n;      // stretched module

            if (n == 1)
                layout.Add(style.Straight(1, layout.Strip(0, length)), Chord(layout.Drawn(0), layout.Drawn(length), c.UpAt(0) + c.UpAt(length), Module));
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
            // Module ends: the first and last are the ports, so the rail leaves and enters them along their direction.
            Vector3D[] pos = new Vector3D[n + 1], tan = new Vector3D[n + 1], up = new Vector3D[n + 1];
            for (int k = 0; k <= n; k++)
            {
                double s = k == n ? c.Length : k * m;
                pos[k] = Drawn(s);
                tan[k] = c.TanAt(s);
                up[k] = c.UpAt(s);
            }

            // Per module: bend steps on one axis (yaw, pitch, roll), or a joint; copper strip or plates.
            int[][] steps = { new int[n], new int[n], new int[n] };
            bool[] joint = new bool[n], strip = new bool[n];
            for (int k = 0; k < n; k++)
                strip[k] = Strip(k * m, k == n - 1 ? c.Length : (k + 1) * m);
            double[] carry = new double[3], want = new double[3];
            int[] rounded = new int[3];
            for (int k = 0; k < n; k++)
            {
                MatrixD f = Chord(pos[k], pos[k + 1], up[k] + up[k + 1], 0);
                want[0] = Turn(tan[k], tan[k + 1], f.Right) / BendStep + carry[0];
                want[1] = Turn(tan[k], tan[k + 1], f.Up) / BendStep + carry[1];
                want[2] = Turn(up[k], up[k + 1], f.Right) / BendStep + carry[2];
                int axis = -1;
                for (int a = 0; a < 3; a++)
                {
                    rounded[a] = (int)Math.Round(want[a]);
                    if (rounded[a] != 0 && (axis < 0 || Math.Abs(want[a]) > Math.Abs(want[axis])))
                        axis = a;
                }
                if (Math.Abs(rounded[0]) > BendSteps || Math.Abs(rounded[1]) > BendSteps || Math.Abs(rounded[2]) > BendSteps)
                {
                    joint[k] = true;
                    carry[0] = carry[1] = carry[2] = 0;
                    continue;
                }
                // The axis that asks most turns; the others wait. At most a step carried: where several axes keep
                // asking at once, the rail lags a little instead of piling up a turn.
                for (int a = 0; a < 3; a++)
                {
                    int take = a == axis ? rounded[a] : 0;
                    steps[a][k] = take;
                    carry[a] = MathHelper.Clamp(want[a] - take, -1, 1);
                }
            }
            int[] yaw = steps[0], pitch = steps[1], roll = steps[2];

            int i = 0;
            while (i < n)
            {
                if (joint[i])
                {
                    Add(Style.Joint, Chord(pos[i], pos[i + 1], up[i] + up[i + 1], Module));
                    i++;
                    continue;
                }
                if (yaw[i] != 0 || pitch[i] != 0 || roll[i] != 0)
                {
                    AddBend(i, pos, up, yaw[i], pitch[i], roll[i], strip[i]);
                    i++;
                    continue;
                }
                int left = 1;
                while (!strip[i] && i + left < n && !joint[i + left] && !strip[i + left]
                       && yaw[i + left] == 0 && pitch[i + left] == 0 && roll[i + left] == 0)
                    left++;
                int take = StraightTake(i, left, pos, up);
                Add(Style.Straight(take, strip[i]), Chord(pos[i], pos[i + take], up[i] + up[i + take], take * Module));
                i += take;
            }
        }

        // The module from s0 to s1 has a copper strip (the span overlaps another branch of a Rail Switch 1 there).
        bool Strip(double s0, double s1)
        {
            return level != null && level.Overlaps(s0, s1);
        }

        // Where the drawn rail runs at arc length s.
        Vector3D Drawn(double s)
        {
            Vector3D p = Curve.PosAt(s);
            return level != null ? p + level.Offset(s) : p;
        }

        // Longest straight piece from module i (at most `left` modules) that the curve is straight and untwisted
        // enough for.
        static int StraightTake(int i, int left, Vector3D[] pos, Vector3D[] up)
        {
            for (int j = 0; j < StraightModules.Length; j++)
            {
                int take = StraightModules[j];
                if (take <= left && (take == 1 || IsStraight(pos, up, i, i + take)))
                    return take;
            }
            return 1;
        }

        static bool IsStraight(Vector3D[] pos, Vector3D[] up, int a, int b)
        {
            Vector3D d = pos[b] - pos[a];
            double length = d.Length();
            if (length < 1e-6)
                return true;
            d /= length;
            Vector3D right = Vector3D.Cross(d, up[a] + up[b]);
            if (right.LengthSquared() > 1e-12 && Math.Abs(Turn(up[a], up[b], Vector3D.Normalize(right))) > BendStep / 2)
                return false;
            for (int k = a + 1; k < b; k++)
            {
                Vector3D v = pos[k] - pos[a];
                if ((v - d * Vector3D.Dot(v, d)).LengthSquared() > StraightTolerance * StraightTolerance)
                    return false;
            }
            return true;
        }

        // Bend module k on its chord.
        void AddBend(int k, Vector3D[] pos, Vector3D[] up, int ys, int ps, int rs, bool strip)
        {
            MatrixD m = Chord(pos[k], pos[k + 1], up[k] + up[k + 1], Module);
            if (ys < 0)
            {
                m.Forward = -m.Forward;
                m.Right = -m.Right;
                ys = -ys;
            }
            Add(Style.Bend(ys, ps, rs, strip), m);
        }

        // How far the direction turns from a to b about the axis square to `side` (towards side), rad.
        static double Turn(Vector3D a, Vector3D b, Vector3D side)
        {
            return Math.Asin(MathHelper.Clamp(Vector3D.Dot(b, side), -1, 1)) - Math.Asin(MathHelper.Clamp(Vector3D.Dot(a, side), -1, 1));
        }

        void Add(string model, MatrixD m)
        {
            Models.Add(model);
            Pieces.Add(m);
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
