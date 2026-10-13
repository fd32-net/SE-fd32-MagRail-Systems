using System.Collections.Generic;
using Sandbox.ModAPI;
using VRage.Utils;
using VRageMath;

namespace MagRail
{
    // A tool help window like the game's (MyGuiControlContextHelp: the welder's window at the top right): a title band
    // with an icon and the name in capitals, under it paragraphs of text, each in its own colour. Sizes and colours are
    // the game's (in GUI units, MyGuiScreenHudSpace / MyGuiControlContextHelp); the height follows the text.
    // Fill it (Clear, Add) every tick or only on change: the lines are wrapped again only when something changed.
    public class HudPanel
    {
        // The game's font colours (Data/Fonts.sbc) and window colours (MyGuiScreenHudSpace.GetDefaultStyle).
        public static readonly Vector4 Blue = new Vector4(220 / 255f, 244 / 255f, 252 / 255f, 1);
        public static readonly Vector4 White = Vector4.One;
        public static readonly Vector4 Red = new Vector4(227 / 255f, 65 / 255f, 65 / 255f, 1);
        public static readonly Vector4 Green = new Vector4(101 / 255f, 182 / 255f, 93 / 255f, 1);
        static readonly Vector4 Background = new Vector4(13f / 85f, 0.20392157f, 0.23137255f, 0.9f);
        static readonly Vector4 TitleBackground = new Vector4(0.20784314f, 4f / 15f, 0.29803923f, 0.9f);

        // GUI units (MyGuiControlContextHelp; across: GuiX, down: GuiY).
        const float PanelWidth = 0.225f, PanelTop = 0.28f, HudRight = 0.99f;
        const float TitleHeight = 0.074f, IconMargin = 0.005f, IconWidth = 0.066f * 0.75f, IconHeight = 0.066f;
        const float NameLeft = 0.06f, NameMiddle = 0.027f, NameMaxWidth = 0.16f, NameScale = 0.83f;
        const float TextMargin = 0.01f, TextScale = 0.68f, BottomMargin = 0.01f;
        // The text wraps this much short of the window's width (MyGuiControlMultilineText: its border 0.01 and
        // its scroll bar 0.02, which the help window keeps even when hidden).
        const float TextWrapLess = 0.03f;

        public string Title = "";
        public MyStringId Icon = MyStringId.NullOrEmpty;

        struct Paragraph
        {
            public string Text;
            public Vector4 Color;
        }

        struct Line
        {
            public string Text;
            public Vector4 Color;
        }

        readonly List<Paragraph> paragraphs = new List<Paragraph>();
        readonly List<Line> lines = new List<Line>();
        readonly List<string> wrapped = new List<string>();
        int laidOut;
        HudFont laidOutFont;
        float laidOutWidth;

        public void Clear()
        {
            paragraphs.Clear();
        }

        public void Add(string text, Vector4 color)
        {
            Paragraph p;
            p.Text = text ?? "";
            p.Color = color;
            paragraphs.Add(p);
        }

        // Draws the window at its place at the top right (Hud.Begin first); `shiftX` pixels to the side (comparing).
        public void Draw(float shiftX = 0)
        {
            HudFont font = HudFont.Current;
            if (font.LineHeight == 0)
                return;
            float width = Hud.GuiX(PanelWidth);
            float right = Hud.HudPoint(HudRight, 0).X + shiftX, left = right - width, top = Hud.GuiY(PanelTop);
            float textLeft = left + Hud.GuiX(TextMargin), textWidth = Hud.GuiX(PanelWidth - TextWrapLess);
            Layout(font, textWidth);

            float line = Hud.LineHeight(font, TextScale);
            float titleHeight = Hud.GuiY(TitleHeight);
            float height = titleHeight + Hud.GuiY(TextMargin) + lines.Count * line + Hud.GuiY(BottomMargin);
            float opacity = MyAPIGateway.Session.Config.HUDBkOpacity;

            // The game multiplies the whole colour (not only its alpha) by the HUD background opacity
            // (MyGuiControlBase: colour mask * transition alpha); measured against its window in game.
            Hud.Rect(left, top, width, height, Background * opacity);
            Hud.NextLayer();
            Hud.Rect(left, top, width, titleHeight, TitleBackground * opacity);
            Hud.NextLayer();
            if (Icon != MyStringId.NullOrEmpty)
                Hud.Sprite(Icon, left + Hud.GuiX(IconMargin), top + Hud.GuiY(IconMargin), Hud.GuiX(IconWidth), Hud.GuiY(IconHeight),
                    Vector4.One, Vector2.Zero, Vector2.One);

            // The name: in capitals, smaller when it does not fit (MyGuiControlLabel auto scale), centred on its line.
            string name = Title.ToUpperInvariant();
            float nameScale = NameScale;
            float nameWidth = font.Measure(name, NameScale * Hud.Scale * HudFont.RenderScale);
            if (nameWidth > Hud.GuiX(NameMaxWidth))
                nameScale *= Hud.GuiX(NameMaxWidth) / nameWidth;
            Hud.Text(font, name, left + Hud.GuiX(NameLeft), top + Hud.GuiY(NameMiddle) - Hud.LineHeight(font, nameScale) * 0.5f, nameScale, Blue);

            float y = top + titleHeight + Hud.GuiY(TextMargin);
            foreach (Line l in lines)
            {
                Hud.Text(font, l.Text, textLeft, y, TextScale, l.Color);
                y += line;
            }
        }

        void Layout(HudFont font, float width)
        {
            // What is laid out, as a hash (no string built every frame).
            int now = 17;
            foreach (Paragraph p in paragraphs)
            {
                now = now * 31 + p.Color.GetHashCode();
                foreach (char c in p.Text)
                    now = now * 31 + c;
                now = now * 31 + 1;
            }
            if (now == laidOut && font == laidOutFont && width == laidOutWidth)
                return;
            laidOut = now;
            laidOutFont = font;
            laidOutWidth = width;
            lines.Clear();
            float scale = TextScale * Hud.Scale * HudFont.RenderScale;
            foreach (Paragraph p in paragraphs)
            {
                font.Wrap(p.Text, scale, width, wrapped);
                foreach (string w in wrapped)
                {
                    Line l;
                    l.Text = w;
                    l.Color = p.Color;
                    lines.Add(l);
                }
            }
        }
    }
}
