using System;
using System.Collections.Generic;
using Sandbox.Game;
using Sandbox.ModAPI;
using VRage.Game;
using VRage.Game.Entity;
using VRage.Game.ModAPI;
using VRage.Input;
using VRage.ModAPI;
using VRage.Utils;
using VRageMath;

namespace MagRail
{
    // The Rail Linker hand tool (Data/PhysicalItems_, HandItems_, Blueprints_MagRail.sbc): a vanilla
    // HandToolBase without actions, so the game does nothing on LMB/RMB and this class reads them.
    // Buttons (PLAN.md 10.16): two implicit modes by what is aimed at. Shape changes only on prototypes; a rail is
    // switched back to prototype first (LMB on it), then changed or deleted.
    //   Ports: LMB on a port, LMB on another port: lay a prototype (a prototype on either port is replaced; a port that
    //   cannot take it flashes red). LMB on a port with a prototype: pick that end up (the span goes, its other end is
    //   the new start). RMB: cancel; on a port with a prototype: delete it. Ctrl+LMB while laying: a port beside the start
    //   joins the bundle (or leaves it): its span is laid with the start's one, beside it all the way (Mate).
    //   Spans: LMB: prototype <-> rail. RMB on a prototype: delete it. MMB on a rail: paint it.
    //   A span ignores the ground (SpanRecord.NoLift) unless Shift is held on the click: then it rests on the ground
    //   (TerrainLift; the preview shows it while Shift is held).
    // A span being laid survives putting the tool away: taken again, it goes on from the same port if that is still
    // there and free (Resume).
    // The tool's display draws what is going on (Screen, LinkerDisplay) and answers clicks with a check or a cross; the
    // help window (DrawHud) says the same in words, with names, numbers and reasons the display has no room for.
    // Client side; changes go through TrackNetwork requests (the server checks the tool is in hand).
    public class RailLinker
    {
        public const string Subtype = "MagRail_RailLinker";
        static readonly MyStringId Icon = MyStringId.GetOrCompute("MagRail_Icon_RailLinker");
        const double AimRange = 40, MarkerRange = 300;
        // End faces far away stay at least this wide on screen: m per m of distance (~15 px across at 1080p).
        const double FaceOnScreen = 0.02;
        // While laying with no port aimed, the free end of the preview: this far in front of the
        // camera, facing the start port (the curve does not swing when the player turns).
        const double FreeEndDistance = 7;
        // The aimed span keeps the aim up to this much further out (AimScore 1 / SpanHold), and wins over a neighbour
        // as close: no flicker with the crosshair on its edge.
        const double SpanHold = 0.8;
        // The preview of a long span is built and drawn only this much of it, around its point nearest to the camera
        // (a span of tens of km would cost every frame); rebuilt when the camera moved PreviewRefocus.
        const double PreviewMaxLength = 3000, PreviewRefocus = 25;

        public static readonly Color FreeColor = new Color(70, 165, 255);
        static readonly Color LinkedColor = new Color(60, 230, 90);
        static readonly Color RailPortColor = new Color(110, 115, 125);
        static readonly Color AimColor = new Color(255, 230, 70);
        // A port that cannot take the span being laid flashes red and shakes this long after the click.
        const double FlashSeconds = 0.5;
        // A block a prototype cuts into stays outlined this long after the click that would make it a rail.
        const double BlockedSeconds = 5;

        // The help window (top right, like the welder's): the mouse buttons, an empty line, then what is aimed at and its
        // numbers or what is wrong (ShowInfo, DrawHud).
        readonly HudPanel panel = new HudPanel();
        string what, detail;
        Vector4 whatColor, detailColor;
        bool held;
        TrackBlockLogic startBlock, aimBlock;
        int startPort = -1, aimPort = -1;
        Span aimSpan;
        TrackBlockLogic flashBlock;
        int flashPort;
        double flashUntil;
        IMySlimBlock blockedBy;
        double blockedUntil;
        SpanCurve preview;          // while laying: start port -> aimed port or free end
        string previewProblem;      // why the aimed port cannot take the span (Loc key)
        // What the preview was built for (BuildPreview), and its lift over the ground (TerrainLift.Measure: reads voxels,
        // so measured again at most every PreviewLiftSeconds; the curve itself follows every frame).
        const double PreviewLiftSeconds = 0.1;
        int previewAimPort = -1;
        TrackBlockLogic previewAimBlock;
        Vector3D previewFrom, previewTo, previewToForward, previewFocus;
        bool previewGround, previewBundle;
        float[] previewLift;
        double previewLiftTime = double.NegativeInfinity;
        bool previewLiftStale;      // the curve moved since its lift was measured
        readonly LinkerDisplay.Screen screen = new LinkerDisplay.Screen();

        // The bundle (Ctrl+LMB while laying): ports beside the start port - facing the same way, in its cross-section,
        // turned any way about the rail - whose spans are laid with the start's one, to the ports as far across and as
        // turned from the aimed one; all of them or none. The start's span leads: it is aimed, the others follow it. All
        // of them, the start's too, follow one curve through the bundle's middle (BundleMiddle), each as far beside it and
        // as turned as its port is (SpanCurve.Beside): a bundle turned about the rail (a spiral) turns round its middle.
        // Not remembered when the tool is put away. While Ctrl is held, the previews are hidden and the bundle's ports lit
        // (Picking).
        const int BundleMax = 4;
        // Beside the start port: facing the same way within this angle, in its cross-section within this distance; the
        // ports at the other end are found as closely.
        static readonly double MateCos = Math.Cos(MathHelper.ToRadians(1));
        const double MateDistance = 0.05;

        // Right, Up, Roll: the port's place and turn as seen from the start port.
        class Mate
        {
            public TrackBlockLogic Block, Target;
            public int Port, TargetPort = -1;
            public float Right, Up, Roll;
            public SpanCurve Preview;
        }

        readonly List<Mate> mates = new List<Mate>();
        SpanCurve guidePreview;     // laying a bundle: the curve through its middle (the start's preview is moved from it)
        SpanCurve matesPreview;     // the guide the mates' previews were moved from
        readonly List<TrackIndex.Entry> found = new List<TrackIndex.Entry>();

        // In the local player's hand (overlays see through models: Draw.SeeThrough).
        public bool Held
        {
            get { return held; }
        }

        // The span under the crosshair (it pulses: SpanLines).
        public Span AimedSpan
        {
            get { return held ? aimSpan : null; }
        }

        // The prototype the span being laid would replace (laying, aimed at a port with a prototype): it pulses red
        // (SpanLines).
        public Span MarkedSpan
        {
            get
            {
                if (!held || startBlock == null || aimBlock == null)
                    return null;
                Span s = Network.SpanAt(aimBlock, aimPort);
                return s != null && !s.Record.WantRail ? s : null;
            }
        }

        static TrackNetwork Network
        {
            get { return MagRailSession.Instance.Network; }
        }

        public static bool IsHolding(IMyCharacter character)
        {
            if (character == null || character.IsDead)
                return false;
            MyEntity tool = character.EquippedTool as MyEntity;
            return tool != null && tool.DefinitionId.HasValue && tool.DefinitionId.Value.SubtypeName == Subtype;
        }

        // On foot, through the player's own eyes (not from a cockpit, camera block or spectator).
        static bool LocalHolds()
        {
            IMySession session = MyAPIGateway.Session;
            return session != null && session.Player != null && session.IsCameraControlledObject
                && IsHolding(session.ControlledObject as IMyCharacter);
        }

