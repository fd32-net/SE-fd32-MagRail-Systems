using System;
using System.Collections.Generic;
using System.Text;
using Sandbox.Game.Entities;
using Sandbox.ModAPI;
using VRage.Game.ModAPI;

namespace MagRail
{
    // Detailed info of a hover engine in the terminal: its rail state, force and hold, power, its grid's
    // drive and how the engines of its construct (mechanical group: rotors, hinges, pistons) compare with the
    // construct's weight here. The drive runs on the server: clients show no drive line (their power numbers come
    // from the server's demand, Net.BroadcastPower).
    public static class EngineInfo
    {
        static readonly List<IMyCubeGrid> construct = new List<IMyCubeGrid>();

        public static void Append(IMyTerminalBlock b, StringBuilder sb)
        {
            try
            {
                HoverEngineLogic e = b.GameLogic != null ? b.GameLogic.GetAs<HoverEngineLogic>() : null;
                if (e == null || e.Settings == null || MagRailSession.Instance == null)
                    return;
                sb.Append(Loc.T("MagRail_Engine_State_" + e.Status)).Append('\n');
                sb.Append(Loc.F("MagRail_Engine_Info_Hold", Force(e.Force), Force(e.HoldForce))).Append('\n');
                // Clients get the demand from the server (Net.BroadcastPower): their power numbers are live too.
                sb.Append(Loc.F("MagRail_Power_Info", Power(e.PowerNow), Power(e.MaxPower)));
                if (e.PowerShort)
                    sb.Append(Loc.T("MagRail_Power_Insufficient"));
                sb.Append('\n');
                if (Net.IsServer)
                {
                    RailBody body = MagRailSession.Instance.Rides.Body(b.CubeGrid);
                    sb.Append(DriveLine(body)).Append('\n');
                    if (body != null && body.Drive.NoControlUnit)
                        sb.Append(Loc.T("MagRail_Drive_NoControlUnit")).Append('\n');
                }
                if (!e.Block.Propulsion)
                    sb.Append(Loc.T("MagRail_Drive_PropulsionOff")).Append('\n');

                construct.Clear();
                MyAPIGateway.GridGroups.GetGroup(b.CubeGrid, GridLinkTypeEnum.Mechanical, construct);
                int count = 0;
                double total = 0;
                foreach (HoverEngineLogic other in MagRailSession.Instance.Engines)
                {
                    if (other.Active && construct.Contains(other.Block.CubeGrid))
                    {
                        count++;
                        total += other.HoldForce;
                    }
                }
                construct.Clear();
                MyCubeGrid grid = b.CubeGrid as MyCubeGrid;
                if (grid == null || grid.Physics == null)
                    return;
                // Not Physics.Mass: on a client it is 0 for a moving grid (the game keeps no mass for client bodies).
                // GetCurrentMass sums the group's shapes, which every client has.
                double mass = grid.GetCurrentMass(GridLinkTypeEnum.Mechanical);
                double weight = mass * RailBody.Gravity(b.CubeGrid.WorldAABB.Center).Length();
                sb.Append(Loc.F("MagRail_Engine_Info_Grid", count, Force(total), (mass / 1000).ToString("0.0")));
                if (weight > 0.01 * mass && total > 0)
                    sb.Append(Loc.F("MagRail_Engine_Info_Weight", Force(weight), (weight / total * 100).ToString("0")));
                sb.Append('\n');
            }
            catch (Exception ex)
            {
                Log.Error("EngineInfo.Append", ex);
            }
        }

        public static string DriveLine(RailBody body)
        {
            if (body == null || !body.Holding)
                return Loc.T("MagRail_Drive_OffRail");
            Drive d = body.Drive;
            switch (d.State)
            {
                case DriveState.Driving: return Loc.F("MagRail_Drive_Pushing", Force(Math.Abs(d.Force))) + PowerNote(d);
                case DriveState.Braking: return Loc.F("MagRail_Drive_Braking", Force(d.BrakeForce));
                case DriveState.Parked: return Loc.F("MagRail_Drive_Parked", Force(d.BrakeForce));
                case DriveState.Dampers: return Loc.F("MagRail_Drive_Dampers", Force(Math.Abs(d.Force))) + PowerNote(d);
                case DriveState.Autopilot: return Loc.F("MagRail_Drive_Autopilot", Force(Math.Abs(d.Force))) + PowerNote(d);
                case DriveState.Cruise: return Loc.F("MagRail_Drive_Cruise", Force(Math.Abs(d.Force))) + PowerNote(d);
                case DriveState.NoController: return Loc.T("MagRail_Drive_NoController");
                case DriveState.Sideways: return Loc.T("MagRail_Drive_Sideways");
            }
            return Loc.T("MagRail_Drive_Idle");
        }

        static string PowerNote(Drive d)
        {
            return d.PowerShare < 0.01 ? Loc.T("MagRail_Drive_NoPower")
                : d.PowerShare < 0.99 ? Loc.F("MagRail_Drive_PowerShare", (d.PowerShare * 100).ToString("0")) : "";
        }

        static string Power(double megawatts)
        {
            return megawatts >= 1 ? Loc.F("MagRail_Unit_MW", megawatts.ToString("0.00")) : Loc.F("MagRail_Unit_kW", (megawatts * 1000).ToString("0"));
        }

        static string Force(double newtons)
        {
            return newtons >= 1e6 ? Loc.F("MagRail_Unit_MN", (newtons / 1e6).ToString("0.0")) : Loc.F("MagRail_Unit_kN", (newtons / 1e3).ToString("0"));
        }
    }
}
