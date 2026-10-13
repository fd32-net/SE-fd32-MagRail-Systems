using System;
using System.Collections.Generic;
using Sandbox.ModAPI;
using VRage.Game.Components;
using VRage.Game.ModAPI;
using VRageMath;

namespace MagRail
{
    // One grid held on the rail by its hover engines, solved as one rigid body; the server writes it. Ported
    // from the old Maglev (MaglevEngineLogic, MaglevRailBody) with its tuning numbers:
    // - every engine that holds the rail is a guide point with a critically damped spring across the rail;
    // - one least-squares solve turns the points, the rail frame's orientation wish and "keep the speed along
    //   the rail" into one change of the grid's linear and angular velocity about its centre of mass;
    // - along the rail only the drive (Drive: thrust, brakes, parking) and a little drag act; couplers, gravity and
    //   outside forces move the train too; the momentum is turned with the rail in curves;
    // - only the hold is limited by the engines' force: following the rail's curves is the rail's job.
    // On a rail inside a moving ship the speeds are taken relative to the rail and the rail's acceleration across
    // it is fed forward like gravity; along the rail the brakes hold it (they cancel the rail's acceleration along
    // it up front, as far as their force goes), else it slides.
    // Guards against clang (PLAN 10.7): hold acceleration and spin change limits, an engine whose error grows
    // fast lets go early, an engine whose rail point jumps is ignored for a few ticks, an engine past the end
    // of the track does not hold (HoverEngineLogic.Probe).
    // The rail is a force (user decision 2026-10-10): couplers, other mods and joints act after it and have the
    // last word. So there is no disturbance observer (the old Maglev's V2.3.3 "disturbance hold", which expected
    // the last tick's outside push again and cancelled it up front). Against a coupler that acts after the rail
    // it wound up about tenfold: in tight curves rail and couplers fought, trains kinked at the couplers and a
    // standing train drove itself along the track (ride logs 2026-10-10). Outside forces now shift a wagon a
    // little against its springs; gravity and the rail's own acceleration, which do not react to us, are fed
    // forward.
    public class RailBody
    {
        const double Step = 1.0 / 60.0;
        const int CaptureRampTicks = 30;
        // Rail drag, m/s^2: constant part + air-like part (0.05 at rest, ~0.13 at 20 m/s, ~2 at 100 m/s).
        const double RollingDrag = 0.05, AirDrag = 0.0002;
        // A point spring at lever r weighs r^2 on rotation: with engines a few metres apart the points steer
        // yaw and pitch and the rail frame mostly keeps roll (and everything on a single-engine grid).
        const double OrientationRowWeight = 1.0, AlongRowWeight = 100.0;
        // Guards.
        const double MaxHoldAccel = 100.0;              // m/s^2 from the hold, whatever the engines' force
        const double MaxAngularAccel = 20.0;            // rad/s^2 of the whole solve
        const double JumpLimit = 1.0;                   // m the rail point may move against its engine in a tick
        const int JumpIgnoreTicks = 10;                 // a jump that stays is taken (a joint to a rail at another height)
        const double EarlyBreakError = 2.0, EarlyBreakRate = 10.0;     // m, and m/s of its growth
        // The carrier's acceleration (measured from its velocity change per tick) is smoothed by this much.
        const double CarrierFilter = 0.5;
        // An engine without rail searches less often after this many ticks.
        const int SearchSlow = 30, SearchSlower = 300;

        struct GuidePoint
        {
            public Vector3D Position, Target, Tangent, Up, RailPoint, RailVelocity;
            public double Lock, Force;
            public IMyCubeGrid Carrier;
        }

        public readonly IMyCubeGrid Grid;
        public readonly List<HoverEngineLogic> Engines = new List<HoverEngineLogic>();
        public readonly Drive Drive;
        readonly List<GuidePoint> points = new List<GuidePoint>();
        readonly List<HoverEngineLogic> held = new List<HoverEngineLogic>();
        readonly TrackCursor ahead = new TrackCursor();
        static readonly double[] matrix = new double[36], rhs = new double[6], row = new double[6];

