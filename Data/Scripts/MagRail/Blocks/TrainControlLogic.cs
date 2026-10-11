using System;
using System.Collections.Generic;
using System.Text;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Game.Entities;
using Sandbox.ModAPI;
using Sandbox.ModAPI.Interfaces.Terminal;
using VRage.Game.Components;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRage.ObjectBuilders;

namespace MagRail
{
    // Train Control Unit: a thruster with (almost) no force. The game keeps a grid's inertia dampeners in its thrust
    // system, which exists only while a working thruster is on the grid; with this block on the cockpit's grid the
    // vanilla Z (the cockpit's checkbox, the HUD, sync, save) works on a train, and the drive reads it (Drive). A
    // mod cannot create the thrust system itself (internal class), so the block is the way. Its thruster controls
    // mean nothing and are hidden; it will carry the train's own settings later.
    [MyEntityComponentDescriptor(typeof(MyObjectBuilder_Thrust), false, "MagRail_TrainControlLarge", "MagRail_TrainControlSmall")]
    public class TrainControlLogic : MyGameLogicComponent
    {
        static readonly string[] HiddenControls = { "OnOff", "Override" };
        static readonly string[] HiddenActionParts = { "OnOff", "Override" };
        static bool terminalReady;

        public IMyThrust Block { get; private set; }
        bool registered;

        public override void Init(MyObjectBuilder_EntityBase objectBuilder)
        {
            Block = (IMyThrust)Entity;
            NeedsUpdate |= MyEntityUpdateEnum.BEFORE_NEXT_FRAME;
        }

        public override void UpdateOnceBeforeFrame()
        {
            try
            {
                if (Block.CubeGrid.Physics == null || MagRailSession.Instance == null)
                    return;
                MagRailSession.Instance.ControlUnits.Add(this);
                registered = true;
                Block.AppendingCustomInfo += AppendInfo;
                CreateTerminal();
            }
            catch (Exception e)
            {
                Log.Error("TrainControlLogic.UpdateOnceBeforeFrame", e);
            }
        }

        public override void Close()
        {
            if (registered && MagRailSession.Instance != null)
                MagRailSession.Instance.ControlUnits.Remove(this);
            if (registered)
                Block.AppendingCustomInfo -= AppendInfo;
            registered = false;
        }

        // A built unit on the grid: the game registers a thruster in the grid's thrust system while it is functional.
        public static bool OnGrid(IMyCubeGrid grid)
        {
            List<TrainControlLogic> units = MagRailSession.Instance.ControlUnits;
            for (int i = 0; i < units.Count; i++)
                if (units[i].Block.CubeGrid == grid && units[i].Block.IsFunctional)
                    return true;
            return false;
        }

        static void AppendInfo(IMyTerminalBlock b, StringBuilder sb)
        {
            try
            {
                MyCubeGrid grid = b.CubeGrid as MyCubeGrid;
                if (grid != null)
                    sb.Append(Loc.T(grid.DampenersEnabled ? "MagRail_Control_DampersOn" : "MagRail_Control_DampersOff")).Append('\n');
                sb.Append(Loc.T("MagRail_Control_Info")).Append('\n');
            }
            catch (Exception ex)
            {
                Log.Error("TrainControlLogic.AppendInfo", ex);
            }
        }

        static bool IsUnit(IMyTerminalBlock b)
        {
            return b != null && b.GameLogic != null && b.GameLogic.GetAs<TrainControlLogic>() != null;
        }

        // Hides the vanilla thruster controls and actions on the unit (on/off does nothing: a thruster stays in the
        // thrust system while it is built).
        static void CreateTerminal()
        {
            if (terminalReady)
                return;
            terminalReady = true;
            List<IMyTerminalControl> controls;
            MyAPIGateway.TerminalControls.GetControls<IMyThrust>(out controls);
            foreach (IMyTerminalControl control in controls)
            {
                if (Array.IndexOf(HiddenControls, control.Id) < 0)
                    continue;
                Func<IMyTerminalBlock, bool> visible = control.Visible;
                control.Visible = b => !IsUnit(b) && (visible == null || visible(b));
            }
            List<IMyTerminalAction> actions;
            MyAPIGateway.TerminalControls.GetActions<IMyThrust>(out actions);
            foreach (IMyTerminalAction action in actions)
            {
                if (!Hidden(action.Id))
                    continue;
                Func<IMyTerminalBlock, bool> enabled = action.Enabled;
                action.Enabled = b => !IsUnit(b) && (enabled == null || enabled(b));
            }
        }

        static bool Hidden(string id)
        {
            if (string.IsNullOrEmpty(id))
                return false;
            foreach (string part in HiddenActionParts)
                if (id.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            return false;
        }
    }
}
