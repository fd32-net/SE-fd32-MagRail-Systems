using System;
using System.Collections.Generic;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.ModAPI;
using VRage.Game;
using VRage.Game.Entity;
using VRage.Game.ModAPI;
using VRage.Game.ModAPI.Interfaces;
using VRage.Utils;
using VRageMath;
using VRageRender;
using IMyEntity = VRage.ModAPI.IMyEntity;

namespace MagRail
{
    // Damage to spans in rail mode (PLAN 10.8, after welding): bullets, explosions, missiles. Spans are not blocks, so the
    // game's weapons do not see them; the mod catches the hits itself and takes them off the weld segment hit as a block
    // loses integrity (SpanWelding.Damage: the stage shown stays until a tool touches it, out of service under the working
    // line, at nothing the segment is destroyed to stage 0; the last one with anything in it destroyed takes the span, for
    // good). A segment at stage 0 is not there for weapons: bullets, missiles and blasts go through. The worlds' and the grids' "destructible blocks" settings hold for spans too (the owner's and
    // the partner's grid).
    // Bullets: every span is a projectile detector (IMyProjectileDetector, SpanTarget): the game asks it about each
    // bullet's flight line within its box, whatever the distance (collision pieces exist only near players) and the span
    // answers with its collision chords' boxes (the section's face outline, as the tools aim). The hit entity is a part of
    // the span (SpanPart): the game makes its metal sparks and sound there, and its bullet holes, which the part puts on
    // the nearest shown track piece. The server takes the bullet's damage off the segment (HitInterceptor, after other
    // mods may have changed it; zero in a safe zone, as the game sets it).
    // Explosions (MyExplosions.OnExplosion, server): every segment within the blast (radius x 1.25, as the game's volumetric
    // one) loses its damage, falling off with the distance.
    // Missiles: they hit collision pieces near players by themselves; elsewhere one whose flight crosses a span is stopped
    // there and blows up (its explosion does the damage; a slug's health pool hits the segment).
    // Collisions (server): a grid hitting a collision piece is deformed by the game as by any static body, its blocks'
    // damage naming the piece as the attacker (MyDamageInformation.AttackerId); the segment nearest the block takes as
    // much (a block the impact takes off whole: its integrity). A block stuck in a rail grinds it down so.
    // Client: segments built finished whose integrity damage took under the working line (SpanWeld.Lost) smoke as a
    // vanilla block damaged under its working line (its "Damage_HeavyMech_Damaged" effect).
    public static class SpanDamage
    {
        const int RefreshTicks = 10;
        // A span's detector box: its curve's bounding sphere and this, m (the section stands over and under the curve).
        const double BoxMargin = 2;
        // Vanilla volumetric explosions reach this far past their sphere.
        const double BlastReach = 1.25;
        const int InterceptorPriority = 100;
        // After other mods' handlers (a shield may take the damage away).
        const int DamagePriority = 1000;

        static readonly Dictionary<Span, SpanTarget> targets = new Dictionary<Span, SpanTarget>();
        static readonly List<Span> gone = new List<Span>();
        static bool loaded;
        static int tick, stamp;

        public static void Load()
        {
            if (MyAPIGateway.Projectiles != null)
                MyAPIGateway.Projectiles.AddOnHitInterceptor(InterceptorPriority, OnHit);
            if (MyAPIGateway.Missiles != null)
                MyAPIGateway.Missiles.OnMissileMoved += OnMissileMoved;
            MyExplosions.OnExplosion += OnExplosion;
            loaded = true;
        }

        // The world is loaded (the damage system is there). The game has no way to take a damage handler off: it goes
        // with the session.
        public static void Start()
        {
            if (Net.IsServer && MyAPIGateway.Session.DamageSystem != null)
                MyAPIGateway.Session.DamageSystem.RegisterBeforeDamageHandler(DamagePriority, OnDamage);
        }

        public static void Unload()
        {
            if (!loaded)
                return;
            loaded = false;
            if (MyAPIGateway.Projectiles != null)
            {
                MyAPIGateway.Projectiles.RemoveOnHitInterceptor(OnHit);
                foreach (SpanTarget t in targets.Values)
                    MyAPIGateway.Projectiles.RemoveHitDetector(t);
            }
            if (MyAPIGateway.Missiles != null)
                MyAPIGateway.Missiles.OnMissileMoved -= OnMissileMoved;
            MyExplosions.OnExplosion -= OnExplosion;
            targets.Clear();
            StopSmoke();
        }

