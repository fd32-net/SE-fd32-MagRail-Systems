using System;
using System.Collections.Generic;
using Sandbox.ModAPI;
using Sandbox.ModAPI.Interfaces.Terminal;
using VRage.Game;
using VRage.Game.ModAPI;
using VRageMath;

namespace MagRail
{
    // TEMPORARY (stage 5 tuning, see DriveTuning): the drive's numbers on sliders in the terminal of cockpits and
    // remote controls (added per concrete interface: controls on IMyShipController do not show), and the HUD line
    // of the piloted train. Single player: values live on the server's side of the session, nothing is synced.
    public static class DriveTuningTerminal
    {
        static bool created;
        static readonly List<IMyTerminalControl> controls = new List<IMyTerminalControl>();

        public static void Create()
        {
            if (created)
                return;
            created = true;
            AddAll<IMyCockpit>();
            AddAll<IMyRemoteControl>();
        }

        static void AddAll<T>() where T : IMyTerminalBlock
        {
            Add<T>(MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlSeparator, T>("MagRail_TuneSep"));
            AddLabel<T>("MagRail_TuneSection", "MagRail_Tune_Section");
            AddCheckbox<T>("MagRail_TuneHud", "MagRail_Tune_Hud", t => t.Hud, (t, v) => t.Hud = v);

            AddLabel<T>("MagRail_TuneBrakeLabel", "MagRail_Tune_Brake");
            AddSlider<T>("MagRail_TuneBrakeFactor", "MagRail_Tune_BrakeFactor", 0, 5, 0.1f,
                t => t.BrakeFactor, (t, v) => t.BrakeFactor = v, v => "x" + v.ToString("0.0"));
            AddSlider<T>("MagRail_TuneBrakeShare", "MagRail_Tune_BrakeShare", 0, 400, 1,
                t => t.BrakeShare * 100, (t, v) => t.BrakeShare = v / 100, Percent);
            AddSlider<T>("MagRail_TuneBrakeMax", "MagRail_Tune_BrakeMax", 0, 100, 0.1f,
                t => t.MaxBrakeAccel, (t, v) => t.MaxBrakeAccel = v, Accel);
            AddSlider<T>("MagRail_TuneBrakeRise", "MagRail_Tune_BrakeRise", 0, 3, 0.05f,
                t => t.BrakeRise, (t, v) => t.BrakeRise = v, v => Loc.F("MagRail_Unit_s", v.ToString("0.00")));
            AddSlider<T>("MagRail_TuneSoftStop", "MagRail_Tune_SoftStop", 0, 5, 0.1f,
                t => t.SoftStop, (t, v) => t.SoftStop = v, Speed);
            AddCheckbox<T>("MagRail_TuneDisturbance", "MagRail_Tune_Disturbance", t => t.Disturbance, (t, v) => t.Disturbance = v);

            AddLabel<T>("MagRail_TuneParkLabel", "MagRail_Tune_Park");
            AddCheckbox<T>("MagRail_TuneParkStopsDrive", "MagRail_Tune_ParkStopsDrive", t => t.ParkStopsDrive, (t, v) => t.ParkStopsDrive = v);
            AddSlider<T>("MagRail_TuneParkShare", "MagRail_Tune_ParkShare", 0, 400, 1,
                t => t.ParkShare * 100, (t, v) => t.ParkShare = v / 100, Percent);
            AddSlider<T>("MagRail_TuneParkMax", "MagRail_Tune_ParkMax", 0, 100, 0.1f,
                t => t.MaxParkAccel, (t, v) => t.MaxParkAccel = v, Accel);
            AddSlider<T>("MagRail_TuneRestAccel", "MagRail_Tune_RestAccel", 0, 5, 0.01f,
                t => t.RestAccel, (t, v) => t.RestAccel = v, Accel);
            AddSlider<T>("MagRail_TuneRestSpeed", "MagRail_Tune_RestSpeed", 0, 2, 0.01f,
                t => t.RestSpeed, (t, v) => t.RestSpeed = v, Speed);

            AddLabel<T>("MagRail_TuneThrustLabel", "MagRail_Tune_Thrust");
            AddSlider<T>("MagRail_TuneThrustShare", "MagRail_Tune_ThrustShare", 0, 200, 1,
                t => t.ThrustShare * 100, (t, v) => t.ThrustShare = v / 100, Percent);
            AddSlider<T>("MagRail_TuneThrustMax", "MagRail_Tune_ThrustMax", 0, 100, 0.1f,
                t => t.MaxThrustAccel, (t, v) => t.MaxThrustAccel = v, Accel);

            IMyTerminalControlButton reset = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlButton, T>("MagRail_TuneReset");
            reset.Title = Loc.Id("MagRail_Tune_Reset");
            reset.Tooltip = Loc.Id("MagRail_Tune_Reset_Tip");
            reset.Action = b =>
            {
                DriveTuning.Reset(b);
                RideLog.Note("tune " + b.CustomName + ": defaults");
                for (int i = 0; i < controls.Count; i++)
                    controls[i].UpdateVisual();
            };
            Add<T>(reset);
        }

