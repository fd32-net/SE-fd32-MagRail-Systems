using System;
using System.Collections.Generic;
using System.Text;
using Sandbox.ModAPI;
using Sandbox.ModAPI.Interfaces.Terminal;
using VRage.ModAPI;
using VRageMath;

namespace MagRail
{
    // Terminal controls and toolbar actions of the track blocks. They are added to IMyUpgradeModule
    // (controls on a base interface do not show on derived blocks) and are visible on our blocks only.
    // Slider and on/off values are terminal properties, so programmable blocks can set them too.
    // Fine steps (1 cm, 1 degree): bigger ones come from Build Vision's modifier keys. Toolbar actions
    // only for what is done often: rail height and the switch branch.
    // Build Vision steps a slider by 10^trunc(log10(1.1 * range)) / 100 (both limits non-zero) and keeps
    // writing its own value while scrolling, so the setter must not move the value elsewhere: the slider
    // ranges are chosen to give its step = our step (2.3 m -> 1 cm, 360 and 91 degrees -> 1 degree).
    public static class TrackTerminal
    {
        const float MinHeight = 0.2f, MaxHeight = 2.5f, HeightStep = 0.01f;
        const float MaxSlope = 45f, MaxBank = 45f;
        // Slider limit for slope and bank: a 90 degree range would give Build Vision 0.1 degree steps.
        const float AngleSliderMargin = 0.5f;

        static bool created;
        static readonly List<IMyTerminalControl> controls = new List<IMyTerminalControl>();

        static TrackBlockLogic Logic(IMyTerminalBlock b)
        {
            TrackBlockLogic logic = b != null && b.GameLogic != null ? b.GameLogic.GetAs<TrackBlockLogic>() : null;
            return logic != null && logic.Settings != null ? logic : null;
        }

        static bool IsTrack(IMyTerminalBlock b)
        {
            return Logic(b) != null;
        }

        static bool IsKind(IMyTerminalBlock b, TrackKind kind)
        {
            TrackBlockLogic logic = Logic(b);
            return logic != null && logic.Kind == kind;
        }

        // Height and head change only while no span in rail mode touches the block.
        static bool CanReshape(IMyTerminalBlock b)
        {
            TrackBlockLogic logic = Logic(b);
            return logic != null && !logic.IsLocked;
        }

        static void Change(IMyTerminalBlock b, Action<TrackSettings> edit)
        {
            TrackBlockLogic logic = Logic(b);
            if (logic == null)
                return;
            TrackSettings s = logic.Settings.Copy();
            edit(s);
            logic.Request(s);
        }

        static float Snap(float value, float step)
        {
            return (float)Math.Round(value / step) * step;
        }

        // Enabled states and values of the open terminal follow a change.
        public static void Refresh()
        {
            for (int i = 0; i < controls.Count; i++)
                controls[i].UpdateVisual();
        }

        public static void Create()
        {
            if (created)
                return;
            created = true;

            Add(MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlSeparator, IMyUpgradeModule>("MagRail_Sep"), IsTrack);
            IMyTerminalControlLabel label = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlLabel, IMyUpgradeModule>("MagRail_Section");
            label.Label = Loc.Id("MagRail_Term_Section");
            Add(label, IsTrack);

            IMyTerminalControlLabel locked = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlLabel, IMyUpgradeModule>("MagRail_LockedNote");
            locked.Label = Loc.Id("MagRail_Term_LockedNote");
            Add(locked, b => IsTrack(b) && !CanReshape(b));

            IMyTerminalControlSlider height = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlSlider, IMyUpgradeModule>("MagRail_Height");
            height.Title = Loc.Id("MagRail_Term_Height");
            height.Tooltip = Loc.Id("MagRail_Term_Height_Tip");
            height.SetLimits(MinHeight, MaxHeight);
            height.Getter = b => Logic(b) != null && !float.IsNaN(Logic(b).Height) ? Logic(b).Height : MinHeight;
            height.Setter = (b, v) => SetHeight(b, v);
            height.Writer = WriteHeight;
            height.Enabled = CanReshape;
            Add(height, IsTrack);

            AddAngle("MagRail_Yaw", "MagRail_Term_Yaw", -180, 180, s => s.Yaw, (s, v) => s.Yaw = v);
            AddAngle("MagRail_Pitch", "MagRail_Term_Pitch", -MaxSlope, MaxSlope, s => s.Pitch, (s, v) => s.Pitch = v, AngleSliderMargin);
            AddAngle("MagRail_Roll", "MagRail_Term_Roll", -MaxBank, MaxBank, s => s.Roll, (s, v) => s.Roll = v, AngleSliderMargin);

            IMyTerminalControlButton reset = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlButton, IMyUpgradeModule>("MagRail_ResetHead");
            reset.Title = Loc.Id("MagRail_Term_ResetHead");
            reset.Tooltip = Loc.Id("MagRail_Term_ResetHead_Tip");
            reset.Action = b => Change(b, s => { s.Yaw = 0; s.Pitch = 0; s.Roll = 0; });
            reset.Enabled = CanReshape;
            Add(reset, b => IsKind(b, TrackKind.FreeRail));

            IMyTerminalControlCombobox branch = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlCombobox, IMyUpgradeModule>("MagRail_Branch");
            branch.Title = Loc.Id("MagRail_Term_Branch");
            branch.Tooltip = Loc.Id("MagRail_Term_Branch_Tip");
            branch.ComboBoxContent = items =>
            {
                items.Add(new MyTerminalControlComboBoxItem { Key = (long)SwitchBranch.Left, Value = Loc.Id("MagRail_Branch_Left") });
                items.Add(new MyTerminalControlComboBoxItem { Key = (long)SwitchBranch.Center, Value = Loc.Id("MagRail_Branch_Center") });
                items.Add(new MyTerminalControlComboBoxItem { Key = (long)SwitchBranch.Right, Value = Loc.Id("MagRail_Branch_Right") });
            };
            branch.Getter = b => Logic(b) != null ? (long)Logic(b).Settings.Branch : 0;
            branch.Setter = (b, v) => Change(b, s => s.Branch = (SwitchBranch)v);
            Add(branch, b => IsKind(b, TrackKind.Switch));

            CreateActions();
        }