        public void HandleInput()
        {
            bool now = LocalHolds();
            if (now != held)
            {
                held = now;
                panel.Reset();
                if (!held)
                    Remember();
                else
                    Resume();
            }
            if (!held)
                return;
            if (startBlock != null && !StartUsable(startBlock, startPort))
            {
                Stop();
                Draw.Notify(Loc.T("MagRail_Linker_StartLost"), MyFontEnum.Red);
            }
            for (int i = mates.Count - 1; i >= 0; i--)
            {
                if (StartUsable(mates[i].Block, mates[i].Port))
                    continue;
                mates.RemoveAt(i);
                Draw.Notify(Loc.T("MagRail_Linker_MateLost"), MyFontEnum.Red);
            }
            RefreshJoints();
            if (MyAPIGateway.Gui.IsCursorVisible || MyAPIGateway.Gui.ChatEntryVisible)
                return;

            Aim();
            VRage.ModAPI.IMyInput input = MyAPIGateway.Input;
            LinkerDisplay.Press(input.IsLeftMousePressed() || input.IsRightMousePressed() || input.IsMiddleMousePressed());
            if (MyAPIGateway.Input.IsNewLeftMousePressed())
            {
                if (startBlock != null && input.IsAnyCtrlKeyPressed())
                    ToggleMate();
                else
                    Left();
            }
            else if (MyAPIGateway.Input.IsNewRightMousePressed())
                Right();
            else if (MyAPIGateway.Input.IsNewMiddleMousePressed())
                Middle();
            BuildPreview(true);
            CheckBundle();
            ShowInfo();
        }

        // Laying ends (laid, cancelled, the start lost, the tool put away): the bundle goes with it.
        void Stop()
        {
            startBlock = null;
            mates.Clear();
        }

        // Laying, aimed at a port the span could go to (not at the start port).
        bool AimsTarget
        {
            get { return aimBlock != null && !(aimBlock == startBlock && aimPort == startPort); }
        }

        int MateAt(TrackBlockLogic b, int port)
        {
            return mates.FindIndex(m => m.Block == b && m.Port == port);
        }

        // Ctrl+LMB while laying: the aimed port joins the bundle, or leaves it.
        void ToggleMate()
        {
            int i = aimBlock != null ? MateAt(aimBlock, aimPort) : -1;
            if (i >= 0)
            {
                mates.RemoveAt(i);
                Draw.Notify(Loc.F("MagRail_Linker_MateOff", mates.Count + 1, BundleMax));
                LinkerDisplay.Flash(LinkerDisplay.Button.Left, true);
                return;
            }
            if (!AimsTarget)
            {
                LinkerDisplay.Flash(LinkerDisplay.Button.Left, false);
                return;
            }
            float right, up, roll;
            string problem = MateProblem(aimBlock, aimPort, out right, out up, out roll);
            if (problem != null)
            {
                Refuse(problem);
                return;
            }
            mates.Add(new Mate { Block = aimBlock, Port = aimPort, Right = right, Up = up, Roll = roll });
            matesPreview = null;
            Draw.Notify(Loc.F("MagRail_Linker_MateOn", mates.Count + 1, BundleMax), MyFontEnum.Green);
            LinkerDisplay.Flash(LinkerDisplay.Button.Left, true);
        }

        // Why the port cannot join the bundle (Loc key), or null; right, up: where it is beside the start port, roll: how
        // far its up is turned from the start's about the way out.
        string MateProblem(TrackBlockLogic b, int port, out float right, out float up, out float roll)
        {
            MatrixD s = startBlock.PortWorld(startPort), m = b.PortWorld(port);
            Vector3D d = m.Translation - s.Translation;
            right = (float)Vector3D.Dot(d, s.Right);
            up = (float)Vector3D.Dot(d, s.Up);
            roll = (float)Math.Atan2(Vector3D.Dot(Vector3D.Cross(s.Up, m.Up), s.Forward), Vector3D.Dot(s.Up, m.Up));
            if (mates.Count + 1 >= BundleMax)
                return "MagRail_Problem_BundleFull";
            if (!StartUsable(b, port))
                return "MagRail_Problem_MateTaken";
            if (Vector3D.Dot(m.Forward, s.Forward) < MateCos || Math.Abs(Vector3D.Dot(d, s.Forward)) > MateDistance)
                return "MagRail_Problem_NotParallel";
            return null;
        }

        // The aimed port refuses (a click): it flashes red, the reason in a message.
        void Refuse(string problem)
        {
            flashBlock = aimBlock;
            flashPort = aimPort;
            flashUntil = Now + FlashSeconds;
            Draw.Notify(Loc.T(problem), MyFontEnum.Red);
            LinkerDisplay.Flash(LinkerDisplay.Button.Left, false);
        }

        // Every tick after BuildPreview: the mates' previews, and at an aimed port whether the bundle can go there.
        void CheckBundle()
        {
            MatePreviews();
            if (mates.Count > 0 && previewProblem == null && AimsTarget && preview != null && guidePreview != null)
                previewProblem = BundleProblem(guidePreview);
        }

        // The mates' previews: the guide moved across.
        void MatePreviews()
        {
            SpanCurve guide = mates.Count > 0 ? guidePreview : null;
            if (guide == matesPreview)
                return;
            matesPreview = guide;
            double middleRight, middleUp;
            BundleMiddle(out middleRight, out middleUp);
            for (int i = 0; i < mates.Count; i++)
            {
                Mate m = mates[i];
                m.Preview = guide != null ? guide.Across(m.Right - middleRight, m.Up - middleUp, m.Roll) : null;
            }
        }

        // The bundle's middle across, as seen from the start port: the mean of its ports' places (the start's is 0, 0;
        // no bundle: 0, 0).
        void BundleMiddle(out double right, out double up)
        {
            right = up = 0;
            for (int i = 0; i < mates.Count; i++)
            {
                right += mates[i].Right;
                up += mates[i].Up;
            }
            right /= mates.Count + 1;
            up /= mates.Count + 1;
        }

        // A port moved across to the bundle's middle (side -1 at the far end: the curve runs into the port there, its
        // right is the port's left).
        static MatrixD ToMiddle(MatrixD port, double right, double up, double side)
        {
            port.Translation += port.Right * (right * side) + port.Up * up;
            return port;
        }

        // Each mate's port at the aimed end (Mate.Target): as far across from the aimed port as the mate's is from the
        // start port (the curve runs into the aimed port: its right is the port's left), facing the same way, its up
        // turned as the mate's (about the curve, which runs into the port), free to take the span; and the mate's curve
        // does not fold back (a turn tighter than the distance across; the start's either). guide: the curve through the
        // bundle's middle to the aimed end. Loc key of what is wrong, or null.
        string BundleProblem(SpanCurve guide)
        {
            MatrixD to = aimBlock.PortWorld(aimPort);
            double middleRight, middleUp;
            BundleMiddle(out middleRight, out middleUp);
            bool previews = guide == matesPreview;
            if ((previews ? preview : guide.Across(-middleRight, -middleUp, 0)).Folds(guide))
                return "MagRail_Problem_MateFolds";
            for (int i = 0; i < mates.Count; i++)
            {
                Mate m = mates[i];
                m.Target = FindPort(to.Translation - to.Right * m.Right + to.Up * m.Up, to.Forward,
                    SpanCurve.Turn(to.Up, -to.Forward, m.Roll), out m.TargetPort);
                if (m.Target == null || (m.Target == startBlock && m.TargetPort == startPort) || MateAt(m.Target, m.TargetPort) >= 0)
                    return "MagRail_Problem_MateMissing";
                string problem = Network.LinkProblem(m.Block, m.Port, m.Target, m.TargetPort);
                if (problem != null)
                    return problem;
                SpanCurve curve = previews && m.Preview != null ? m.Preview
                    : guide.Across(m.Right - middleRight, m.Up - middleUp, m.Roll);
                if (curve.Folds(guide))
                    return "MagRail_Problem_MateFolds";
            }
            return null;
        }