        static TrackNetwork Network
        {
            get { return MagRailSession.Instance != null ? MagRailSession.Instance.Network : null; }
        }

        // ---- detectors (server and client: every machine flies the bullets) ----

        public static void Update(TrackNetwork network, int now)
        {
            tick = now;
            if (now % RefreshTicks != 0 || MyAPIGateway.Projectiles == null)
                return;
            stamp++;
            for (int i = 0; i < network.Spans.Count; i++)
            {
                Span s = network.Spans[i];
                SpanTarget t;
                bool has = targets.TryGetValue(s, out t);
                if (!s.Bound || !s.IsRail || !s.Owner.Style.Solid)
                    continue;
                if (has)
                    t.Stamp = stamp;
                TrackLayout layout = network.LayoutOf(s);
                BoundingSphereD bounds = layout.Curve.Bounds;
                BoundingSphereD world = new BoundingSphereD(Vector3D.Transform(bounds.Center, TrackNetwork.FrameMatrix(s)),
                    bounds.Radius + BoxMargin);
                BoundingBoxD box = BoundingBoxD.CreateFromSphere(world);
                if (has && t.Box.Contains(box) == ContainmentType.Contains)
                    continue;
                if (has)
                    MyAPIGateway.Projectiles.RemoveHitDetector(t);
                else
                {
                    t = new SpanTarget(s) { Stamp = stamp };
                    targets[s] = t;
                }
                // A span moving with its grid: room for the next refreshes.
                var physics = s.Owner.Block.CubeGrid.Physics;
                if (physics != null && !s.Owner.Block.CubeGrid.IsStatic)
                    box.Inflate(physics.LinearVelocity.Length() * RefreshTicks * 2 / 60.0);
                t.Box = box;
                MyAPIGateway.Projectiles.AddHitDetector(t);
            }
            // Gone, or no longer a rail.
            foreach (KeyValuePair<Span, SpanTarget> pair in targets)
                if (pair.Value.Stamp != stamp)
                    gone.Add(pair.Key);
            for (int i = 0; i < gone.Count; i++)
            {
                MyAPIGateway.Projectiles.RemoveHitDetector(targets[gone[i]]);
                targets.Remove(gone[i]);
            }
            gone.Clear();
        }

        // The nearest collision chord box of span s a line from `origin` along `direction` (normalized) meets within
        // `length` (not on a segment at stage 0): the distance to it and its weld segment.
        public static bool Intersect(TrackNetwork network, Span s, Vector3D origin, Vector3D direction, double length,
            out double at, out int segment)
        {
            at = length;
            segment = -1;
            TrackLayout layout = network.LayoutOf(s);
            if (layout == null)
                return false;
            MatrixD frame = TrackNetwork.FrameMatrix(s);
            TrackStyle style = layout.Style;
            float[] done = s.Weld(layout);
            for (int j = 0; j < layout.Collision.Count; j++)
            {
                if (done != null && done[layout.CollisionSegment[j]] <= 0)
                    continue;
                double half = layout.CollisionHalf[j];
                MatrixD m = layout.Collision[j] * frame;
                Vector3D c = m.Translation;
                double along = Math.Max(0, Math.Min(length, Vector3D.Dot(c - origin, direction)));
                if (Vector3D.DistanceSquared(origin + direction * along, c) > (half + BoxMargin) * (half + BoxMargin))
                    continue;
                BoundingBoxD box = new BoundingBoxD(new Vector3D(-style.FaceHalfWidth, style.FaceBottom, -half),
                    new Vector3D(style.FaceHalfWidth, style.FaceTop, half));
                MatrixD inverse = MatrixD.Invert(m);
                double? hit = box.Intersects(new RayD(Vector3D.Transform(origin, inverse), Vector3D.TransformNormal(direction, inverse)));
                if (hit.HasValue && hit.Value <= at)
                {
                    at = hit.Value;
                    segment = layout.CollisionSegment[j];
                }
            }
            return segment >= 0;
        }

