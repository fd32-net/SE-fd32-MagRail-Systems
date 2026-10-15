using System;
using System.Collections.Generic;
using VRageMath;

namespace MagRail
{
    // Rail Switch: its three branch ports share one point, so the spans laid from them start in one place and
    // overlap until they part. Drawn as they are, their surfaces lie in one another and flicker, and their heights
    // differ by what their curves do. Where a span overlaps another branch of its switch (a span, or the rail block
    // joined to the centre branch end to end), its pieces are drawn level with the reference branch (the lowest rank
    // present: C, L, R) and a millimetre or so below and outside it by rank, so the reference is on top and no two
    // surfaces lie in one plane. Once the span is clear of the others the shift fades out. Only the look: trains ride
    // the span's curve. The level is held up to MaxLevel: spans that part in height faster part as they are.
    public class FanLevel
    {
        const double Drop = 0.001;                              // below the reference per rank, m
        static readonly double[] Out = { 0, 0.0005, 0.00075 };     // outwards by rank: L to its left, R to its right, m
        const double MaxLevel = 0.05;
        const double Fade = 4 * TrackLayout.Module;
        const double Step = SpanCurve.Step;
        const double OverlapMargin = 0.02;

        // Another branch, in the span's frame, by distance from the switch point.
        class Branch
        {
            public Func<double, Vector3D> At;
            public double Length;
            public int Rank;
        }

        // A span end at a switch point.
        class End
        {
            public bool AtStart;        // the owner end (s = 0)
            public int Rank;
            public Vector3D Point, Up;  // the switch point and its up
            public double Clear;        // distance from the point where the span is clear of the other branches
            public double[] Level;      // per Step from the point up to Clear: reference height minus own, clamped
        }

        readonly SpanCurve curve;
        readonly List<End> ends = new List<End>();

        FanLevel(SpanCurve curve)
        {
            this.curve = curve;
        }

        // ---- cache key: what the shift depends on besides the span's own curve ----

        static readonly List<object> keyScratch = new List<object>();

        // True when the other branches at the span's switch points changed since the last call (the key is kept in
        // the span).
        public static bool KeyChanged(TrackNetwork network, Span s)
        {
            keyScratch.Clear();
            CollectKey(network, s.Owner, s.OwnerPort, keyScratch);
            CollectKey(network, s.Partner, s.PartnerPort, keyScratch);
            List<object> old = s.FanKey;
            bool same = (old == null ? 0 : old.Count) == keyScratch.Count;
            for (int i = 0; same && i < keyScratch.Count; i++)
                same = old[i] == keyScratch[i];
            if (same)
                return false;
            if (old == null)
                s.FanKey = old = new List<object>();
            old.Clear();
            old.AddRange(keyScratch);
            return true;
        }

        static void CollectKey(TrackNetwork network, TrackBlockLogic block, int port, List<object> key)
        {
            if (block.FanRank(port) < 0)
                return;
            key.Add(block);
            for (int rank = 0; rank < TrackBlockLogic.BranchPorts.Length; rank++)
            {
                int q = block.PortIndex(TrackBlockLogic.BranchPorts[rank]);
                if (q < 0 || q == port)
                    continue;
                Span sib = network.SpanAt(block, q);
                if (sib != null)
                {
                    key.Add(sib.Bound && sib.IsRail ? network.CurveOf(sib) : null);
                    continue;
                }
                int otherPort;
                TrackLane lane = JointLane(network, block, q, out otherPort);
                key.Add(lane != null ? lane.Curve : null);
            }
        }

        // The lane of a block joined end to end to the centre branch, entered at otherPort. Another switch's entry:
        // its centre lane, not the selected one (LaneFrom), so turning that switch does not rebuild these spans; the
        // lanes leave the entry together, and only that stretch is near the branches here.
        static TrackLane JointLane(TrackNetwork network, TrackBlockLogic block, int port, out int otherPort)
        {
            TrackBlockLogic other;
            if (block.FanRank(port) != (int)SwitchBranch.Center || !network.FindJoint(block, port, out other, out otherPort))
            {
                otherPort = -1;
                return null;
            }
            if (other.Kind != TrackKind.Switch || otherPort != other.PortIndex("entry"))
                return other.LaneFrom(otherPort);
            int centre = other.PortIndex(TrackBlockLogic.BranchPorts[(int)SwitchBranch.Center]);
            List<TrackLane> lanes = other.Lanes;
            for (int i = 0; i < lanes.Count; i++)
                if (lanes[i].To == centre)
                    return lanes[i];
            return other.LaneFrom(otherPort);
        }

        // ---- build ----

        // Null when no end of the span shares its switch point with another branch.
        public static FanLevel Build(TrackNetwork network, Span s, SpanCurve c, double width)
        {
            FanLevel level = null;
            MatrixD toSpan = MatrixD.Invert(TrackNetwork.FrameMatrix(s));
            for (int e = 0; e < 2; e++)
            {
                TrackBlockLogic block = e == 0 ? s.Owner : s.Partner;
                int port = e == 0 ? s.OwnerPort : s.PartnerPort;
                int rank = block.FanRank(port);
                if (rank < 0)
                    continue;
                List<Branch> others = Others(network, block, port, toSpan);
                if (others.Count == 0)
                    continue;
                level = level ?? new FanLevel(c);
                level.ends.Add(level.MakeEnd(e == 0, rank, others, width));
            }
            return level;
        }

