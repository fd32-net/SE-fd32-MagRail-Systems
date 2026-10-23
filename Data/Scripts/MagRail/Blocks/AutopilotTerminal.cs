using System;
using System.Collections.Generic;
using System.Text;
using Sandbox.ModAPI;
using SpaceEngineers.Game.ModAPI;
using Sandbox.ModAPI.Interfaces.Terminal;
using VRage.ModAPI;
using VRage.Utils;

namespace MagRail
{
    // The autopilot's terminal and toolbar actions (as the old Maglev's).
    public partial class AutopilotLogic
    {
        static bool terminalReady;
        static readonly List<IMyTerminalBlock> timerScratch = new List<IMyTerminalBlock>();

        static bool IsAutopilot(IMyTerminalBlock b)
        {
            return Get(b) != null;
        }

        static bool HasSelected(IMyTerminalBlock b)
        {
            AutopilotLogic a = Get(b);
            return a != null && a.Config.Has(a.Config.Selected);
        }

        static void Run(IMyTerminalBlock b, int command, int value, long arg = 0, string text = null)
        {
            AutopilotLogic a = Get(b);
            if (a != null)
                a.Request(command, value, arg, text);
        }

        static void CreateTerminal()
        {
            if (terminalReady)
                return;
            terminalReady = true;
            IMyTerminalControls tc = MyAPIGateway.TerminalControls;

            IMyTerminalControlButton go = tc.CreateControl<IMyTerminalControlButton, IMyUpgradeModule>("MagRail_ApGo");
            go.Title = Loc.Id("MagRail_Ap_Go");
            go.Tooltip = Loc.Id("MagRail_Ap_Go_Tip");
            go.Action = b => Run(b, CmdStart, -1);
            go.Enabled = HasSelected;
            go.Visible = IsAutopilot;
            tc.AddControl<IMyUpgradeModule>(go);

            IMyTerminalControlButton stop = tc.CreateControl<IMyTerminalControlButton, IMyUpgradeModule>("MagRail_ApStop");
            stop.Title = Loc.Id("MagRail_Ap_Stop");
            stop.Tooltip = Loc.Id("MagRail_Ap_Stop_Tip");
            stop.Action = b => Run(b, CmdStop, 0);
            stop.Visible = IsAutopilot;
            tc.AddControl<IMyUpgradeModule>(stop);

            IMyTerminalControlListbox stations = tc.CreateControl<IMyTerminalControlListbox, IMyUpgradeModule>("MagRail_ApStations");
            stations.Title = Loc.Id("MagRail_Ap_Stations");
            stations.Tooltip = Loc.Id("MagRail_Ap_Stations_Tip");
            stations.VisibleRowsCount = 6;
            stations.Multiselect = false;
            stations.ListContent = (b, items, selected) =>
            {
                AutopilotLogic a = Get(b);
                if (a == null)
                    return;
                for (int i = 0; i < a.Config.Stations.Count; i++)
                {
                    var item = new MyTerminalControlListBoxItem(MyStringId.GetOrCompute((i + 1) + ". " + a.Config.Name(i)), MyStringId.NullOrEmpty, i);
                    items.Add(item);
                    if (i == a.Config.Selected)
                        selected.Add(item);
                }
            };
            stations.ItemSelected = (b, list) =>
            {
                if (list.Count > 0 && list[0].UserData is int)
                    Run(b, CmdSelect, (int)list[0].UserData);
            };
            stations.Visible = IsAutopilot;
            tc.AddControl<IMyUpgradeModule>(stations);

            IMyTerminalControlTextbox name = tc.CreateControl<IMyTerminalControlTextbox, IMyUpgradeModule>("MagRail_ApName");
            name.Title = Loc.Id("MagRail_Ap_Name");
            name.Tooltip = Loc.Id("MagRail_Ap_Name_Tip");
            name.Getter = b =>
            {
                AutopilotLogic a = Get(b);
                return new StringBuilder(a != null && a.Config.Has(a.Config.Selected) ? a.Config.Stations[a.Config.Selected].Name : "");
            };
            name.Setter = (b, text) =>
            {
                AutopilotLogic a = Get(b);
                if (a != null && a.Config.Has(a.Config.Selected))
                    a.Request(CmdRename, a.Config.Selected, 0, text != null ? text.ToString() : "");
            };
            name.Enabled = HasSelected;
            name.Visible = IsAutopilot;
            tc.AddControl<IMyUpgradeModule>(name);

            IMyTerminalControlButton remember = tc.CreateControl<IMyTerminalControlButton, IMyUpgradeModule>("MagRail_ApRemember");
            remember.Title = Loc.Id("MagRail_Ap_Remember");
            remember.Tooltip = Loc.Id("MagRail_Ap_Remember_Tip");
            remember.Action = b => Run(b, CmdRemember, -1);
            remember.Visible = IsAutopilot;
            tc.AddControl<IMyUpgradeModule>(remember);

            IMyTerminalControlButton move = tc.CreateControl<IMyTerminalControlButton, IMyUpgradeModule>("MagRail_ApMoveHere");
            move.Title = Loc.Id("MagRail_Ap_Move");
            move.Tooltip = Loc.Id("MagRail_Ap_Move_Tip");
            move.Action = b =>
            {
                AutopilotLogic a = Get(b);
                if (a != null)
                    a.Request(CmdRemember, a.Config.Selected);
            };
            move.Enabled = HasSelected;
            move.Visible = IsAutopilot;
            tc.AddControl<IMyUpgradeModule>(move);

            IMyTerminalControlButton remove = tc.CreateControl<IMyTerminalControlButton, IMyUpgradeModule>("MagRail_ApRemove");
            remove.Title = Loc.Id("MagRail_Ap_Delete");
            remove.Tooltip = Loc.Id("MagRail_Ap_Delete_Tip");
            remove.Action = b =>
            {
                AutopilotLogic a = Get(b);
                if (a != null)
                    a.Request(CmdRemove, a.Config.Selected);
            };
            remove.Enabled = HasSelected;
            remove.Visible = IsAutopilot;
            tc.AddControl<IMyUpgradeModule>(remove);

            AddOrderButton("MagRail_ApUp", "MagRail_Ap_Up", "MagRail_Ap_Up_Tip", -1);
            AddOrderButton("MagRail_ApDown", "MagRail_Ap_Down", "MagRail_Ap_Down_Tip", 1);

            IMyTerminalControlListbox timers = tc.CreateControl<IMyTerminalControlListbox, IMyUpgradeModule>("MagRail_ApTimer");
            timers.Title = Loc.Id("MagRail_Ap_Timer");
            timers.Tooltip = Loc.Id("MagRail_Ap_Timer_Tip");
            timers.VisibleRowsCount = 4;
            timers.Multiselect = false;
            timers.ListContent = (b, items, selected) =>
            {
                AutopilotLogic a = Get(b);
                if (a == null || !a.Config.Has(a.Config.Selected))
                    return;
                long chosen = a.Config.Stations[a.Config.Selected].TimerId;
                var none = new MyTerminalControlListBoxItem(MyStringId.GetOrCompute(Loc.T("MagRail_Ap_TimerNone")), MyStringId.NullOrEmpty, 0L);
                items.Add(none);
                if (chosen == 0)
                    selected.Add(none);
                CollectTimers(b);
                for (int i = 0; i < timerScratch.Count; i++)
                {
                    IMyTerminalBlock t = timerScratch[i];
                    var item = new MyTerminalControlListBoxItem(MyStringId.GetOrCompute(t.CustomName), MyStringId.NullOrEmpty, t.EntityId);
                    items.Add(item);
                    if (t.EntityId == chosen)
                        selected.Add(item);
                }
                timerScratch.Clear();
            };
            timers.ItemSelected = (b, list) =>
            {
                AutopilotLogic a = Get(b);
                if (a != null && list.Count > 0 && list[0].UserData is long && a.Config.Has(a.Config.Selected))
                    a.Request(CmdTimer, a.Config.Selected, (long)list[0].UserData);
            };
            timers.Enabled = HasSelected;
            timers.Visible = IsAutopilot;
            tc.AddControl<IMyUpgradeModule>(timers);

            IMyTerminalControlSlider speed = tc.CreateControl<IMyTerminalControlSlider, IMyUpgradeModule>("MagRail_ApSpeed");
            speed.Title = Loc.Id("MagRail_Ap_MaxSpeed");
            speed.Tooltip = Loc.Id("MagRail_Ap_MaxSpeed_Tip");
            speed.SetLimits(b => 1f, b => (float)Cruise.MaxSpeed(b.CubeGrid));
            speed.Getter = b => IsAutopilot(b) ? Get(b).Config.MaxSpeed : 1f;
            speed.Setter = (b, v) => Run(b, CmdMaxSpeed, (int)Math.Round(v));
            speed.Writer = (b, sb) =>
            {
                if (IsAutopilot(b))
                    sb.Append(Cruise.Speed(Get(b).Config.MaxSpeed));
            };
            speed.Visible = IsAutopilot;
            tc.AddControl<IMyUpgradeModule>(speed);

            AddAccelSlider("MagRail_ApAccel", "MagRail_Ap_Accel", "MagRail_Ap_Accel_Tip", CmdAccel, c => c.Accel);
            AddAccelSlider("MagRail_ApBrake", "MagRail_Ap_Brake", "MagRail_Ap_Brake_Tip", CmdBrake, c => c.Brake);
            AddLateralSlider();

            IMyTerminalControlCheckbox park = tc.CreateControl<IMyTerminalControlCheckbox, IMyUpgradeModule>("MagRail_ApPark");
            park.Title = Loc.Id("MagRail_Ap_Park");
            park.Tooltip = Loc.Id("MagRail_Ap_Park_Tip");
            park.Getter = b => IsAutopilot(b) && Get(b).Config.Park;
            park.Setter = (b, v) => Run(b, CmdPark, v ? 1 : 0);
            park.Visible = IsAutopilot;
            tc.AddControl<IMyUpgradeModule>(park);

            IMyTerminalControlCheckbox precise = tc.CreateControl<IMyTerminalControlCheckbox, IMyUpgradeModule>("MagRail_ApPrecise");
            precise.Title = Loc.Id("MagRail_Ap_Precise");
            precise.Tooltip = Loc.Id("MagRail_Ap_Precise_Tip");
            precise.Getter = b => IsAutopilot(b) && Get(b).Config.Precise;
            precise.Setter = (b, v) => Run(b, CmdPrecise, v ? 1 : 0);
            precise.Visible = IsAutopilot;
            tc.AddControl<IMyUpgradeModule>(precise);

            IMyTerminalControlCheckbox show = tc.CreateControl<IMyTerminalControlCheckbox, IMyUpgradeModule>("MagRail_ApShowGps");
            show.Title = Loc.Id("MagRail_Ap_ShowGps");
            show.Tooltip = Loc.Id("MagRail_Ap_ShowGps_Tip");
            show.Getter = b => IsAutopilot(b) && Get(b).Config.ShowStations;
            show.Setter = (b, v) => Run(b, CmdShowStations, v ? 1 : 0);
            show.Visible = IsAutopilot;
            tc.AddControl<IMyUpgradeModule>(show);

            for (int i = 0; i < AutopilotConfig.MaxStations; i++)
            {
                int index = i;
                AddAction("MagRail_ApGoStation" + (i + 1), Loc.F("MagRail_Ap_ActGoStation", i + 1), CmdStart, index,
                    @"Textures\GUI\Icons\Actions\Start.dds", (a, sb) => sb.Append(ShortName(a.Config, index)));
            }
            AddAction("MagRail_ApGoNext", Loc.T("MagRail_Ap_ActGoNext"), CmdNext, 0, @"Textures\GUI\Icons\Actions\Start.dds",
                (a, sb) => sb.Append(ShortName(a.Config, a.Config.NextIndex)));
            AddAction("MagRail_ApParkAction", Loc.T("MagRail_Ap_ActPark"), CmdPark, -1, @"Textures\GUI\Icons\Actions\Toggle.dds",
                (a, sb) => sb.Append(Loc.T(a.Config.Park ? "MagRail_Ap_SlotParkOn" : "MagRail_Ap_SlotParkOff")));
            AddAction("MagRail_ApStopAction", Loc.T("MagRail_Ap_ActStop"), CmdStop, 0, @"Textures\GUI\Icons\Actions\SwitchOff.dds",
                (a, sb) => sb.Append(Loc.T(a.Config.Running || a.Config.Active ? "MagRail_Ap_SlotRun" : "MagRail_Ap_SlotIdle")));
        }

