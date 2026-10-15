using System;
using System.Collections.Generic;
using VRage.Game.ModAPI;
using VRageMath;

namespace MagRail
{
    // What the physics takes from the rail at a point, in world coordinates.
    public struct TrackSample
    {
        public Vector3D Point;      // on the rail (moved sideways by the lateral offset)
        public Vector3D Tangent;    // along the rail the way the cursor faces
        public Vector3D Up;         // rail up
        public double Distance;     // from the projected point to Point
        // The moving grid the rail is on (the rail moves with it), null on a station.
        public IMyCubeGrid Carrier;
    }

    // The single way the physics sees the track. Where a point of a train is: on a span in rail mode or on a
    // lane inside a track block, at arc length S along its curve, facing Dir (+1 = towards growing S).
    // Kept between ticks, so the next projection looks only near the last place; the index is searched
    // only to capture the rail. Advance runs along the track through spans, lanes, joints and switches:
    // coming in through a switch entry it takes the selected branch, through a branch it goes to the entry.
    // The lateral offset moves the point to the right of the cursor's direction; S stays the centre line's.
    public class TrackCursor
    {
        const double Window = 10;       // local projection looks this far around S, m
        const int MaxHops = 64;         // pieces crossed by one Advance
        // A point further than this past the end of a piece is not over it: such a piece is not captured.
        public const double PastEnd = 0.5;
        // Capture on a switch: a branch that is not selected must be this much nearer to win. Where the
        // branches still overlap (near the entry) the selected one is taken; at the far end they are 0.8 m apart.
        const double SelectedBranchBonus = 0.5;

        public Span Span;
        public TrackLane Lane;
        public double S;
        public int Dir = 1;
        // The last Advance or TryProject stopped at the end of the track.
        public bool DeadEnd;

        static readonly List<TrackIndex.Entry> scratch = new List<TrackIndex.Entry>();

        static TrackNetwork Network
        {
            get { return MagRailSession.Instance.Network; }
        }

        public bool Valid
        {
            get { return Span != null || Lane != null; }
        }

        public void Reset()
        {
            Span = null;
            Lane = null;
            S = 0;
            Dir = 1;
            DeadEnd = false;
        }

        public void Reverse()
        {
            Dir = -Dir;
        }

        public void CopyFrom(TrackCursor other)
        {
            Span = other.Span;
            Lane = other.Lane;
            S = other.S;
            Dir = other.Dir;
            DeadEnd = other.DeadEnd;
        }

        // ---- the piece of track under the cursor ----

        public SpanCurve Curve
        {
            get { return Span != null ? Network.CurveOf(Span) : Lane.Curve; }
        }

        public MatrixD Frame
        {
            get { return Span != null ? TrackNetwork.FrameMatrix(Span) : Lane.Block.Block.WorldMatrix; }
        }

        // The piece still carries trains (a span can turn prototype or lose a block).
        public bool Rideable
        {
            get { return Span != null ? Span.Bound && Span.IsRail : Lane.Block.Rideable; }
        }

        IMyCubeGrid Carrier
        {
            get
            {
                IMyCubeGrid grid = Span != null ? (TrackNetwork.InGridFrame(Span) ? Span.Owner.Block.CubeGrid : null) : Lane.Block.Block.CubeGrid;
                return grid != null && !grid.IsStatic ? grid : null;
            }
        }

        // ---- projection ----

        // The rail point nearest to pos within maxDistance: near the cursor's place first, else anywhere (then
        // the cursor faces along `forward`; rails on ignoreGrid are not captured). Past the end of the track the
        // cursor moves to another rail only within switchDistance (default maxDistance). False: no rail that
        // close; the cursor is reset.
        public bool TryProject(Vector3D pos, Vector3D forward, double maxDistance, double lateral, out TrackSample sample,
            IMyCubeGrid ignoreGrid = null, double switchDistance = -1)
        {
            if (Valid && Rideable)
            {
                ProjectLocal(pos, lateral);
                // Past the end of the track: maybe over another rail now (backing off a rail onto a lower one).
                if (!DeadEnd || !Capture(pos, switchDistance >= 0 ? switchDistance : maxDistance, ignoreGrid))
                {
                    sample = Sample(pos, lateral);
                    if (sample.Distance <= maxDistance)
                        return true;
                }
                else
                {
                    return Captured(pos, forward, maxDistance, lateral, out sample);
                }
            }
            if (!Capture(pos, maxDistance, ignoreGrid))
            {
                Reset();
                sample = new TrackSample();
                return false;
            }
            return Captured(pos, forward, maxDistance, lateral, out sample);
        }

        // After a capture: face along `forward`, then the projection with the lateral offset.
        bool Captured(Vector3D pos, Vector3D forward, double maxDistance, double lateral, out TrackSample sample)
        {
            if (Vector3D.Dot(Vector3D.TransformNormal(Curve.TanAt(S), Frame), forward) < 0)
                Dir = -1;
            ProjectLocal(pos, lateral);
            sample = Sample(pos, lateral);
            return sample.Distance <= maxDistance;
        }

