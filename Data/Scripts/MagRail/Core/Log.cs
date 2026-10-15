using System;
using System.Collections.Generic;
using VRage.Utils;

namespace MagRail
{
    // The mod's log lines in SpaceEngineers.log, all prefixed "MagRail: ".
    // Error() writes a failure once per place: a failure repeating every tick would flood the log.
    public static class Log
    {
        const string Prefix = "MagRail: ";
        static readonly HashSet<string> reported = new HashSet<string>();

        public static void Info(string text)
        {
            MyLog.Default.WriteLineAndConsole(Prefix + text);
        }

        public static void Error(string where, Exception e)
        {
            if (!reported.Add(where))
                return;
            MyLog.Default.WriteLineAndConsole(Prefix + "ERROR in " + where + ": " + e);
        }

        // A failure without an exception (once per place and message).
        public static void Error(string where, string message)
        {
            if (!reported.Add(where + ": " + message))
                return;
            MyLog.Default.WriteLineAndConsole(Prefix + "ERROR in " + where + ": " + message);
        }

        public static void Clear()
        {
            reported.Clear();
        }
    }
}
