using System;
using Sandbox.ModAPI;
using VRage.Game;
using VRageMath;

namespace MagRail
{
    // Checks of the track for the physics, without the physics (clients, chat commands):
    // - /magrail debug: every hover engine draws a line from its guide point to the rail point its cursor
    //   found, green = the rail would be captured (hover point within 4 m, engine upright to the rail),
    //   yellow = found but too far or tilted; a white arrow = the cursor's direction.
    // - /magrail cursor [speed | off]: a ghost cursor runs along the track from the rail point looked at,
    //   through spans, lanes, joints and switches, turns back at a dead end; the HUD says where it is.
    public static class TrackDebug
    {
        const double GhostSpeed = 20, LookRange = 150, LookStep = 0.5, LookRadius = 1.5;
        const int TurnTicks = 60;
        static readonly Color Ghost = new Color(80, 200, 255), Arrow = Color.White, UpColor = new Color(120, 140, 255);

        public static bool Engines;
        static TrackCursor ghost;
        static double speed = GhostSpeed;
        static int turning;
        static readonly HudLine hud = new HudLine();

        // /magrail cursor [speed | off]
        public static string StartGhost(string arg)
        {
            if (arg == "off")
            {
                Stop();
                return Loc.T("MagRail_Debug_GhostOff");
            }
            double v;
            speed = double.TryParse(arg, out v) && v > 0 ? v : GhostSpeed;
            MatrixD camera = MyAPIGateway.Session.Camera.WorldMatrix;
            TrackCursor c = new TrackCursor();
            for (double d = LookStep; d <= LookRange; d += LookStep)
            {
                TrackSample sample;
                if (c.TryProject(camera.Translation + camera.Forward * d, camera.Forward, LookRadius, 0, out sample))
                {
                    ghost = c;
                    turning = 0;
                    return Loc.F("MagRail_Debug_GhostOn", speed.ToString("0.#"));
                }
            }
            Stop();
            return Loc.T("MagRail_Debug_NoRail");
        }

        static void Stop()
        {
            ghost = null;
            hud.Hide();
        }

        public static void Update()
        {
            if (ghost == null)
                return;
            if (turning > 0)
            {
                if (--turning == 0)
                    ghost.Reverse();
                return;
            }
            if (ghost.Advance(speed / 60))
                return;
            if (!ghost.Valid)
            {
                Stop();
                Draw.Notify(Loc.T("MagRail_Debug_GhostLost"), MyFontEnum.Red);
                return;
            }
            turning = TurnTicks;
        }

        public static void DrawFrame(MagRailSession session)
        {
            if (Engines)
                for (int i = 0; i < session.Engines.Count; i++)
                    DrawEngine(session.Engines[i]);
            if (ghost == null)
                return;
            // A piece gone under the ghost: the next Update stops it.
            if (!ghost.Valid || !ghost.Rideable)
                return;
            TrackSample s = ghost.Sample(Vector3D.Zero, 0);
            Vector3D side = Vector3D.Cross(s.Tangent, s.Up);
            Draw.Line(s.Point - side * 0.6, s.Point + side * 0.6, Ghost, 0.12f);
            Draw.Line(s.Point, s.Point + s.Tangent * 2, Arrow, 0.06f);
            Draw.Line(s.Point, s.Point + s.Up, UpColor, 0.06f);
            hud.Show(Describe(ghost), ghost.DeadEnd ? MyFontEnum.Red : MyFontEnum.White);
        }

        static void DrawEngine(HoverEngineLogic engine)
        {
            Vector3D guide = engine.GuidePoint;
            TrackSample s;
            if (!engine.Cursor.TryProject(guide, engine.Block.WorldMatrix.Forward, engine.SearchDistance, 0, out s))
                return;
            Vector3D error = s.Point + s.Up * engine.HoverHeight - guide;
            error -= s.Tangent * Vector3D.Dot(error, s.Tangent);
            bool capture = error.Length() <= HoverEngineLogic.CaptureDistance && Vector3D.Dot(engine.Block.WorldMatrix.Up, s.Up) >= 0.5;
            Draw.Line(guide, s.Point, capture ? Draw.Green : Draw.Yellow, 0.05f);
            Draw.Line(s.Point, s.Point + s.Tangent * 1.5, Arrow, 0.04f);
        }

        static string Describe(TrackCursor c)
        {
            string text;
            double length = c.Curve.Length;
            if (c.Span != null)
            {
                text = Loc.F("MagRail_Debug_OnSpan", c.S.ToString("0.0"), length.ToString("0.0"));
            }
            else
            {
                TrackBlockLogic b = c.Lane.Block;
                int from = c.Dir > 0 ? c.Lane.From : c.Lane.To, to = c.Dir > 0 ? c.Lane.To : c.Lane.From;
                text = Loc.F("MagRail_Debug_OnLane", b.Block.CustomName, RailLinker.PortLabel(b, from), RailLinker.PortLabel(b, to), c.S.ToString("0.0"), length.ToString("0.0"));
            }
            return c.DeadEnd ? text + " · " + Loc.T("MagRail_Debug_DeadEnd") : text;
        }

        public static void Clear()
        {
            Stop();
            Engines = false;
        }
    }
}