        // A port at the point within MateDistance, facing forward with this up (the nearest), not joined end to end.
        TrackBlockLogic FindPort(Vector3D at, Vector3D forward, Vector3D up, out int port)
        {
            TrackBlockLogic best = null;
            double bestDistance = MateDistance;
            port = -1;
            found.Clear();
            Network.Index.Query(Network, at, MateDistance, found);
            for (int i = 0; i < found.Count; i++)
            {
                TrackBlockLogic b = found[i].Block;
                if (b == null || !b.PortsKnown || !b.Block.IsFunctional)
                    continue;
                for (int q = 0; q < b.PortCount; q++)
                {
                    MatrixD m = b.PortWorld(q);
                    double d = Vector3D.Distance(m.Translation, at);
                    if (d <= bestDistance && Vector3D.Dot(m.Forward, forward) >= MateCos
                        && Vector3D.Dot(m.Up, up) >= MateCos && !Joined(b, q))
                    {
                        bestDistance = d;
                        best = b;
                        port = q;
                    }
                }
            }
            found.Clear();
            return best;
        }

        // Every tick (HandleInput: also why the aimed port cannot take it) and every frame (DrawFrame: the free end
        // follows the camera without lagging behind it at a frame rate over 60).
        void BuildPreview(bool tick)
        {
            if (tick)
                previewProblem = null;
            bool ground = Ground;
            if (startBlock == null)
            {
                preview = null;
                return;
            }
            MatrixD from = startBlock.PortWorld(startPort);
            MatrixD to;
            bool toStatic = true, freeEnd = true;
            if (aimBlock != null && !(aimBlock == startBlock && aimPort == startPort))
            {
                freeEnd = false;
                if (tick)
                    previewProblem = Network.LinkProblem(startBlock, startPort, aimBlock, aimPort);
                to = aimBlock.PortWorld(aimPort);
                toStatic = aimBlock.Block.CubeGrid.IsStatic;
            }
            else
            {
                MatrixD cam = MyAPIGateway.Session.Camera.WorldMatrix;
                Vector3D end = cam.Translation + cam.Forward * FreeEndDistance;
                Vector3D back = from.Translation - end;
                if (back.LengthSquared() < 0.01)
                {
                    preview = null;
                    return;
                }
                back.Normalize();
                Vector3D up = from.Up - back * Vector3D.Dot(from.Up, back);
                up = up.LengthSquared() > 1e-6 ? Vector3D.Normalize(up) : Vector3D.CalculatePerpendicularVector(back);
                to = MatrixD.CreateWorld(end, back, up);
            }
            // A bundle: the curve runs through its middle, the start's span beside it.
            double middleRight, middleUp;
            BundleMiddle(out middleRight, out middleUp);
            from = ToMiddle(from, middleRight, middleUp, 1);
            to = ToMiddle(to, middleRight, middleUp, -1);

            // Over planet ground the preview is lifted like the span will be (TerrainLift): the curve is rebuilt
            // whenever its ends move, the lift measured again every PreviewLiftSeconds (at once when the aimed port
            // changes) and laid on the new curve in between.
            bool bundle = mates.Count > 0;
            bool same = preview != null && bundle == previewBundle && Vector3D.DistanceSquared(from.Translation, previewFrom) < 1e-6
                        && Vector3D.DistanceSquared(to.Translation, previewTo) < 1e-6
                        && Vector3D.DistanceSquared(to.Forward, previewToForward) < 1e-8
                        && !(preview.Cut && Vector3D.DistanceSquared(MyAPIGateway.Session.Camera.Position, previewFocus)
                             > PreviewRefocus * PreviewRefocus);
            bool targetChanged = aimBlock != previewAimBlock || aimPort != previewAimPort || ground != previewGround;
            double now = MyAPIGateway.Session.ElapsedPlayTime.TotalSeconds;
            bool remeasure = targetChanged || ((!same || previewLiftStale) && now - previewLiftTime >= PreviewLiftSeconds);
            if (same && !remeasure)
                return;
            previewFrom = from.Translation;
            previewTo = to.Translation;
            previewToForward = to.Forward;
            previewAimBlock = aimBlock;
            previewAimPort = aimPort;
            previewGround = ground;
            previewFocus = MyAPIGateway.Session.Camera.Position;
            previewBundle = bundle;
            SpanCurve curve = bundle ? SpanCurve.Bundle(from, to, PreviewMaxLength, previewFocus)
                : SpanCurve.Build(from, to, PreviewMaxLength, previewFocus);
            guidePreview = curve;
            if (bundle)
            {
                preview = curve.Across(-middleRight, -middleUp, 0);
                return;
            }
            if (!ground || !startBlock.Block.CubeGrid.IsStatic || !toStatic)
            {
                preview = curve;
                return;
            }
            if (remeasure)
            {
                // A cut preview ends in the air like a free end (its end is no port).
                previewLift = TerrainLift.Measure(curve, MatrixD.Identity, startBlock.Style, freeEnd || curve.Cut);
                previewLiftTime = now;
            }
            previewLiftStale = !remeasure;
            preview = TerrainLift.Apply(curve, MatrixD.Identity, previewLift);
        }

        void Aim()
        {
            Span was = aimSpan;
            aimBlock = null;
            aimPort = -1;
            aimSpan = null;
            MatrixD cam = MyAPIGateway.Session.Camera.WorldMatrix;
            Vector3D origin = cam.Translation, dir = cam.Forward;
            double best = double.MaxValue;     // the nearest port under the crosshair
            foreach (TrackBlockLogic b in Network.Blocks)
            {
                if (!b.PortsKnown || Vector3D.DistanceSquared(b.Block.WorldMatrix.Translation, origin) > (AimRange + 15) * (AimRange + 15))
                    continue;
                for (int i = 0; i < b.PortCount; i++)
                {
                    if (!Offered(b, i))
                        continue;
                    double depth = PortDepth(b, i, origin, dir);
                    if (depth < best)
                    {
                        best = depth;
                        aimBlock = b;
                        aimPort = i;
                    }
                }
            }
            if (aimBlock != null || startBlock != null)
                return;
            best = 1;
            for (int i = 0; i < Network.Spans.Count; i++)
            {
                Span s = Network.Spans[i];
                if (!s.Bound)
                    continue;
                SpanCurve c = Network.CurveOf(s);
                MatrixD frame = TrackNetwork.FrameMatrix(s);
                if (Vector3D.Distance(Vector3D.Transform(c.Bounds.Center, frame), origin) - c.Bounds.Radius > AimRange)
                    continue;
                TrackStyle style = s.Owner.Style;
                double middle = (style.FaceTop + style.FaceBottom) * 0.5;
                double tolerance = Math.Max(0.4, (style.FaceTop - style.FaceBottom) * 0.5);
                for (int n = 0; n < c.Pos.Count; n += 2)
                {
                    // Far out of reach (a long span): skip the points that cannot come into reach.
                    Vector3D point = Vector3D.Transform(c.Pos[n], frame);
                    double far = Vector3D.Distance(point, origin) - AimRange - 10;
                    if (far > 0)
                    {
                        // The loop's own step (n += 2) still moves on when nothing can be skipped.
                        n = Math.Max(n, c.Ahead(n, far) - 2);
                        continue;
                    }
                    // Across the rail's whole width (a wide rail is caught at its edges too).
                    Vector3D up = Vector3D.TransformNormal(c.Up[n], frame);
                    Vector3D side = Vector3D.TransformNormal(Vector3D.Cross(c.Tan[n], c.Up[n]), frame) * style.FaceHalfWidth;
                    Vector3D centre = point + up * middle;
                    double score = AimScore(centre - side, centre + side, origin, dir, tolerance) * (s == was ? SpanHold : 1);
                    if (score < best)
                    {
                        best = score;
                        aimSpan = s;
                    }
                }
            }
        }