        static void Add<T>(IMyTerminalControl control) where T : IMyTerminalBlock
        {
            MyAPIGateway.TerminalControls.AddControl<T>(control);
            controls.Add(control);
        }

        static void AddLabel<T>(string id, string key) where T : IMyTerminalBlock
        {
            IMyTerminalControlLabel label = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlLabel, T>(id);
            label.Label = Loc.Id(key);
            Add<T>(label);
        }

        static void AddCheckbox<T>(string id, string key, Func<DriveTuning, bool> get, Action<DriveTuning, bool> set) where T : IMyTerminalBlock
        {
            IMyTerminalControlCheckbox box = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlCheckbox, T>(id);
            box.Title = Loc.Id(key);
            box.Tooltip = Loc.Id(key + "_Tip");
            box.Getter = b => get(DriveTuning.For(b));
            box.Setter = (b, v) => Changed(b, t => set(t, v));
            Add<T>(box);
        }

        static void AddSlider<T>(string id, string key, float min, float max, float step, Func<DriveTuning, double> get,
            Action<DriveTuning, float> set, Func<float, string> format) where T : IMyTerminalBlock
        {
            IMyTerminalControlSlider slider = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlSlider, T>(id);
            slider.Title = Loc.Id(key);
            slider.Tooltip = Loc.Id(key + "_Tip");
            slider.SetLimits(min, max);
            slider.Getter = b => (float)get(DriveTuning.For(b));
            slider.Setter = (b, v) => Changed(b, t => set(t, (float)Math.Round(MathHelper.Clamp(v, min, max) / step) * step));
            slider.Writer = (b, sb) => sb.Append(format((float)get(DriveTuning.For(b))));
            Add<T>(slider);
        }

        static void Changed(IMyTerminalBlock b, Action<DriveTuning> edit)
        {
            DriveTuning t = DriveTuning.Edit(b);
            edit(t);
            DriveTuning.Save(b, t);
            RideLog.Note("tune " + b.CustomName + ": " + t);
        }

        static string Percent(float v)
        {
            return v.ToString("0") + "%";
        }

        static string Accel(float v)
        {
            return Loc.F("MagRail_Unit_mps2", v.ToString("0.00"));
        }

        static string Speed(float v)
        {
            return Loc.F("MagRail_Unit_mps", v.ToString("0.00"));
        }
    }

    // TEMPORARY (stage 5 tuning): one HUD line for the train the local player drives (single player): its drive,
    // speed, measured deceleration, stopping distance at that deceleration and mass.
    public static class DriveTuningHud
    {
        const int TextTicks = 10;
        // Deceleration smoothing per tick (about a quarter of a second).
        const double Smoothing = 0.07;
        static readonly HudLine line = new HudLine();
        static RailBody body;
        static double lastSpeed, decel;
        static int tick;
        static string text;

        public static void Update(Rides rides)
        {
            IMyPlayer player = MyAPIGateway.Session.Player;
            IMyShipController c = player != null && player.Controller != null ? player.Controller.ControlledEntity as IMyShipController : null;
            RailBody b = c != null && DriveTuning.For(c).Hud ? rides.Driven(c) : null;
            if (b == null || !b.Holding)
            {
                Clear();
                return;
            }
            double speed = Math.Abs(b.Speed);
            if (b != body)
            {
                body = b;
                decel = 0;
                text = null;
            }
            else
            {
                decel += ((lastSpeed - speed) * 60 - decel) * Smoothing;
            }
            lastSpeed = speed;
            if (text == null || ++tick % TextTicks == 0)
            {
                string stop = decel > 0.05 && speed > 0.1 ? Loc.F("MagRail_Unit_m", (speed * speed / (2 * decel)).ToString("0.0")) : "-";
                double mass = b.Grid.Physics != null ? b.Grid.Physics.Mass / 1000 : 0;
                text = Loc.F("MagRail_Tune_HudLine", EngineInfo.DriveLine(b), speed.ToString("0.00"), decel.ToString("0.00"), stop, mass.ToString("0.0"));
                if (b.Drive.BrakeCapped)
                    text += Loc.T("MagRail_Tune_HudCapped");
            }
            line.Show(text, b.Drive.BrakeCapped ? MyFontEnum.Red : MyFontEnum.White);
        }

        public static void Clear()
        {
            line.Hide();
            body = null;
            text = null;
        }
    }
}
