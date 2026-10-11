using System.Collections.Generic;
using System.Globalization;
using Sandbox.ModAPI;
using VRage.ModAPI;

namespace MagRail
{
    // TEMPORARY (stage 5 tuning): the drive's numbers on sliders in the cockpit / remote control terminal, so the
    // brakes and parking are tuned in game instead of by rebuilds. Each controller keeps its own set (not synced:
    // single player); a train reads the set of its reference controller (Drive). A changed set is kept in a world
    // variable by the controller's id (saved with the world, no storage GUID needed). Changes go to /magrail log
    // as "# tune" notes. When the numbers are found they become Drive's constants and this goes away (with
    // DriveTuningTerminal).
    public class DriveTuning
    {
        public static readonly DriveTuning Default = new DriveTuning();
        static readonly Dictionary<long, DriveTuning> sets = new Dictionary<long, DriveTuning>();
        // Controllers with no saved set (looked up once).
        static readonly HashSet<long> unsaved = new HashSet<long>();
        const string VariablePrefix = "MagRail_DriveTuning_";

        // Shares of each engine's force (HoverEngineLogic.Force): thrust at 100% Power, the most of the Space brake,
        // parking; the Space brake as times the engine's thrust (Drive.BrakeFactor).
        public double ThrustShare = Drive.ThrustShare, BrakeShare = Drive.BrakeShare, ParkShare = Drive.ParkShare;
        public double BrakeFactor = Drive.BrakeFactor;
        // Acceleration limits of the grid, m/s^2 (tuned in game 2026-10-11, "Test cocpit": 100 = the hold's own
        // limit, so in effect only the engines' force limits the grid).
        public double MaxThrustAccel = 100.0, MaxBrakeAccel = 100.0, MaxParkAccel = 100.0;
        // Rest hold: brake (m/s^2) and the speed below which it holds.
        public double RestAccel = 0.49, RestSpeed = 0.3;
        // Space brake only: seconds to rise from BrakeRiseStart to full force (0 = at once), and the speed (m/s)
        // below which its force falls with the speed, down to SoftStopFloor at a standstill (0 = off). The rise
        // starts at half the force: from nothing, the first moment felt like no brake at all (user, 2026-10-11).
        public double BrakeRise = 0.5, SoftStop;
        public const double BrakeRiseStart = 0.5, SoftStopFloor = 0.1;
        // The brakes cancel last tick's push along the rail up front (Drive.Brake).
        public bool Disturbance = true;
        // HUD line of the piloted train's drive (DriveTuningHud).
        public bool Hud;
        // Parking (the controller's hand brake) stops W/S on every grid of the train (Drive.ReadInput).
        public bool ParkStopsDrive = true;

        public static DriveTuning For(IMyEntity controller)
        {
            if (controller == null)
                return Default;
            DriveTuning t;
            if (sets.TryGetValue(controller.EntityId, out t))
                return t;
            if (unsaved.Contains(controller.EntityId))
                return Default;
            string text;
            t = MyAPIGateway.Utilities.GetVariable(VariablePrefix + controller.EntityId, out text) ? Parse(text) : null;
            if (t == null)
            {
                unsaved.Add(controller.EntityId);
                return Default;
            }
            sets[controller.EntityId] = t;
            return t;
        }

        public static DriveTuning Edit(IMyEntity controller)
        {
            DriveTuning t = For(controller);
            if (t == Default)
            {
                sets[controller.EntityId] = t = new DriveTuning();
                unsaved.Remove(controller.EntityId);
            }
            return t;
        }

        public static void Save(IMyEntity controller, DriveTuning t)
        {
            MyAPIGateway.Utilities.SetVariable(VariablePrefix + controller.EntityId, t.Serialize());
        }

        public static void Reset(IMyEntity controller)
        {
            sets.Remove(controller.EntityId);
            unsaved.Add(controller.EntityId);
            MyAPIGateway.Utilities.RemoveVariable(VariablePrefix + controller.EntityId);
        }

        public static void Clear()
        {
            sets.Clear();
            unsaved.Clear();
        }

        string Serialize()
        {
            double[] v = { ThrustShare, BrakeShare, ParkShare, MaxThrustAccel, MaxBrakeAccel, MaxParkAccel, RestAccel, RestSpeed,
                BrakeRise, SoftStop, Disturbance ? 1 : 0, Hud ? 1 : 0, ParkStopsDrive ? 1 : 0, BrakeFactor };
            string[] parts = new string[v.Length];
            for (int i = 0; i < v.Length; i++)
                parts[i] = v[i].ToString("R", CultureInfo.InvariantCulture);
            return string.Join(";", parts);
        }

        // null if the text is not a whole set: an older layout is dropped (defaults); the 12-value one had shares
        // of the hold force, the 13-value one a fixed Space brake share.
        static DriveTuning Parse(string text)
        {
            string[] parts = text != null ? text.Split(';') : new string[0];
            if (parts.Length != 14)
                return null;
            double[] v = new double[parts.Length];
            for (int i = 0; i < parts.Length; i++)
                if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out v[i]))
                    return null;
            return new DriveTuning
            {
                ThrustShare = v[0], BrakeShare = v[1], ParkShare = v[2],
                MaxThrustAccel = v[3], MaxBrakeAccel = v[4], MaxParkAccel = v[5],
                RestAccel = v[6], RestSpeed = v[7], BrakeRise = v[8], SoftStop = v[9],
                Disturbance = v[10] != 0, Hud = v[11] != 0, ParkStopsDrive = v[12] != 0, BrakeFactor = v[13],
            };
        }

        public override string ToString()
        {
            return "thrust " + RideLog.N(ThrustShare * 100) + "% max " + RideLog.N(MaxThrustAccel)
                + " | brake x" + RideLog.N(BrakeFactor) + " thrust, at most " + RideLog.N(BrakeShare * 100) + "% max " + RideLog.N(MaxBrakeAccel)
                + " rise " + RideLog.N(BrakeRise) + " s soft " + RideLog.N(SoftStop)
                + " | park " + RideLog.N(ParkShare * 100) + "% max " + RideLog.N(MaxParkAccel)
                + " | rest " + RideLog.N(RestAccel) + " below " + RideLog.N(RestSpeed)
                + " | disturbance " + (Disturbance ? "on" : "off") + " | park stops W/S " + (ParkStopsDrive ? "on" : "off");
        }
    }
}