        // The port takes the aim only with the crosshair on its end face as drawn (Draw.Face: the section's outline with
        // its stroke, FACES / STROKE in ModelSource/overlay_textures.py; far away the face grows about the rail point,
        // DrawEnd), a switch's branch on its third (the texture splits the section's width in three, the side thirds out
        // to the edge): how far along the view the crosshair meets it (of several faces under it the nearest wins);
        // otherwise double.MaxValue.
        const double FaceStroke = 0.05;

        static double PortDepth(TrackBlockLogic b, int port, Vector3D origin, Vector3D dir)
        {
            TrackStyle style = b.Style;
            MatrixD m = b.PortWorld(port);
            double scale = Math.Max(1, Vector3D.Distance(m.Translation, origin) * FaceOnScreen / style.Width);
            double edge = style.Width / 2 + FaceStroke, from = -edge, to = edge;
            if (b.FanRank(port) >= 0)
            {
                double side = Vector3D.Dot(b.PortAim(port) - m.Translation, m.Right), split = style.Width / 6;
                if (side > 1e-3)
                    from = split;
                else if (side < -1e-3)
                    to = -split;
                else
                {
                    from = -split;
                    to = split;
                }
            }
            double bottom = -style.Feet - FaceStroke, top = style.CrestHeight + FaceStroke;
            double facing = Vector3D.Dot(dir, m.Forward);
            if (Math.Abs(facing) < 1e-6)
                return double.MaxValue;
            double t = Vector3D.Dot(m.Translation - origin, m.Forward) / facing;
            if (t < 0.5 || t > AimRange)
                return double.MaxValue;
            Vector3D on = origin + dir * t - m.Translation;
            double x = Vector3D.Dot(on, m.Right) / scale, z = Vector3D.Dot(on, m.Up) / scale;
            return x < from || x > to || z < bottom || z > top ? double.MaxValue : t;
        }

        // The segment a-b: its point closest to the view ray.
        static double AimScore(Vector3D a, Vector3D b, Vector3D origin, Vector3D dir, double tolerance)
        {
            Vector3D v = b - a, w = origin - a;
            double bv = Vector3D.Dot(dir, v), c = v.LengthSquared(), d = Vector3D.Dot(dir, w), e = Vector3D.Dot(v, w);
            double denom = c - bv * bv;
            double k = denom > 1e-9 ? MathHelper.Clamp((e - bv * d) / denom, 0, 1) : 0;
            return AimScore(a + v * k, origin, dir, tolerance);
        }

        // < 1 when the point is within the aim tolerance of the view ray (grows with distance).
        static double AimScore(Vector3D p, Vector3D origin, Vector3D dir, double tolerance)
        {
            Vector3D v = p - origin;
            double t = Vector3D.Dot(v, dir);
            if (t < 0.5 || t > AimRange)
                return double.MaxValue;
            return (v - dir * t).Length() / (tolerance + 0.012 * t);
        }

        void Left()
        {
            if (startBlock != null)
            {
                if (!AimsTarget || MateAt(aimBlock, aimPort) >= 0)
                {
                    LinkerDisplay.Flash(LinkerDisplay.Button.Left, false);
                    return;
                }
                string problem = Network.LinkProblem(startBlock, startPort, aimBlock, aimPort);
                double middleRight, middleUp;
                BundleMiddle(out middleRight, out middleUp);
                if (problem == null && mates.Count > 0)
                    problem = BundleProblem(SpanCurve.Bundle(ToMiddle(startBlock.PortWorld(startPort), middleRight, middleUp, 1),
                        ToMiddle(aimBlock.PortWorld(aimPort), middleRight, middleUp, -1)));
                if (problem != null)
                {
                    Refuse(problem);
                    return;
                }
                bool replaces = Network.SpanAt(aimBlock, aimPort) != null;
                bool ground = Ground;
                Network.RequestLink(startBlock, startPort, aimBlock, aimPort, !ground,
                    mates.Count > 0 ? new[] { (float)-middleRight, (float)-middleUp, 0f } : null);
                for (int i = 0; i < mates.Count; i++)
                {
                    Mate m = mates[i];
                    Network.RequestLink(m.Block, m.Port, m.Target, m.TargetPort, true,
                        new[] { (float)(m.Right - middleRight), (float)(m.Up - middleUp), m.Roll });
                }
                int laid = mates.Count + 1;
                Stop();
                Draw.Notify(laid > 1 ? Loc.F("MagRail_Linker_LinkedBundle", laid)
                    : Loc.T(replaces ? "MagRail_Linker_Replaced" : ground ? "MagRail_Linker_LinkedGround" : "MagRail_Linker_Linked"),
                    MyFontEnum.Green);
                LinkerDisplay.Flash(LinkerDisplay.Button.Left, true);
                return;
            }
            if (aimBlock != null)
            {
                Span span = Network.SpanAt(aimBlock, aimPort);
                if (span == null)
                {
                    startBlock = aimBlock;
                    startPort = aimPort;
                    pickedSpan = null;
                    Draw.Notify(Loc.F("MagRail_Linker_Started", PortTitle(startBlock, startPort)), MyFontEnum.Green);
                    LinkerDisplay.Flash(LinkerDisplay.Button.Left, true);
                }
                else if (span.Record.WantRail)
                    RailFirst(LinkerDisplay.Button.Left);
                else
                {
                    startBlock = span.Other(aimBlock);
                    startPort = startBlock == span.Owner ? span.OwnerPort : span.PartnerPort;
                    pickedSpan = span;
                    Network.RequestUnlink(span);
                    Draw.Notify(Loc.T("MagRail_Linker_PickedUp"), MyFontEnum.Green);
                    LinkerDisplay.Flash(LinkerDisplay.Button.Left, true);
                }
                return;
            }
            if (aimSpan != null)
            {
                bool rail = !aimSpan.Record.WantRail, free = TrackNetwork.FreeRails;
                // Survival: a rail with parts welded in is ground off first (TrackNetwork.CanUnbuild, the server checks too).
                if (!rail && !TrackNetwork.CanUnbuild(aimSpan, free))
                {
                    Draw.Notify(Loc.T("MagRail_Linker_Welded"), MyFontEnum.Red, 3000);
                    LinkerDisplay.Flash(LinkerDisplay.Button.Left, false);
                    return;
                }
                // Not through blocks (TrackCollision.BlockInside): the one in the way is outlined for a while.
                IMySlimBlock inside = rail ? TrackCollision.BlockInside(Network, aimSpan) : null;
                if (inside != null)
                {
                    blockedBy = inside;
                    blockedUntil = Now + BlockedSeconds;
                    Draw.Notify(Loc.F("MagRail_Linker_Blocked", BlockName(inside)), MyFontEnum.Red, 3000);
                    LinkerDisplay.Flash(LinkerDisplay.Button.Left, false);
                    return;
                }
                Network.RequestMode(aimSpan, rail);
                if (rail && aimSpan.Problem != null)
                    Draw.Notify(Loc.F("MagRail_Linker_RailWaits", Loc.T(aimSpan.Problem)), MyFontEnum.Red, 3000);
                else
                    Draw.Notify(Loc.T(!rail ? "MagRail_Linker_ToPrototype" : free ? "MagRail_Linker_ToRail"
                        : "MagRail_Linker_ToRailWeld"), MyFontEnum.Green, rail && !free ? 3000 : 2000);
                LinkerDisplay.Flash(LinkerDisplay.Button.Left, true);     // switched (a rail that waits shows its cross)
                return;
            }
            LinkerDisplay.Flash(LinkerDisplay.Button.Left, false);
        }