        // Moves S to the nearest point within Window, going on to the next pieces when pos is past an end.
        void ProjectLocal(Vector3D pos, double lateral)
        {
            DeadEnd = false;
            for (int hop = 0; hop < 4; hop++)
            {
                SpanCurve c = Curve;
                Vector3D local = ToLocal(pos, Frame);
                int first = c.Index(Math.Max(0, S - Window));
                int last = Math.Min(c.Index(Math.Min(c.Length, S + Window)) + 1, c.Pos.Count - 1);
                double d2;
                S = Nearest(c, local, lateral * Dir, first, last, out d2);
                int past = 0;
                if (S >= c.Length - 1e-6 && Vector3D.Dot(local - c.Pos[c.Pos.Count - 1], c.Tan[c.Tan.Count - 1]) > 1e-3)
                    past = 1;
                else if (S <= 1e-6 && Vector3D.Dot(local - c.Pos[0], c.Tan[0]) < -1e-3)
                    past = -1;
                if (past == 0)
                    return;
                int move = past;
                if (!Cross(past > 0 ? 1 : 0, ref move))
                {
                    DeadEnd = true;
                    return;
                }
            }
        }

        // The nearest rideable rail to pos anywhere (centre lines) that pos is over (not past its end); false if
        // none within maxDistance. The cursor is left as it was when nothing is found.
        bool Capture(Vector3D pos, double maxDistance, IMyCubeGrid ignoreGrid)
        {
            double best = maxDistance;
            Span bestSpan = null;
            TrackLane bestLane = null;
            double bestS = 0;
            scratch.Clear();
            Network.Index.Query(Network, pos, maxDistance, scratch);
            for (int i = 0; i < scratch.Count; i++)
            {
                TrackIndex.Entry e = scratch[i];
                double d2, s;
                if (e.Block != null)
                {
                    if (!e.Block.Rideable || e.Block.Block.CubeGrid == ignoreGrid)
                        continue;
                    Vector3D local = ToLocal(pos, e.Block.Block.WorldMatrix);
                    List<TrackLane> lanes = e.Block.Lanes;
                    TrackLane selected = e.Block.Kind == TrackKind.Switch ? e.Block.LaneFrom(e.Block.PortIndex("entry")) : null;
                    for (int k = 0; k < lanes.Count; k++)
                    {
                        SpanCurve c = lanes[k].Curve;
                        s = Nearest(c, local, 0, 0, c.Pos.Count - 1, out d2);
                        if (IsPastEnd(c, local, s))
                            continue;
                        double d = Math.Sqrt(d2) + (selected != null && lanes[k] != selected ? SelectedBranchBonus : 0);
                        if (d < best)
                        {
                            best = d;
                            bestSpan = null;
                            bestLane = lanes[k];
                            bestS = s;
                        }
                    }
                    continue;
                }
                if (!e.Span.Bound || !e.Span.IsRail || (TrackNetwork.InGridFrame(e.Span) && e.Span.Owner.Block.CubeGrid == ignoreGrid))
                    continue;
                SpanCurve curve = Network.CurveOf(e.Span);
                Vector3D spanLocal = ToLocal(pos, TrackNetwork.FrameMatrix(e.Span));
                s = Nearest(curve, spanLocal, 0, e.First, Math.Min(e.Last, curve.Pos.Count - 1), out d2);
                if (Math.Sqrt(d2) < best && !IsPastEnd(curve, spanLocal, s))
                {
                    best = Math.Sqrt(d2);
                    bestSpan = e.Span;
                    bestLane = null;
                    bestS = s;
                }
            }
            scratch.Clear();
            if (bestSpan == null && bestLane == null)
                return false;
            Span = bestSpan;
            Lane = bestLane;
            S = bestS;
            Dir = 1;
            return true;
        }

        // The nearest point s is an end of the curve and `local` lies beyond it.
        static bool IsPastEnd(SpanCurve c, Vector3D local, double s)
        {
            int last = c.Pos.Count - 1;
            if (s >= c.Length - 1e-6)
                return Vector3D.Dot(local - c.Pos[last], c.Tan[last]) > PastEnd;
            return s <= 1e-6 && Vector3D.Dot(local - c.Pos[0], c.Tan[0]) < -PastEnd;
        }

        // Arc length of the point of samples first..last nearest to `local`, the polyline moved sideways by side
        // (to the right of growing S); d2 = squared distance.
        static double Nearest(SpanCurve c, Vector3D local, double side, int first, int last, out double d2)
        {
            d2 = double.MaxValue;
            double s = 0;
            if (last <= first)
            {
                d2 = Vector3D.DistanceSquared(local, Offset(c, first, side));
                return c.S[first];
            }
            Vector3D a = Offset(c, first, side);
            for (int i = first; i < last; i++)
            {
                Vector3D b = Offset(c, i + 1, side);
                Vector3D ab = b - a;
                double len2 = ab.LengthSquared();
                double t = len2 > 1e-12 ? MathHelper.Clamp(Vector3D.Dot(local - a, ab) / len2, 0, 1) : 0;
                double d = Vector3D.DistanceSquared(local, a + ab * t);
                if (d < d2)
                {
                    d2 = d;
                    s = c.S[i] + t * (c.S[i + 1] - c.S[i]);
                }
                a = b;
            }
            return s;
        }

