using System;
using Sandbox.ModAPI;
using VRage.Game;
using VRage.Game.ModAPI;
using VRage.Utils;
using VRageMath;
using BlendTypeEnum = VRageRender.MyBillboard.BlendTypeEnum;

namespace MagRail
{
    // Billboard lines, dots and quads shared by the visuals and the Rail Linker (clients only), and HUD lines.
    // Lines and dots are white with a dark outline (ModelSource/overlay_textures.py), tinted by the colour.
    // `through`: while the Rail Linker is held (SeeThrough), also a dimmer copy seen through models: moved towards the
    // camera to 1/100 of the distance and made as much smaller, so it looks the same but stands in front of everything
    // (BuildInfo's way; IgnoreDepth materials and BlendTypeEnum.AdditiveTop were tried and did not show in game).
    // `intensity` over 1: brighter than the colour (HDR, glows).
    public static class Draw
    {
        static readonly MyStringId LineMaterial = MyStringId.GetOrCompute("MagRail_Line");
        static readonly MyStringId DotMaterial = MyStringId.GetOrCompute("MagRail_Dot");
        static readonly MyStringId Square = MyStringId.GetOrCompute("Square");

        // The see-through copy, dimmer by default (lines and sections behind walls would clutter the view).
        public const float DimThrough = 0.45f;
        const double NearRatio = 0.01;
        // Lines stay at least this thick for their distance (~3 px on a 1080p screen).
        const double MinWidthPerMetre = 0.0025;

        // Per frame (Begin): the camera, the see-through pass on or off, 0..1 pulsing about once a second.
        public static Vector3D Camera;
        public static bool SeeThrough;
        public static float Pulse;

        public static void Begin(bool linkerHeld)
        {
            Camera = MyAPIGateway.Session.Camera.WorldMatrix.Translation;
            SeeThrough = linkerHeld;
            Pulse = 0.5f + 0.5f * (float)Math.Sin(MyAPIGateway.Session.ElapsedPlayTime.TotalSeconds * 2 * Math.PI * 1.2);
        }

        // Thickness for a line `distance` from the camera: `near`, or thicker far away to stay visible.
        public static float Width(double distance, float near)
        {
            return Math.Max(near, (float)(distance * MinWidthPerMetre));
        }

        public static void Line(Vector3D a, Vector3D b, Color color, float thickness, bool through = false, float intensity = 1,
            float throughAlpha = DimThrough)
        {
            Vector3D d = b - a;
            double length = d.Length();
            if (length < 0.001)
                return;
            Vector3 dir = (Vector3)(d / length);
            MyTransparentGeometry.AddLineBillboard(LineMaterial, color, a, dir, (float)length, thickness, BlendTypeEnum.Standard,
                intensity: intensity);
            if (through && SeeThrough)
                MyTransparentGeometry.AddLineBillboard(LineMaterial, color * throughAlpha, Near(a), dir, (float)(length * NearRatio),
                    (float)(thickness * NearRatio), BlendTypeEnum.Standard, intensity: intensity);
        }

        public static void Dot(Vector3D p, Color color, float radius, bool through = false, float intensity = 1,
            float throughAlpha = DimThrough)
        {
            // AddPointBillboard takes no intensity: the colour goes over 1 instead.
            Vector4 c = color.ToVector4() * new Vector4(intensity, intensity, intensity, 1);
            MyTransparentGeometry.AddPointBillboard(DotMaterial, c, p, radius, 0, blendType: BlendTypeEnum.Standard);
            if (through && SeeThrough)
                MyTransparentGeometry.AddPointBillboard(DotMaterial, c * throughAlpha, Near(p), (float)(radius * NearRatio), 0,
                    blendType: BlendTypeEnum.Standard);
        }

        // The rail's end face (TrackStyle.FaceMaterial: the whole, or a switch's third `part`; its outline, or its
        // inside: fill) in the plane across `right` and `up` at the rail point p, `scale` times its size (about p).
        public static void Face(TrackStyle style, int part, bool fill, Vector3D p, Vector3D right, Vector3D up, Color color,
            float scale, bool through = false, float intensity = 1,
            float throughAlpha = DimThrough)
        {
            float halfWidth = style.FaceHalfWidth * scale, halfHeight = (style.FaceTop - style.FaceBottom) * 0.5f * scale;
            Vector3D centre = p + up * ((style.FaceTop + style.FaceBottom) * 0.5f * scale);
            Vector3 left = -right, upward = up;
            Vector4 c = color.ToVector4() * new Vector4(intensity, intensity, intensity, 1);
            MyStringId material = style.FaceMaterial(part, fill);
            MyTransparentGeometry.AddBillboardOriented(material, c, centre, left, upward, halfWidth, halfHeight, Vector2.Zero,
                BlendTypeEnum.Standard);
            if (through && SeeThrough)
                MyTransparentGeometry.AddBillboardOriented(material, c * throughAlpha, Near(centre), left, upward,
                    (float)(halfWidth * NearRatio), (float)(halfHeight * NearRatio), Vector2.Zero, BlendTypeEnum.Standard);
        }

        // A flat translucent quad (never through models): corners in order round it.
        public static void Quad(Vector3D p0, Vector3D p1, Vector3D p2, Vector3D p3, Color color)
        {
            MyQuadD quad;
            quad.Point0 = p0;
            quad.Point1 = p1;
            quad.Point2 = p2;
            quad.Point3 = p3;
            Vector3D centre = (p0 + p1 + p2 + p3) * 0.25;
            MyTransparentGeometry.AddQuad(Square, ref quad, color, ref centre, blendType: BlendTypeEnum.Standard);
        }

        static Vector3D Near(Vector3D p)
        {
            return Camera + (p - Camera) * NearRatio;
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