        // The weld segment of span s nearest to a world point (its collision chord's box); -1: none, or that one is at
        // stage 0.
        static int SegmentNear(TrackNetwork network, Span s, Vector3D point)
        {
            TrackLayout layout = network.LayoutOf(s);
            if (layout == null)
                return -1;
            MatrixD frame = TrackNetwork.FrameMatrix(s);
            TrackStyle style = layout.Style;
            int best = -1;
            double bestDistance = double.MaxValue;
            for (int j = 0; j < layout.Collision.Count; j++)
            {
                double d = BoxDistance(layout.Collision[j] * frame, layout.CollisionHalf[j], style, point);
                if (d < bestDistance)
                {
                    bestDistance = d;
                    best = layout.CollisionSegment[j];
                }
            }
            float[] done = s.Weld(layout);
            return best >= 0 && done != null && done[best] <= 0 ? -1 : best;
        }

        // From a world point to a collision chord's face box (m: the chord in the world), m.
        static double BoxDistance(MatrixD m, double half, TrackStyle style, Vector3D point)
        {
            Vector3D local = Vector3D.Transform(point, MatrixD.Invert(m));
            Vector3D near = Vector3D.Clamp(local, new Vector3D(-style.FaceHalfWidth, style.FaceBottom, -half),
                new Vector3D(style.FaceHalfWidth, style.FaceTop, half));
            return Vector3D.Distance(local, near);
        }

        // Neither the world nor the grids under it keep the span from harm.
        static bool Destructible(Span s)
        {
            return MyAPIGateway.Session.SessionSettings.DestructibleBlocks
                && ((MyCubeGrid)s.Owner.Block.CubeGrid).BlocksDestructionEnabled
                && (s.Partner == null || ((MyCubeGrid)s.Partner.Block.CubeGrid).BlocksDestructionEnabled);
        }

        // ---- hits ----

        // Every machine: after the game found the bullet's target, before it shows the hit.
        static void OnHit(ref MyProjectileInfo projectile, ref MyProjectileHitInfo info)
        {
            try
            {
                SpanPart part = info.HitEntity as SpanPart;
                TrackNetwork network = Network;
                if (part == null || network == null || !part.Span.Bound || !part.Span.IsRail)
                    return;
                int g = SegmentNear(network, part.Span, info.HitPosition);
                // A collision piece (hit before the detector: rare): it has no render object, the game would put its sparks
                // in its frame as if in the world. On a segment at stage 0 (its low frame near a player stops the bullet:
                // physics) the bullet just ends, unseen.
                if (part.InScene)
                    info.AddHitParticles = false;
                if (g < 0)
                {
                    info.AddDecals = info.AddHitParticles = info.PlayHitSound = false;
                    info.Damage = 0;
                    return;
                }
                if (Net.IsServer && info.Damage > 0 && Destructible(part.Span))
                    SpanWelding.Damage(network, part.Span, g, info.Damage, tick);
            }
            catch (Exception e)
            {
                Log.Error("SpanDamage.OnHit", e);
            }
        }

        struct Blast
        {
            public Span Span;
            public int Segment;
            public double Distance;
        }

        static readonly List<Blast> blasts = new List<Blast>();

