using System;
using System.Text;
using Sandbox.ModAPI;

namespace MagRail
{
    // Detailed info of a hover engine in the terminal: its rail state, its hold force and how the engines of
    // its grid compare with the grid's weight here.
    public static class EngineInfo
    {
        public static void Append(IMyTerminalBlock b, StringBuilder sb)
        {
            try
            {
                HoverEngineLogic e = b.GameLogic != null ? b.GameLogic.GetAs<HoverEngineLogic>() : null;
                if (e == null || e.Settings == null || MagRailSession.Instance == null)
                    return;
                sb.Append(Loc.T("MagRail_Engine_State_" + e.Status)).Append('\n');
                sb.Append(Loc.F("MagRail_Engine_Info_Hold", Force(e.HoldForce))).Append('\n');

                int count = 0;
                double total = 0;
                foreach (HoverEngineLogic other in MagRailSession.Instance.Engines)
                {
                    if (other.Block.CubeGrid == b.CubeGrid && other.Active)
                    {
                        count++;
                        total += other.HoldForce;
                    }
                }
                if (b.CubeGrid.Physics == null)
                    return;
                double mass = b.CubeGrid.Physics.Mass;
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

        static string Force(double newtons)
        {
            return newtons >= 1e6 ? Loc.F("MagRail_Unit_MN", (newtons / 1e6).ToString("0.0")) : Loc.F("MagRail_Unit_kN", (newtons / 1e3).ToString("0"));
        }
    }
}