        // Moves the selected station one place up (-1) or down (+1) the list.
        static void AddOrderButton(string id, string titleKey, string tipKey, int step)
        {
            IMyTerminalControlButton button = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlButton, IMyUpgradeModule>(id);
            button.Title = Loc.Id(titleKey);
            button.Tooltip = Loc.Id(tipKey);
            button.Action = b =>
            {
                AutopilotLogic a = Get(b);
                if (a != null)
                    a.Request(CmdOrder, a.Config.Selected, step);
            };
            button.Enabled = b =>
            {
                AutopilotLogic a = Get(b);
                return a != null && a.Config.Has(a.Config.Selected) && a.Config.Has(a.Config.Selected + step);
            };
            button.Visible = IsAutopilot;
            MyAPIGateway.TerminalControls.AddControl<IMyUpgradeModule>(button);
        }

        // An m/s^2 slider, steps of 0.1.
        static void AddAccelSlider(string id, string titleKey, string tipKey, int command, Func<AutopilotConfig, double> get)
        {
            IMyTerminalControlSlider s = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlSlider, IMyUpgradeModule>(id);
            s.Title = Loc.Id(titleKey);
            s.Tooltip = Loc.Id(tipKey);
            s.SetLimits((float)AutopilotConfig.MinAccel, (float)AutopilotConfig.MaxAccel);
            s.Getter = b => IsAutopilot(b) ? (float)get(Get(b).Config) : (float)AutopilotConfig.MinAccel;
            s.Setter = (b, v) => Run(b, command, (int)Math.Round(v * 10));
            s.Writer = (b, sb) =>
            {
                if (IsAutopilot(b))
                    sb.Append(Loc.F("MagRail_Unit_mps2", get(Get(b).Config).ToString("0.0")));
            };
            s.Visible = IsAutopilot;
            MyAPIGateway.TerminalControls.AddControl<IMyUpgradeModule>(s);
        }