        // /magrail debug (server): the last tick's hold and spin change against their limits (1 = at the limit)
        // and the outside push (the change of velocity, m/s, and spin, rad/s, since our last write that we did
        // not cause: couplers, contacts, hits, thrusters).
        public double HoldLoad, SpinLoad, PushLinear, PushAngular;
        // /magrail debug: how many times an engine of this grid let go of the rail.
        public int Drops;
        // /magrail log (RideLog): the last tick's state. Speed along the rail (relative to it),
        // the largest guide point error, the body's turn from the rail frame (yaw / pitch / roll, degrees), the
        // outside push per tick as measured (along / side / up the rail, m/s; spin, rad/s) and the engines holding.
        public double Speed, MaxError, Yaw, Pitch, Roll, PushAlong, PushSide, PushUp;
        public int HeldCount;
        // Where the first holding engine is: the span (its two blocks) or the block, its place on it (m), the
        // rail's radius there (m, 0 = straight).
        public string Place = "";
        public double PlaceS, Radius;
        Vector3D rawPush, rawSpin, orientationError;

        bool captured;
        int capturedTicks;
        int forwardAxis = -1;           // grid axis (0 Forward, 1 Left, 2 Up; sign-free) held along the rail
        Vector3D lastRailForward, lastRailUp;
        bool hasLastRail;
        Vector3D lastControlForward;    // rail direction of last tick's velocity (momentum carry)
        bool hasLastControl;
        Vector3D lastDesiredForward, lastDesiredUp;     // last tick's wished frame: its change is the rail's turn rate
        bool hasLastDesired;
        Vector3D lastWrittenLinear, lastWrittenAngular;
        int lastWrittenTick = -999;
        IMyCubeGrid lastCarrier;        // the moving grid under the rail last tick and its motion then
        int lastCarrierTick = -999;
        Vector3D lastCarrierLinear, lastCarrierAngular, carrierAccel, carrierAlpha;

        public bool Holding
        {
            get { return captured; }
        }

        public RailBody(IMyCubeGrid grid)
        {
            Grid = grid;
            Drive = new Drive(grid);
        }

