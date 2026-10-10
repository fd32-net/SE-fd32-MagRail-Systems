using Sandbox.ModAPI;
using VRage.Game;
using VRage.Game.Entity;
using VRage.Game.ModAPI;
using VRage.Input;
using VRage.ModAPI;
using VRageMath;

namespace MagRail
{
    // The Rail Linker hand tool (Data/PhysicalItems_, HandItems_, Blueprints_MagRail.sbc): a vanilla
    // HandToolBase without actions, so the game does nothing on LMB/RMB and this class reads them.
    //   LMB on a port, LMB on another port: lay a span (always a prototype first).
    //   LMB on a span: prototype <-> rail.
    //   RMB: cancel; on a port with a span: pick that end up (the span goes, its other end is the new
    //   start); Shift+RMB on a port: delete all spans of that block; on a span: delete it.
    // Client side; changes go through TrackNetwork requests (the server checks the tool is in hand).
    public class RailLinker
    {
        public const string Subtype = "MagRail_RailLinker";
        const double AimRange = 40, MarkerRange = 60;
        // While laying with no port aimed, the free end of the preview: this far in front of the
        // camera, facing the start port (the curve does not swing when the player turns).
        const double FreeEndDistance = 7;

        static readonly Color FreeColor = new Color(70, 165, 255);
        static readonly Color LinkedColor = new Color(60, 230, 90);
        static readonly Color AimColor = new Color(255, 230, 70);

        readonly HudLine hint = new HudLine(), info = new HudLine();
        bool held;
        TrackBlockLogic startBlock, aimBlock;
        int startPort = -1, aimPort = -1;
        Span aimSpan;
        SpanCurve preview;          // while laying: start port -> aimed port or free end
        string previewProblem;      // why the aimed port cannot take the span (Loc key)

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

        // The item has the welder model for when it lies on the ground; the hand tool entity uses the
        // same model: hidden, so the hand stays empty, palm out. Every client, each time it is taken.
        public static void HideInHand(IMyEntity entity)
        {
            MyEntity tool = entity as MyEntity;
            if (tool != null && tool.DefinitionId.HasValue && tool.DefinitionId.Value.TypeId == typeof(MyObjectBuilder_HandToolBase)
                && tool.DefinitionId.Value.SubtypeName == Subtype)
                entity.Visible = false;
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
                if (!held)
                {
                    startBlock = null;
                    hint.Hide();
                    info.Hide();
                }
            }
            if (!held)
                return;
            if (startBlock != null && Network.Find(startBlock.Block.EntityId) == null)
                startBlock = null;
            hint.Show(Loc.T("MagRail_Linker_Hint"));
            if (MyAPIGateway.Gui.IsCursorVisible || MyAPIGateway.Gui.ChatEntryVisible)
            {
                info.Hide();
                return;
            }

            Aim();
            if (MyAPIGateway.Input.IsNewLeftMousePressed())
                Left();
            else if (MyAPIGateway.Input.IsNewRightMousePressed())
                Right(MyAPIGateway.Input.IsAnyShiftKeyPressed());
            BuildPreview();
            ShowInfo();
        }

        void BuildPreview()
        {
            preview = null;
            previewProblem = null;
            if (startBlock == null)
                return;
            MatrixD from = startBlock.PortWorld(startPort);
            if (aimBlock != null && !(aimBlock == startBlock && aimPort == startPort))
            {
                previewProblem = Network.LinkProblem(startBlock, startPort, aimBlock, aimPort);
                preview = SpanCurve.Build(from, aimBlock.PortWorld(aimPort));
                return;
            }
            MatrixD cam = MyAPIGateway.Session.Camera.WorldMatrix;
            Vector3D end = cam.Translation + cam.Forward * FreeEndDistance;
            Vector3D back = from.Translation - end;
            if (back.LengthSquared() < 0.01)
                return;
            back.Normalize();
            Vector3D up = from.Up - back * Vector3D.Dot(from.Up, back);
            up = up.LengthSquared() > 1e-6 ? Vector3D.Normalize(up) : Vector3D.CalculatePerpendicularVector(back);
            preview = SpanCurve.Build(from, MatrixD.CreateWorld(end, back, up));
        }

