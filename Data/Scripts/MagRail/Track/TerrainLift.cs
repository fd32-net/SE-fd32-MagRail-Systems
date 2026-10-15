using System;
using System.Collections.Generic;
using System.Diagnostics;
using Sandbox.Game.Entities;
using VRage.ModAPI;
using VRage.Voxels;
using VRageMath;

namespace MagRail
{
    // A span over planet ground rests on it like a flexible batten (Batten): where the rail would run into the ground,
    // the span is lifted (straight up from the planet's centre; the course stays) by the batten that
    // bends least over the whole span, so it spans small bumps, rests on the high ones, and its curvature changes
    // smoothly (no jolt where the rail starts to rise); over pits and wherever it runs clear of the ground it keeps its
    // own curve: the nodes set the rail's height. An end sunk into the ground lets the rail run as deep near it.
    // Not strict: the rail may sink into the ground by half its height, and near its ends (it leaves its ports
    // level) further.
    // The ground is read from the voxels (IMyStorage.ReadRange; sees digging, unlike the planet's height map).
    // A prototype is lifted live (Measure, then Apply); a span switched to rail keeps the lift measured then
    // (SpanRecord.Lift): built is built, the ground changing later does not move it.
    public static class TerrainLift
    {
        // Ground samples along the span, m.
        const double Step = 1.0;
        // Near an end the lift is at most an arc of this radius from it (the rail leaves its ports level), m.
        const double EndRadius = 60;
        // Window searched for the ground around the height map, m; moved up or down at most MaxMoves times.
        const double Window = 4;
        const int MaxMoves = 12;
        // Voxels read in one go at most.
        const int MaxBox = 16384;

        // Statistics (chat /magrail).
        public static int Calls, Lifted;
        public static long Voxels;
        public static double TotalMs, WorstMs;

        static readonly MyStorageData data = new MyStorageData(MyStorageDataTypeFlags.Content);
        static Vector3I boxMin, boxMax = new Vector3I(-1);
        static IMyStorage storage;
        static Vector3D corner;
        static readonly List<double> clear = new List<double>();
        static double[] need = new double[0], lift = new double[0];

        // The curve lifted over the ground as it is now (a prototype, the Rail Linker's preview).
        public static SpanCurve Live(SpanCurve c, MatrixD frame, TrackStyle style, bool freeEnd = false)
        {
            return Apply(c, frame, Measure(c, frame, style, freeEnd));
        }

        // The lift over the ground along the curve, m, at points evenly over its length (about every Step); null
        // where nothing needs lifting or no planet is near. frame: the span's frame (the curve's points are in it).
        // freeEnd: the end is no node (the Rail Linker's preview aiming at no port, its end hanging in the air): the
        // start's clearance holds all along. style: the rail's width, feet and height (TrackStyle).
        public static float[] Measure(SpanCurve c, MatrixD frame, TrackStyle style, bool freeEnd = false)
        {
            Vector3D center = Vector3D.Transform(c.Bounds.Center, frame);
            MyPlanet planet = MyGamePruningStructure.GetClosestPlanet(center);
            if (planet == null)
                return null;
            Vector3D pc = planet.PositionComp.GetPosition();
            if (Vector3D.Distance(center, pc) - c.Bounds.Radius > planet.MaximumRadius + 2)
                return null;

            Stopwatch watch = Stopwatch.StartNew();
            Calls++;
            storage = ((IMyVoxelBase)planet).Storage;
            corner = ((IMyVoxelBase)planet).PositionLeftBottomCorner;
            boxMax = new Vector3I(-1);
            boxMin = Vector3I.Zero;
            try
            {
                return Rope(c, frame, style, freeEnd, planet, pc);
            }
            finally
            {
                storage = null;
                double ms = watch.Elapsed.TotalMilliseconds;
                TotalMs += ms;
                WorstMs = Math.Max(WorstMs, ms);
            }
        }

        // The curve with its points moved up (from the nearest planet's centre) by a measured lift.
        public static SpanCurve Apply(SpanCurve c, MatrixD frame, float[] lift)
        {
            if (lift == null || lift.Length < 2)
                return c;
            Vector3D center = Vector3D.Transform(c.Bounds.Center, frame);
            MyPlanet planet = MyGamePruningStructure.GetClosestPlanet(center);
            if (planet == null)
                return c;
            Vector3D pc = planet.PositionComp.GetPosition();
            MatrixD toFrame = MatrixD.Invert(frame);
            int n = lift.Length - 1;
            Vector3D[] offset = new Vector3D[c.Pos.Count];
            for (int i = 0; i < offset.Length; i++)
            {
                double x = c.Length > 1e-6 ? c.S[i] / c.Length * n : 0;
                int k = Math.Min(n - 1, (int)x);
                double l = lift[k] + (lift[k + 1] - lift[k]) * MathHelper.Clamp(x - k, 0, 1);
                if (l <= 1e-4)
                    continue;
                Vector3D p = Vector3D.Transform(c.Pos[i], frame);
                offset[i] = Vector3D.TransformNormal(Vector3D.Normalize(p - pc) * l, toFrame);
            }
            return c.Shifted(offset);
        }

