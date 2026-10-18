using System;
using System.Collections.Generic;
using System.Globalization;
using Sandbox.ModAPI;
using VRage.Game.Entity;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRageMath;

namespace MagRail
{
    // Cosmetic (PLAN 10.23): the engine's platform, subpart "Platform" under the base (the block), hung on struts. It
    // keeps the player's gap (EngineSettings.PlatformGap) between the rail's top and its bottom and lies parallel to
    // the rail, where it would be under a base held ideally (the guide point offsets move the whole engine), as long as
    // the struts reach: past their stroke the base drags it along (only moved, it stays parallel to the rail), and as
    // far as it may turn from the base: past a limit it stays turned at the limit. Off the rail it goes back to rest. The engine has an axis but no front: the platform takes the rail's way nearer the
    // base's Forward.
    // Everything comes from the model, so each engine model has its own:
    // - dummy subpart_Platform: the platform's place at rest; the platform's origin is its bottom centre, so the
    //   dummy's depth under the block centre is the base's height over the platform, and the engine holds rail top +
    //   gap + that (HoverEngineLogic.HoverHeight); its custom properties "yaw_limit", "pitch_limit", "roll_limit"
    //   (degrees): how far the platform may turn from rest (yaw about the base's Up, then pitch, then roll about its
    //   own forward; a missing one: no limit);
    // - dummies strut_<n> in the block model (base pivots) and strut_<n> in the platform model (platform pivots): a
    //   strut runs between the two of one name. Struts have two sections (StrutLook.Two): a sleeve from the base and a
    //   rod from the platform. On the base's dummy: "min" / "max", the stroke (pivot to pivot, m); "parts", the section
    //   models <parts>Sleeve / <parts>Rod, and "sleeve" / "rod", their lengths (m). A model without struts: the
    //   platform alone, nothing limits it.
    // Clients only; its own cursor, so the physics' and the debug cursors are not moved. Nothing is synced: the
    // platform follows the rail and the base every client sees.
    public class EnginePlatform
    {
        public const string PartName = "Platform";
        const string DummyName = "subpart_" + PartName, StrutPrefix = "strut_";

        // Share of the way per tick from rest to the rail after capture, and back to rest without a rail.
        const double BlendIn = 0.1, BlendOut = 0.06;
        const double ViewDistance = 300;
        // Struts exist within this camera distance, m (removed beyond HideDistance).
        const double ShowDistance = 150, HideDistance = 170;
        // Without a rail the index is searched this often (ticks); a platform on a still block is looked at this
        // often (a switch flipped under a parked train).
        const int SearchTicks = 10, IdleTicks = 30;
        // The stroke limit: steps towards rest, then halvings (1/32768 of the way); a strut this much past its stroke
        // (m) still fits.
        const int LimitSteps = 8, LimitHalvings = 12;
        const float StrokeTolerance = 1e-4f;

        // TEMPORARY (test T2, /magrail platform <m>): every base is drawn as if it hung this much lower (the platform
        // this much higher under it), to see the struts reach their stroke without touching the physics.
        public static float DebugSink;

        // The copper's top over the rail point: the gap is measured from it (the same in every style).
        public static double RailTop
        {
            get { return TrackStyle.Narrow.Top; }
        }

        struct Mount
        {
            public Vector3 Base, Platform;      // pivots: block coordinates, platform coordinates
            public float Min, Max;
            public StrutLook Look;
        }

        readonly HoverEngineLogic engine;
        readonly TrackCursor cursor = new TrackCursor();
        readonly List<Mount> mounts = new List<Mount>();
        Strut[] struts;
        MyEntitySubpart part;
        Matrix rest;            // the platform's place in the model
        Matrix target;          // its last place on the rail (block coordinates)
        double yawLimit, pitchLimit, rollLimit;     // radians
        double blend;           // 0 at rest .. 1 on the rail
        bool settled;
        MatrixD lastWorld;
        int nextSearch, nextIdle;

        public EnginePlatform(HoverEngineLogic engine)
        {
            this.engine = engine;
        }

