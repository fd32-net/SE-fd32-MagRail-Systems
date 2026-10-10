using System;
using Sandbox.ModAPI;

namespace MagRail
{
    // Chat command /magrail: counts of track blocks, spans and track pieces, and the mod's time per tick.
    // /magrail debug, /magrail cursor [speed | off]: track checks, see TrackDebug.
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
        }
    }
}