        static List<Branch> Others(TrackNetwork network, TrackBlockLogic block, int port, MatrixD toSpan)
        {
            List<Branch> list = new List<Branch>();
            for (int rank = 0; rank < TrackBlockLogic.BranchPorts.Length; rank++)
            {
                int q = block.PortIndex(TrackBlockLogic.BranchPorts[rank]);
                if (q < 0 || q == port)
                    continue;
                Span sib = network.SpanAt(block, q);
                if (sib != null)
                {
                    if (!sib.Bound || !sib.IsRail)
                        continue;
                    SpanCurve c = network.CurveOf(sib);
                    MatrixD m = TrackNetwork.FrameMatrix(sib) * toSpan;
                    bool start = sib.Owner == block && sib.OwnerPort == q;
                    list.Add(new Branch
                    {
                        At = d => Vector3D.Transform(c.PosAt(start ? d : c.Length - d), m), Length = c.Length, Rank = rank,
                    });
                    continue;
                }
                int otherPort;
                TrackLane lane = JointLane(network, block, q, out otherPort);
                if (lane == null)
                    continue;
                // The lane's curve is in its block's coordinates, S = 0 at its From port.
                SpanCurve lc = lane.Curve;
                MatrixD lm = lane.Block.Block.WorldMatrix * toSpan;
                bool fromStart = lane.From == otherPort;
                list.Add(new Branch
                {
                    At = d => Vector3D.Transform(lc.PosAt(fromStart ? d : lc.Length - d), lm), Length = lc.Length, Rank = rank,
                });
            }
            return list;
        }

        End MakeEnd(bool atStart, int rank, List<Branch> others, double width)
        {
            End end = new End { AtStart = atStart, Rank = rank };
            end.Point = curve.PosAt(atStart ? 0 : curve.Length);
            end.Up = curve.UpAt(atStart ? 0 : curve.Length);
            Branch reference = null;
            for (int i = 0; i < others.Count; i++)
                if (others[i].Rank < rank && (reference == null || others[i].Rank < reference.Rank))
                    reference = others[i];

            double reach = width + OverlapMargin;
            List<double> level = new List<double>();
            end.Clear = curve.Length;
            for (int i = 0; ; i++)
            {
                double d = Math.Min(i * Step, curve.Length);
                Vector3D p = At(atStart, d);
                bool overlaps = false;
                for (int k = 0; k < others.Count && !overlaps; k++)
                {
                    if (d > others[k].Length)
                        continue;
                    Vector3D v = others[k].At(d) - p;
                    v -= end.Up * Vector3D.Dot(v, end.Up);
                    overlaps = v.LengthSquared() < reach * reach;
                }
                if (!overlaps)
                {
                    end.Clear = d;
                    break;
                }
                double h = 0;
                if (reference != null)
                    h = MathHelper.Clamp(Vector3D.Dot(reference.At(Math.Min(d, reference.Length)) - p, end.Up), -MaxLevel, MaxLevel);
                level.Add(h);
                if (d >= curve.Length)
                    break;
            }
            if (level.Count == 0)
                level.Add(0);
            end.Level = level.ToArray();
            return end;
        }

        Vector3D At(bool atStart, double d)
        {
            return curve.PosAt(atStart ? d : curve.Length - d);
        }

        // ---- use ----

        // The module from s0 to s1 (s0 < s1) starts or ends where the span still overlaps another branch: its copper
        // is a strip (TrackLayout), as on the other branches there, so no plates lie offset over one another.
        public bool Overlaps(double s0, double s1)
        {
            for (int i = 0; i < ends.Count; i++)
            {
                End e = ends[i];
                if ((e.AtStart ? s0 : curve.Length - s1) < e.Clear)
                    return true;
            }
            return false;
        }

        // Shift of the drawn rail at arc length s, in the span's frame.
        public Vector3D Offset(double s)
        {
            Vector3D sum = Vector3D.Zero;
            for (int i = 0; i < ends.Count; i++)
            {
                End e = ends[i];
                double d = e.AtStart ? s : curve.Length - s;
                double w = d <= e.Clear ? 1 : d >= e.Clear + Fade ? 0 : 1 - (d - e.Clear) / Fade;
                if (w <= 0)
                    continue;
                double x = Math.Min(d, e.Clear) / Step;
                int a = Math.Min((int)x, e.Level.Length - 1), b = Math.Min(a + 1, e.Level.Length - 1);
                double h = e.Level[a] + (e.Level[b] - e.Level[a]) * (x - a);
                Vector3D up = curve.UpAt(s), along = e.AtStart ? curve.TanAt(s) : -curve.TanAt(s);
                Vector3D right = Vector3D.Cross(along, up);
                double side = e.Rank == (int)SwitchBranch.Left ? -Out[e.Rank] : Out[e.Rank];
                sum += w * (e.Up * h - up * (Drop * e.Rank) + right * side);
            }
            return sum;
        }
    }
}
