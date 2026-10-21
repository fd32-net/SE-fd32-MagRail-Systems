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
        // Drive controls the game greys out without a wheel (enabled only with the wheel's constraint): enabled on
        // our engines. Their toolbar actions have no such check.
        static readonly string[] DriveControls = { "Propulsion", "Power", "Speed Limit", "Propulsion override" };
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

        static bool HasPlatform(IMyTerminalBlock b)
        {
            HoverEngineLogic logic = Logic(b);
            return logic != null && logic.HasPlatform;
        }

        static bool HasPlatformOffset(IMyTerminalBlock b)
        {
            HoverEngineLogic logic = Logic(b);
            return logic != null && logic.HasPlatformOffset;
        }

        static bool NoPlatform(IMyTerminalBlock b)
        {
            HoverEngineLogic logic = Logic(b);
            return logic != null && !logic.HasPlatform;
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
                s => s.HoverHeight, (s, v) => s.HoverHeight = v, v => Loc.F("MagRail_Unit_m", v.ToString("0.00")), NoPlatform);
            AddSlider("MagRail_PlatformGap", "MagRail_EngineTerm_Gap", EngineSettings.MinGap, EngineSettings.MaxGap,
                s => s.PlatformGap, (s, v) => s.PlatformGap = v, v => Loc.F("MagRail_Unit_m", v.ToString("0.00")), HasPlatform);
            // The offset's range is the model's.
            AddSlider("MagRail_PlatformOffset", "MagRail_EngineTerm_Offset",
                b => Logic(b) != null && Logic(b).HasPlatformOffset ? (float)Logic(b).PlatformOffsetMin : EngineSettings.MinOffset,
                b => Logic(b) != null && Logic(b).HasPlatformOffset ? (float)Logic(b).PlatformOffsetMax : EngineSettings.MaxOffset,
                s => s.PlatformOffset, (s, v) => s.PlatformOffset = v, v => Loc.F("MagRail_Unit_m", v.ToString("0.00")), HasPlatformOffset);
            AddSlider("MagRail_HoldStrength", "MagRail_EngineTerm_Hold", EngineSettings.MinHold, EngineSettings.MaxHold,
                s => s.HoldStrength, (s, v) => s.HoldStrength = v, v => v.ToString("0.00") + "x");
            AddSlider("MagRail_SideOffset", "MagRail_EngineTerm_SideOffset", -EngineSettings.MaxSideOffset, EngineSettings.MaxSideOffset,
                s => s.SideOffset, (s, v) => s.SideOffset = v, v => Loc.F("MagRail_Unit_m", v.ToString("0.00")));
        }

        static void Add(IMyTerminalControl control, Func<IMyTerminalBlock, bool> visible = null)
        {
            control.Visible = visible ?? IsEngine;
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
            Func<float, string> format, Func<IMyTerminalBlock, bool> visible = null)
        {
            AddSlider(id, titleKey, b => min, b => max, get, set, format, visible);
        }

        static void AddSlider(string id, string titleKey, Func<IMyTerminalBlock, float> min, Func<IMyTerminalBlock, float> max,
            Func<EngineSettings, float> get, Action<EngineSettings, float> set, Func<float, string> format,
            Func<IMyTerminalBlock, bool> visible = null)
        {
            IMyTerminalControlSlider slider = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlSlider, IMyMotorSuspension>(id);
            slider.Title = Loc.Id(titleKey);
            slider.Tooltip = Loc.Id(titleKey + "_Tip");
            slider.SetLimits(min, max);
            slider.Getter = b => Logic(b) != null ? get(Logic(b).Settings) : min(b);
            slider.Setter = (b, v) => Change(b, s => set(s, Snap(MathHelper.Clamp(v, min(b), max(b)))));
            slider.Writer = (b, sb) => { if (Logic(b) != null) sb.Append(format(get(Logic(b).Settings))); };
            Add(slider, visible);
        }

        static void HideVanilla()
        {
            List<IMyTerminalControl> vanilla;
            MyAPIGateway.TerminalControls.GetControls<IMyMotorSuspension>(out vanilla);
            foreach (IMyTerminalControl control in vanilla)
            {
                if (Array.IndexOf(DriveControls, control.Id) >= 0)
                {
                    Func<IMyTerminalBlock, bool> enabled = control.Enabled;
                    control.Enabled = b => IsEngine(b) || enabled == null || enabled(b);
                    continue;
                }
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