        // Server: every segment in the blast, its nearest point's distance; then the damage (a span may go meanwhile).
        static void OnExplosion(ref MyExplosionInfo info)
        {
            try
            {
                TrackNetwork network = Network;
                if (!Net.IsServer || network == null || !info.ApplyForceAndDamage || info.Damage <= 0)
                    return;
                BoundingSphereD sphere = info.ExplosionSphere;
                double reach = sphere.Radius * BlastReach;
                for (int i = 0; i < network.Spans.Count; i++)
                {
                    Span s = network.Spans[i];
                    if (!s.Bound || !s.IsRail || !s.Owner.Style.Solid || !Destructible(s))
                        continue;
                    TrackLayout layout = network.LayoutOf(s);
                    MatrixD frame = TrackNetwork.FrameMatrix(s);
                    BoundingSphereD bounds = layout.Curve.Bounds;
                    if (Vector3D.Distance(Vector3D.Transform(bounds.Center, frame), sphere.Center) - bounds.Radius > reach + BoxMargin)
                        continue;
                    int first = blasts.Count;
                    float[] done = s.Weld(layout);
                    for (int j = 0; j < layout.Collision.Count; j++)
                    {
                        if (done != null && done[layout.CollisionSegment[j]] <= 0)
                            continue;
                        double half = layout.CollisionHalf[j];
                        MatrixD m = layout.Collision[j] * frame;
                        if (Vector3D.Distance(m.Translation, sphere.Center) > reach + half + BoxMargin)
                            continue;
                        double d = BoxDistance(m, half, layout.Style, sphere.Center);
                        if (d > reach)
                            continue;
                        int g = layout.CollisionSegment[j];
                        int k = first;
                        while (k < blasts.Count && blasts[k].Segment != g)
                            k++;
                        if (k < blasts.Count)
                        {
                            if (d < blasts[k].Distance)
                                blasts[k] = new Blast { Span = s, Segment = g, Distance = d };
                        }
                        else
                            blasts.Add(new Blast { Span = s, Segment = g, Distance = d });
                    }
                }
                for (int i = 0; i < blasts.Count; i++)
                    if (blasts[i].Span.Bound)
                        SpanWelding.Damage(network, blasts[i].Span, blasts[i].Segment,
                            info.Damage * (float)(1 - blasts[i].Distance / reach), tick);
            }
            catch (Exception e)
            {
                Log.Error("SpanDamage.OnExplosion", e);
            }
            finally
            {
                blasts.Clear();
            }
        }

        // Every machine, each frame of a missile's flight: the stretch it flies this frame (whether the game moved it
        // already or its body will) crossing a span stops it there, to blow up the next frame (MaxTrajectory reached; a
        // client's copy just goes, the server's explosion comes over the network).
        static void OnMissileMoved(IMyMissile missile, ref Vector3 velocity)
        {
            try
            {
                TrackNetwork network = Network;
                if (network == null || targets.Count == 0 || missile.MarkedForClose)
                    return;
                Vector3D step = (Vector3D)velocity / 60.0;
                double length = step.Length();
                if (length < 1e-3)
                    return;
                Vector3D from = missile.GetPosition() - step, direction = step / length;
                LineD line = new LineD(from, from + direction * length * 2);
                double best = length * 2;
                Span span = null;
                int bestSegment = -1;
                foreach (SpanTarget t in targets.Values)
                {
                    double at;
                    int segment;
                    if (!t.Span.Bound || !t.Box.Intersects(ref line))
                        continue;
                    if (Intersect(network, t.Span, from, direction, best, out at, out segment) && at < best)
                    {
                        best = at;
                        span = t.Span;
                        bestSegment = segment;
                    }
                }
                if (span == null)
                    return;
                missile.SetPosition(from + direction * best);
                missile.LinearVelocity = Vector3.Zero;
                if (missile.Physics != null)
                    missile.Physics.LinearVelocity = Vector3.Zero;
                missile.MaxTrajectory = 0;
                // A slug (artillery, railgun: a missile with a health pool) does its damage by hitting, as on a block.
                if (Net.IsServer && missile.HealthPool > 0 && Destructible(span))
                    SpanWelding.Damage(network, span, bestSegment, missile.HealthPool, tick);
            }
            catch (Exception e)
            {
                Log.Error("SpanDamage.OnMissileMoved", e);
            }
        }

        // Server, maybe off the main thread (deformation runs in the physics' callbacks and in parallel tasks): a block
        // deformed by a hit on a collision piece; the span takes it on the main thread.
        static void OnDamage(object target, ref MyDamageInformation info)
        {
            if (!loaded || !info.IsDeformation || info.AttackerId == 0 || info.Amount <= 0)
                return;
            IMySlimBlock block = target as IMySlimBlock;
            if (block == null)
                return;
            // The game asks with 1 about a block the impact takes off whole (MyGridPhysics.ApplyDeformation): -1.
            float amount = info.Amount == 1 ? -1 : info.Amount;
            long attacker = info.AttackerId;
            MyAPIGateway.Utilities.InvokeOnGameThread(() => Collided(attacker, block, amount));
        }

