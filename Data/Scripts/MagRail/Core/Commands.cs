using System;
using System.Diagnostics;
using Sandbox.ModAPI;
using VRage.Game.ModAPI;
using VRageMath;

namespace MagRail
{
    // Chat command /magrail: counts of track blocks, spans and track pieces, and the mod's time per tick.
    // /magrail debug, /magrail cursor [speed | off]: track checks, see TrackDebug.
    // /magrail log, /magrail mark <text>: the ride log, see RideLog.
    // /magrail skin: TEMPORARY probe - the player's build skin (P menu) as the game saves it, and how long that takes.
    // /magrail platform [m]: TEMPORARY (engine platform test T2) - bases drawn this much lower, EnginePlatform.DebugSink.
    public static class Commands
    {
        public static void OnMessage(string text, ref bool sendToOthers)
        {
            if (!text.StartsWith("/magrail"))
                return;
            sendToOthers = false;
            string[] words = text.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            string command = words.Length > 1 ? words[1].ToLowerInvariant() : "";
            if (command == "debug")
            {
                TrackDebug.Engines = !TrackDebug.Engines;
                MyAPIGateway.Utilities.ShowMessage("MagRail", Loc.T(TrackDebug.Engines ? "MagRail_Debug_EnginesOn" : "MagRail_Debug_EnginesOff"));
                return;
            }
            if (command == "log")
            {
                MyAPIGateway.Utilities.ShowMessage("MagRail", RideLog.Toggle());
                return;
            }
            if (command == "mark")
            {
                RideLog.Note("mark " + (text.Length > 14 ? text.Substring(14).Trim() : ""));
                MyAPIGateway.Utilities.ShowMessage("MagRail", Loc.T(RideLog.Active ? "MagRail_Log_Mark" : "MagRail_Log_Off"));
                return;
            }
            if (command == "skin")
            {
                ProbeSkin();
                return;
            }
            if (command == "platform")
            {
                float sink;
                if (words.Length < 3 || !float.TryParse(words[2], System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out sink))
                    sink = 0;
                EnginePlatform.DebugSink = sink;
                MyAPIGateway.Utilities.ShowMessage("MagRail", Loc.F("MagRail_Cmd_PlatformSink", sink.ToString("0.00")));
                return;
            }
            if (command == "cursor")
            {
                MyAPIGateway.Utilities.ShowMessage("MagRail", TrackDebug.StartGhost(words.Length > 2 ? words[2].ToLowerInvariant() : ""));
                return;
            }
            MagRailSession session = MagRailSession.Instance;
            int rail = 0, prototype = 0, unbound = 0;
            foreach (Span s in session.Network.Spans)
            {
                if (!s.Bound)
                    unbound++;
                else if (s.IsRail)
                    rail++;
                else
                    prototype++;
            }
            MyAPIGateway.Utilities.ShowMessage("MagRail", Loc.F("MagRail_Cmd_Summary", session.Network.Blocks.Count, rail, prototype, unbound));
            MyAPIGateway.Utilities.ShowMessage("MagRail", Loc.F("MagRail_Cmd_Pieces", session.Pieces.EntityCount, session.Collision.EntityCount));
            MyAPIGateway.Utilities.ShowMessage("MagRail", Loc.F("MagRail_Cmd_Perf", session.UpdateTime.Average.ToString("0.00"),
                session.UpdateTime.Worst.ToString("0.00"), session.DrawTime.Average.ToString("0.00"), session.DrawTime.Worst.ToString("0.00")));
            MyAPIGateway.Utilities.ShowMessage("MagRail", Loc.F("MagRail_Cmd_Rides", session.Engines.Count, session.Rides.Count,
                session.RideTime.Average.ToString("0.00"), session.RideTime.Worst.ToString("0.00")));
            MyAPIGateway.Utilities.ShowMessage("MagRail", Loc.F("MagRail_Cmd_Terrain", TerrainLift.Calls, TerrainLift.Lifted,
                TerrainLift.TotalMs.ToString("0.0"), TerrainLift.WorstMs.ToString("0.0"), (TerrainLift.Voxels / 1000).ToString()));
        }

        // How the Rail Linker reads the build skin (RailLinker.BuildSkin), and how long it takes.
        static void ProbeSkin()
        {
            IMyPlayer me = MyAPIGateway.Session.Player;
            if (me == null)
                return;
            Stopwatch watch = Stopwatch.StartNew();
            string skin = RailLinker.BuildSkin(me);
            double ms = watch.Elapsed.TotalMilliseconds;
            Vector3 hsv = me.SelectedBuildColor;
            string text = Loc.F("MagRail_Cmd_Skin", skin == "" ? "-" : skin,
                hsv.X.ToString("0.000") + " " + hsv.Y.ToString("0.000") + " " + hsv.Z.ToString("0.000"), ms.ToString("0.0"));
            MyAPIGateway.Utilities.ShowMessage("MagRail", text);
            Log.Info(text);
        }
    }
}