        void Aim()
        {
            aimBlock = null;
            aimPort = -1;
            aimSpan = null;
            MatrixD cam = MyAPIGateway.Session.Camera.WorldMatrix;
            Vector3D origin = cam.Translation, dir = cam.Forward;
            double best = 1;
            foreach (TrackBlockLogic b in Network.Blocks)
            {
                if (!b.PortsKnown || Vector3D.DistanceSquared(b.Block.WorldMatrix.Translation, origin) > (AimRange + 15) * (AimRange + 15))
                    continue;
                for (int i = 0; i < b.PortCount; i++)
                {
                    double score = AimScore(b.PortWorld(i).Translation, origin, dir, 0.3);
                    if (score < best)
                    {
                        best = score;
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
                for (int n = 0; n < c.Pos.Count; n += 2)
                {
                    double score = AimScore(Vector3D.Transform(c.Pos[n], frame), origin, dir, 0.4);
                    if (score < best)
                    {
                        best = score;
                        aimSpan = s;
                    }
                }
            }
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
                if (aimBlock == null || (aimBlock == startBlock && aimPort == startPort))
                    return;
                string problem = Network.LinkProblem(startBlock, startPort, aimBlock, aimPort);
                if (problem != null)
                {
                    Draw.Notify(Loc.T(problem), MyFontEnum.Red);
                    return;
                }
                Network.RequestLink(startBlock, startPort, aimBlock, aimPort);
                startBlock = null;
                Draw.Notify(Loc.T("MagRail_Linker_Linked"), MyFontEnum.Green);
                return;
            }
            if (aimBlock != null)
            {
                if (Network.SpanAt(aimBlock, aimPort) != null)
                {
                    Draw.Notify(Loc.T("MagRail_Linker_PortBusy"), MyFontEnum.Red);
                    return;
                }
                startBlock = aimBlock;
                startPort = aimPort;
                Draw.Notify(Loc.F("MagRail_Linker_Started", PortTitle(startBlock, startPort)), MyFontEnum.Green);
                return;
            }
            if (aimSpan != null)
            {
                bool rail = !aimSpan.Record.WantRail;
                Network.RequestMode(aimSpan, rail);
                if (rail && aimSpan.Problem != null)
                    Draw.Notify(Loc.F("MagRail_Linker_RailWaits", Loc.T(aimSpan.Problem)), MyFontEnum.Red, 3000);
                else
                    Draw.Notify(Loc.T(rail ? "MagRail_Linker_ToRail" : "MagRail_Linker_ToPrototype"), MyFontEnum.Green);
            }
        }

        void Right(bool shift)
        {
            if (startBlock != null)
            {
                startBlock = null;
                Draw.Notify(Loc.T("MagRail_Linker_Cancelled"));
                return;
            }
            if (aimBlock != null)
            {
                if (shift)
                {
                    for (int i = 0; i < aimBlock.PortCount; i++)
                    {
                        Span s = Network.SpanAt(aimBlock, i);
                        if (s != null)
                            Network.RequestUnlink(s);
                    }
                    Draw.Notify(Loc.T("MagRail_Linker_BlockCleared"));
                    return;
                }
                Span span = Network.SpanAt(aimBlock, aimPort);
                if (span == null)
                    return;
                startBlock = span.Other(aimBlock);
                startPort = startBlock == span.Owner ? span.OwnerPort : span.PartnerPort;
                Network.RequestUnlink(span);
                Draw.Notify(Loc.T("MagRail_Linker_PickedUp"), MyFontEnum.Green);
                return;
            }
            if (aimSpan != null)
            {
                Network.RequestUnlink(aimSpan);
                Draw.Notify(Loc.T("MagRail_Linker_Deleted"));
            }
        }

        void ShowInfo()
        {
            if (startBlock != null)
            {
                string text = Loc.F("MagRail_Linker_From", PortTitle(startBlock, startPort));
                if (preview != null)
                    text += " → " + CurveText(preview);
                if (previewProblem != null)
                    info.Show(text + " — " + Loc.T(previewProblem), MyFontEnum.Red);
                else
                    info.Show(text, aimBlock != null ? MyFontEnum.Green : MyFontEnum.White);
                return;
            }
            if (aimBlock != null)
            {
                bool busy = Network.SpanAt(aimBlock, aimPort) != null;
                info.Show(PortTitle(aimBlock, aimPort) + ": " + Loc.T(busy ? "MagRail_Linker_AimBusy" : "MagRail_Linker_AimFree"));
                return;
            }
            // The aimed span may have just been deleted (RMB) or lost its partner.
            if (aimSpan != null && aimSpan.Bound)
            {
                string text = Loc.T(aimSpan.IsRail ? "MagRail_Mode_Rail" : "MagRail_Mode_Prototype") + ", " + CurveText(Network.CurveOf(aimSpan));
                if (aimSpan.Record.WantRail && aimSpan.Problem != null)
                    text += " — " + Loc.F("MagRail_Linker_RailWaits", Loc.T(aimSpan.Problem));
                info.Show(text);
                return;
            }
            info.Hide();
        }

        static string CurveText(SpanCurve c)
        {
            // The game font has no infinity sign.
            if (double.IsInfinity(c.MinRadius))
                return Loc.F("MagRail_Linker_Straight", c.Length.ToString("0.0"));
            return Loc.F("MagRail_Linker_Curve", c.Length.ToString("0.0"), c.MinRadius.ToString("0.0"));
        }

        static string PortTitle(TrackBlockLogic block, int port)
        {
            return block.Block.CustomName + " · " + PortLabel(block, port);
        }

        // The port's name for players (the dummy name if there is no translation).
        public static string PortLabel(TrackBlockLogic block, int port)
        {
            string key = "MagRail_Port_" + block.PortName(port);
            string name = Loc.T(key);
            return name == key ? block.PortName(port) : name;
        }

        // ---- drawing (every frame) ----

        public void DrawFrame()
        {
            if (!held)
                return;
            Vector3D camera = MyAPIGateway.Session.Camera.WorldMatrix.Translation;
            foreach (TrackBlockLogic b in Network.Blocks)
            {
                if (!b.PortsKnown || Vector3D.DistanceSquared(b.Block.WorldMatrix.Translation, camera) > MarkerRange * MarkerRange)
                    continue;
                for (int i = 0; i < b.PortCount; i++)
                {
                    bool aimed = (b == aimBlock && i == aimPort) || (b == startBlock && i == startPort);
                    Color color = aimed ? AimColor : Network.SpanAt(b, i) != null ? LinkedColor : FreeColor;
                    DrawPort(b.PortWorld(i), color, aimed);
                }
            }
            if (preview != null)
                SpanLines.DrawCurve(preview, MatrixD.Identity, LineStyle.Preview);
        }

        // A cross across the rail at the port and a stroke outwards.
        static void DrawPort(MatrixD port, Color color, bool aimed)
        {
            double h = aimed ? 0.35 : 0.25;
            float thickness = aimed ? 0.08f : 0.05f;
            Vector3D p = port.Translation;
            MagRail.Draw.Line(p - port.Right * h, p + port.Right * h, color, thickness);
            MagRail.Draw.Line(p - port.Up * h, p + port.Up * h, color, thickness);
            MagRail.Draw.Line(p, p + port.Forward * h * 2, color, thickness);
        }
    }
}
