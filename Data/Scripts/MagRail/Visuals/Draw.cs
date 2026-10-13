using Sandbox.ModAPI;
using VRage.Game;
using VRage.Game.ModAPI;
using VRage.Utils;
using VRageMath;
using BlendTypeEnum = VRageRender.MyBillboard.BlendTypeEnum;

namespace MagRail
{
    // Billboard lines and HUD lines shared by the visuals and the Rail Linker (clients only).
    public static class Draw
    {
        static readonly MyStringId Material = MyStringId.GetOrCompute("Square");

        public static void Line(Vector3D a, Vector3D b, Color color, float thickness)
        {
            Vector3D d = b - a;
            float length = (float)d.Length();
            if (length < 0.001f)
                return;
            MyTransparentGeometry.AddLineBillboard(Material, color, a, (Vector3)(d / length), length, thickness, BlendTypeEnum.Standard);
        }

        // Prototype colour by how well the track pieces can follow the shape (TrackLayout), per module of
        // rail: green = smooth; yellow = close to the limit; red = beyond it, joints show chamfers (the curve turns
        // or twists more than a bend model can take: radius under ~12 m, a half roll in under ~40 m).
        public static readonly Color Green = new Color(60, 230, 90);
        public static readonly Color Yellow = new Color(255, 210, 60);
        public static readonly Color Red = new Color(255, 70, 50);

        public static Color ByShape(double curvature, double twistRate)
        {
            double bend = curvature * TrackLayout.Module, twist = twistRate * TrackLayout.Module;
            if (bend > TrackLayout.MaxBend || twist > TrackLayout.MaxTwist)
                return Red;
            return bend > 0.6 * TrackLayout.MaxBend || twist > 0.6 * TrackLayout.MaxTwist ? Yellow : Green;
        }

        public static void Notify(string text, string font = MyFontEnum.White, int ms = 2000)
        {
            MyAPIGateway.Utilities.ShowNotification(text, ms, font);
        }
    }

    // A HUD line kept on screen while it is refreshed every tick. A shown notification does not take
    // a new text: a changed text gets a new notification. Hide it whenever refreshing stops (an
    // expired notification does not come back with ResetAliveTime).
    public class HudLine
    {
        IMyHudNotification note;
        string shown;

        public void Show(string text, string font = MyFontEnum.White)
        {
            string key = font + text;
            if (note != null && key == shown)
            {
                note.ResetAliveTime();
                return;
            }
            Hide();
            note = MyAPIGateway.Utilities.CreateNotification(text, 300, font);
            note.Show();
            shown = key;
        }

        public void Hide()
        {
            if (note != null)
                note.Hide();
            note = null;
            shown = null;
        }
    }
}