        static Vector3D Offset(SpanCurve c, int i, double side)
        {
            return side == 0 ? c.Pos[i] : c.Pos[i] + Vector3D.Cross(c.Tan[i], c.Up[i]) * side;
        }

        static Vector3D ToLocal(Vector3D world, MatrixD frame)
        {
            return Vector3D.TransformNormal(world - frame.Translation, MatrixD.Transpose(frame));
        }

        // The rail at the cursor; Distance from pos.
        public TrackSample Sample(Vector3D pos, double lateral)
        {
            SpanCurve c = Curve;
            MatrixD frame = Frame;
            int i = c.Index(S);
            double f = c.Fraction(i, S);
            Vector3D t = Vector3D.Normalize(Vector3D.Lerp(c.Tan[i], c.Tan[i + 1], f));
            Vector3D up = Vector3D.Lerp(c.Up[i], c.Up[i + 1], f);
            up = Vector3D.Normalize(up - t * Vector3D.Dot(up, t));
            Vector3D p = Vector3D.Lerp(c.Pos[i], c.Pos[i + 1], f) + Vector3D.Cross(t, up) * (lateral * Dir);
            TrackSample sample = new TrackSample
            {
                Point = Vector3D.Transform(p, frame),
                Tangent = Vector3D.TransformNormal(t, frame) * Dir,
                Up = Vector3D.TransformNormal(up, frame),
                Carrier = Carrier,
            };
            sample.Distance = Vector3D.Distance(pos, sample.Point);
            return sample;
        }

        // ---- moving along the track ----

        // Moves the cursor `distance` along its direction (back if negative) across pieces. False: stopped at
        // the end of the track (DeadEnd), or the piece is no rail any more (the cursor is reset).
        public bool Advance(double distance)
        {
            if (!Valid)
                return false;
            if (!Rideable)
            {
                Reset();
                return false;
            }
            int move = distance >= 0 ? Dir : -Dir;
            double left = Math.Abs(distance);
            for (int hop = 0; hop < MaxHops; hop++)
            {
                double length = Curve.Length;
                double room = move > 0 ? length - S : S;
                if (left <= room)
                {
                    S += move * left;
                    DeadEnd = false;
                    return true;
                }
                left -= room;
                if (!Cross(move > 0 ? 1 : 0, ref move))
                {
                    DeadEnd = true;
                    return false;
                }
            }
            return false;
        }

        // Metres from S to the end of the piece the cursor faces.
        public double PieceLeft
        {
            get { return Dir > 0 ? Curve.Length - S : S; }
        }

        public bool SamePiece(TrackCursor other)
        {
            return Span == other.Span && Lane == other.Lane;
        }

        // Moves to the start of the next piece the cursor faces (route walks: Route). False: the end of the track,
        // or the piece is no rail any more.
        public bool NextPiece()
        {
            if (!Valid || !Rideable)
                return false;
            int move = Dir;
            return Cross(Dir > 0 ? 1 : 0, ref move);
        }

        // Leaves the piece through `end` (0 = S 0, 1 = S Length) moving `move` (+1 / -1 along S); on the next
        // piece `move` is its own and Dir keeps facing the same way along the track. False: nothing there
        // (the cursor stays at the end).
        bool Cross(int end, ref int move)
        {
            Span nextSpan;
            TrackLane nextLane;
            int entry;
            if (!Next(end, out nextSpan, out nextLane, out entry))
            {
                S = end == 0 ? 0 : Curve.Length;
                return false;
            }
            int nextMove = entry == 0 ? 1 : -1;
            Dir = Dir * move * nextMove;
            move = nextMove;
            Span = nextSpan;
            Lane = nextLane;
            S = entry == 0 ? 0 : Curve.Length;
            return true;
        }

        // The piece behind an end of the current one and the end it is entered at.
        bool Next(int end, out Span nextSpan, out TrackLane nextLane, out int entry)
        {
            nextSpan = null;
            nextLane = null;
            entry = 0;
            if (Span != null)
                return EnterBlock(end == 0 ? Span.Owner : Span.Partner, end == 0 ? Span.OwnerPort : Span.PartnerPort, out nextLane, out entry);

            TrackNetwork network = Network;
            TrackBlockLogic block = Lane.Block;
            int port = Lane.Port(end);
            Span s = network.SpanAt(block, port);
            if (s != null)
            {
                // A prototype ends the track.
                if (!s.IsRail)
                    return false;
                nextSpan = s;
                entry = s.Owner == block && s.OwnerPort == port ? 0 : 1;
                return true;
            }
            TrackBlockLogic other;
            int otherPort;
            return network.FindJoint(block, port, out other, out otherPort) && EnterBlock(other, otherPort, out nextLane, out entry);
        }

        static bool EnterBlock(TrackBlockLogic block, int port, out TrackLane lane, out int entry)
        {
            lane = block.Rideable ? block.LaneFrom(port) : null;
            entry = lane != null && lane.To == port ? 1 : 0;
            return lane != null;
        }
    }
}
