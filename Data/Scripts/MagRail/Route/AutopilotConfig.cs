using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Sandbox.ModAPI;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRageMath;

namespace MagRail
{
    // An autopilot's stations and settings, kept in the block's Storage and sent whole over the network.
    public class AutopilotConfig
    {
        public const int MaxStations = 10;
        const int MaxNameLength = 40;
        const string Header = "MR1";

        public class Station
        {
            public string Name = "";
            // The rail point under the block when it was remembered: in the coordinates of the rail's grid (it
            // may be a ship), and in the world for when that grid is gone.
            public long GridId;
            public Vector3D Local, World;
            // Timer block triggered on arrival (0 = none).
            public long TimerId;

            public Vector3D Point
            {
                get
                {
                    IMyEntity entity;
                    IMyCubeGrid grid = GridId != 0 && MyAPIGateway.Entities.TryGetEntityById(GridId, out entity) ? entity as IMyCubeGrid : null;
                    return grid != null ? Vector3D.Transform(Local, grid.WorldMatrix) : World;
                }
            }
        }

        public readonly List<Station> Stations = new List<Station>();
        public int Selected = -1;
        public int MaxSpeed = 20;
        // Driving (or about to drive) to Target; Last is the station last started for (Next goes on from it).
        public bool Running;
        public int Target = -1, Last = -1;
        // The autopilot drives or halts the train now (server state, sent to clients).
        public bool Active;
        // Park the train (hand brake) on arrival; show the stations as GPS marks.
        public bool Park, ShowStations;
        // Stop exactly over the station (creeping up to it, backing up if past), else wherever the braking curve ends.
        public bool Precise = true;
        // Acceleration and braking asked of the train (m/s^2), and the side force allowed in curves (g; 0 = no
        // limit, curves do not slow it). User decisions 2026-10-15: set per autopilot, 1..50 m/s^2 (default 20),
        // 1..9 g or no limit.
        public double Accel = 20, Brake = 20, Lateral = 3;
        public const double MinAccel = 1, MaxAccel = 50, MinLateral = 1, MaxLateral = 9, G = 9.81;

        // The side acceleration allowed in curves, m/s^2 (0 = no limit).
        public double LateralAccel
        {
            get { return Lateral * G; }
        }

        // Line 1: header; line 2: selected, max speed, running, target, last, active, park, show stations,
        // acceleration, braking, side acceleration, precise stop (tab separated); then a line per station: world x y z, grid id, local x y z, timer id, name.
        public string Save()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append(Header).Append('\n').Append(Selected).Append('\t').Append(MaxSpeed).Append('\t').Append(Running ? 1 : 0)
                .Append('\t').Append(Target).Append('\t').Append(Last).Append('\t').Append(Active ? 1 : 0)
                .Append('\t').Append(Park ? 1 : 0).Append('\t').Append(ShowStations ? 1 : 0)
                .Append('\t').Append(Accel.ToString("R", CultureInfo.InvariantCulture)).Append('\t').Append(Brake.ToString("R", CultureInfo.InvariantCulture))
                .Append('\t').Append(Lateral.ToString("R", CultureInfo.InvariantCulture)).Append('\t').Append(Precise ? 1 : 0);
            for (int i = 0; i < Stations.Count; i++)
            {
                Station s = Stations[i];
                sb.Append('\n');
                Vec(sb, s.World).Append(s.GridId).Append('\t');
                Vec(sb, s.Local).Append(s.TimerId).Append('\t').Append(s.Name);
            }
            return sb.ToString();
        }

        static StringBuilder Vec(StringBuilder sb, Vector3D v)
        {
            return sb.Append(v.X.ToString("R", CultureInfo.InvariantCulture)).Append('\t').Append(v.Y.ToString("R", CultureInfo.InvariantCulture))
                .Append('\t').Append(v.Z.ToString("R", CultureInfo.InvariantCulture)).Append('\t');
        }