        static float[] Rope(SpanCurve c, MatrixD frame, TrackStyle style, bool freeEnd, MyPlanet planet, Vector3D pc)
        {
            int n = Math.Max(2, (int)Math.Ceiling(c.Length / Step));
            // The rail may sink this much into the ground: half its height, feet to copper top.
            double tolerance = (style.Feet + style.Top) * 0.5;
            if (need.Length < n + 1)
            {
                need = new double[n + 1];
                lift = new double[n + 1];
            }

            // Clearance of the rail over the ground at every sample: the lowest of the middle and both edges.
            clear.Clear();
            for (int k = 0; k <= n; k++)
            {
                double s = c.Length * k / n;
                Vector3D p = Vector3D.Transform(c.PosAt(s), frame);
                Vector3D up = Vector3D.Normalize(p - pc);
                Vector3D side = Vector3D.Cross(Vector3D.TransformNormal(c.TanAt(s), frame), up);
                side = side.LengthSquared() > 1e-8 ? Vector3D.Normalize(side) * (style.Width * 0.5) : Vector3D.Zero;
                double r = Vector3D.Distance(p, pc) - style.Feet;
                double ground = Ground(planet, pc, p);
                ground = Math.Max(ground, Ground(planet, pc, p + side));
                ground = Math.Max(ground, Ground(planet, pc, p - side));
                clear.Add(r - ground);
            }

            // How far the rail would run into the ground at each sample. Only that lifts it: a rail over the ground
            // keeps its curve however high it runs. An end sunk into the ground lets the rail run as deep near it,
            // changing evenly to the other end.
            double c0 = Math.Min(0, clear[0]), c1 = freeEnd ? c0 : Math.Min(0, clear[n]), most = 0;
            for (int k = 0; k <= n; k++)
            {
                double want = c0 + (c1 - c0) * k / n;
                need[k] = Math.Max(0, want - clear[k] - tolerance);
                most = Math.Max(most, need[k]);
            }
            if (most <= 0)
                return null;

            // Near the ends the rail may cut into the ground rather than turn hard: it leaves its ports level.
            for (int k = 0; k <= n; k++)
                need[k] = Math.Min(need[k], EndLimit(c.Length * k / n, c.Length));
            Batten.Solve(need, n, lift);
            Lifted++;
            float[] result = new float[n + 1];
            for (int k = 0; k <= n; k++)
                result[k] = (float)lift[k];
            return result;
        }

        // The ends leave their ports level: at most an arc of EndRadius from each end.
        static double EndLimit(double s, double length)
        {
            double d = Math.Min(s, length - s);
            return d * d / (2 * EndRadius);
        }

        // Distance from the planet's centre to the top of the ground under (and over) point p.
        static double Ground(MyPlanet planet, Vector3D pc, Vector3D p)
        {
            Vector3D up = Vector3D.Normalize(p - pc);
            double guess = Vector3D.Distance(planet.GetClosestSurfacePointGlobal(ref p), pc);
            double lo = guess - Window, hi = guess + Window;
            for (int move = 0; move <= MaxMoves; move++)
            {
                // From the top down, the first solid sample; between it and the one above lies the surface.
                const double step = 0.25;
                float aboveContent = Content(pc + up * hi);
                if (aboveContent >= 127.5f)
                {
                    lo = hi - 1;
                    hi += 2 * Window;
                    continue;
                }
                for (double r = hi - step; r >= lo; r -= step)
                {
                    float v = Content(pc + up * r);
                    if (v >= 127.5f)
                        return r + step * (127.5f - v) / (aboveContent - v);
                    aboveContent = v;
                }
                hi = lo + 1;
                lo -= 2 * Window;
            }
            return guess;
        }

        // Voxel content at a world point (0 empty .. 255 full), trilinear between the voxel corners.
        static float Content(Vector3D world)
        {
            Vector3D local = world - corner;
            Vector3I v = new Vector3I((int)Math.Floor(local.X), (int)Math.Floor(local.Y), (int)Math.Floor(local.Z));
            if (!(v.X >= boxMin.X && v.Y >= boxMin.Y && v.Z >= boxMin.Z && v.X + 1 <= boxMax.X && v.Y + 1 <= boxMax.Y && v.Z + 1 <= boxMax.Z))
                Read(v);
            Vector3D f = local - new Vector3D(v.X, v.Y, v.Z);
            Vector3I o = v - boxMin;
            float c000 = data.Content(o.X, o.Y, o.Z), c100 = data.Content(o.X + 1, o.Y, o.Z);
            float c010 = data.Content(o.X, o.Y + 1, o.Z), c110 = data.Content(o.X + 1, o.Y + 1, o.Z);
            float c001 = data.Content(o.X, o.Y, o.Z + 1), c101 = data.Content(o.X + 1, o.Y, o.Z + 1);
            float c011 = data.Content(o.X, o.Y + 1, o.Z + 1), c111 = data.Content(o.X + 1, o.Y + 1, o.Z + 1);
            float fx = (float)f.X, fy = (float)f.Y, fz = (float)f.Z;
            float x00 = c000 + (c100 - c000) * fx, x10 = c010 + (c110 - c010) * fx;
            float x01 = c001 + (c101 - c001) * fx, x11 = c011 + (c111 - c011) * fx;
            float y0 = x00 + (x10 - x00) * fy, y1 = x01 + (x11 - x01) * fy;
            return y0 + (y1 - y0) * fz;
        }

        // Reads a box of voxels around v (a cube of 2 x Window + 2 voxels, at most MaxBox).
        static void Read(Vector3I v)
        {
            int half = (int)Window + 1;
            boxMin = v - new Vector3I(half);
            boxMax = v + new Vector3I(half + 1);
            Vector3I size = boxMax - boxMin + Vector3I.One;
            if (size.X * size.Y * size.Z > MaxBox)
            {
                boxMin = v;
                boxMax = v + Vector3I.One;
                size = new Vector3I(2);
            }
            data.Resize(size);
            storage.ReadRange(data, MyStorageDataTypeFlags.Content, 0, boxMin, boxMax);
            Voxels += size.X * size.Y * size.Z;
        }
    }
}