        // amount -1: the block is taken off whole, its integrity; asked again about it (more contact points), it is gone.
        static void Collided(long attacker, IMySlimBlock block, float amount)
        {
            try
            {
                if (amount < 0)
                {
                    if (block.CubeGrid.GetCubeBlock(block.Position) != block)
                        return;
                    amount = block.Integrity;
                }
                TrackNetwork network = Network;
                IMyEntity entity;
                if (!loaded || network == null || !MyAPIGateway.Entities.TryGetEntityById(attacker, out entity))
                    return;
                SpanPart part = entity as SpanPart;
                if (part == null || !part.Span.Bound || !part.Span.IsRail || !Destructible(part.Span))
                    return;
                Vector3D center;
                block.ComputeWorldCenter(out center);
                int g = SegmentNear(network, part.Span, center);
                if (g >= 0)
                    SpanWelding.Damage(network, part.Span, g, amount, tick);
            }
            catch (Exception e)
            {
                Log.Error("SpanDamage.Collided", e);
            }
        }

        // ---- smoke (client) ----

        const string SmokeEffect = "Damage_HeavyMech_Damaged";
        const double SmokeRange = 1000;
        const int SmokeTicks = 30;

        class Smoke
        {
            public Span Span;
            public int Segment, Stamp;
            public MyParticleEffect Effect;
        }

        static readonly List<Smoke> smokes = new List<Smoke>();
        static int smokeStamp;

        // A smoke at the middle of every segment near the camera built finished but damaged under the working line, there
        // while it is (and placed again while its span moves with a grid).
        public static void ClientUpdate(TrackNetwork network, int now)
        {
            if (now % SmokeTicks != 0 || MyAPIGateway.Session.Camera == null)
                return;
            Vector3D camera = MyAPIGateway.Session.Camera.WorldMatrix.Translation;
            smokeStamp++;
            for (int i = 0; i < network.Spans.Count; i++)
            {
                Span s = network.Spans[i];
                SpanWeld weld = s.Record.Weld;
                if (!s.Bound || !s.IsRail || weld == null || weld.Damaged == null)
                    continue;
                TrackLayout layout = network.LayoutOf(s);
                MatrixD frame = TrackNetwork.FrameMatrix(s);
                BoundingSphereD bounds = layout.Curve.Bounds;
                if (Vector3D.Distance(Vector3D.Transform(bounds.Center, frame), camera) - bounds.Radius > SmokeRange)
                    continue;
                float[] done = s.Weld(layout), integrity = s.Integrity(layout);
                for (int h = 0; h < weld.Damaged.Length; h++)
                {
                    int g = weld.Damaged[h];
                    if (g < 0 || g >= done.Length || SpanWeld.Stage(done[g], layout.Style.Working) != SpanWeld.Finished
                        || SpanWeld.Stage(integrity[g], layout.Style.Working) == SpanWeld.Finished)
                        continue;
                    MatrixD at;
                    if (!SegmentMiddle(layout, frame, g, out at))
                        continue;
                    Smoke smoke = null;
                    for (int k = 0; k < smokes.Count && smoke == null; k++)
                        if (smokes[k].Span == s && smokes[k].Segment == g)
                            smoke = smokes[k];
                    if (smoke == null)
                    {
                        smoke = new Smoke { Span = s, Segment = g };
                        smokes.Add(smoke);
                    }
                    if (smoke.Effect == null || smoke.Effect.IsStopped)
                    {
                        Vector3D point = at.Translation;
                        MyParticleEffect effect;
                        smoke.Effect = MyParticlesManager.TryCreateParticleEffect(SmokeEffect, ref at, ref point, uint.MaxValue, out effect)
                            ? effect : null;
                        if (smoke.Effect != null)
                            smoke.Effect.UserScale = (float)(0.45 + 0.15 * layout.Style.Width);
                    }
                    else
                        smoke.Effect.WorldMatrix = at;
                    smoke.Stamp = smokeStamp;
                }
            }
            for (int k = smokes.Count - 1; k >= 0; k--)
                if (smokes[k].Stamp != smokeStamp)
                {
                    if (smokes[k].Effect != null)
                        smokes[k].Effect.Stop(false);
                    smokes.RemoveAt(k);
                }
        }