        public void Update(int tick)
        {
            HeldCount = 0;
            MyPhysicsComponentBase physics = Grid.Physics;
            if (physics == null || Grid.IsStatic)
            {
                Release();
                return;
            }
            Vector3D v = physics.LinearVelocity, w = physics.AngularVelocity;
            Vector3D com = physics.CenterOfMassWorld;
            Vector3D searchForward = hasLastRail ? lastRailForward
                : v.LengthSquared() > 0.36 ? Vector3D.Normalize(v) : Engines[0].Block.WorldMatrix.Forward;

            points.Clear();
            held.Clear();
            HoverEngineLogic first = null;
            Vector3D firstTangent = Vector3D.Zero;
            IMyCubeGrid carrier = null;
            bool skipped = false;
            double lockSum = 0;
            for (int i = 0; i < Engines.Count; i++)
            {
                HoverEngineLogic e = Engines[i];
                TrackSample s;
                int result = Track(e, tick, searchForward, out s);
                if (result == 1)
                    skipped = true;
                if (result != 2)
                    continue;
                if (first == null)
                {
                    first = e;
                    firstTangent = s.Tangent;
                    carrier = s.Carrier;
                }
                lockSum += e.Settings.HoldStrength;
                held.Add(e);
                points.Add(new GuidePoint
                {
                    Position = e.GuidePoint,
                    Target = s.Point + s.Up * e.HoverHeight,
                    Tangent = Orient(s.Tangent, searchForward),
                    Up = s.Up,
                    RailPoint = s.Point,
                    RailVelocity = VelocityAt(s.Carrier, s.Point),
                    Lock = e.Settings.HoldStrength,
                    Force = e.HoldForce,
                    Carrier = s.Carrier,
                });
            }
            if (points.Count == 0)
            {
                // An engine ignored for a jump keeps the body's state; no write this tick.
                if (!skipped)
                    Release();
                return;
            }

            UpdateCarrier(carrier, tick);
            Vector3D carrierVelocity = VelocityAt(carrier, com);
            Vector3D relative = v - carrierVelocity;
            Vector3D railForward, up, pathForward;
            Frame(first, firstTangent, relative, com, out railForward, out up, out pathForward);
            double frameSpeed = relative.Length();
            SmoothFrame(ref railForward, ref up, frameSpeed);

            if (!captured)
            {
                captured = true;
                capturedTicks = 0;
                forwardAxis = PickForwardAxis(railForward, first.Block.WorldMatrix.Up);
            }
            if (capturedTicks < CaptureRampTicks)
                capturedTicks++;
            double ramp = capturedTicks / (double)CaptureRampTicks;

            if (Vector3D.Dot(pathForward, railForward) < 0)
                pathForward = -pathForward;
            Vector3D carried = CarryAroundCurve(relative, pathForward) + carrierVelocity;
            double speed = Math.Abs(Vector3D.Dot(relative, pathForward));
            Vector3D gravity = Gravity(com);
            MeasurePush(tick, v, w, gravity);

            double railLock = MathHelper.Clamp(lockSum / points.Count, 0.15, 3.0);
            Vector3D orientation = OrientationChange(railForward, up, first.Block.WorldMatrix.Up, speed, railLock, w);
            Vector3D linearChange, angularChange;
            if (!Solve(com, carried, w, pathForward, gravity, speed, orientation, out linearChange, out angularChange))
            {
                linearChange = Vector3D.Zero;
                angularChange = Vector3D.Zero;
            }
            linearChange *= ramp;
            angularChange *= ramp;
            double holdLimit = HoldLimit(physics.Mass), spinLimit = MaxAngularAccel * Step;
            HoldLoad = linearChange.Length() / holdLimit;
            SpinLoad = angularChange.Length() / spinLimit;
            linearChange = ClampLength(linearChange, holdLimit);
            angularChange = ClampLength(angularChange, spinLimit);
            linearChange += carried - v;

            double along = Vector3D.Dot(v + linearChange - carrierVelocity, pathForward);
            double drag = Math.Min(Math.Abs(along), (RollingDrag + AirDrag * along * along) * Step * ramp);
            linearChange -= pathForward * (Math.Sign(along) * drag);
            along -= Math.Sign(along) * drag;

            // The drive: thrust at the centre of mass, then the brakes (Drive).
            Drive.ReadInput(tick);
            double thrust = Drive.Thrust(held, pathForward, along, physics.Mass, ramp, tick);
            linearChange += pathForward * thrust;
            along += thrust;
            double carriedAlong = Vector3D.Dot(carried - carrierVelocity, pathForward);
            linearChange += pathForward * Drive.Brake(carriedAlong, along, physics.Mass, ramp, thrust == 0, lastWrittenTick == tick - 1);

            Vector3D newLinear = v + linearChange, newAngular = w + angularChange;
            if (!IsFinite(newLinear) || !IsFinite(newAngular))
            {
                Release();
                return;
            }
            physics.LinearVelocity = newLinear;
            physics.AngularVelocity = newAngular;
            lastWrittenLinear = newLinear;
            lastWrittenAngular = newAngular;
            lastWrittenTick = tick;
            Record(first, relative, pathForward, railForward, up);
        }

        // The state RideLog writes.
        void Record(HoverEngineLogic first, Vector3D relative, Vector3D pathForward, Vector3D railForward, Vector3D up)
        {
            HeldCount = points.Count;
            TrackCursor c = first.Cursor;
            Place = !c.Valid ? "?" : c.Span != null
                ? "span " + c.Span.Owner.Block.CustomName + " - " + (c.Span.Partner != null ? c.Span.Partner.Block.CustomName : "?")
                : "block " + c.Lane.Block.Block.CustomName;
            PlaceS = c.S;
            Radius = 0;
            SpanCurve curve = c.Valid ? c.Curve : null;
            if (curve != null && curve.Curvature.Count > 0)
            {
                double k = curve.Curvature[curve.Index(c.S)];
                Radius = k > 1e-4 ? 1.0 / k : 0;
            }
            Speed = Vector3D.Dot(relative, pathForward);
            MaxError = 0;
            for (int i = 0; i < points.Count; i++)
                MaxError = Math.Max(MaxError, Vector3D.Distance(points[i].Target, points[i].Position));
            Vector3D side = Vector3D.Cross(railForward, up);
            const double Deg = 180.0 / Math.PI;
            Yaw = Vector3D.Dot(orientationError, up) * Deg;
            Pitch = Vector3D.Dot(orientationError, side) * Deg;
            Roll = Vector3D.Dot(orientationError, railForward) * Deg;
            PushAlong = Vector3D.Dot(rawPush, pathForward);
            PushSide = Vector3D.Dot(rawPush, side);
            PushUp = Vector3D.Dot(rawPush, up);
        }