        static readonly Dictionary<string, IMyModelDummy> dummies = new Dictionary<string, IMyModelDummy>(),
            partDummies = new Dictionary<string, IMyModelDummy>();

        // The base's height over the platform at rest (the platform dummy's depth under the block centre); false: the
        // model has no platform.
        public static bool ReadRest(IMyModel model, out double rest)
        {
            rest = 0;
            if (model == null)
                return false;
            dummies.Clear();
            model.GetDummies(dummies);
            IMyModelDummy dummy;
            bool found = dummies.TryGetValue(DummyName, out dummy);
            if (found)
                rest = -dummy.Matrix.Translation.Y;
            dummies.Clear();
            return found;
        }

        public void Update(Vector3D camera, int tick)
        {
            MyEntitySubpart sub;
            if (!engine.Block.TryGetSubpart(PartName, out sub))
            {
                part = null;
                RemoveStruts();
                return;
            }
            if (sub != part)
            {
                // A new model (rebuilt block): the platform starts at its place in it.
                part = sub;
                rest = sub.PositionComp.LocalMatrixRef;
                target = rest;
                blend = 0;
                settled = false;
                ReadMounts();
                ReadTurnLimits();
                RemoveStruts();
            }
            MatrixD world = engine.Block.WorldMatrix;
            double distSq = Vector3D.DistanceSquared(world.Translation, camera);
            double show = struts != null ? HideDistance : ShowDistance;
            if (distSq > show * show)
                RemoveStruts();
            else if (struts == null && mounts.Count > 0)
                AddStruts();
            if (distSq > ViewDistance * ViewDistance)
                return;
            if (settled && tick < nextIdle && Same(world, lastWorld) && !(struts != null && !struts[0].Placed))
                return;
            lastWorld = world;
            nextIdle = tick + IdleTicks;

            Matrix onRail;
            if (Target(world, tick, out onRail))
            {
                target = onRail;
                blend = Math.Min(1, blend + BlendIn);
                settled = blend >= 1;
            }
            else
            {
                blend = Math.Max(0, blend - BlendOut);
                settled = blend <= 0;
            }
            Matrix local = blend >= 1 ? target : blend <= 0 ? rest : Between(rest, target, (float)blend);
            part.PositionComp.SetLocalMatrix(ref local);
            PlaceStruts(local);
        }

        // The platform's place on the rail, in block coordinates, within the struts' stroke; false: no rail for it.
        bool Target(MatrixD world, int tick, out Matrix local)
        {
            local = rest;
            if (!engine.Active)
            {
                cursor.Reset();
                return false;
            }
            if (!cursor.Valid && tick < nextSearch)
                return false;
            if (!cursor.Valid)
                nextSearch = tick + SearchTicks;
            TrackSample s;
            double error;
            RailState state = engine.Probe(cursor, world.Forward, out s, out error);
            // Like the physics: held near, let go far; one not over the engine's rail is not kept.
            if (state != RailState.Held && !(state == RailState.Far && error <= HoverEngineLogic.BreakawayDistance))
            {
                cursor.Reset();
                return false;
            }
            Vector3D up = Vector3D.Normalize(s.Up);
            Vector3D forward = s.Tangent - up * Vector3D.Dot(s.Tangent, up);
            if (forward.LengthSquared() < 1e-8)
                return false;
            forward.Normalize();
            if (Vector3D.Dot(forward, world.Forward) < 0)
                forward = -forward;
            // The base as the rail holds it ideally: along the rail, its guide point at the hover height over the rail.
            // The platform hangs under it as at rest, so the side offset moves the whole engine.
            EngineSettings set = engine.Settings;
            MatrixD ideal = MatrixD.CreateWorld(Vector3D.Zero, forward, up);
            ideal.Translation = s.Point + up * (engine.HoverHeight + DebugSink)
                - ideal.Right * set.SideOffset;
            local = (Matrix)((MatrixD)rest * ideal * MatrixD.Invert(world));
            LimitTurn(ref local);
            Limit(ref local);
            return true;
        }