        void Right()
        {
            if (startBlock != null)
            {
                Stop();
                Draw.Notify(Loc.T("MagRail_Linker_Cancelled"));
                LinkerDisplay.Flash(LinkerDisplay.Button.Right, true);
                return;
            }
            Delete(aimBlock != null ? Network.SpanAt(aimBlock, aimPort) : aimSpan);
        }

        // A prototype goes; a rail stays (it is switched to prototype first).
        void Delete(Span span)
        {
            if (span == null)
            {
                LinkerDisplay.Flash(LinkerDisplay.Button.Right, false);
                return;
            }
            if (span.Record.WantRail)
            {
                RailFirst(LinkerDisplay.Button.Right);
                return;
            }
            Network.RequestUnlink(span);
            Draw.Notify(Loc.T("MagRail_Linker_Deleted"));
            LinkerDisplay.Flash(LinkerDisplay.Button.Right, true);
        }

        static void RailFirst(LinkerDisplay.Button button)
        {
            Draw.Notify(Loc.F("MagRail_Linker_RailFirst", Keys.Control(MyControlsSpace.PRIMARY_TOOL_ACTION)), MyFontEnum.Red);
            LinkerDisplay.Flash(button, false);
        }

        // Shift held while laying: the span rests on the ground (the menus and chat take the keyboard).
        static bool FollowGround
        {
            get
            {
                return !MyAPIGateway.Gui.IsCursorVisible && !MyAPIGateway.Gui.ChatEntryVisible
                       && MyAPIGateway.Input.IsAnyShiftKeyPressed();
            }
        }

        // Ctrl held while laying: picking the bundle's ports (the menus and chat take the keyboard).
        bool Picking
        {
            get
            {
                return startBlock != null && !MyAPIGateway.Gui.IsCursorVisible && !MyAPIGateway.Gui.ChatEntryVisible
                       && MyAPIGateway.Input.IsAnyCtrlKeyPressed();
            }
        }

        // A bundle ignores the ground: each span would follow its own and they would not stay side by side.
        bool Ground
        {
            get { return FollowGround && mates.Count == 0; }
        }

        // The span being laid when the tool is put away: its start by block id and port name (the block may stream out
        // and in again as a new object meanwhile).
        long rememberedBlock;
        string rememberedPort;

        void Remember()
        {
            rememberedBlock = startBlock != null ? startBlock.Block.EntityId : 0;
            rememberedPort = startBlock != null ? startBlock.PortName(startPort) : null;
            Stop();
        }

        // Taken again: goes on laying from the remembered port if it is still there and free.
        void Resume()
        {
            if (rememberedPort == null)
                return;
            TrackBlockLogic b = Network.Find(rememberedBlock);
            int port = b != null && b.PortsKnown ? b.PortIndex(rememberedPort) : -1;
            rememberedPort = null;
            if (port < 0 || !StartUsable(b, port))
            {
                Draw.Notify(Loc.T("MagRail_Linker_StartLost"), MyFontEnum.Red);
                return;
            }
            startBlock = b;
            startPort = port;
            Draw.Notify(Loc.F("MagRail_Linker_Resumed", PortTitle(startBlock, startPort)), MyFontEnum.Green);
        }

        // The prototype picked up last (LMB on its end): on a client it stays on the start port until the server's
        // answer removes it, and the port counts as free meanwhile.
        Span pickedSpan;

        // A span can still be laid from this port: the block is there and whole, the port has no span (another player may
        // have laid one; the picked-up prototype on its way out does not count) and is not joined end to end to another
        // block.
        bool StartUsable(TrackBlockLogic b, int port)
        {
            TrackBlockLogic other;
            int otherPort;
            Span span = Network.SpanAt(b, port);
            if (span != null && !(span == pickedSpan && !span.Record.WantRail))
                return false;
            return Network.Find(b.Block.EntityId) == b && b.Block.IsFunctional
                   && !Network.FindJoint(b, port, out other, out otherPort);
        }

        static double Now
        {
            get { return MyAPIGateway.Session.ElapsedPlayTime.TotalSeconds; }
        }

        // Paint like the vanilla paint: MMB paints the aimed rail with the player's colour and skin (picked with P); a
        // prototype is not painted.
        void Middle()
        {
            IMyPlayer player = MyAPIGateway.Session.Player;
            if (startBlock != null || aimSpan == null || !aimSpan.Bound || !aimSpan.Record.WantRail || player == null)
            {
                LinkerDisplay.Flash(LinkerDisplay.Button.Wheel, false);
                return;
            }
            Network.RequestPaint(aimSpan, player.SelectedBuildColor, BuildSkin(player));
            LinkerDisplay.Flash(LinkerDisplay.Button.Wheel, true);
        }

        // The mod API has no getter for the player's build skin; the player's saved data has it
        // (MyObjectBuilder_Player.BuildArmorSkin), and IMySession.GetCheckpoint builds that data in memory
        // (the whole checkpoint, a few ms: only on a click).
        public static string BuildSkin(IMyPlayer player)
        {
            MyObjectBuilder_Checkpoint checkpoint = MyAPIGateway.Session.GetCheckpoint("MagRail");
            if (checkpoint != null && checkpoint.AllPlayersData != null)
                foreach (KeyValuePair<MyObjectBuilder_Checkpoint.PlayerId, MyObjectBuilder_Player> p in checkpoint.AllPlayersData.Dictionary)
                    if (p.Value.IdentityId == player.IdentityId)
                        return p.Value.BuildArmorSkin ?? "";
            return "";
        }