        // Max side force in curves: 1..9 g, and the slider's last step past 9 g is "no limit".
        static void AddLateralSlider()
        {
            const float NoLimit = (float)AutopilotConfig.MaxLateral + 1;
            IMyTerminalControlSlider s = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlSlider, IMyUpgradeModule>("MagRail_ApLateral");
            s.Title = Loc.Id("MagRail_Ap_Lateral");
            s.Tooltip = Loc.Id("MagRail_Ap_Lateral_Tip");
            s.SetLimits((float)AutopilotConfig.MinLateral, NoLimit);
            s.Getter = b =>
            {
                if (!IsAutopilot(b))
                    return NoLimit;
                double g = Get(b).Config.Lateral;
                return g <= 0 ? NoLimit : (float)g;
            };
            s.Setter = (b, v) => Run(b, CmdLateral, v > AutopilotConfig.MaxLateral ? 0 : (int)Math.Round(v * 10));
            s.Writer = (b, sb) =>
            {
                if (!IsAutopilot(b))
                    return;
                double g = Get(b).Config.Lateral;
                sb.Append(g <= 0 ? Loc.T("MagRail_Ap_NoLimit") : Loc.F("MagRail_Unit_g", g.ToString("0.0")));
            };
            s.Visible = IsAutopilot;
            MyAPIGateway.TerminalControls.AddControl<IMyUpgradeModule>(s);
        }

