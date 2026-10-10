using System;
using System.Collections.Generic;
using System.Text;
using Sandbox.ModAPI;
using Sandbox.ModAPI.Interfaces.Terminal;
using VRageMath;

namespace MagRail
{
    // Terminal controls of the hover engines. The engine is a wheel suspension: our controls are added to
    // IMyMotorSuspension and visible on our engines only, and the vanilla suspension controls that mean
    // nothing here (wheel, steering, strength, height...) are hidden on them. The drive controls (Propulsion,
    // Braking, Power, Speed Limit) stay for the drive. Slider values are terminal properties for programmable
    // blocks; 1 cm steps (ranges chosen for Build Vision's step, see TrackTerminal).
    public static class EngineTerminal
    {
        const float Step = 0.01f;

        static readonly string[] HiddenControls =
        {
            "Add Top Part", "Steering", "AirShock", "InvertSteering", "InvertPropulsion", "MaxSteerAngle", "Strength",
            "Height", "Friction", "Steer override", "Weld speed", "Force weld", "SafetyDetach", "ShareInertiaTensor",
        };
        // Toolbar actions hidden on our engines: any whose id contains one of these (slider actions are
        // "Increase<Id>" / "Decrease<Id>" / "Reset<Id>").
        static readonly string[] HiddenActionParts =
        {
            "Add Top Part", "Attach", "Detach", "Steering", "AirShock", "InvertSteering", "InvertPropulsion", "MaxSteerAngle",
            "Strength", "Height", "Friction", "Steer override", "SteerOverride", "Weld speed", "Force weld", "SafetyDetach",
            "ShareInertiaTensor",
        };

        static bool created;
        static readonly List<IMyTerminalControl> controls = new List<IMyTerminalControl>();

        static HoverEngineLogic Logic(IMyTerminalBlock b)
        {
            HoverEngineLogic logic = b != null && b.GameLogic != null ? b.GameLogic.GetAs<HoverEngineLogic>() : null;
            return logic != null && logic.Settings != null ? logic : null;
        }

        static bool IsEngine(IMyTerminalBlock b)
        {
            return Logic(b) != null;
        }

        static void Change(IMyTerminalBlock b, Action<EngineSettings> edit)
        {
            HoverEngineLogic logic = Logic(b);
            if (logic == null)
                return;
            EngineSettings s = logic.Settings.Copy();
            edit(s);
            logic.Request(s);
        }

        static float Snap(float value)
        {
            return (float)Math.Round(value / Step) * Step;
        }

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
            HideVanilla();

            Add(MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlSeparator, IMyMotorSuspension>("MagRail_EngineSep"));
            AddLabel("MagRail_EngineSection", "MagRail_EngineTerm_Section");
            AddSlider("MagRail_HoverHeight", "MagRail_EngineTerm_Height", EngineSettings.MinHeight, EngineSettings.MaxHeight,
                s => s.HoverHeight, (s, v) => s.HoverHeight = v, v => Loc.F("MagRail_Unit_m", v.ToString("0.00")));
            AddSlider("MagRail_HoldStrength", "MagRail_EngineTerm_Hold", EngineSettings.MinHold, EngineSettings.MaxHold,
                s => s.HoldStrength, (s, v) => s.HoldStrength = v, v => v.ToString("0.00") + "x");
            AddLabel("MagRail_EngineOffsets", "MagRail_EngineTerm_Offsets");
            AddSlider("MagRail_OffsetRight", "MagRail_EngineTerm_OffsetRight", -EngineSettings.MaxOffsetRight, EngineSettings.MaxOffsetRight,
                s => s.OffsetRight, (s, v) => s.OffsetRight = v, v => Loc.F("MagRail_Unit_m", v.ToString("0.00")));
            AddSlider("MagRail_OffsetForward", "MagRail_EngineTerm_OffsetForward", -EngineSettings.MaxOffsetForward, EngineSettings.MaxOffsetForward,
                s => s.OffsetForward, (s, v) => s.OffsetForward = v, v => Loc.F("MagRail_Unit_m", v.ToString("0.00")));
            AddSlider("MagRail_OffsetUp", "MagRail_EngineTerm_OffsetUp", -EngineSettings.MaxOffsetUp, EngineSettings.MaxOffsetUp,
                s => s.OffsetUp, (s, v) => s.OffsetUp = v, v => Loc.F("MagRail_Unit_m", v.ToString("0.00")));
        }

        static void Add(IMyTerminalControl control)
        {
            control.Visible = IsEngine;
            MyAPIGateway.TerminalControls.AddControl<IMyMotorSuspension>(control);
            controls.Add(control);
        }

        static void AddLabel(string id, string key)
        {
            IMyTerminalControlLabel label = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlLabel, IMyMotorSuspension>(id);
            label.Label = Loc.Id(key);
            Add(label);
        }

        static void AddSlider(string id, string titleKey, float min, float max, Func<EngineSettings, float> get, Action<EngineSettings, float> set,
            Func<float, string> format)
        {
            IMyTerminalControlSlider slider = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlSlider, IMyMotorSuspension>(id);
            slider.Title = Loc.Id(titleKey);
            slider.Tooltip = Loc.Id(titleKey + "_Tip");
            slider.SetLimits(min, max);
            slider.Getter = b => Logic(b) != null ? get(Logic(b).Settings) : min;
            slider.Setter = (b, v) => Change(b, s => set(s, Snap(MathHelper.Clamp(v, min, max))));
            slider.Writer = (b, sb) => { if (Logic(b) != null) sb.Append(format(get(Logic(b).Settings))); };
            Add(slider);
        }

        static void HideVanilla()
        {
            List<IMyTerminalControl> vanilla;
            MyAPIGateway.TerminalControls.GetControls<IMyMotorSuspension>(out vanilla);
            foreach (IMyTerminalControl control in vanilla)
            {
                if (Array.IndexOf(HiddenControls, control.Id) < 0)
                    continue;
                Func<IMyTerminalBlock, bool> visible = control.Visible;
                control.Visible = b => !IsEngine(b) && (visible == null || visible(b));
            }

            List<IMyTerminalAction> actions;
            MyAPIGateway.TerminalControls.GetActions<IMyMotorSuspension>(out actions);
            foreach (IMyTerminalAction action in actions)
            {
                if (!HiddenAction(action.Id))
                    continue;
                Func<IMyTerminalBlock, bool> enabled = action.Enabled;
                action.Enabled = b => !IsEngine(b) && (enabled == null || enabled(b));
            }
        }

        static bool HiddenAction(string id)
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