        // The window's two state lines (every tick): what is aimed at or laid from, then its numbers or what is wrong.
        void ShowInfo()
        {
            what = detail = "";
            whatColor = detailColor = HudPanel.White;
            if (startBlock != null)
            {
                what = Loc.F("MagRail_Linker_From", PortTitle(startBlock, startPort));
                if (previewProblem != null)
                {
                    detail = Loc.T(previewProblem);
                    detailColor = HudPanel.Red;
                }
                else if (preview != null)
                    detail = CurveText(preview);
                return;
            }
            if (aimBlock != null)
            {
                what = PortTitle(aimBlock, aimPort);
                Span span = Network.SpanAt(aimBlock, aimPort);
                if (span == null)
                    detail = Loc.T("MagRail_Linker_AimFree");
                else if (span.Record.WantRail)
                    detail = Loc.F("MagRail_Linker_AimRail", Keys.Control(MyControlsSpace.PRIMARY_TOOL_ACTION));
                else
                    detail = Loc.T("MagRail_Mode_Prototype") + (span.Bound ? " · " + CurveText(Network.CurveOf(span)) : "");
                return;
            }
            // The aimed span may have just been deleted (RMB) or lost its partner.
            if (aimSpan != null && aimSpan.Bound)
            {
                what = Loc.T(!aimSpan.IsRail ? "MagRail_Mode_Prototype" : aimSpan.Record.Weld != null ? "MagRail_Mode_Building" : "MagRail_Mode_Rail");
                if (aimSpan.Record.WantRail && aimSpan.Problem != null)
                {
                    detail = Loc.F("MagRail_Linker_RailWaits", Loc.T(aimSpan.Problem));
                    detailColor = HudPanel.Red;
                }
                else
                    detail = CurveText(Network.CurveOf(aimSpan));
                return;
            }
            what = Loc.T("MagRail_Linker_AimNothing");
        }

        // The help window, every frame while the tool is in hand (Hud.Begin first, after DrawFrame: the buttons are the
        // display's hints). Always the same lines in the same places: each mouse button with what it does now (faded
        // when it does nothing), an empty line, then the state (ShowInfo).
        public void DrawHud()
        {
            if (!held || !Hud.Visible)
                return;
            panel.Title = Loc.T("DisplayName_Item_MagRail_RailLinker");
            panel.Icon = Icon;
            panel.Clear();
            AddKey(LinkerDisplay.Button.Left, Keys.Control(MyControlsSpace.PRIMARY_TOOL_ACTION));
            AddKey(LinkerDisplay.Button.Right, Keys.Control(MyControlsSpace.SECONDARY_TOOL_ACTION));
            AddKey(LinkerDisplay.Button.Wheel, Keys.Mouse(MyMouseButtonsEnum.Middle));
            // Shift: only while laying a single span; held, it is lit. Ctrl+LMB: only while laying, with the bundle's size;
            // Ctrl held, it is lit.
            string shift = Keys.Key(MyKeys.Shift);
            if (startBlock == null || mates.Count > 0)
                panel.AddKey(shift, "—", HudPanel.Dim, HudPanel.Dim);
            else if (FollowGround)
                panel.AddKey(shift, Loc.T("MagRail_Linker_DoGround"), HudPanel.Green, HudPanel.Green);
            else
                panel.AddKey(shift, Loc.T("MagRail_Linker_DoGround"), HudPanel.Blue, HudPanel.Blue);
            string ctrl = Keys.Key(MyKeys.Control) + "+" + Keys.Control(MyControlsSpace.PRIMARY_TOOL_ACTION);
            if (startBlock == null)
                panel.AddKey(ctrl, "—", HudPanel.Dim, HudPanel.Dim);
            else
            {
                Vector4 lit = Picking ? HudPanel.Green : HudPanel.Blue;
                panel.AddKey(ctrl, Loc.F("MagRail_Linker_DoMate", mates.Count + 1, BundleMax), lit, lit);
            }
            panel.Add("", HudPanel.White);
            panel.Add(what, whatColor, true);
            panel.Add(detail, detailColor);
            panel.Draw();
        }

        void AddKey(LinkerDisplay.Button button, string key)
        {
            string text = HintText(screen.Hints[(int)button]);
            if (text == null)
                panel.AddKey(key, "—", HudPanel.Dim, HudPanel.Dim);
            else
                panel.AddKey(key, text, HudPanel.Blue, HudPanel.Blue);
        }

        // What a display hint means here and now, in words (null: the button does nothing).
        string HintText(LinkerDisplay.HintKind hint)
        {
            switch (hint)
            {
                case LinkerDisplay.HintKind.Grab:
                    return Loc.T(aimBlock != null && Network.SpanAt(aimBlock, aimPort) == null ? "MagRail_Linker_DoStart" : "MagRail_Linker_DoGrab");
                case LinkerDisplay.HintKind.Prototype:
                    if (startBlock == null)
                        return Loc.T("MagRail_Linker_DoToPrototype");
                    return Loc.T(aimBlock != null && Network.SpanAt(aimBlock, aimPort) != null ? "MagRail_Linker_DoReplace" : "MagRail_Linker_DoLay");
                case LinkerDisplay.HintKind.Rail:
                    return Loc.T("MagRail_Linker_DoToRail");
                case LinkerDisplay.HintKind.Break:
                    return Loc.T("MagRail_Linker_DoDelete");
                case LinkerDisplay.HintKind.Cancel:
                    return Loc.T("MagRail_Linker_DoCancel");
                case LinkerDisplay.HintKind.Paint:
                    return Loc.F("MagRail_Linker_DoPaint", Keys.Control(MyControlsSpace.LANDING_GEAR));
            }
            return null;
        }

        static string CurveText(SpanCurve c)
        {
            // The game font has no infinity sign.
            if (double.IsInfinity(c.MinRadius))
                return Loc.F("MagRail_Linker_Straight", c.FullLength.ToString("0.0"));
            return Loc.F("MagRail_Linker_Curve", c.FullLength.ToString("0.0"), c.MinRadius.ToString("0.0"));
        }

        static string PortTitle(TrackBlockLogic block, int port)
        {
            return block.Block.CustomName + " · " + PortLabel(block, port);
        }

        // The port's name for players (the dummy name if there is no translation); a flipped switch
        // (TrackSettings.Flipped) names its left branch right and back.
        public static string PortLabel(TrackBlockLogic block, int port)
        {
            string key = "MagRail_Port_" + ShownPort(block, port);
            string name = Loc.T(key);
            return name == key ? block.PortName(port) : name;
        }

        // The port's dummy name as the player sees it: a flipped switch's left branch is its right one.
        static string ShownPort(TrackBlockLogic block, int port)
        {
            string dummy = block.PortName(port);
            if (block.Settings != null && block.Settings.Flipped)
                dummy = dummy == "branch_l" ? "branch_r" : dummy == "branch_r" ? "branch_l" : dummy;
            return dummy;
        }

        // A switch's branch for the screen: 'L', 'C', 'R'; other ports none.
        static char BranchLetter(TrackBlockLogic block, int port)
        {
            string dummy = ShownPort(block, port);
            return dummy == "branch_l" ? 'L' : dummy == "branch_c" ? 'C' : dummy == "branch_r" ? 'R' : '\0';
        }

        // ---- drawing (every frame) ----

