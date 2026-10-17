using System;
using System.Collections.Generic;
using System.Globalization;
using Sandbox.Definitions;
using Sandbox.ModAPI;
using VRage.Game;
using VRage.Game.ModAPI;
using VRageMath;

namespace MagRail
{
    // Cruise control, like a car's, ported from the old Maglev (MaglevCruise). It lives on the Train Control Unit
    // (user decision 2026-10-15): in its terminal buttons Set (on at the current speed) / Resume (on at the
    // remembered one) / Cancel and a speed slider; toolbar actions the same plus + / - (the target only). Every unit keeps its own cruise, but only one cruises a train at a time, as with the
    // vanilla AI blocks (user decision 2026-10-15): switching one on switches off the others on the grids
    // mechanically joined (rotor, hinge, piston); the drive follows the one that is on (Drive.ReadCruise).
    // The axis is the unit's own (user idea 2026-10-15): the way it pushes as a thruster, which the game shows
    // after its name in the terminal ("(Forward)" relative to the cockpit). So no cockpit is needed to cruise.
    // The target has a sign (user decision 2026-10-17): plus along the axis, minus back, so one unit drives both
    // ways. The key that drives the target's way is gas over it, back to the target on release; the other one,
    // Space and parking cancel, the target is kept for Resume. Speeds in m/s, steps of 1 m/s, up to the world's
    // max ship speed either way. The server decides; a client's
    // terminal or toolbar sends a request and shows the change at once, the server's broadcast confirms it.
    public struct Cruise
    {
        public const int NoteNone = 0, NoteChanged = 1, NoteBrake = 2, NoteParking = 3;
        public const int CmdSet = 1, CmdResume = 2, CmdCancel = 3, CmdPlus = 5, CmdMinus = 6, CmdSpeed = 7;
        const double SpeedStep = 1.0;

        public bool Engaged, HasTarget;
        public double Target;

        static readonly List<TrainControlLogic> units = new List<TrainControlLogic>();

        // The game's max ship speed for the grid size (100 m/s in vanilla; speed mods change it).
        public static double MaxSpeed(IMyCubeGrid grid)
        {
            MyEnvironmentDefinition env = MyDefinitionManager.Static.EnvironmentDefinition;
            double max = grid != null && grid.GridSizeEnum == MyCubeSize.Small ? env.SmallShipMaxSpeed : env.LargeShipMaxSpeed;
            return max > 0 ? max : 100.0;
        }

        public string Save()
        {
            return (Engaged ? "1" : "0") + ";" + (HasTarget ? "1" : "0") + ";" + Target.ToString("R", CultureInfo.InvariantCulture);
        }

        public static Cruise Load(string text)
        {
            Cruise s = new Cruise();
            if (string.IsNullOrEmpty(text))
                return s;
            string[] parts = text.Split(';');
            double target;
            if (parts.Length >= 3 && double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out target))
            {
                s.Engaged = parts[0] == "1";
                s.HasTarget = parts[1] == "1";
                s.Target = target;
            }
            return s;
        }

        // ---- commands ----

        // Terminal / toolbar entry point, on whichever machine ran it.
        public static void Request(TrainControlLogic unit, int command, double value)
        {
            if (unit == null)
                return;
            if (Net.IsServer)
            {
                Publish(unit, Run(unit, unit.Cruise, command, value), NoteChanged);
                return;
            }
            unit.SetCruise(Run(unit, unit.Cruise, command, value));
            Net.SendCruiseRequest(unit, command, value);
        }

        // Server: a client's request.
        public static void ServerCommand(TrainControlLogic unit, int command, double value)
        {
            Publish(unit, Run(unit, unit.Cruise, command, value), NoteChanged);
        }