        public static AutopilotConfig Load(string text)
        {
            AutopilotConfig c = new AutopilotConfig();
            if (string.IsNullOrEmpty(text))
                return c;
            string[] lines = text.Split('\n');
            if (lines.Length < 2 || lines[0] != Header)
                return c;
            string[] head = lines[1].Split('\t');
            if (head.Length < 8)
                return c;
            int.TryParse(head[0], out c.Selected);
            int speed;
            if (int.TryParse(head[1], out speed))
                c.MaxSpeed = Math.Max(1, speed);
            c.Running = head[2] == "1";
            int.TryParse(head[3], out c.Target);
            int.TryParse(head[4], out c.Last);
            c.Active = head[5] == "1";
            c.Park = head[6] == "1";
            c.ShowStations = head[7] == "1";
            double v;
            if (head.Length > 10)
            {
                if (Num(head[8], out v))
                    c.Accel = MathHelper.Clamp(v, MinAccel, MaxAccel);
                if (Num(head[9], out v))
                    c.Brake = MathHelper.Clamp(v, MinAccel, MaxAccel);
                if (Num(head[10], out v))
                    c.Lateral = ClampLateral(v);
            }
            if (head.Length > 11)
                c.Precise = head[11] != "0";
            for (int i = 2; i < lines.Length; i++)
            {
                string[] f = lines[i].Split('\t');
                Station s = new Station();
                if (f.Length < 9 || !Vec(f, 0, out s.World) || !long.TryParse(f[3], out s.GridId) || !Vec(f, 4, out s.Local)
                    || !long.TryParse(f[7], out s.TimerId))
                    continue;
                s.Name = f[8];
                c.Stations.Add(s);
            }
            c.ClampIndices();
            return c;
        }

        static bool Vec(string[] f, int at, out Vector3D v)
        {
            double x, y, z;
            bool ok = Num(f[at], out x) & Num(f[at + 1], out y) & Num(f[at + 2], out z);
            v = new Vector3D(x, y, z);
            return ok;
        }

        static bool Num(string s, out double v)
        {
            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v);
        }

        // 0 = no limit, else MinLateral..MaxLateral g.
        public static double ClampLateral(double g)
        {
            return g <= 0 || g > MaxLateral ? 0 : Math.Max(MinLateral, g);
        }

        public void ClampIndices()
        {
            int n = Stations.Count;
            if (Selected >= n || (Selected < 0 && n > 0))
                Selected = n - 1;
            if (Target >= n)
                Target = -1;
            if (Last >= n)
                Last = -1;
            if (Target < 0)
                Running = false;
        }

        public bool Has(int index)
        {
            return index >= 0 && index < Stations.Count;
        }

        public int NextIndex
        {
            get { return Stations.Count > 0 ? (Last + 1) % Stations.Count : -1; }
        }

        public static string CleanName(string name)
        {
            if (name == null)
                return "";
            name = name.Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ').Trim();
            return name.Length > MaxNameLength ? name.Substring(0, MaxNameLength) : name;
        }

        // In the local player's language: the name, or "Station N" when it has none.
        public string Name(int index)
        {
            if (!Has(index))
                return "-";
            string name = Stations[index].Name;
            return string.IsNullOrEmpty(name) ? Loc.F("MagRail_Ap_StationN", index + 1) : name;
        }

        // The station as a status argument (AutopilotStatus): rendered in each player's language.
        public string Arg(int index)
        {
            if (!Has(index))
                return "-";
            string name = Stations[index].Name;
            return string.IsNullOrEmpty(name) ? AutopilotStatus.StationArg(index + 1) : name;
        }
    }

    // A status line as it travels over the network: a text key and its arguments, rendered on each machine in its
    // player's language. An argument is plain text, a station number without a name, or another text key.
    public static class AutopilotStatus
    {
        const char Separator = '\u001f', StationMark = '\u0001', KeyMark = '\u0002';

        public static string Make(string key, params string[] args)
        {
            StringBuilder sb = new StringBuilder(key);
            for (int i = 0; i < args.Length; i++)
                sb.Append(Separator).Append(args[i]);
            return sb.ToString();
        }

        public static string StationArg(int number)
        {
            return StationMark + number.ToString(CultureInfo.InvariantCulture);
        }

        public static string KeyArg(string key)
        {
            return KeyMark + key;
        }

        public static string Key(string status)
        {
            int i = status.IndexOf(Separator);
            return i < 0 ? status : status.Substring(0, i);
        }

        public static string Render(string status)
        {
            if (string.IsNullOrEmpty(status))
                return "";
            string[] parts = status.Split(Separator);
            object[] args = new object[parts.Length - 1];
            for (int i = 1; i < parts.Length; i++)
            {
                string a = parts[i];
                if (a.Length > 0 && a[0] == StationMark)
                    args[i - 1] = Loc.F("MagRail_Ap_StationN", a.Substring(1));
                else if (a.Length > 0 && a[0] == KeyMark)
                    args[i - 1] = Loc.T(a.Substring(1));
                else
                    args[i - 1] = a;
            }
            return Loc.F(parts[0], args);
        }
    }
}