        public void DrawFrame()
        {
            if (!held)
                return;
            if (!MyAPIGateway.Gui.IsCursorVisible && !MyAPIGateway.Gui.ChatEntryVisible)
            {
                BuildPreview(false);
                MatePreviews();
            }
            foreach (TrackBlockLogic b in Network.Blocks)
            {
                if (!b.PortsKnown || Vector3D.DistanceSquared(b.Block.WorldMatrix.Translation, MagRail.Draw.Camera) > MarkerRange * MarkerRange)
                    continue;
                for (int i = 0; i < b.PortCount; i++)
                {
                    int rank = b.FanRank(i);
                    if (rank == (int)SwitchBranch.Center)
                        DrawSwitchFace(b);
                    else if (rank < 0 && Offered(b, i))
                        DrawPort(b, i);
                }
            }
            if (preview != null && !Picking)
            {
                SpanLines.DrawCurve(preview, MatrixD.Identity, startBlock.Style, LineStyle.Preview);
                for (int i = 0; i < mates.Count; i++)
                    if (mates[i].Preview != null)
                        SpanLines.DrawCurve(mates[i].Preview, MatrixD.Identity, mates[i].Block.Style, LineStyle.Preview);
            }
            if (blockedBy != null)
                DrawBlocked();
            IMyCharacter character = MyAPIGateway.Session.ControlledObject as IMyCharacter;
            if (character != null)
                LinkerDisplay.Draw(character.EquippedTool, DisplayColor(), Screen(), true);
        }

        static readonly Vector3D[] corners = new Vector3D[8];

        // The block a span would cut into (Left): its box outlined in red, seen through everything, for a while.
        void DrawBlocked()
        {
            if (Now > blockedUntil || blockedBy.IsDestroyed || blockedBy.CubeGrid.Closed)
            {
                blockedBy = null;
                return;
            }
            MyOrientedBoundingBoxD box = TrackCollision.BlockBox(blockedBy);
            Vector3D x = Vector3D.Transform(Vector3D.Right * box.HalfExtent.X, box.Orientation);
            Vector3D y = Vector3D.Transform(Vector3D.Up * box.HalfExtent.Y, box.Orientation);
            Vector3D z = Vector3D.Transform(Vector3D.Backward * box.HalfExtent.Z, box.Orientation);
            for (int i = 0; i < 8; i++)
                corners[i] = box.Center + ((i & 1) != 0 ? x : -x) + ((i & 2) != 0 ? y : -y) + ((i & 4) != 0 ? z : -z);
            float width = MagRail.Draw.Width(Vector3D.Distance(box.Center, MagRail.Draw.Camera), 0.03f);
            for (int i = 0; i < 8; i++)
                for (int bit = 1; bit < 8; bit <<= 1)
                    if ((i & bit) == 0)
                        MagRail.Draw.Line(corners[i], corners[i | bit], MagRail.Draw.Red, width, true, throughAlpha: 1);
        }

        static string BlockName(IMySlimBlock b)
        {
            IMyTerminalBlock terminal = b.FatBlock as IMyTerminalBlock;
            return terminal != null ? terminal.CustomName : b.BlockDefinition.DisplayNameText;
        }

        // The display's picture of what the tool is doing (ModelSource/display/linker_screens.svg): the start or aimed
        // port is the left stub's; laying - dashes in the preview's colours to the right (an arrow while no port is
        // aimed; the aimed port's stub on the right, red with a cross if it cannot take the span); a port with a span or
        // a span aimed at - both stubs with the prototype's line between (bars in its colours) or the rail; the length
        // in the top right corner; the profile in the top left one (the style of what is aimed at or laid; nothing: none);
        // the mouse with what its buttons would do now (PLAN.md 10.16); a switch's branch (L / C / R) over the stub of its
        // port - the left one: the start or aimed port, the right one: the port laid to; under the line the ground, lit
        // if the span follows it (laying: Shift held; a prototype: laid so), with Shift left of it while laying.
        LinkerDisplay.Screen Screen()
        {
            LinkerDisplay.Screen s = screen;
            s.Clear();
            Span span = startBlock != null ? null : aimBlock != null ? Network.SpanAt(aimBlock, aimPort) : aimSpan;
            if (span != null && !span.Bound)
                span = null;
            s.Profile = startBlock != null ? startBlock.Style : aimBlock != null ? aimBlock.Style : span != null ? span.Owner.Style : null;
            if (startBlock == null && aimBlock != null)
                s.BranchLeft = BranchLetter(aimBlock, aimPort);
            if (startBlock != null)
            {
                s.StubLeft = s.CapLeft = s.PortLeft = true;
                s.BranchLeft = BranchLetter(startBlock, startPort);
                s.ShiftKey = s.GroundShown = true;
                s.Ground = Ground;
                s.Hints[(int)LinkerDisplay.Button.Right] = LinkerDisplay.HintKind.Cancel;
                if (preview != null)
                {
                    LinkerDisplay.Line(s, LinkerDisplay.LineKind.Dash, preview);
                    s.Text = LinkerDisplay.Length(preview.FullLength);
                }
                if (aimBlock != null && !(aimBlock == startBlock && aimPort == startPort))
                {
                    s.StubRight = s.PortRight = true;
                    s.BranchRight = BranchLetter(aimBlock, aimPort);
                    if (previewProblem == null)
                        s.Hints[(int)LinkerDisplay.Button.Left] = LinkerDisplay.HintKind.Prototype;
                    else
                    {
                        s.Right = MagRail.Draw.Red;
                        s.Refused = true;
                    }
                }
                else
                    s.Arrow = preview != null;
                return s;
            }
            if (span == null)
            {
                s.StubLeft = s.CapLeft = s.PortLeft = aimBlock != null;
                if (aimBlock != null)
                    s.Hints[(int)LinkerDisplay.Button.Left] = LinkerDisplay.HintKind.Grab;
                return s;
            }
            SpanCurve curve = Network.CurveOf(span);
            s.Text = LinkerDisplay.Length(curve.Length);
            s.StubLeft = s.StubRight = true;
            if (span.Record.WantRail)
            {
                s.Rail = true;
                if (aimBlock == null)
                {
                    s.Hints[(int)LinkerDisplay.Button.Left] = LinkerDisplay.HintKind.Prototype;
                    s.Hints[(int)LinkerDisplay.Button.Wheel] = LinkerDisplay.HintKind.Paint;
                }
                s.Refused = span.Problem != null;
            }
            else
            {
                s.CapLeft = s.PortLeft = s.PortRight = true;
                s.GroundShown = true;
                s.Ground = !span.Record.NoLift;
                LinkerDisplay.Line(s, LinkerDisplay.LineKind.Bar, curve);
                s.Hints[(int)LinkerDisplay.Button.Left] = aimBlock != null ? LinkerDisplay.HintKind.Grab : LinkerDisplay.HintKind.Rail;
                s.Hints[(int)LinkerDisplay.Button.Right] = LinkerDisplay.HintKind.Break;
            }
            return s;
        }

        // The display's button, coloured like the tool's state.
        Color DisplayColor()
        {
            if (startBlock != null)
                return previewProblem != null ? MagRail.Draw.Red : aimBlock != null ? LinkedColor : AimColor;
            return aimBlock != null || aimSpan != null ? AimColor : FreeColor;
        }

        // Ports offered to the player (drawn and aimed at): not joined end to end, not on a damaged block (the model
        // shows what is wrong); while laying, the start port, the bundle's and the ports of other blocks of the same style.
        bool Offered(TrackBlockLogic b, int port)
        {
            if (!b.Block.IsFunctional || Joined(b, port))
                return false;
            if (startBlock == null)
                return true;
            return b == startBlock ? port == startPort || MateAt(b, port) >= 0 : b.Style == startBlock.Style;
        }

        void DrawPort(TrackBlockLogic b, int port)
        {
            MatrixD m = b.PortWorld(port);
            DrawEnd(b, port, -1, m, m.Translation);
        }

