using System;
using System.Collections.Generic;
using Sandbox.ModAPI;
using VRage.Game;
using VRage.ModAPI;
using VRage.Utils;
using VRageMath;
using VRageRender;
using BlendTypeEnum = VRageRender.MyBillboard.BlendTypeEnum;

namespace MagRail
{
    // Flat sprites and text on the screen (clients only), in screen pixels from the top left corner: billboards a
    // little in front of the camera, drawn after post-processing (PostPP: not tone-mapped, over everything), so they
    // look like the game's own HUD. Sizes follow the game's GUI rules (MyGuiManager.UpdateScreenSize): everything
    // scales with the screen height (1 at 1080 px); a GUI unit across is the height times 4/3 (narrower screens:
    // their width); HUD positions are on the safe full screen (the middle third on three monitors).
    public static class Hud
    {
        public static readonly MyStringId Blank = MyStringId.GetOrCompute("MagRail_HudBlank");

        // Billboards this far in front of the camera (m; further, the hand and voxels cover the window); layers drawn later
        // sit a little closer (LayerStep of the depth), the screen size stays the same.
        const double Depth = 0.1;
        const double LayerStep = 0.005;
        // Wider than this is taken for three monitors side by side (the game knows it from its settings; 32:9 is 3.6).
        const float TripleHeadAspect = 3.9f;

        public static float Width, Height;
        public static bool Visible;
        static MatrixD camera;
        static double tanHalfY, tanHalfX;
        static int layer;

        // The render thread draws a frame's billboards after the game has gone on to the next one: a billboard object
        // must not change until it is drawn, or a moving player sees it a frame ahead every other frame. So the frames
        // take turns with these pools (Text HUD API keeps 4, Rich HUD Framework 3).
        const int Pools = 4;
        static readonly List<MyBillboard>[] pools = { new List<MyBillboard>(), new List<MyBillboard>(), new List<MyBillboard>(), new List<MyBillboard>() };
        static List<MyBillboard> pool = pools[0];
        static int poolIndex, used;

        // Per frame, after Draw.Begin, before drawing: false (and nothing should be drawn) while the HUD is hidden (Tab to minimal or to
        // no hints, cutscenes) or a menu screen is open.
        public static bool Begin()
        {
            poolIndex = (poolIndex + 1) % Pools;
            pool = pools[poolIndex];
            used = 0;
            layer = 0;
            IMyCamera cam = MyAPIGateway.Session.Camera;
            Width = cam.ViewportSize.X;
            Height = cam.ViewportSize.Y;
            camera = Draw.CameraMatrix;
            tanHalfY = Math.Tan(cam.FovWithZoom * 0.5);
            tanHalfX = tanHalfY * Width / Height;
            // HudState: 0 minimal, 1 full with hints, 2 full without hints (MyStatHudMode; the game's tool help shows in 1).
            Visible = Height > 0 && MyAPIGateway.Session.Config.HudState == 1 && !MyAPIGateway.Session.Config.MinimalHud
                && !MyAPIGateway.Gui.IsCursorVisible;
            return Visible;
        }

        // The game's GUI scale: text and sizes at 1080 px screen height.
        public static float Scale
        {
            get { return Height / 1080f; }
        }

        // GUI units (MyGuiManager normalized coordinates) in pixels: across, and down.
        public static float GuiX(float units)
        {
            return units * Math.Min(Width, Height * 4f / 3f);
        }

        public static float GuiY(float units)
        {
            return units * Height;
        }

        // A HUD position (0..1 of the safe full screen, MyGuiScreenHudBase.ConvertHudToNormalizedGuiPosition) in pixels.
        public static Vector2 HudPoint(float x, float y)
        {
            bool triple = Width / Height > TripleHeadAspect;
            float safeWidth = triple ? Width / 3 : Width;
            return new Vector2((Width - safeWidth) * 0.5f + x * safeWidth, y * Height);
        }

        // Draw what follows over what was drawn before.
        public static void NextLayer()
        {
            layer++;
        }

        // A sprite over the pixel rectangle (x, y from the top left, w, h), the texture part uvOffset..+uvSize.
        public static void Sprite(MyStringId material, float x, float y, float w, float h, Vector4 color, Vector2 uvOffset, Vector2 uvSize)
        {
            double depth = Depth * (1 - layer * LayerStep);
            Vector3D centre = Point(x + w * 0.5f, y + h * 0.5f, depth);
            Vector3 left = (Vector3)camera.Left, up = (Vector3)camera.Up;
            float pixel = (float)(2 * depth * tanHalfY / Height);
            MyQuadD quad;
            MyUtils.GetBillboardQuadOriented(out quad, ref centre, w * 0.5f * pixel, h * 0.5f * pixel, ref left, ref up);

            if (used == pool.Count)
                pool.Add(new MyBillboard());
            MyBillboard b = pool[used++];
            b.Material = material;
            b.LocalType = MyBillboard.LocalTypeEnum.Custom;
            b.BlendType = BlendTypeEnum.PostPP;
            b.Position0 = quad.Point0;
            b.Position1 = quad.Point1;
            b.Position2 = quad.Point2;
            b.Position3 = quad.Point3;
            b.UVOffset = uvOffset;
            b.UVSize = uvSize;
            b.DistanceSquared = (float)(depth * depth);
            // Billboards blend with premultiplied alpha.
            Vector4 linear = color.ToLinearRGB();
            b.Color = new Vector4(linear.X * color.W, linear.Y * color.W, linear.Z * color.W, color.W);
            b.ColorIntensity = 1;
            b.Reflectivity = 0;
            b.SoftParticleDistanceScale = 0;
            b.CustomViewProjection = -1;
            b.ParentID = uint.MaxValue;
            MyTransparentGeometry.AddBillboard(b, false);
        }

        public static void Rect(float x, float y, float w, float h, Vector4 color)
        {
            Sprite(Blank, x, y, w, h, color, Vector2.Zero, Vector2.One);
        }

        // One line of text from the pixel (x, y) (its top left) at `scale` (the game's text scale: 1 = a label),
        // drawn the way the game draws it (MyRenderFont.DrawCharGlyph). Returns its width in pixels.
        public static float Text(HudFont font, string text, float x, float y, float scale, Vector4 color)
        {
            float s = scale * Scale * HudFont.RenderScale;
            // Text starts on a whole pixel, as the game's (MyGuiManager.DrawString).
            x = (float)Math.Ceiling(x);
            y = (float)Math.Ceiling(y);
            float at = x;
            char last = '\0';
            bool first = true;
            for (int i = 0; i < text.Length; i++)
            {
                HudFont.Glyph g;
                if (!font.TryGet(text[i], out g))
                    continue;
                if (!first)
                    at += HudFont.Spacing * s;
                at += font.Kern(last, text[i]) * s;
                last = text[i];
                first = false;
                if (g.Width > 0 && g.Height > 0)
                    Sprite(g.Material, at + g.Bearing * s, y + (g.Bearing + g.HeightOffset + 3.8333333f) * s, g.Width * s, g.Height * s,
                        color, g.UvOffset, g.UvSize);
                at += g.Advance * s;
            }
            return at - x;
        }

        // Height of a text line at `scale`, in pixels.
        public static float LineHeight(HudFont font, float scale)
        {
            return font.LineHeight * scale * Scale * HudFont.RenderScale;
        }

        static Vector3D Point(float x, float y, double depth)
        {
            double sx = (x / Width * 2 - 1) * tanHalfX * depth, sy = (1 - y / Height * 2) * tanHalfY * depth;
            return camera.Translation + camera.Forward * depth + camera.Right * sx + camera.Up * sy;
        }
    }
}