        // Past the struts' stroke the base drags the platform: it moves (not turning) straight towards its place at rest
        // under the base, to the first place where every strut is within its stroke (a few steps, then halving). The
        // same for compressed and stretched struts, exact and even: under a base lifted or pressed straight the
        // platform moves straight. (Struts pulling the platform in turn slid it back or forward, user test
        // 2026-10-18; a compressed strut pushing along itself could push it through the base.)
        void Limit(ref Matrix local)
        {
            if (mounts.Count == 0 || Fits(local))
                return;
            Vector3 from = local.Translation, to = rest.Translation;
            float lo = 0;
            for (int k = 1; k <= LimitSteps; k++)
            {
                float hi = k / (float)LimitSteps;
                local.Translation = Vector3.Lerp(from, to, hi);
                if (!Fits(local))
                {
                    lo = hi;
                    continue;
                }
                for (int h = 0; h < LimitHalvings; h++)
                {
                    float mid = (lo + hi) * 0.5f;
                    local.Translation = Vector3.Lerp(from, to, mid);
                    if (Fits(local))
                        hi = mid;
                    else
                        lo = mid;
                }
                local.Translation = Vector3.Lerp(from, to, hi);
                return;
            }
            local.Translation = to;
        }

        bool Fits(Matrix local)
        {
            for (int i = 0; i < mounts.Count; i++)
            {
                Mount m = mounts[i];
                float len = Vector3.Distance(Vector3.Transform(m.Platform, local), m.Base);
                if (len < m.Min - StrokeTolerance || len > m.Max + StrokeTolerance)
                    return false;
            }
            return true;
        }

        // The platform turned from rest no further than the model allows, about its own origin: yaw about the base's
        // Up, then pitch about the platform's Right, then roll about its Forward (EngineSwivel's yaw and pitch).
        void LimitTurn(ref Matrix local)
        {
            Matrix restTurn = rest;
            restTurn.Translation = Vector3.Zero;
            Matrix back = Matrix.Transpose(restTurn);
            Vector3 f = Vector3.TransformNormal(local.Forward, back), u = Vector3.TransformNormal(local.Up, back);
            double yaw = Math.Atan2(f.X, -f.Z), pitch = Math.Atan2(f.Y, Math.Sqrt(f.X * f.X + f.Z * f.Z));
            Vector3 level = LevelUp(f);
            double roll = Math.Atan2(Vector3.Dot(Vector3.Cross(level, u), f), Vector3.Dot(level, u));
            double y = MathHelper.Clamp(yaw, -yawLimit, yawLimit), p = MathHelper.Clamp(pitch, -pitchLimit, pitchLimit),
                r = MathHelper.Clamp(roll, -rollLimit, rollLimit);
            if (y == yaw && p == pitch && r == roll)
                return;
            Vector3 f2 = new Vector3(Math.Sin(y) * Math.Cos(p), Math.Sin(p), -Math.Cos(y) * Math.Cos(p));
            Vector3 level2 = LevelUp(f2);
            Vector3 u2 = level2 * (float)Math.Cos(r) + Vector3.Cross(f2, level2) * (float)Math.Sin(r);
            Matrix turned = Matrix.CreateWorld(Vector3.Zero, f2, u2) * restTurn;
            turned.Translation = local.Translation;
            local = turned;
        }

        // The up of a frame looking along f without roll (its right level), in the rest frame.
        static Vector3 LevelUp(Vector3 f)
        {
            Vector3 up = Vector3.Up - f * f.Y;
            return up.LengthSquared() > 1e-8f ? Vector3.Normalize(up) : Vector3.Backward;
        }

        // The turn limits from the block model's platform dummy (each model its own); a missing one: no limit.
        void ReadTurnLimits()
        {
            yawLimit = pitchLimit = rollLimit = Math.PI;
            IMyModel model = engine.Block.Model;
            if (model == null)
                return;
            dummies.Clear();
            model.GetDummies(dummies);
            IMyModelDummy dummy;
            if (dummies.TryGetValue(DummyName, out dummy) && dummy.CustomData != null)
            {
                yawLimit = MathHelper.ToRadians(MathHelper.Clamp(Number(dummy.CustomData, "yaw_limit", 180f), 0f, 180f));
                pitchLimit = MathHelper.ToRadians(MathHelper.Clamp(Number(dummy.CustomData, "pitch_limit", 180f), 0f, 180f));
                rollLimit = MathHelper.ToRadians(MathHelper.Clamp(Number(dummy.CustomData, "roll_limit", 180f), 0f, 180f));
            }
            dummies.Clear();
        }