        // ---- engines ----

        // 0: the engine does not hold; 1: it holds but is ignored this tick (its rail point jumped); 2: it holds.
        // An engine that does not hold searches the rail afresh every time (no cursor kept).
        int Track(HoverEngineLogic e, int tick, Vector3D searchForward, out TrackSample s)
        {
            s = new TrackSample();
            if (!e.Active)
            {
                e.Status = RailState.Off;
                e.Release();
                return 0;
            }
            if (e.LostTicks > 0 && tick < e.NextSearchTick)
                return 0;
            double error;
            RailState state = e.Probe(e.Cursor, searchForward, out s, out error);
            // Captured near, let go far; tilted only matters for the capture.
            bool hold = state == RailState.Held
                || (e.Held && (state == RailState.Far || state == RailState.Tilted) && error <= HoverEngineLogic.BreakawayDistance);
            if (hold && e.Held && error > EarlyBreakError && (error - e.LastError) / Step > EarlyBreakRate)
                hold = false;
            double lastError = e.LastError;
            e.LastError = error;
            if (!hold)
            {
                if (e.Held)
                {
                    Drops++;
                    RideLog.Note("drop " + Grid.CustomName + " / " + e.Block.CustomName + ": " + state + ", error "
                        + RideLog.N(error) + " m, growing " + RideLog.N((error - lastError) / Step) + " m/s");
                }
                if (state == RailState.NoRail)
                {
                    e.LostTicks++;
                    e.NextSearchTick = tick + (e.LostTicks < SearchSlow ? 2 : e.LostTicks < SearchSlower ? 12 : 60);
                }
                else
                {
                    e.LostTicks = 0;
                }
                e.Status = state == RailState.Held ? RailState.Far : state;
                e.Release();
                // The next search starts afresh: past a dead end the nearest rail may be a neighbouring track, and
                // a cursor left there would keep projecting onto it while the engine's own rail is under it again.
                e.Cursor.Reset();
                return 0;
            }
            e.LostTicks = 0;
            e.Status = RailState.Held;
            Vector3D offset = s.Point - e.GuidePoint;
            if (e.HasLastOffset && Vector3D.Distance(offset, e.LastOffset) > JumpLimit && ++e.JumpTicks < JumpIgnoreTicks)
            {
                if (e.JumpTicks == 1)
                    RideLog.Note("jump " + Grid.CustomName + " / " + e.Block.CustomName + ": " + RideLog.N(Vector3D.Distance(offset, e.LastOffset)) + " m");
                return 1;
            }
            e.JumpTicks = 0;
            e.LastOffset = offset;
            e.HasLastOffset = true;
            e.Held = true;
            return 2;
        }

        // Body state back to "not on the rail": nothing is written, the grid keeps its momentum and falls.
        void Release()
        {
            captured = false;
            capturedTicks = 0;
            forwardAxis = -1;
            hasLastRail = false;
            hasLastControl = false;
            hasLastDesired = false;
            lastWrittenTick = -999;
            Drive.Release();
        }

        // ---- rail frame ----

