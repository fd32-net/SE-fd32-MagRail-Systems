using System;
using System.Collections.Generic;
using System.Text;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Game.Entities;
using Sandbox.Game.EntityComponents;
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
    // mean nothing and are hidden. Each unit has its own cruise control (Cruise); one cruises a train at a time.
    [MyEntityComponentDescriptor(typeof(MyObjectBuilder_Thrust), false, "MagRail_TrainControlLarge", "MagRail_TrainControlSmall")]
    public class TrainControlLogic : MyGameLogicComponent
    {
        static readonly Guid CruiseGuid = new Guid("42b0d51d-5b05-4f56-83c7-99f840a40ff0");
        static readonly string[] HiddenControls = { "OnOff", "Override" };
        static readonly List<IMyCubeGrid> groupScratch = new List<IMyCubeGrid>();
        static readonly string[] HiddenActionParts = { "OnOff", "Override" };
        static bool terminalReady;

        public IMyThrust Block { get; private set; }
        // This unit's cruise (only one unit of a train has it on).
        public Cruise Cruise;
        bool registered;

        public override void Init(MyObjectBuilder_EntityBase objectBuilder)
        {
            Block = (IMyThrust)Entity;
            string text;
            if (Block.Storage != null && Block.Storage.TryGetValue(CruiseGuid, out text))
                Cruise = Cruise.Load(text);
            NeedsUpdate |= MyEntityUpdateEnum.BEFORE_NEXT_FRAME;
        }

        public void SetCruise(Cruise s)
        {
            Cruise = s;
            if (Block.Storage == null)
                Block.Storage = new MyModStorageComponent();
            Block.Storage[CruiseGuid] = s.Save();
            Block.RefreshCustomInfo();
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

        // Every working unit on the grids mechanically joined to this one (rotor, hinge, piston).
        public static void CollectTrain(IMyCubeGrid grid, List<TrainControlLogic> into)
        {
            into.Clear();
            groupScratch.Clear();
            MyAPIGateway.GridGroups.GetGroup(grid, GridLinkTypeEnum.Mechanical, groupScratch);
            List<TrainControlLogic> units = MagRailSession.Instance.ControlUnits;
            for (int i = 0; i < units.Count; i++)
                if (units[i].Block.IsFunctional && (units[i].Block.CubeGrid == grid || groupScratch.Contains(units[i].Block.CubeGrid)))
                    into.Add(units[i]);
            groupScratch.Clear();
        }

        static void AppendInfo(IMyTerminalBlock b, StringBuilder sb)
        {
            try
            {
                MyCubeGrid grid = b.CubeGrid as MyCubeGrid;
                if (grid != null)
                    sb.Append(Loc.T(grid.DampenersEnabled ? "MagRail_Control_DampersOn" : "MagRail_Control_DampersOff")).Append('\n');
                TrainControlLogic unit = Get(b);
                if (unit != null)
                {
                    Cruise c = unit.Cruise;
                    sb.Append(c.Engaged ? Loc.F("MagRail_Cruise_InfoOn", Cruise.Speed(c.Target))
                        : c.HasTarget ? Loc.F("MagRail_Cruise_InfoPaused", Cruise.Speed(c.Target))
                        : Loc.T("MagRail_Cruise_InfoOff")).Append('\n');
                }
                sb.Append(Loc.T("MagRail_Control_Info")).Append('\n');
            }
            catch (Exception ex)
            {
                Log.Error("TrainControlLogic.AppendInfo", ex);
            }
        }

        public static TrainControlLogic Get(IMyTerminalBlock b)
        {
            return b != null && b.GameLogic != null ? b.GameLogic.GetAs<TrainControlLogic>() : null;
        }

        static bool IsUnit(IMyTerminalBlock b)
        {
            return Get(b) != null;
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
            CreateCruiseControls();
        }

        // ---- cruise terminal ----

        static void CreateCruiseControls()
        {
            IMyTerminalControlSeparator separator = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlSeparator, IMyThrust>("MagRail_CruiseSep");
            separator.Visible = IsUnit;
            MyAPIGateway.TerminalControls.AddControl<IMyThrust>(separator);

            IMyTerminalControlLabel label = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlLabel, IMyThrust>("MagRail_CruiseLabel");
            label.Label = Loc.Id("MagRail_Cruise_Section");
            label.Visible = IsUnit;
            MyAPIGateway.TerminalControls.AddControl<IMyThrust>(label);

            AddCruiseButton("MagRail_CruiseSetButton", "MagRail_Cruise_BtnSet", "MagRail_Cruise_BtnSet_Tip", Cruise.CmdSet);
            AddCruiseButton("MagRail_CruiseResumeButton", "MagRail_Cruise_BtnResume", "MagRail_Cruise_BtnResume_Tip", Cruise.CmdResume);
            AddCruiseButton("MagRail_CruiseCancelButton", "MagRail_Cruise_BtnCancel", "MagRail_Cruise_BtnCancel_Tip", Cruise.CmdCancel);

            IMyTerminalControlSlider speed = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlSlider, IMyThrust>("MagRail_CruiseSpeed");
            speed.Title = Loc.Id("MagRail_Cruise_Speed");
            speed.Tooltip = Loc.Id("MagRail_Cruise_Speed_Tip");
            speed.SetLimits(b => 0f, b => (float)Cruise.MaxSpeed(b.CubeGrid));
            speed.Getter = b => IsUnit(b) ? (float)Get(b).Cruise.Target : 0f;
            speed.Setter = (b, v) => Cruise.Request(Get(b), Cruise.CmdSpeed, v);
            speed.Writer = (b, sb) =>
            {
                if (IsUnit(b))
                    sb.Append(Cruise.Speed(Get(b).Cruise.Target));
            };
            speed.Visible = IsUnit;
            MyAPIGateway.TerminalControls.AddControl<IMyThrust>(speed);

            AddCruiseAction("MagRail_CruiseSet", "MagRail_Cruise_ActSet", Cruise.CmdSet, @"Textures\GUI\Icons\Actions\Start.dds");
            AddCruiseAction("MagRail_CruiseResume", "MagRail_Cruise_ActResume", Cruise.CmdResume, @"Textures\GUI\Icons\Actions\Reset.dds");
            AddCruiseAction("MagRail_CruiseCancel", "MagRail_Cruise_ActCancel", Cruise.CmdCancel, @"Textures\GUI\Icons\Actions\SwitchOff.dds");
            AddCruiseAction("MagRail_CruisePlus", "MagRail_Cruise_ActPlus", Cruise.CmdPlus, @"Textures\GUI\Icons\Actions\Increase.dds");
            AddCruiseAction("MagRail_CruiseMinus", "MagRail_Cruise_ActMinus", Cruise.CmdMinus, @"Textures\GUI\Icons\Actions\Decrease.dds");
        }

        static void AddCruiseButton(string id, string titleKey, string tipKey, int command)
        {
            IMyTerminalControlButton button = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlButton, IMyThrust>(id);
            button.Title = Loc.Id(titleKey);
            button.Tooltip = Loc.Id(tipKey);
            button.Action = b => Cruise.Request(Get(b), command, 0);
            button.Visible = IsUnit;
            MyAPIGateway.TerminalControls.AddControl<IMyThrust>(button);
        }

        static void AddCruiseAction(string id, string nameKey, int command, string icon)
        {
            IMyTerminalAction action = MyAPIGateway.TerminalControls.CreateAction<IMyThrust>(id);
            action.Name = new StringBuilder(Loc.T(nameKey));
            action.Icon = icon;
            action.Action = b => Cruise.Request(Get(b), command, 0);
            action.Writer = (b, sb) =>
            {
                TrainControlLogic unit = Get(b);
                if (unit != null)
                    sb.Append(unit.Cruise.Engaged ? Cruise.Speed(unit.Cruise.Target) : Loc.T("MagRail_Cruise_SlotOff"));
            };
            action.Enabled = IsUnit;
            MyAPIGateway.TerminalControls.AddAction<IMyThrust>(action);
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