        // The world matrix of segment g's middle collision chord (its up: the rail's up).
        static bool SegmentMiddle(TrackLayout layout, MatrixD frame, int g, out MatrixD at)
        {
            int first = -1, last = -1;
            for (int j = 0; j < layout.Collision.Count; j++)
                if (layout.CollisionSegment[j] == g)
                {
                    if (first < 0)
                        first = j;
                    last = j;
                }
            if (first < 0)
            {
                at = MatrixD.Identity;
                return false;
            }
            MatrixD m = layout.Collision[(first + last) / 2] * frame;
            at = MatrixD.CreateWorld(m.Translation, Vector3D.Normalize(m.Forward), Vector3D.Normalize(m.Up));
            return true;
        }

        static void StopSmoke()
        {
            for (int k = 0; k < smokes.Count; k++)
                if (smokes[k].Effect != null)
                    smokes[k].Effect.Stop();
            smokes.Clear();
        }
    }

    // A span's projectile detector (SpanDamage): its box, bullets' lines tested against its collision chords.
    public class SpanTarget : IMyProjectileDetector
    {
        public readonly Span Span;
        public BoundingBoxD Box;
        public int Stamp;
        // What the game is told a bullet hit: a part of the span not in the scene (world frame: the game's sparks land at
        // the hit point).
        readonly SpanPart part;

        public SpanTarget(Span span)
        {
            Span = span;
            part = new SpanPart(span);
        }

        public bool IsDetectorEnabled
        {
            get { return Span.Bound && Span.IsRail; }
        }

        public IMyEntity HitEntity
        {
            get { return part; }
        }

        public BoundingBoxD DetectorAABB
        {
            get { return Box; }
        }

        public bool GetDetectorIntersectionWithLine(ref LineD line, out Vector3D? hit)
        {
            hit = null;
            TrackNetwork network = MagRailSession.Instance != null ? MagRailSession.Instance.Network : null;
            double at;
            int segment;
            if (network == null || !IsDetectorEnabled || !SpanDamage.Intersect(network, Span, line.From, line.Direction, line.Length, out at, out segment))
                return false;
            hit = line.From + line.Direction * at;
            return true;
        }
    }

    // An entity standing for a span where something hits it: its collision pieces (TrackCollision) and what its detector
    // reports (SpanTarget). Marks of the hits go on the span's nearest shown track piece (bullet holes, missile scorches).
    public class SpanPart : MyEntity, IMyDecalProxy
    {
        public readonly Span Span;

        public SpanPart(Span span)
        {
            Span = span;
        }

        public void AddDecals(ref MyHitInfo hitInfo, MyStringHash source, Vector3 forwardDirection, object customdata,
            IMyDecalHandler decalHandler, MyStringHash physicalMaterial, MyStringHash voxelMaterial, bool isTrail,
            MyDecalFlags flags = MyDecalFlags.None, int aliveUntil = int.MaxValue, List<uint> outids = null)
        {
            TrackPieces pieces = MagRailSession.Instance != null ? MagRailSession.Instance.Pieces : null;
            MyEntity piece = pieces != null ? pieces.PieceNear(Span, hitInfo.Position) : null;
            if (piece == null)
                return;
            MyStringHash material = physicalMaterial.GetHashCode() == 0 ? Metal : physicalMaterial;
            MyDecalRenderInfo info = new MyDecalRenderInfo
            {
                Source = source,
                PhysicalMaterial = material,
                VoxelMaterial = material,
                Forward = forwardDirection,
                Flags = flags,
                AliveUntil = aliveUntil,
                IsTrail = isTrail,
                Position = Vector3D.Transform(hitInfo.Position, piece.PositionComp.WorldMatrixInvScaled),
                Normal = Vector3D.TransformNormal(hitInfo.Normal, piece.PositionComp.WorldMatrixInvScaled),
                RenderObjectIds = piece.Render.RenderObjectIDs,
            };
            decalHandler.AddDecal(ref info, outids);
        }

        static readonly MyStringHash Metal = MyStringHash.GetOrCompute("Metal");
    }
}