        // The rail's direction and up for the body. One engine: the rail under it, turned a little towards the
        // rail ~0.1 s ahead (the grid pitches into a slope or banks into a curve slightly before it gets there);
        // pathForward stays the rail's own tangent. Several: the best-fit line through their targets (long
        // grids follow the chord of a curve, not one end's tangent) and their mean up.
        void Frame(HoverEngineLogic first, Vector3D firstTangent, Vector3D relative, Vector3D com,
            out Vector3D railForward, out Vector3D up, out Vector3D pathForward)
        {
            railForward = points[0].Tangent;
            up = points[0].Up;
            pathForward = railForward;
            if (points.Count == 1)
            {
                double along = Vector3D.Dot(relative, railForward);
                double lead = MathHelper.Clamp(Math.Abs(along) * 0.1, 1.0, 8.0) * (along < -0.5 ? -1 : 1);
                // The cursor's own direction may be the other way round.
                if (Vector3D.Dot(firstTangent, railForward) < 0)
                    lead = -lead;
                ahead.CopyFrom(first.Cursor);
                ahead.Advance(lead);
                if (ahead.Valid)
                {
                    TrackSample a = ahead.Sample(com, 0);
                    railForward = Vector3D.Normalize(Vector3D.Lerp(railForward, Orient(a.Tangent, railForward), 0.12));
                    up = Reject(Vector3D.Lerp(up, a.Up, 0.12), railForward);
                }
                return;
            }

            Vector3D reference = points[0].Tangent;
            Vector3D centroid = Vector3D.Zero, upSum = Vector3D.Zero, weighted = Vector3D.Zero;
            double scoreSum = 0, rear = double.MaxValue, front = double.MinValue;
            Vector3D rearTarget = Vector3D.Zero, frontTarget = Vector3D.Zero;
            for (int i = 0; i < points.Count; i++)
            {
                GuidePoint g = points[i];
                double score = Vector3D.Dot(g.Position, reference);
                centroid += g.Target;
                upSum += g.Up;
                scoreSum += score;
                weighted += g.Target * score;
                if (score < rear)
                {
                    rear = score;
                    rearTarget = g.Target;
                }
                if (score > front)
                {
                    front = score;
                    frontTarget = g.Target;
                }
            }
            centroid /= points.Count;
            Vector3D regression = weighted - centroid * scoreSum;
            Vector3D chosen = regression.LengthSquared() > 0.25 ? regression : frontTarget - rearTarget;
            if (chosen.LengthSquared() > 0.25)
            {
                railForward = Orient(chosen, reference);
                pathForward = railForward;
            }
            if (upSum.LengthSquared() > 1e-6)
                up = Reject(upSum, railForward);
        }

        // Keeps the frame continuous through tight curves and twists: a blend with last tick's, quicker at speed.
        void SmoothFrame(ref Vector3D railForward, ref Vector3D up, double speed)
        {
            double alpha = MathHelper.Clamp(0.58 + MathHelper.Clamp(speed, 0.0, 100.0) / 220.0, 0.58, 0.85);
            if (hasLastRail)
            {
                railForward = Vector3D.Normalize(Vector3D.Lerp(lastRailForward, Orient(railForward, lastRailForward), alpha));
                up = Reject(up, railForward);
                if (Vector3D.Dot(up, lastRailUp) < 0)
                    up = -up;
                up = Reject(Vector3D.Lerp(lastRailUp, up, alpha), railForward);
            }
            else
            {
                up = Reject(up, railForward);
            }
            lastRailForward = railForward;
            lastRailUp = up;
            hasLastRail = true;
        }

        // ---- velocities ----

        // Turns the velocity by the rotation that took last tick's rail direction to this one (the rail's
        // centripetal push), so a curve does not show up as an outward error. A jump over ~11 degrees is not carried.
        Vector3D CarryAroundCurve(Vector3D velocity, Vector3D railForward)
        {
            Vector3D previous = lastControlForward;
            bool hasPrevious = hasLastControl;
            lastControlForward = railForward;
            hasLastControl = true;
            if (!hasPrevious)
                return velocity;
            if (Vector3D.Dot(previous, railForward) < 0)
                previous = -previous;
            double cos = Vector3D.Dot(previous, railForward);
            if (cos < 0.98)
                return velocity;
            Vector3D axis = Vector3D.Cross(previous, railForward);
            double sin = axis.Length();
            if (sin < 1e-9)
                return velocity;
            axis /= sin;
            return velocity * cos + Vector3D.Cross(axis, velocity) * sin + axis * (Vector3D.Dot(axis, velocity) * (1.0 - cos));
        }

        // The outside push of the last tick (only shown, not cancelled: see the class comment).
        void MeasurePush(int tick, Vector3D velocity, Vector3D angular, Vector3D gravity)
        {
            if (lastWrittenTick != tick - 1)
            {
                rawPush = Vector3D.Zero;
                rawSpin = Vector3D.Zero;
            }
            else
            {
                rawPush = velocity - lastWrittenLinear - gravity * Step;
                rawSpin = angular - lastWrittenAngular;
            }
            PushLinear = rawPush.Length();
            PushAngular = rawSpin.Length();
        }