        // The branch ports of a Rail Switch share one point: one end face, each third lit by its branch.
        void DrawSwitchFace(TrackBlockLogic b)
        {
            MatrixD face = b.PortWorld(b.PortIndex(TrackBlockLogic.BranchPorts[(int)SwitchBranch.Center]));
            for (int rank = 0; rank < TrackBlockLogic.BranchPorts.Length; rank++)
            {
                int q = b.PortIndex(TrackBlockLogic.BranchPorts[rank]);
                if (q >= 0 && Offered(b, q))
                    DrawEnd(b, q, rank, face, b.PortAim(q));
            }
        }

        // The rail's end face (or the switch's third `part`) in the port's plane, a dot where the port is aimed at and,
        // on a free port, a stick outwards with a ball on its end - all of it one colour: free blue, with a prototype
        // green, with a rail grey; aimed at, it pulses white. Bright and seen through models, so ports are easy to find
        // along a long rail (as bright behind a model as in front of it); far away all of it keeps a size on screen.
        // A port that refused the span being laid is red and shakes sideways for FlashSeconds.
        void DrawEnd(TrackBlockLogic b, int port, int part, MatrixD face, Vector3D aim)
        {
            const float Bright = 3;
            TrackStyle style = b.Style;
            Span span = Network.SpanAt(b, port);
            Color color = span == null ? FreeColor : span.Record.WantRail ? RailPortColor : LinkedColor;
            if (Picking && ((b == startBlock && port == startPort) || MateAt(b, port) >= 0))
                color = AimColor;
            float fill = SpanLines.FillAlpha, bright = Bright;
            double distance = Vector3D.Distance(face.Translation, MagRail.Draw.Camera);
            float scale = (float)Math.Max(1, distance * FaceOnScreen / style.Width);
            double flash = b == flashBlock && port == flashPort ? flashUntil - Now : 0;
            if (flash > 0)
            {
                color = MagRail.Draw.Red;
                fill = 0.5f;
                Vector3D shake = face.Right * (0.08 * scale * Math.Sin(flash * 2 * Math.PI * 16) * flash / FlashSeconds);
                face.Translation += shake;
                aim += shake;
            }
            else if (b == aimBlock && port == aimPort)
            {
                float pulse = MagRail.Draw.Pulse;
                color = Color.Lerp(color, Color.White, 0.5f * pulse);
                fill += 0.25f * pulse;
                bright *= 1 + pulse;
            }
            Vector3D forward = b.PortWorld(port).Forward;
            MagRail.Draw.Face(style, part, true, face.Translation, face.Right, face.Up, color * fill, scale, true, bright, throughAlpha: 1);
            MagRail.Draw.Face(style, part, false, face.Translation, face.Right, face.Up, color, scale, true, bright, throughAlpha: 1);
            Vector3D p = face.Translation + (aim - face.Translation) * scale;
            MagRail.Draw.Dot(p, color, Math.Max(0.1f * scale, (float)(distance * 0.004)), true, bright, throughAlpha: 1);
            if (span != null)
                return;
            Vector3D tip = p + forward * 0.7 * scale;
            MagRail.Draw.Line(p, tip, color, MagRail.Draw.Width(distance, 0.05f * scale), true, bright, throughAlpha: 1);
            MagRail.Draw.Dot(tip, color, Math.Max(0.13f * scale, (float)(distance * 0.005)), true, bright, throughAlpha: 1);
        }

        // ---- joints ----

        // Ports joined end to end to another block (TrackNetwork.FindJoint's test) get no marker and are not aimed at: a
        // bit per port, found again every JointRefresh ticks for the blocks in marker range. All their free ports are put
        // in cells of JointCell first and each is matched with the ports in its cell and the ones around it: a
        // FindJoint per port (a track index query each) took ~100 ms near a big station.
        const int JointRefresh = 30;
        const double JointCell = 1;     // not under TrackNetwork.JointDistance * 2: the cells around hold every match
        readonly Dictionary<TrackBlockLogic, int> joined = new Dictionary<TrackBlockLogic, int>();
        int jointTicks = JointRefresh;

        struct FreePort
        {
            public TrackBlockLogic Block;
            public int Port;
            public Vector3D Point, Forward;
        }

        readonly List<FreePort> freePorts = new List<FreePort>();
        readonly Dictionary<Vector3I, List<int>> portCells = new Dictionary<Vector3I, List<int>>();
        readonly List<List<int>> cellPool = new List<List<int>>();

        bool Joined(TrackBlockLogic b, int port)
        {
            int mask;
            return joined.TryGetValue(b, out mask) && (mask & (1 << port)) != 0;
        }

        void RefreshJoints()
        {
            if (++jointTicks < JointRefresh)
                return;
            jointTicks = 0;
            joined.Clear();
            freePorts.Clear();
            foreach (List<int> list in portCells.Values)
            {
                list.Clear();
                cellPool.Add(list);
            }
            portCells.Clear();
            // A little past the marker range: a block just out of it may still meet one in it.
            Vector3D camera = MyAPIGateway.Session.Camera.WorldMatrix.Translation;
            double range = MarkerRange + 10;
            foreach (TrackBlockLogic b in Network.Blocks)
            {
                if (!b.PortsKnown || Vector3D.DistanceSquared(b.Block.WorldMatrix.Translation, camera) > range * range)
                    continue;
                for (int i = 0; i < b.PortCount; i++)
                {
                    if (!b.IsJointPort(i) || Network.SpanAt(b, i) != null)
                        continue;
                    MatrixD m = b.PortWorld(i);
                    Vector3I cell = Vector3I.Floor(m.Translation / JointCell);
                    List<int> list;
                    if (!portCells.TryGetValue(cell, out list))
                    {
                        list = cellPool.Count > 0 ? cellPool[cellPool.Count - 1] : new List<int>();
                        if (cellPool.Count > 0)
                            cellPool.RemoveAt(cellPool.Count - 1);
                        portCells[cell] = list;
                    }
                    list.Add(freePorts.Count);
                    freePorts.Add(new FreePort { Block = b, Port = i, Point = m.Translation, Forward = m.Forward });
                }
            }
            for (int n = 0; n < freePorts.Count; n++)
            {
                FreePort p = freePorts[n];
                if (!Meets(p))
                    continue;
                int mask;
                joined.TryGetValue(p.Block, out mask);
                joined[p.Block] = mask | (1 << p.Port);
            }
        }

        // A free port of another block, one a train can ride onto, meets this one end to end (TrackNetwork.FindJoint).
        bool Meets(FreePort p)
        {
            Vector3I cell = Vector3I.Floor(p.Point / JointCell);
            const double reach = TrackNetwork.JointDistance * TrackNetwork.JointDistance;
            for (int x = -1; x <= 1; x++)
                for (int y = -1; y <= 1; y++)
                    for (int z = -1; z <= 1; z++)
                    {
                        List<int> list;
                        if (!portCells.TryGetValue(cell + new Vector3I(x, y, z), out list))
                            continue;
                        for (int k = 0; k < list.Count; k++)
                        {
                            FreePort q = freePorts[list[k]];
                            if (q.Block != p.Block && q.Block.Rideable && Vector3D.DistanceSquared(q.Point, p.Point) <= reach
                                && Vector3D.Dot(q.Forward, p.Forward) <= TrackNetwork.JointFacingCos)
                                return true;
                        }
                    }
            return false;
        }
    }
}