        static void Add(IMyTerminalControl control, Func<IMyTerminalBlock, bool> visible)
        {
            control.Visible = visible;
            MyAPIGateway.TerminalControls.AddControl<IMyUpgradeModule>(control);
            controls.Add(control);
        }

        static void SetHeight(IMyTerminalBlock b, float value)
        {
            TrackBlockLogic logic = Logic(b);
            if (logic == null || float.IsNaN(logic.StandardHeight))
                return;
            float standard = logic.StandardHeight;
            float height = Snap(MathHelper.Clamp(value, MinHeight, MaxHeight), HeightStep);
            Change(b, s => s.HeightOffset = Snap(height - standard, HeightStep));
        }

        static void WriteHeight(IMyTerminalBlock b, StringBuilder sb)
        {
            TrackBlockLogic logic = Logic(b);
            if (logic == null || float.IsNaN(logic.Height))
                return;
            sb.Append(Loc.F("MagRail_Unit_m", logic.Height.ToString("0.00")));
            if (Math.Abs(logic.Settings.HeightOffset) < 0.001f)
                sb.Append(' ').Append(Loc.T("MagRail_Term_Standard"));
        }

        static void AddAngle(string id, string titleKey, float min, float max, Func<TrackSettings, float> get, Action<TrackSettings, float> set,
            float sliderMargin = 0f)
        {
            IMyTerminalControlSlider slider = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlSlider, IMyUpgradeModule>(id);
            slider.Title = Loc.Id(titleKey);
            slider.Tooltip = Loc.Id(titleKey + "_Tip");
            slider.SetLimits(min - sliderMargin, max + sliderMargin);
            slider.Getter = b => Logic(b) != null ? get(Logic(b).Settings) : 0;
            slider.Setter = (b, v) => Change(b, s => set(s, MathHelper.Clamp(Snap(v, 1f), min, max)));
            slider.Writer = (b, sb) => { if (Logic(b) != null) sb.Append(get(Logic(b).Settings).ToString("0")).Append('°'); };
            slider.Enabled = CanReshape;
            Add(slider, b => IsKind(b, TrackKind.FreeRail));
        }

        // ---- toolbar actions ----

        const string IconToggle = @"Textures\GUI\Icons\Actions\Toggle.dds";
        const string IconOn = @"Textures\GUI\Icons\Actions\SwitchOn.dds";
        const string IconUp = @"Textures\GUI\Icons\Actions\Increase.dds";
        const string IconDown = @"Textures\GUI\Icons\Actions\Decrease.dds";

        static void CreateActions()
        {
            AddAction("MagRail_HeightUp", "MagRail_Act_HeightUp", IconUp, IsTrack, b => StepHeight(b, HeightStep), WriteHeight);
            AddAction("MagRail_HeightDown", "MagRail_Act_HeightDown", IconDown, IsTrack, b => StepHeight(b, -HeightStep), WriteHeight);

            Func<IMyTerminalBlock, bool> sw = b => IsKind(b, TrackKind.Switch);
            AddAction("MagRail_BranchLeft", "MagRail_Act_BranchLeft", IconOn, sw, b => Change(b, s => s.Branch = SwitchBranch.Left), WriteBranch);
            AddAction("MagRail_BranchCenter", "MagRail_Act_BranchCenter", IconOn, sw, b => Change(b, s => s.Branch = SwitchBranch.Center), WriteBranch);
            AddAction("MagRail_BranchRight", "MagRail_Act_BranchRight", IconOn, sw, b => Change(b, s => s.Branch = SwitchBranch.Right), WriteBranch);
            // Left -> Center -> Right -> Left.
            AddAction("MagRail_BranchNext", "MagRail_Act_BranchNext", IconToggle, sw,
                b => Change(b, s => s.Branch = s.Branch == SwitchBranch.Left ? SwitchBranch.Center
                    : s.Branch == SwitchBranch.Center ? SwitchBranch.Right : SwitchBranch.Left), WriteBranch);
        }

        static void AddAction(string id, string nameKey, string icon, Func<IMyTerminalBlock, bool> enabled,
            Action<IMyTerminalBlock> action, Action<IMyTerminalBlock, StringBuilder> writer)
        {
            IMyTerminalAction a = MyAPIGateway.TerminalControls.CreateAction<IMyUpgradeModule>(id);
            a.Name = new StringBuilder(Loc.T(nameKey));
            a.Icon = icon;
            a.Enabled = enabled;
            a.Action = action;
            a.Writer = writer;
            MyAPIGateway.TerminalControls.AddAction<IMyUpgradeModule>(a);
        }

        static void StepHeight(IMyTerminalBlock b, float delta)
        {
            TrackBlockLogic logic = Logic(b);
            if (logic != null && !float.IsNaN(logic.Height))
                SetHeight(b, logic.Height + delta);
        }

        static void WriteBranch(IMyTerminalBlock b, StringBuilder sb)
        {
            TrackBlockLogic logic = Logic(b);
            if (logic == null)
                return;
            SwitchBranch branch = logic.Settings.Branch;
            sb.Append(branch == SwitchBranch.Left ? "L" : branch == SwitchBranch.Right ? "R" : "C");
        }
    }
}
