using System;
using System.Collections.Generic;
using System.Globalization;
using VRage.Game.Entity;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRageMath;

namespace MagRail
{
    // Cosmetic: the engine's round lower part (subpart "Swivel") lies along the rail under it. It turns about the
    // base's Up (yaw), then tilts about its own Right (pitch), both within the model's limits. The base is the block:
    // the physics, the guide point and the engine's orientation are the base's, nothing here is synced. The part is
    // symmetric front to back, so it takes the rail's way nearer to the base's Forward: the engine has no direction.
    // Each model sets its own limits: custom properties "yaw_limit" and "pitch_limit" (degrees) on its dummy
    // subpart_Swivel, which is also the pivot. A model without that subpart is a one-piece engine: nothing turns.
    // Clients only; its own cursor, so the physics' and the debug cursors are not moved.
    public class EngineSwivel
    {
        public const string PartName = "Swivel";
        const string DummyName = "subpart_" + PartName;

        // Limits of a model that does not set them, degrees.
        const double DefaultYawLimit = 45, DefaultPitchLimit = 12;
        // Share of the way to the target angles per tick: following the rail, going back to rest without one.
        const double Follow = 0.3, Return = 0.06;
        const double ViewDistance = 300;
        // Without a rail the index is searched this often (ticks); a part at rest on a still block is looked at
        // this often (a switch flipped under a parked train).
        const int SearchTicks = 10, IdleTicks = 30;

        readonly HoverEngineLogic engine;
        readonly TrackCursor cursor = new TrackCursor();
        MyEntitySubpart part;
        Matrix rest;            // the part's place in the model
        double yawLimit, pitchLimit;
        double yaw, pitch;
        bool settled;
        MatrixD lastWorld;
        int nextSearch, nextIdle;

        public EngineSwivel(HoverEngineLogic engine)
        {
            this.engine = engine;
        }

        public void Update(Vector3D camera, int tick)
        {
            MyEntitySubpart sub;
            if (!engine.Block.TryGetSubpart(PartName, out sub))
            {
                part = null;
                return;
            }
            if (sub != part)
            {
                // A new model (rebuilt block): the part starts at its place in it.
                part = sub;
                rest = sub.PositionComp.LocalMatrixRef;
                ReadLimits();
                yaw = pitch = 0;
                settled = false;
            }
            MatrixD world = engine.Block.WorldMatrix;
            if (Vector3D.DistanceSquared(world.Translation, camera) > ViewDistance * ViewDistance)
                return;
            if (settled && tick < nextIdle && Same(world, lastWorld))
                return;
            lastWorld = world;
            nextIdle = tick + IdleTicks;

            double targetYaw = 0, targetPitch = 0;
            bool onRail = Target(world, tick, ref targetYaw, ref targetPitch);
            double k = onRail ? Follow : Return;
            double dy = targetYaw - yaw, dp = targetPitch - pitch;
            settled = Math.Abs(dy) < 1e-4 && Math.Abs(dp) < 1e-4;
            yaw = settled ? targetYaw : yaw + dy * k;
            pitch = settled ? targetPitch : pitch + dp * k;

            // Pitch about the part's Right, then yaw about the base's Up (VRageMath: v * M, Forward = -Z).
            Matrix local = Matrix.CreateRotationX((float)pitch) * Matrix.CreateRotationY((float)-yaw) * rest;
            part.PositionComp.SetLocalMatrix(ref local);
        }

        // The angles that lay the part along the rail under its pivot; false: no rail for it (back to rest).
        bool Target(MatrixD world, int tick, ref double targetYaw, ref double targetPitch)
        {
            if (!engine.Active)
            {
                cursor.Reset();
                return false;
            }
            if (!cursor.Valid && tick < nextSearch)
                return false;
            if (!cursor.Valid)
                nextSearch = tick + SearchTicks;
            Vector3D pivot = Vector3D.Transform(rest.Translation, world);
            TrackSample s;
            if (!cursor.TryProject(pivot, world.Forward, engine.SearchDistance, 0, out s, engine.Block.CubeGrid,
                    engine.HoverHeight + HoverEngineLogic.CaptureDistance)
                || s.Distance > engine.HoverHeight + HoverEngineLogic.BreakawayDistance
                || Vector3D.Dot(world.Up, s.Up) < 0.5)
            {
                // Like the physics' cursor: one not over the engine's rail is not kept.
                cursor.Reset();
                return false;
            }
            // The rail's way in the part's rest frame, turned to the base's front half.
            Vector3D t = Vector3D.TransformNormal(Vector3D.TransformNormal(s.Tangent, MatrixD.Transpose(world)), Matrix.Transpose(rest));
            if (t.Z > 0)
                t = -t;
            targetYaw = MathHelper.Clamp(Math.Atan2(t.X, -t.Z), -yawLimit, yawLimit);
            targetPitch = MathHelper.Clamp(Math.Atan2(t.Y, Math.Sqrt(t.X * t.X + t.Z * t.Z)), -pitchLimit, pitchLimit);
            return true;
        }

        static readonly Dictionary<string, IMyModelDummy> dummies = new Dictionary<string, IMyModelDummy>();

        // The limits from the block model's swivel dummy (each model its own).
        void ReadLimits()
        {
            yawLimit = DefaultYawLimit;
            pitchLimit = DefaultPitchLimit;
            IMyModel model = engine.Block.Model;
            IMyModelDummy dummy;
            if (model != null)
            {
                dummies.Clear();
                model.GetDummies(dummies);
                if (dummies.TryGetValue(DummyName, out dummy) && dummy.CustomData != null)
                {
                    yawLimit = Degrees(dummy.CustomData, "yaw_limit", yawLimit);
                    pitchLimit = Degrees(dummy.CustomData, "pitch_limit", pitchLimit);
                }
                dummies.Clear();
            }
            yawLimit = MathHelper.ToRadians(MathHelper.Clamp(yawLimit, 0, 90));
            pitchLimit = MathHelper.ToRadians(MathHelper.Clamp(pitchLimit, 0, 90));
        }

        static double Degrees(IReadOnlyDictionary<string, object> data, string key, double fallback)
        {
            object value;
            double d;
            return data.TryGetValue(key, out value) && value != null
                && double.TryParse(value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out d) ? d : fallback;
        }

        static bool Same(MatrixD a, MatrixD b)
        {
            return Vector3D.DistanceSquared(a.Translation, b.Translation) < 1e-6
                && Vector3D.Dot(a.Forward, b.Forward) > 1 - 1e-8 && Vector3D.Dot(a.Up, b.Up) > 1 - 1e-8;
        }
    }
}