        // Timer blocks the autopilot can trigger (its grid and everything joined to it), by name.
        static void CollectTimers(IMyTerminalBlock b)
        {
            timerScratch.Clear();
            var terminal = MyAPIGateway.TerminalActionsHelper.GetTerminalSystemForGrid(b.CubeGrid);
            if (terminal != null)
                terminal.GetBlocksOfType<IMyTimerBlock>(timerScratch);
            timerScratch.Sort((x, y) => string.Compare(x.CustomName, y.CustomName, StringComparison.CurrentCultureIgnoreCase));
        }

        // Toolbar text: the station name, cut to what the slot shows.
        static string ShortName(AutopilotConfig c, int index)
        {
            if (!c.Has(index))
                return "-";
            string name = c.Name(index);
            return name.Length > 10 ? name.Substring(0, 10) : name;
        }

        static void AddAction(string id, string name, int command, int value, string icon, Action<AutopilotLogic, StringBuilder> writer)
        {
            IMyTerminalAction action = MyAPIGateway.TerminalControls.CreateAction<IMyUpgradeModule>(id);
            action.Name = new StringBuilder(name);
            action.Icon = icon;
            action.Action = b => Run(b, command, value);
            action.Writer = (b, sb) =>
            {
                AutopilotLogic a = Get(b);
                if (a != null)
                    writer(a, sb);
            };
            action.Enabled = IsAutopilot;
            MyAPIGateway.TerminalControls.AddAction<IMyUpgradeModule>(action);
        }
    }
}