        static Cruise Run(TrainControlLogic unit, Cruise s, int command, double value)
        {
            double max = MaxSpeed(unit.Block.CubeGrid);
            switch (command)
            {
                case CmdSet:
                    s.Engaged = true;
                    s.HasTarget = true;
                    s.Target = AxisSpeed(unit, max);
                    break;
                case CmdResume:
                    if (!s.HasTarget)
                        return Run(unit, s, CmdSet, value);
                    s.Engaged = true;
                    break;
                case CmdCancel:
                    s.Engaged = false;
                    break;
                case CmdPlus:
                case CmdMinus:
                    // Only the target; on or off stays as it is.
                    double from = s.HasTarget ? s.Target : AxisSpeed(unit, max);
                    s.Target = MathHelper.Clamp(from + (command == CmdPlus ? SpeedStep : -SpeedStep), -max, max);
                    s.HasTarget = true;
                    break;
                case CmdSpeed:
                    s.Target = MathHelper.Clamp(Math.Round(value), -max, max);
                    s.HasTarget = true;
                    break;
            }
            return s;
        }

        // The way the unit pushes as a thruster (its nozzle faces the other way): the cruise axis, plus targets.
        public static Vector3D Axis(TrainControlLogic unit)
        {
            return unit.Block.WorldMatrix.Backward;
        }

        // The way the cruise drives: the axis, or against it for a minus target (a zero target: the axis).
        public static Vector3D Direction(TrainControlLogic unit)
        {
            return unit.Cruise.Target < 0 ? -Axis(unit) : Axis(unit);
        }

        // Speed along the axis, whole m/s, minus = back.
        static double AxisSpeed(TrainControlLogic unit, double max)
        {
            if (unit.Block.CubeGrid.Physics == null)
                return 0;
            double v = Vector3D.Dot(unit.Block.CubeGrid.Physics.LinearVelocity, Axis(unit));
            return MathHelper.Clamp(Math.Round(v), -max, max);
        }

        // ---- state ----

        // Server: stores the unit's state (switched on, the train's other units are switched off), sends it to
        // everyone and tells the local pilot.
        public static void Publish(TrainControlLogic unit, Cruise s, int note)
        {
            if (s.Engaged)
                CancelOthers(unit);
            unit.SetCruise(s);
            Net.BroadcastCruise(unit, s, note);
            ShowNote(unit, s, note);
        }

        // Server: switches off every unit of the train that cruises, except `keep` (null: all of them).
        public static void CancelTrain(IMyCubeGrid grid, TrainControlLogic keep)
        {
            TrainControlLogic.CollectTrain(grid, units);
            for (int i = 0; i < units.Count; i++)
            {
                TrainControlLogic u = units[i];
                if (u == keep || !u.Cruise.Engaged)
                    continue;
                Cruise c = u.Cruise;
                c.Engaged = false;
                u.SetCruise(c);
                Net.BroadcastCruise(u, c, NoteNone);
            }
            units.Clear();
        }

        static void CancelOthers(TrainControlLogic unit)
        {
            CancelTrain(unit.Block.CubeGrid, unit);
        }

        // Client: the server's state.
        public static void Apply(TrainControlLogic unit, Cruise s, int note)
        {
            unit.SetCruise(s);
            ShowNote(unit, s, note);
        }

        // HUD note for the local player driving this train.
        static void ShowNote(TrainControlLogic unit, Cruise s, int note)
        {
            if (note == NoteNone || MyAPIGateway.Session == null || MyAPIGateway.Utilities.IsDedicated)
                return;
            IMyShipController local = MyAPIGateway.Session.ControlledObject as IMyShipController;
            if (local == null || (local.CubeGrid != unit.Block.CubeGrid
                && !MyAPIGateway.GridGroups.HasConnection(local.CubeGrid, unit.Block.CubeGrid, GridLinkTypeEnum.Mechanical)))
                return;
            string text = note == NoteBrake ? Loc.T("MagRail_Cruise_CancelBrake")
                : note == NoteParking ? Loc.T("MagRail_Cruise_CancelParking")
                : s.Engaged ? Loc.F("MagRail_Cruise_On", Speed(s.Target))
                : s.HasTarget ? Loc.F("MagRail_Cruise_OffResume", Speed(s.Target))
                : Loc.T("MagRail_Cruise_Off");
            MyAPIGateway.Utilities.ShowNotification(text, 2000, "White");
        }

        public static string Speed(double ms)
        {
            return Loc.F("MagRail_Unit_mps", ms.ToString("0", CultureInfo.InvariantCulture));
        }
    }
}