        // ---- struts ----

        // The strut pairs of the block model and the platform model.
        void ReadMounts()
        {
            mounts.Clear();
            IMyModel model = engine.Block.Model;
            IMyModel partModel = ((IMyEntity)part).Model;
            if (model == null || partModel == null)
                return;
            dummies.Clear();
            partDummies.Clear();
            model.GetDummies(dummies);
            partModel.GetDummies(partDummies);
            foreach (KeyValuePair<string, IMyModelDummy> pair in dummies)
            {
                IMyModelDummy other;
                if (!pair.Key.StartsWith(StrutPrefix) || !partDummies.TryGetValue(pair.Key, out other))
                    continue;
                IReadOnlyDictionary<string, object> data = pair.Value.CustomData;
                object parts;
                string prefix = data != null && data.TryGetValue("parts", out parts) && parts != null ? parts.ToString() : null;
                if (string.IsNullOrEmpty(prefix))
                {
                    Log.Info("EnginePlatform: " + engine.Block.BlockDefinition.SubtypeId + " " + pair.Key + " has no 'parts'");
                    continue;
                }
                StrutLook look = StrutLook.Two(prefix, Number(data, "sleeve", 0.5f), Number(data, "rod", 0.5f));
                mounts.Add(new Mount
                {
                    Base = pair.Value.Matrix.Translation,
                    Platform = other.Matrix.Translation,
                    Min = Number(data, "min", 0f),
                    Max = Number(data, "max", float.MaxValue),
                    Look = look,
                });
            }
            dummies.Clear();
            partDummies.Clear();
        }

        void AddStruts()
        {
            MyEntity grid = (MyEntity)engine.Block.CubeGrid;
            struts = new Strut[mounts.Count];
            for (int i = 0; i < struts.Length; i++)
                struts[i] = new Strut(mounts[i].Look, grid);
        }

        void PlaceStruts(Matrix local)
        {
            if (struts == null)
                return;
            if (struts[0].Parent != (MyEntity)engine.Block.CubeGrid)
            {
                // The block went over to another grid (split, merge).
                RemoveStruts();
                AddStruts();
            }
            Matrix blockLocal = engine.Block.PositionComp.LocalMatrixRef;
            for (int i = 0; i < struts.Length; i++)
                struts[i].Place(mounts[i].Base, Vector3.Transform(mounts[i].Platform, local), blockLocal);
        }

        void RemoveStruts()
        {
            if (struts == null)
                return;
            foreach (Strut s in struts)
                s.Remove();
            struts = null;
        }

        // The block is closed.
        public void Remove()
        {
            RemoveStruts();
        }

        // ---- helpers ----

        static float Number(IReadOnlyDictionary<string, object> data, string key, float fallback)
        {
            object value;
            float f;
            return data != null && data.TryGetValue(key, out value) && value != null
                && float.TryParse(value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out f) ? f : fallback;
        }

        static Matrix Between(Matrix a, Matrix b, float t)
        {
            Quaternion qa = Quaternion.CreateFromRotationMatrix(a), qb = Quaternion.CreateFromRotationMatrix(b);
            Matrix m = Matrix.CreateFromQuaternion(Quaternion.Slerp(qa, qb, t));
            m.Translation = Vector3.Lerp(a.Translation, b.Translation, t);
            return m;
        }

        static bool Same(MatrixD a, MatrixD b)
        {
            return Vector3D.DistanceSquared(a.Translation, b.Translation) < 1e-6
                && Vector3D.Dot(a.Forward, b.Forward) > 1 - 1e-8 && Vector3D.Dot(a.Up, b.Up) > 1 - 1e-8;
        }
    }
}
