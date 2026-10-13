using Sandbox.ModAPI;
using VRage.Input;
using VRage.ModAPI;
using VRage.Utils;

namespace MagRail
{
    // Key names for help texts, as the game shows them in its own help ([LMB], MyIngameHelpObjective.GetHighlightedControl):
    // in the game's language and after the player's rebinding.
    public static class Keys
    {
        // A game control (MyControlsSpace): its keyboard key and/or mouse button.
        public static string Control(MyStringId control)
        {
            IMyControl c = MyAPIGateway.Input.GetGameControl(control);
            if (c == null)
                return "[?]";
            string keyboard = Name(c.GetControlButtonName(MyGuiInputDeviceEnum.Keyboard));
            string mouse = Name(c.GetControlButtonName(MyGuiInputDeviceEnum.Mouse));
            if (keyboard != null && mouse != null)
                return "[" + keyboard + "/" + mouse + "]";
            return "[" + (keyboard ?? mouse ?? MyAPIGateway.Input.GetUnassignedName()) + "]";
        }

        public static string Mouse(MyMouseButtonsEnum button)
        {
            return "[" + MyAPIGateway.Input.GetName(button) + "]";
        }

        public static string Key(MyKeys key)
        {
            return "[" + MyAPIGateway.Input.GetKeyName(key) + "]";
        }

        static string Name(string name)
        {
            return string.IsNullOrEmpty(name) || name == MyAPIGateway.Input.GetUnassignedName() ? null : name;
        }
    }
}