        // The carrier's acceleration and angular acceleration from its velocity change since last tick, smoothed
        // (a hit on the ship is one tick's spike). Another carrier or a gap in the ticks starts from zero.
        void UpdateCarrier(IMyCubeGrid carrier, int tick)
        {
            if (carrier == null || carrier.Physics == null)
            {
                lastCarrier = null;
                return;
            }
            Vector3D linear = carrier.Physics.LinearVelocity, angular = carrier.Physics.AngularVelocity;
            if (carrier == lastCarrier && lastCarrierTick == tick - 1)
            {
                Vector3D accel = ClampLength((linear - lastCarrierLinear) / Step, MaxHoldAccel);
                Vector3D alpha = ClampLength((angular - lastCarrierAngular) / Step, MaxAngularAccel);
                carrierAccel += (accel - carrierAccel) * CarrierFilter;
                carrierAlpha += (alpha - carrierAlpha) * CarrierFilter;
            }
            else
            {
                carrierAccel = Vector3D.Zero;
                carrierAlpha = Vector3D.Zero;
            }
            lastCarrier = carrier;
            lastCarrierTick = tick;
            lastCarrierLinear = linear;
            lastCarrierAngular = angular;
        }

        // Acceleration of the carrier's material point at the guide point's rail point: the ship's own, its
        // angular acceleration's and the centripetal one of its spin. Zero on a station.
        Vector3D RailAcceleration(GuidePoint g)
        {
            if (g.Carrier == null || g.Carrier != lastCarrier || g.Carrier.Physics == null)
                return Vector3D.Zero;
            Vector3D r = g.RailPoint - g.Carrier.Physics.CenterOfMassWorld;
            Vector3D w = g.Carrier.Physics.AngularVelocity;
            return carrierAccel + Vector3D.Cross(carrierAlpha, r) + Vector3D.Cross(w, Vector3D.Cross(w, r));
        }

        // The wished change of angular velocity: from the body frame (the grid axis picked at capture and the
        // engine's up) to the rail frame, plus the rail frame's own turn rate fed forward so curves need no
        // standing error. Stiffer while slow, so couplers squeezing a standing train cannot turn its wagons.
        Vector3D OrientationChange(Vector3D desiredForward, Vector3D desiredUp, Vector3D bodyUp, double speed, double railLock, Vector3D angular)
        {
            if (forwardAxis < 0)
                return Vector3D.Zero;
            bodyUp = Vector3D.Normalize(bodyUp);
            Vector3D bodyForward = Reject(GridAxis(forwardAxis), bodyUp);
            desiredForward = Orient(desiredForward, bodyForward);
            desiredUp = Reject(desiredUp, desiredForward);

            Vector3D railTurn = Vector3D.Zero;
            if (hasLastDesired)
            {
                Vector3D lastForward = Orient(lastDesiredForward, desiredForward);
                Vector3D turn = RotationBetweenFrames(lastForward, lastDesiredUp, desiredForward, desiredUp);
                // A frame jump (re-capture, a joint) is not a rate.
                if (turn.LengthSquared() < 0.05 * 0.05)
                    railTurn = turn / Step;
            }
            lastDesiredForward = desiredForward;
            lastDesiredUp = desiredUp;
            hasLastDesired = true;

            Vector3D error = RotationBetweenFrames(bodyForward, bodyUp, desiredForward, desiredUp);
            orientationError = error;
            double gain = MathHelper.Clamp((3.0 + 2.0 * Math.Max(0.0, 1.0 - speed / 20.0) + speed / 40.0) * railLock, 1.5, 8.0);
            double maxAngular = MathHelper.Clamp(1.85 + speed / 30.0, 1.85, 4.60);
            double blend = MathHelper.Clamp(0.30 + speed / 220.0, 0.30, 0.54);
            return (railTurn + ClampLength(error * gain, maxAngular) - angular) * blend;
        }

        // ---- the solve ----

        static double PointOmega(double railLock, double speed)
        {
            return MathHelper.Clamp((6.5 + Math.Min(speed * 0.04, 3.5)) * railLock, 3.0, 24.0);
        }

