using System;
using System.Collections.Generic;

namespace MagRail
{
    // The autopilot's route (user decision 2026-10-15: the switches stay as they are; a train never throws them).
    // From a place on the track there is then one way forward and one way back: a switch entry leads into its
    // selected branch, a branch into the entry (TrackCursor). So no graph and no search: a walk along the track
    // the way the cursor faces finds the station or does not. Walked again every tick, so a switch thrown, a rail
    // turned prototype or a block lost on the way shows at once.
    public static class Route
    {
        // A walk gives up after this many pieces or metres (a loop without the station, a very long line).
        const int MaxPieces = 4000;
        public const double MaxLength = 50000;
        // Curve speed scan: a rail point every this many metres.
        const double ScanStep = 2.0;

        static readonly TrackCursor walker = new TrackCursor();

        // Metres along the track from `from` the way it faces to `to`'s place, or -1 if the walk does not get
        // there within maxLength (the end of the track, a loop, too far). `to` only gives the place (piece, S).
        public static double Distance(TrackCursor from, TrackCursor to, double maxLength)
        {
            if (!from.Valid || !to.Valid)
                return -1;
            walker.CopyFrom(from);
            double walked = 0;
            for (int piece = 0; piece < MaxPieces; piece++)
            {
                if (walker.SamePiece(to))
                {
                    double ahead = (to.S - walker.S) * walker.Dir;
                    // On the first piece the station may be behind: walk on (a loop may bring it ahead). Past a
                    // segment not finished yet (PieceLeft ends there) it is not reached.
                    if (ahead >= 0 && ahead <= walker.PieceLeft + 1e-6)
                        return walked + ahead <= maxLength ? walked + ahead : -1;
                }
                walked += walker.PieceLeft;
                if (walked > maxLength || !walker.NextPiece())
                    return -1;
            }
            return -1;
        }

        // A speed limit ahead: at Ahead metres from where the scan started the train may go at most Speed.
        public struct Limit
        {
            public double Ahead, Speed, Radius;
        }

        // Curve limits over `length` metres ahead of `from`: v = sqrt(lateral / curvature) wherever it is below
        // maxSpeed, one point every ScanStep. Lanes inside track blocks are straight.
        public static void CurveLimits(TrackCursor from, double length, double lateral, double maxSpeed, List<Limit> into)
        {
            into.Clear();
            if (!from.Valid)
                return;
            walker.CopyFrom(from);
            double walked = 0;
            for (int piece = 0; piece < MaxPieces && walked < length; piece++)
            {
                double left = walker.PieceLeft;
                if (walker.Span != null)
                {
                    SpanCurve c = walker.Curve;
                    for (double d = 0; d <= left && walked + d <= length; d += ScanStep)
                    {
                        double k = c.Curvature.Count > 0 ? c.Curvature[c.Index(walker.S + d * walker.Dir)] : 0;
                        if (k <= 1e-6)
                            continue;
                        double v = Math.Sqrt(lateral / k);
                        if (v < maxSpeed)
                            into.Add(new Limit { Ahead = walked + d, Speed = v, Radius = 1 / k });
                    }
                }
                walked += left;
                if (!walker.NextPiece())
                    return;
            }
        }

        // The highest speed now that can still slow down at `accel` to every limit ahead (moved has been driven
        // since the scan) and that the train, `trainLength` long, keeps until its tail has left a limit too.
        // radius: the curve that sets it (0 = none does).
        public static double Allowed(List<Limit> limits, double moved, double accel, double trainLength, double maxSpeed, out double radius)
        {
            double v = maxSpeed;
            radius = 0;
            for (int i = 0; i < limits.Count; i++)
            {
                Limit l = limits[i];
                double ahead = l.Ahead - moved;
                if (ahead < -trainLength)
                    continue;
                double here = ahead <= 0 ? l.Speed : Math.Sqrt(l.Speed * l.Speed + 2 * accel * ahead);
                if (here < v)
                {
                    v = here;
                    radius = l.Radius;
                }
            }
            return v;
        }
    }
}
