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
    // Forward along the chord, Up. A FanLevel shifts the drawn pieces (not the collision) near a Rail Switch; where
    // the span still overlaps another branch there, its modules are laid one by one without gaps in the copper
    // (TrackStyle: strip models), as the other branch's are. A style without strip models gets no FanLevel
    // (TrackNetwork.LayoutOf): its switch branches are laid as they are.
    // The copper has a gap on every GapModules-th module boundary from the span's start (where the Rail Linker draws
    // the sections, SpanLines) but the one a module before the end, and on both ends (the blocks' rail parts have a
    // gap half on their ends: a whole gap on every joint), half of it on each piece: a model has the gap halves its
    // stretch of the copper texture has (TrackStyle), and every piece is the model, turned round or not, whose halves
    // fall on exactly the gaps of its boundaries. No module has two (a span of one module: only at its start).
    // An end on a branch port of a Rail Switch: the copper there is dark while the branch is not selected (Glow), from
    // where its plates start (the first module past its strip modules) and comes back after it (the overlap would eat
    // up the fade). Up to SwitchFade past where it is clear of every branch the straight pieces are a gap to gap stretch at most (the glow
    // steps hide in the gaps); that does not depend on the selection, so turning the switch only relights the pieces.
    // Where it lies over the other branches (its strip modules), a module also has a bare model (no copper, Bare): an
    // unselected branch shows that one there (TrackPieces), so only the selected branch's copper is seen.
    // Weld segments (SpanWeld, PLAN 10.8): the stretches between the copper's gaps (every GapModules modules but the
    // one a module before the end), about 2.5 m. The collision is laid per segment (a span being built is walked on from
    // stage 0 on). A span still being built is laid
    // Segmented: no straight piece crosses a segment's end, so a segment shows its own finished pieces; and with
    // construction stage pieces (Stages), single modules on their chords or one for a straight whole segment.
    public class TrackLayout
    {
        public const double Module = 0.625;
        public const double CollisionLength = 2.5;
        // A gap in the copper every GapModules modules (2.5 m; ModelSource/copper_texture.py REPEAT, at least 2).
        public const int GapModules = 4;
        // Straight pieces, in modules; a straight run takes the longest that fits first.
        static readonly int[] StraightModules = { 16, 8, 4, 2, 1 };
        // Bend models turn by yaw, pitch or roll in steps of BendStep, at most BendSteps (TrackBends.py, WideTrack.py)
        // per module: MaxBend (radius about 12 m), MaxTwist (half a roll in about 40 m); prototype colours (Draw).
        public const int BendSteps = 6;
        static readonly double BendStep = MathHelper.ToRadians(0.5);
        public static readonly double MaxBend = BendSteps * BendStep;
        public static readonly double MaxTwist = BendSteps * BendStep;
        // Straight modules join into one piece while the curve points between them stay this close to its chord, m,
        // and its ends' ups within half a step.
        const double StraightTolerance = 0.003;
        // An unselected switch branch: its copper is dark where its plates start and comes back to full glow over this
        // length, m, or over the rest of the span if that is shorter.
        public const double SwitchFade = 20;

        public readonly SpanCurve Curve;
        public readonly TrackStyle Style;
        readonly FanLevel level;
        public readonly List<string> Models = new List<string>();       // model names
        public readonly List<MatrixD> Pieces = new List<MatrixD>();     // stretched along Forward
        public readonly List<MatrixD> Collision = new List<MatrixD>();
        // By Pieces: the bare model shown instead while the switch branch is not selected (Bared), or null.
        public readonly List<string> Bare = new List<string>();
        // By Collision: the segment it belongs to, half its chord's length (the box is the CollisionLength model).
        public readonly List<int> CollisionSegment = new List<int>();
        public readonly List<double> CollisionHalf = new List<double>();
        // Laid for a span being built (see above); then the stage pieces: their chords (stretched to the stage model,
        // a module or a whole segment: StageFull) and segments.
        public readonly bool Segmented;
        public readonly List<MatrixD> Stages = new List<MatrixD>();
        public readonly List<bool> StageFull = new List<bool>();
        public readonly List<int> StageSegment = new List<int>();
        readonly List<int> pieceSegment = new List<int>();
        readonly List<bool> bareAtStart = new List<bool>();
        // Arc lengths of the pieces' ends, m (by Pieces).
        readonly List<double> pieceStart = new List<double>(), pieceEnd = new List<double>();
        // The span's start / end is on a switch branch port (fades out while the branch is not selected).
        readonly bool fadeStart, fadeEnd;
        // Distance from the span's start / end where its plates start (past the strip modules), m.
        double platesStart, platesEnd;
        int modules;
        double moduleLength;

        TrackLayout(SpanCurve curve, TrackStyle style, FanLevel level, bool fadeStart, bool fadeEnd, bool segmented)
        {
            Segmented = segmented;
            Curve = curve;
            Style = style;
            this.level = level;
            this.fadeStart = fadeStart;
            this.fadeEnd = fadeEnd;
        }

        // Unscaled piece of CollisionLength, its collision used for the Collision matrices.
        public string CollisionModel
        {
            get { return Style.Straight((int)Math.Round(CollisionLength / Module)); }
        }

        // fadeStart, fadeEnd: the span's start / end is on a branch port of a Rail Switch; segmented: the span is being
        // built (see above).
        public static TrackLayout Build(SpanCurve c, TrackStyle style, FanLevel level, bool fadeStart, bool fadeEnd, bool segmented)
        {
            TrackLayout layout = new TrackLayout(c, style, level, fadeStart, fadeEnd, segmented);
            double length = c.Length;
            int n = Math.Max(1, (int)Math.Round(length / Module));
            double m = length / n;      // stretched module
            layout.modules = n;
            layout.moduleLength = m;

            // Module ends: the first and last are the ports, so the rail leaves and enters them along their direction.
            Vector3D[] pos = new Vector3D[n + 1], tan = new Vector3D[n + 1], up = new Vector3D[n + 1];
            for (int k = 0; k <= n; k++)
            {
                double s = k == n ? c.Length : k * m;
                pos[k] = layout.Drawn(s);
                tan[k] = c.TanAt(s);
                up[k] = c.UpAt(s);
            }

            if (n == 1)
                layout.Add(style.Straight(1), Chord(pos[0], pos[1], up[0] + up[1], Module), 0, 1);
            else
                layout.Rail(n, pos, tan, up);
            if (segmented)
                layout.LayStages(pos, up);

            for (int g = 0; g < layout.Segments; g++)
            {
                double a = layout.SegmentStart(g), b = layout.SegmentEnd(g);
                int count = Math.Max(1, (int)Math.Ceiling((b - a) / CollisionLength - 0.1));
                for (int i = 0; i < count; i++)
                {
                    double s0 = a + (b - a) * i / count, s1 = a + (b - a) * (i + 1) / count;
                    layout.Collision.Add(Chord(c.PosAt(s0), c.PosAt(s1), c.UpAt(s0) + c.UpAt(s1), 0));
                    layout.CollisionSegment.Add(g);
                    layout.CollisionHalf.Add((s1 - s0) / 2);
                }
            }
            return layout;
        }

        // ---- weld segments ----

        public int Segments
        {
            get { return modules < 2 ? 1 : 1 + (modules - 2) / GapModules; }
        }

        // The segment module k (from 0) is in.
        int SegmentOf(int k)
        {
            return Math.Min(k / GapModules, Segments - 1);
        }

        // Module boundary k ends a segment (not the span's ends).
        bool SegmentBoundary(int k)
        {
            return k > 0 && k % GapModules == 0 && k < modules - 1;
        }

        int SegmentFirstModule(int g)
        {
            return g * GapModules;
        }

        int SegmentEndModule(int g)
        {
            return g == Segments - 1 ? modules : (g + 1) * GapModules;
        }

        // The segment arc length s (m) is in.
        public int SegmentAt(double s)
        {
            return SegmentOf(Math.Max(0, (int)(s / moduleLength)));
        }

        // Arc lengths of segment g's ends, m.
        public double SegmentStart(int g)
        {
            return SegmentFirstModule(g) * moduleLength;
        }

        public double SegmentEnd(int g)
        {
            int k = SegmentEndModule(g);
            return k == modules ? Curve.Length : k * moduleLength;
        }

        // The segment track piece i lies in (Segmented: wholly).
        public int PieceSegment(int i)
        {
            return pieceSegment[i];
        }

        // The stage pieces: a straight whole segment as one, else module by module.
        void LayStages(Vector3D[] pos, Vector3D[] up)
        {
            for (int g = 0; g < Segments; g++)
            {
                int a = SegmentFirstModule(g), b = SegmentEndModule(g);
                if (b - a == GapModules && IsStraight(pos, up, a, b))
                {
                    AddStage(Chord(pos[a], pos[b], up[a] + up[b], GapModules * Module), true, g);
                    continue;
                }
                for (int k = a; k < b; k++)
                    AddStage(Chord(pos[k], pos[k + 1], up[k] + up[k + 1], Module), false, g);
            }
        }

        void AddStage(MatrixD m, bool full, int segment)
        {
            Stages.Add(m);
            StageFull.Add(full);
            StageSegment.Add(segment);
        }

        void Rail(int n, Vector3D[] pos, Vector3D[] tan, Vector3D[] up)
        {
            SpanCurve c = Curve;
            double m = moduleLength;

            // Per module: bend steps on one axis (yaw, pitch, roll), or a joint; strip: no gaps (a switch's overlap).
            int[][] steps = { new int[n], new int[n], new int[n] };
            bool[] joint = new bool[n], strip = new bool[n], fade = new bool[n];
            for (int k = 0; k < n; k++)
                strip[k] = Strip(k * m, k == n - 1 ? c.Length : (k + 1) * m);
            int first = 0, last = n;
            while (first < n && strip[first])
                first++;
            while (last > 0 && strip[last - 1])
                last--;
            platesStart = Math.Min(first * m, c.Length);
            platesEnd = Math.Max(0, c.Length - last * m);
            double fadeFromStart = fadeStart ? platesStart + SwitchFade : 0, fadeFromEnd = fadeEnd ? platesEnd + SwitchFade : 0;
            for (int k = 0; k < n; k++)
                fade[k] = k * m < fadeFromStart || (k + 1) * m > c.Length - fadeFromEnd;
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
                    bool start, end;
                    Ends(i, false, out start, out end);
                    AddModule(i, pos, up, start || end ? Style.JointGap : Style.Joint, end, null);
                    i++;
                    continue;
                }
                if (yaw[i] != 0 || pitch[i] != 0 || roll[i] != 0)
                {
                    AddBend(i, pos, up, yaw[i], pitch[i], roll[i], strip[i]);
                    i++;
                    continue;
                }
                // Where the copper may fade (a switch branch), a gap to gap stretch at most, not across the fade's edge;
                // segmented, not across a segment's end.
                int left = 1, most = fade[i] || Segmented ? GapModules : n;
                while (!strip[i] && left < most && i + left < n && !joint[i + left] && !strip[i + left] && fade[i + left] == fade[i]
                       && yaw[i + left] == 0 && pitch[i + left] == 0 && roll[i + left] == 0 && !(Segmented && SegmentBoundary(i + left)))
                    left++;
                bool turn;
                int take = StraightTake(i, left, pos, up, out turn);
                if (take == 1)
                {
                    bool start, end;
                    Ends(i, strip[i], out start, out end);
                    AddModule(i, pos, up, Style.Straight(1, !start && !end && Style.HasStrips), end,
                        strip[i] && Style.HasBare ? Style.StraightBare : null);
                }
                else
                {
                    MatrixD piece = Chord(pos[i], pos[i + take], up[i] + up[i + take], take * Module);
                    Add(Style.Straight(take), turn ? TurnedRound(piece) : piece, i, i + take);
                }
                i += take;
            }
        }

        // The module from s0 to s1 has a copper strip (the span overlaps another branch of a Rail Switch there).
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

        // A gap in the copper on module boundary k.
        bool Gap(int k)
        {
            return Style.HasStrips && (k == 0 || k == modules || k % GapModules == 0 && k < modules - 1);
        }

        // The gap halves module k has: at its start, at its end (on a strip module only on the span's ends: all the
        // branches of a switch have them there).
        void Ends(int k, bool strip, out bool start, out bool end)
        {
            start = (!strip || k == 0) && Gap(k);
            end = (!strip || k + 1 == modules) && Gap(k + 1);
        }

        // Longest straight piece from module i (at most `left` modules) that the curve is straight and untwisted
        // enough for and whose gaps, turned round or not, fall on the boundaries' (one module: any, AddModule).
        int StraightTake(int i, int left, Vector3D[] pos, Vector3D[] up, out bool turn)
        {
            turn = false;
            for (int j = 0; j < StraightModules.Length; j++)
            {
                int take = StraightModules[j];
                if (take > left)
                    continue;
                if (take == 1)
                    return 1;
                if (!IsStraight(pos, up, i, i + take))
                    continue;
                if (GapsFit(i, take, false))
                    return take;
                if (GapsFit(i, take, true))
                {
                    turn = true;
                    return take;
                }
            }
            return 1;
        }

        // A straight piece of `take` modules from module i (its gaps every GapModules from its start; turned round:
        // from its end) has exactly the gaps of its boundaries.
        bool GapsFit(int i, int take, bool turn)
        {
            if (!Style.HasStrips)
                return true;
            for (int r = 0; r <= take; r++)
                if (((turn ? take - r : r) % GapModules == 0) != Gap(i + r))
                    return false;
            return true;
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

        // Bend module k on its chord, with its gap halves (Ends). A yaw bend to the right has them at its start
        // (Bend) or end (BendEnd), a left bend is one of these turned round; a pitch or roll bend turned round is the
        // same bend with the half at its end.
        void AddBend(int k, Vector3D[] pos, Vector3D[] up, int ys, int ps, int rs, bool strip)
        {
            bool start, end;
            Ends(k, strip, out start, out end);
            string bare = strip && Style.HasBare ? Style.BendBare(ys, ps, rs) : null;
            if (ys != 0)
            {
                bool left = ys < 0;
                if (left)
                {
                    bool t = start;
                    start = end;
                    end = t;
                }
                ys = Math.Abs(ys);
                AddModule(k, pos, up, start ? Style.Bend(ys, 0, 0) : end ? Style.BendEnd(ys)
                    : Style.Bend(ys, 0, 0, Style.HasStrips), left, bare);
            }
            else
                AddModule(k, pos, up, Style.Bend(0, ps, rs, !start && !end && Style.HasStrips), end, bare);
        }

        // One module long model on module k's chord, turned round or not; bare: its bare model (Bare) or null.
        void AddModule(int k, Vector3D[] pos, Vector3D[] up, string model, bool turn, string bare)
        {
            MatrixD m = Chord(pos[k], pos[k + 1], up[k] + up[k + 1], Module);
            Add(model, turn ? TurnedRound(m) : m, k, k + 1);
            Bare[Bare.Count - 1] = bare;
        }

        // The piece turned round about its up (its ends swapped).
        static MatrixD TurnedRound(MatrixD m)
        {
            m.Forward = -m.Forward;
            m.Right = -m.Right;
            return m;
        }

        // How far the direction turns from a to b about the axis square to `side` (towards side), rad.
        static double Turn(Vector3D a, Vector3D b, Vector3D side)
        {
            return Math.Asin(MathHelper.Clamp(Vector3D.Dot(b, side), -1, 1)) - Math.Asin(MathHelper.Clamp(Vector3D.Dot(a, side), -1, 1));
        }

        // The piece from module boundary k0 to k1.
        void Add(string model, MatrixD m, int k0, int k1)
        {
            Models.Add(model);
            Pieces.Add(m);
            pieceStart.Add(k0 * moduleLength);
            pieceEnd.Add(k1 == modules ? Curve.Length : k1 * moduleLength);
            Bare.Add(null);
            bareAtStart.Add(k0 + k1 < modules);
            pieceSegment.Add(SegmentOf(k0));
        }

        // Piece i shows its bare model: it has one and the switch branch at its end of the span is not selected
        // (selectedStart, selectedEnd as in Glow).
        public bool Bared(int i, int selectedStart, int selectedEnd)
        {
            return Bare[i] != null && (bareAtStart[i] ? selectedStart : selectedEnd) >= 0;
        }

        // Share of the full copper glow on piece i, 0..1; selectedStart, selectedEnd: the selected branch (rank) of the
        // switch at the span's start / end when it is not this span's branch, else -1. Dark up to where the plates start,
        // then linear over SwitchFade (or the rest of the span), by the piece's end nearer to the switch.
        public float Glow(int i, int selectedStart, int selectedEnd)
        {
            double g = 1;
            if (selectedStart >= 0 && fadeStart)
                g = Math.Min(g, Fade(pieceStart[i], platesStart));
            if (selectedEnd >= 0 && fadeEnd)
                g = Math.Min(g, Fade(Curve.Length - pieceEnd[i], platesEnd));
            return (float)MathHelper.Clamp(g, 0, 1);
        }

        // d: distance from the switch point; plates: where the plates start from it.
        double Fade(double d, double plates)
        {
            if (d < plates)
                return 0;
            return (d - plates) / Math.Max(Module, Math.Min(SwitchFade, Curve.Length - plates));
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