        // Velocity change (linear at the centre of mass, angular) that best gives every guide point its spring
        // response across the rail, keeps the along-rail speed and follows the orientation wish.
        bool Solve(Vector3D com, Vector3D velocity, Vector3D angular, Vector3D railForward, Vector3D gravity, double speed,
            Vector3D orientationChange, out Vector3D linearChange, out Vector3D angularChange)
        {
            linearChange = Vector3D.Zero;
            angularChange = Vector3D.Zero;
            Array.Clear(matrix, 0, 36);
            Array.Clear(rhs, 0, 6);
            for (int i = 0; i < points.Count; i++)
            {
                GuidePoint g = points[i];
                Vector3D r = g.Position - com;
                Vector3D n1 = Reject(g.Up, g.Tangent);
                Vector3D n2 = Vector3D.Cross(g.Tangent, n1);
                Vector3D pointVelocity = velocity + Vector3D.Cross(angular, r) - g.RailVelocity;
                Vector3D e = g.Target - g.Position;
                double omega = PointOmega(g.Lock, speed);
                double maxAccel = MathHelper.Clamp(60.0 * g.Lock, 30.0, 150.0);

                // Critically damped spring in the plane across the rail at this point.
                double a1 = Vector3D.Dot(e, n1) * omega * omega - Vector3D.Dot(pointVelocity, n1) * 2.0 * omega;
                double a2 = Vector3D.Dot(e, n2) * omega * omega - Vector3D.Dot(pointVelocity, n2) * 2.0 * omega;
                double aLen = Math.Sqrt(a1 * a1 + a2 * a2);
                if (aLen > maxAccel)
                {
                    a1 *= maxAccel / aLen;
                    a2 *= maxAccel / aLen;
                }
                // Gravity across the rail is cancelled up front (physics adds it after this write), and the rail's
                // own acceleration (a rail on an accelerating or turning ship) is followed up front.
                Vector3D feed = RailAcceleration(g) - gravity;
                AddRow(n1, Vector3D.Cross(r, n1), (a1 + Vector3D.Dot(feed, n1)) * Step, 1.0);
                AddRow(n2, Vector3D.Cross(r, n2), (a2 + Vector3D.Dot(feed, n2)) * Step, 1.0);
            }

            // Passive along the rail: no change of the along-rail speed.
            AddRow(railForward, Vector3D.Zero, 0.0, AlongRowWeight);

            AddRow(Vector3D.Zero, Vector3D.UnitX, orientationChange.X, OrientationRowWeight);
            AddRow(Vector3D.Zero, Vector3D.UnitY, orientationChange.Y, OrientationRowWeight);
            AddRow(Vector3D.Zero, Vector3D.UnitZ, orientationChange.Z, OrientationRowWeight);

            for (int i = 0; i < 6; i++)
                matrix[i * 6 + i] += 1e-6;
            if (!Solve6())
                return false;
            linearChange = new Vector3D(rhs[0], rhs[1], rhs[2]);
            angularChange = new Vector3D(rhs[3], rhs[4], rhs[5]);
            return IsFinite(linearChange) && IsFinite(angularChange);
        }

        // Accumulates weight * a * a^T and weight * a * target into the normal equations, a = (linear, angular).
        static void AddRow(Vector3D linear, Vector3D angularCoef, double target, double weight)
        {
            row[0] = linear.X; row[1] = linear.Y; row[2] = linear.Z;
            row[3] = angularCoef.X; row[4] = angularCoef.Y; row[5] = angularCoef.Z;
            for (int i = 0; i < 6; i++)
            {
                double wi = weight * row[i];
                if (wi == 0.0)
                    continue;
                rhs[i] += wi * target;
                for (int j = 0; j < 6; j++)
                    matrix[i * 6 + j] += wi * row[j];
            }
        }

        // Gaussian elimination with partial pivoting; the solution is left in rhs.
        static bool Solve6()
        {
            for (int col = 0; col < 6; col++)
            {
                int pivot = col;
                for (int r = col + 1; r < 6; r++)
                    if (Math.Abs(matrix[r * 6 + col]) > Math.Abs(matrix[pivot * 6 + col]))
                        pivot = r;
                if (Math.Abs(matrix[pivot * 6 + col]) < 1e-12)
                    return false;
                if (pivot != col)
                {
                    for (int k = 0; k < 6; k++)
                    {
                        double tmp = matrix[col * 6 + k];
                        matrix[col * 6 + k] = matrix[pivot * 6 + k];
                        matrix[pivot * 6 + k] = tmp;
                    }
                    double t = rhs[col];
                    rhs[col] = rhs[pivot];
                    rhs[pivot] = t;
                }
                for (int r = col + 1; r < 6; r++)
                {
                    double f = matrix[r * 6 + col] / matrix[col * 6 + col];
                    if (f == 0.0)
                        continue;
                    for (int k = col; k < 6; k++)
                        matrix[r * 6 + k] -= f * matrix[col * 6 + k];
                    rhs[r] -= f * rhs[col];
                }
            }
            for (int r = 5; r >= 0; r--)
            {
                double sum = rhs[r];
                for (int k = r + 1; k < 6; k++)
                    sum -= matrix[r * 6 + k] * rhs[k];
                rhs[r] = sum / matrix[r * 6 + r];
            }
            return true;
        }

