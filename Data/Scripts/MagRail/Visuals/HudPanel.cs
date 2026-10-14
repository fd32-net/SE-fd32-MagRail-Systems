using System;
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
    // Nothing jumps: each paragraph keeps the most lines it has had since Reset (what is under it stays in place), a
    // one-line paragraph is cut with "..." instead of wrapped, and a paragraph with a key (AddKey) has the key in a
    // column of its own and its text beside it.
    public class HudPanel
    {
        // The game's font colours (Data/Fonts.sbc) and window colours (MyGuiScreenHudSpace.GetDefaultStyle).
        public static readonly Vector4 Blue = new Vector4(220 / 255f, 244 / 255f, 252 / 255f, 1);
        public static readonly Vector4 White = Vector4.One;
        public static readonly Vector4 Red = new Vector4(227 / 255f, 65 / 255f, 65 / 255f, 1);
        public static readonly Vector4 Green = new Vector4(101 / 255f, 182 / 255f, 93 / 255f, 1);
        // Not available now (a key that does nothing): the help colour, faded.
        public static readonly Vector4 Dim = new Vector4(220 / 255f, 244 / 255f, 252 / 255f, 0.35f);
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
            public string Key, Text;
            public Vector4 KeyColor, Color;
            public bool OneLine;
        }

        struct Line
        {
            public string Key, Text;
            public Vector4 KeyColor, Color;
            public int Paragraph, Row;      // which paragraph, its which line
        }

        readonly List<Paragraph> paragraphs = new List<Paragraph>();
        readonly List<Line> lines = new List<Line>();
        readonly List<string> wrapped = new List<string>();
        readonly List<int> kept = new List<int>();      // the lines each paragraph keeps
        int laidOut;
        HudFont laidOutFont;
        float laidOutWidth, keyWidth;

        public void Clear()
        {
            paragraphs.Clear();
        }

        // Forgets the kept heights (the window is shown anew: the tool is taken in hand).
        public void Reset()
        {
            kept.Clear();
            laidOut = 0;
        }

        public void Add(string text, Vector4 color, bool oneLine = false)
        {
            Paragraph p;
            p.Key = null;
            p.Text = text ?? "";
            p.KeyColor = p.Color = color;
            p.OneLine = oneLine;
            paragraphs.Add(p);
        }

        // A key ("[LMB]") in the key column and what it does beside it.
        public void AddKey(string key, string text, Vector4 keyColor, Vector4 color)
        {
            Paragraph p;
            p.Key = key ?? "";
            p.Text = text ?? "";
            p.KeyColor = keyColor;
            p.Color = color;
            p.OneLine = false;
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
            int count = 0;
            foreach (int k in kept)
                count += k;
            float height = titleHeight + Hud.GuiY(TextMargin) + count * line + Hud.GuiY(BottomMargin);
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

            float first = top + titleHeight + Hud.GuiY(TextMargin), y = first;
            int paragraph = 0;
            foreach (Line l in lines)
            {
                // Under the lines the paragraphs before keep, used now or not.
                for (; paragraph < l.Paragraph; paragraph++)
                    first += kept[paragraph] * line;
                y = first + l.Row * line;
                if (l.Key == null)
                    Hud.Text(font, l.Text, textLeft, y, TextScale, l.Color);
                else
                {
                    if (l.Row == 0)
                        Hud.Text(font, l.Key, textLeft, y, TextScale, l.KeyColor);
                    Hud.Text(font, l.Text, textLeft + keyWidth, y, TextScale, l.Color);
                }
            }
        }

        void Layout(HudFont font, float width)
        {
            // What is laid out, as a hash (no string built every frame).
            int now = 17;
            foreach (Paragraph p in paragraphs)
            {
                now = now * 31 + p.Color.GetHashCode();
                now = now * 31 + p.KeyColor.GetHashCode();
                now = now * 31 + (p.OneLine ? 2 : 3);
                if (p.Key != null)
                    foreach (char c in p.Key)
                        now = now * 31 + c;
                now = now * 31 + 5;
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
            // The key column: as wide as the widest key and a space.
            keyWidth = 0;
            foreach (Paragraph p in paragraphs)
                if (p.Key != null)
                    keyWidth = Math.Max(keyWidth, font.Measure(p.Key + " ", scale));
            if (kept.Count > paragraphs.Count)
                kept.RemoveRange(paragraphs.Count, kept.Count - paragraphs.Count);
            for (int n = 0; n < paragraphs.Count; n++)
            {
                Paragraph p = paragraphs[n];
                if (p.OneLine)
                {
                    wrapped.Clear();
                    wrapped.Add(Cut(font, p.Text, scale, width));
                }
                else
                    font.Wrap(p.Text, scale, p.Key != null ? width - keyWidth : width, wrapped);
                for (int i = 0; i < wrapped.Count; i++)
                {
                    Line l;
                    l.Key = p.Key;
                    l.KeyColor = p.KeyColor;
                    l.Text = wrapped[i];
                    l.Color = p.Color;
                    l.Paragraph = n;
                    l.Row = i;
                    lines.Add(l);
                }
                if (n == kept.Count)
                    kept.Add(0);
                kept[n] = Math.Max(kept[n], Math.Max(1, wrapped.Count));
            }
        }

        // `text` on one line: cut where it overflows, with "..." (the game font has no ellipsis sign).
        static string Cut(HudFont font, string text, float scale, float width)
        {
            if (font.Measure(text, scale) <= width)
                return text;
            const string More = "...";
            int end = text.Length;
            while (end > 0 && font.Measure(text.Substring(0, end).TrimEnd() + More, scale) > width)
                end--;
            return text.Substring(0, end).TrimEnd() + More;
        }
    }
}
