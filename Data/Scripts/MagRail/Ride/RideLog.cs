using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Sandbox.ModAPI;
using VRage.Game.ModAPI;

namespace MagRail
{
    // /magrail log: every tick of every grid on the rail to a CSV file in the mod's local storage
    // (%AppData%\SpaceEngineers\Storage\<mod>_MagRail\ride-*.csv), with the pilot's input, and engine drops and
    // /magrail mark notes as "#" lines. For tuning the ride: the developer reads the file. Server only (single
    // player); stops itself after 10 minutes.
    public static class RideLog
    {
        const int MaxTicks = 60 * 600;
        const string Header = "tick,grid,held,engines,mass_t,speed,input_fwd,input_side,input_up,dampers,pilot,err_max,"
            + "yaw_deg,pitch_deg,roll_deg,push_along,push_side,push_up,push_spin,hold_pct,turn_pct,drops,"
            + "drive,thrust_kn,brake_kn,power_pct,"
            + "drift,path_fwd_x,path_fwd_y,path_fwd_z,"
            + "place,place_m,radius_m,x,y,z";

        static TextWriter writer;
        static int tick;

        public static bool Active
        {
            get { return writer != null; }
        }

        public static string Toggle()
        {
            if (writer != null)
            {
                Stop();
                return Loc.T("MagRail_Log_Stopped");
            }
            if (!Net.IsServer)
                return Loc.T("MagRail_Log_ServerOnly");
            string name = "ride-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".csv";
            writer = MyAPIGateway.Utilities.WriteFileInLocalStorage(name, typeof(RideLog));
            tick = 0;
            writer.WriteLine("# MagRail " + MagRailSession.Version + " ride log " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            writer.WriteLine(Header);
            return Loc.F("MagRail_Log_Started", name);
        }

        public static void Stop()
        {
            if (writer == null)
                return;
            writer.WriteLine("# " + tick + " stop");
            writer.Flush();
            writer.Close();
            writer = null;
        }

        public static void Note(string text)
        {
            if (writer != null)
                writer.WriteLine("# " + tick + " " + text);
        }

        public static void Record(IEnumerable<RailBody> bodies)
        {
            tick++;
            Vector3Input input = PilotInput();
            foreach (RailBody b in bodies)
            {
                double mass = b.Grid.Physics != null ? b.Grid.Physics.Mass / 1000.0 : 0;
                VRageMath.Vector3D pos = b.Grid.WorldAABB.Center;
                writer.WriteLine(tick + "," + b.Grid.CustomName.Replace(',', ' ') + "," + b.HeldCount + "," + b.Engines.Count + "," + N(mass)
                    + "," + N(b.Speed) + "," + N(input.Forward) + "," + N(input.Side) + "," + N(input.Up) + "," + (input.Dampers ? 1 : 0)
                    + "," + (input.Grid == b.Grid ? 1 : 0) + "," + N(b.MaxError)
                    + "," + N(b.Yaw) + "," + N(b.Pitch) + "," + N(b.Roll)
                    + "," + N(b.PushAlong) + "," + N(b.PushSide) + "," + N(b.PushUp) + "," + N(b.PushAngular)
                    + "," + N(b.HoldLoad * 100) + "," + N(b.SpinLoad * 100) + "," + b.Drops
                    + "," + b.Drive.State + "," + N(b.Drive.Force / 1000) + "," + N(b.Drive.BrakeForce / 1000) + "," + N(b.Drive.PowerShare * 100)
                    + "," + N(b.Drift) + "," + N(b.PathForward.X) + "," + N(b.PathForward.Y) + "," + N(b.PathForward.Z)
                    + "," + b.Place.Replace(',', ' ') + "," + N(b.PlaceS) + "," + N(b.Radius)
                    + "," + N(pos.X) + "," + N(pos.Y) + "," + N(pos.Z));
            }
            if (tick % 60 == 0)
                writer.Flush();
            if (tick >= MaxTicks)
            {
                Note("10 minutes: stopped");
                Stop();
                MyAPIGateway.Utilities.ShowMessage("MagRail", Loc.T("MagRail_Log_Limit"));
            }
        }

        public static string N(double v)
        {
            return v.ToString("0.####", CultureInfo.InvariantCulture);
        }

        struct Vector3Input
        {
            public double Forward, Side, Up;
            public bool Dampers;
            public IMyCubeGrid Grid;
        }

        // The local player's ship controller: W/S = forward +-1, D/A = side +-1, Space/C = up +-1.
        static Vector3Input PilotInput()
        {
            Vector3Input input = new Vector3Input();
            IMyPlayer player = MyAPIGateway.Session.Player;
            IMyShipController controller = player != null && player.Controller != null ? player.Controller.ControlledEntity as IMyShipController : null;
            if (controller == null)
                return input;
            input.Forward = -controller.MoveIndicator.Z;
            input.Side = controller.MoveIndicator.X;
            input.Up = controller.MoveIndicator.Y;
            input.Dampers = controller.DampenersOverride;
            input.Grid = controller.CubeGrid;
            return input;
        }
    }
}