        // The linear velocity change the engines on the rail can push in one tick.
        double HoldLimit(double mass)
        {
            double force = 0;
            for (int i = 0; i < points.Count; i++)
                force += points[i].Force;
            return (mass > 0 ? Math.Min(force / mass, MaxHoldAccel) : MaxHoldAccel) * Step;
        }

        // ---- helpers ----

        // Grid axis most aligned with the rail among those square to the engine's up.
        int PickForwardAxis(Vector3D railForward, Vector3D engineUp)
        {
            int best = 0;
            double bestDot = -1;
            for (int i = 0; i < 3; i++)
            {
                Vector3D axis = GridAxis(i);
                if (Math.Abs(Vector3D.Dot(axis, engineUp)) > 0.5)
                    continue;
                double d = Math.Abs(Vector3D.Dot(axis, railForward));
                if (d > bestDot)
                {
                    bestDot = d;
                    best = i;
                }
            }
            return best;
        }

        Vector3D GridAxis(int axis)
        {
            MatrixD m = Grid.WorldMatrix;
            return axis == 1 ? m.Left : axis == 2 ? m.Up : m.Forward;
        }

        // Rotation vector (axis * angle) that turns frame 1 onto frame 2; exact at any angle.
        static Vector3D RotationBetweenFrames(Vector3D forward1, Vector3D up1, Vector3D forward2, Vector3D up2)
        {
            Vector3D right1 = Vector3D.Cross(forward1, up1), right2 = Vector3D.Cross(forward2, up2);
            Vector3D w = (Vector3D.Cross(forward1, forward2) + Vector3D.Cross(up1, up2) + Vector3D.Cross(right1, right2)) * 0.5;
            double cos = MathHelper.Clamp((Vector3D.Dot(forward1, forward2) + Vector3D.Dot(up1, up2) + Vector3D.Dot(right1, right2) - 1.0) * 0.5, -1.0, 1.0);
            double sin = w.Length();
            if (sin > 1e-6)
                return w * (Math.Atan2(sin, cos) / sin);
            if (cos > 0)
                return Vector3D.Zero;
            // Half turn: the axis is where the axis pairs sum up.
            Vector3D axis = forward1 + forward2;
            if (axis.LengthSquared() < 1e-6)
                axis = up1 + up2;
            if (axis.LengthSquared() < 1e-6)
                axis = right1 + right2;
            return Vector3D.Normalize(axis) * Math.PI;
        }

        static Vector3D Orient(Vector3D v, Vector3D reference)
        {
            v = Vector3D.Normalize(v);
            return Vector3D.Dot(v, reference) < 0 ? -v : v;
        }

        // v made square to the unit axis and normalized.
        static Vector3D Reject(Vector3D v, Vector3D axis)
        {
            Vector3D r = v - axis * Vector3D.Dot(v, axis);
            return r.LengthSquared() > 1e-10 ? Vector3D.Normalize(r) : Vector3D.CalculatePerpendicularVector(axis);
        }

        static Vector3D ClampLength(Vector3D v, double max)
        {
            if (!IsFinite(v))
                return Vector3D.Zero;
            double length = v.Length();
            return length > max ? v * (max / length) : v;
        }

        static bool IsFinite(Vector3D v)
        {
            return !(double.IsNaN(v.X) || double.IsNaN(v.Y) || double.IsNaN(v.Z)
                || double.IsInfinity(v.X) || double.IsInfinity(v.Y) || double.IsInfinity(v.Z));
        }

        static Vector3D VelocityAt(IMyCubeGrid grid, Vector3D point)
        {
            return grid != null && grid.Physics != null ? (Vector3D)grid.Physics.GetVelocityAtPoint(point) : Vector3D.Zero;
        }

        public static Vector3D Gravity(Vector3D position)
        {
            float interference;
            Vector3D gravity = MyAPIGateway.Physics.CalculateNaturalGravityAt(position, out interference);
            return gravity + MyAPIGateway.Physics.CalculateArtificialGravityAt(position, interference);
        }
    }
}
