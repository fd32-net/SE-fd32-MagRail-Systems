using System;
using System.Collections.Generic;
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
        const double AimRange = 40, MarkerRange = 300;
        // End faces far away stay at least this wide on screen: m per m of distance (~15 px across at 1080p).
        const double FaceOnScreen = 0.02;
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
        // What the preview was built for (BuildPreview), and its lift over the ground (TerrainLift.Measure: reads voxels,
        // so measured again at most every PreviewLiftSeconds; the curve itself follows every frame).
        const double PreviewLiftSeconds = 0.1;
        int previewAimPort = -1;
        TrackBlockLogic previewAimBlock;
        Vector3D previewFrom, previewTo, previewToForward;
        float[] previewLift;
        double previewLiftTime = double.NegativeInfinity;
        bool previewLiftStale;      // the curve moved since its lift was measured

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
            RefreshJoints();
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
            else if (MyAPIGateway.Input.IsNewMiddleMousePressed())
                Middle();
            BuildPreview(true);
            ShowInfo();
        }

        // Every tick (HandleInput: also why the aimed port cannot take it) and every frame (DrawFrame: the free end
        // follows the camera without lagging behind it at a frame rate over 60).
        void BuildPreview(bool tick)
        {
            if (tick)
                previewProblem = null;
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

            // Over planet ground the preview is lifted like the span will be (TerrainLift): the curve is rebuilt
            // whenever its ends move, the lift measured again every PreviewLiftSeconds (at once when the aimed port
            // changes) and laid on the new curve in between.
            bool same = preview != null && Vector3D.DistanceSquared(from.Translation, previewFrom) < 1e-6
                        && Vector3D.DistanceSquared(to.Translation, previewTo) < 1e-6
                        && Vector3D.DistanceSquared(to.Forward, previewToForward) < 1e-8;
            bool targetChanged = aimBlock != previewAimBlock || aimPort != previewAimPort;
            double now = MyAPIGateway.Session.ElapsedPlayTime.TotalSeconds;
            bool remeasure = targetChanged || ((!same || previewLiftStale) && now - previewLiftTime >= PreviewLiftSeconds);
            if (same && !remeasure)
                return;
            previewFrom = from.Translation;
            previewTo = to.Translation;
            previewToForward = to.Forward;
            previewAimBlock = aimBlock;
            previewAimPort = aimPort;
            SpanCurve curve = SpanCurve.Build(from, to);
            if (!startBlock.Block.CubeGrid.IsStatic || !toStatic)
            {
                preview = curve;
                return;
            }
            if (remeasure)
            {
                previewLift = TerrainLift.Measure(curve, MatrixD.Identity, startBlock.Style.Width, freeEnd);
                previewLiftTime = now;
            }
            previewLiftStale = !remeasure;
            preview = TerrainLift.Apply(curve, MatrixD.Identity, previewLift);
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
                    if (Joined(b, i))
                        continue;
                    double score = AimScore(b.PortAim(i), origin, dir, 0.3);
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

        // Paint like the vanilla paint: MMB paints the aimed span with the player's colour and skin (picked with P).
        void Middle()
        {
            if (startBlock != null || aimSpan == null || !aimSpan.Bound)
                return;
            IMyPlayer player = MyAPIGateway.Session.Player;
            if (player == null)
                return;
            Network.RequestPaint(aimSpan, player.SelectedBuildColor, BuildSkin(player));
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
            if (!MyAPIGateway.Gui.IsCursorVisible && !MyAPIGateway.Gui.ChatEntryVisible)
                BuildPreview(false);
            foreach (TrackBlockLogic b in Network.Blocks)
            {
                if (!b.PortsKnown || Vector3D.DistanceSquared(b.Block.WorldMatrix.Translation, MagRail.Draw.Camera) > MarkerRange * MarkerRange)
                    continue;
                for (int i = 0; i < b.PortCount; i++)
                {
                    int rank = b.FanRank(i);
                    if (rank == (int)SwitchBranch.Center)
                        DrawSwitchFace(b);
                    else if (rank < 0 && !Joined(b, i))
                        DrawPort(b, i);
                }
            }
            if (preview != null)
                SpanLines.DrawCurve(preview, MatrixD.Identity, startBlock.Style, LineStyle.Preview);
        }

        // 0 = none, 1 = start of the span being laid, 2 = aimed.
        int Selection(TrackBlockLogic b, int port)
        {
            return b == aimBlock && port == aimPort ? 2 : b == startBlock && port == startPort ? 1 : 0;
        }

        void DrawPort(TrackBlockLogic b, int port)
        {
            MatrixD m = b.PortWorld(port);
            DrawEnd(b.Style, -1, m, m.Translation, m.Forward, Network.SpanAt(b, port) != null, Selection(b, port));
        }

        // The branch ports of a Rail Switch 1 share one point: one end face, each third lit by its branch.
        void DrawSwitchFace(TrackBlockLogic b)
        {
            MatrixD face = b.PortWorld(b.PortIndex(TrackBlockLogic.BranchPorts[(int)SwitchBranch.Center]));
            for (int rank = 0; rank < TrackBlockLogic.BranchPorts.Length; rank++)
            {
                int q = b.PortIndex(TrackBlockLogic.BranchPorts[rank]);
                if (q >= 0 && !Joined(b, q))
                    DrawEnd(b.Style, rank, face, b.PortAim(q), b.PortWorld(q).Forward, Network.SpanAt(b, q) != null, Selection(b, q));
            }
        }

        // The rail's end face (or the switch's third `part`) in the port's plane, a dot where the port is aimed at and,
        // unless linked, a stick outwards with a ball on its end. Bright and seen through models, so ports are easy to
        // find along a long rail (as bright behind a model as in front of it); far away all of it keeps a size on
        // screen. The outline, dot and stick show linked or
        // free; the inside is yellow while the port is selected (selection: 1 = start, 2 = aimed, pulses).
        static void DrawEnd(TrackStyle style, int part, MatrixD face, Vector3D aim, Vector3D forward, bool linked, int selection)
        {
            const float Bright = 3;
            Color color = linked ? LinkedColor : FreeColor;
            double distance = Vector3D.Distance(face.Translation, MagRail.Draw.Camera);
            float scale = (float)Math.Max(1, distance * FaceOnScreen / style.Width);
            float pulse = selection == 2 ? MagRail.Draw.Pulse : 0;
            if (selection > 0)
                MagRail.Draw.Face(style, part, true, face.Translation, face.Right, face.Up, AimColor * (0.6f + 0.25f * pulse), scale,
                    true, Bright * (1 + pulse), throughAlpha: 1);
            else
                MagRail.Draw.Face(style, part, true, face.Translation, face.Right, face.Up, color * SpanLines.FillAlpha, scale, true, throughAlpha: 1);
            MagRail.Draw.Face(style, part, false, face.Translation, face.Right, face.Up, color, scale, true, Bright, throughAlpha: 1);
            Vector3D p = face.Translation + (aim - face.Translation) * scale;
            MagRail.Draw.Dot(p, color, Math.Max(0.1f * scale, (float)(distance * 0.004)), true, Bright, throughAlpha: 1);
            if (linked)
                return;
            Vector3D tip = p + forward * 0.7 * scale;
            MagRail.Draw.Line(p, tip, color, MagRail.Draw.Width(distance, 0.05f * scale), true, Bright, throughAlpha: 1);
            MagRail.Draw.Dot(tip, color, Math.Max(0.13f * scale, (float)(distance * 0.005)), true, Bright, throughAlpha: 1);
        }

        // ---- joints ----

        // Ports joined end to end to another block (TrackNetwork.FindJoint) get no marker and are not aimed at: a bit
        // per port, found again every JointRefresh ticks for the blocks in marker range.
        const int JointRefresh = 30;
        readonly Dictionary<TrackBlockLogic, int> joined = new Dictionary<TrackBlockLogic, int>();
        int jointTicks = JointRefresh;

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
            Vector3D camera = MyAPIGateway.Session.Camera.WorldMatrix.Translation;
            foreach (TrackBlockLogic b in Network.Blocks)
            {
                if (!b.PortsKnown || Vector3D.DistanceSquared(b.Block.WorldMatrix.Translation, camera) > MarkerRange * MarkerRange)
                    continue;
                int mask = 0;
                TrackBlockLogic other;
                int otherPort;
                for (int i = 0; i < b.PortCount; i++)
                    if (Network.FindJoint(b, i, out other, out otherPort))
                        mask |= 1 << i;
                if (mask != 0)
                    joined[b] = mask;
            }
        }
    }
}
