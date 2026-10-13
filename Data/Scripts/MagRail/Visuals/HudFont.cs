using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Sandbox.ModAPI;
using VRage;
using VRage.Utils;
using VRageMath;

namespace MagRail
{
    // The game's own HUD font (Content/Fonts/white), read from the game files the way VRageRender.MyFont reads them,
    // so the mod's windows (Hud, HudPanel) look like the game's and every game language has its letters: the main
    // font (Latin, Cyrillic) for all languages but Chinese and Japanese, which have their own fonts (Data/Fonts.sbc).
    // The textures are the game's: one transparent material per bitmap, MagRail_Font_<file name without .dds>
    // (Data/TransparentMaterials_MagRail.sbc).
    public class HudFont
    {
        public struct Glyph
        {
            public MyStringId Material;
            public Vector2 UvOffset, UvSize;
            public int Width, Height, Advance, Bearing, HeightOffset;
        }

        // Extra space between letters in pixels of the font (MyFont.Spacing: 1 for every game font).
        public const int Spacing = 1;
        // The game draws its GUI fonts at this fraction of the asked scale (MyFont.MeasureString, MyRenderFont.DrawString).
        public const float RenderScale = 144f / 185f;

        public int LineHeight { get; private set; }
        public int Baseline { get; private set; }

        readonly Dictionary<char, Glyph> glyphs = new Dictionary<char, Glyph>();
        readonly Dictionary<int, int> kerning = new Dictionary<int, int>();
        Glyph replacement;
        bool hasReplacement;

        static HudFont current;
        static MyLanguagesEnum currentLanguage;

        // The font for the game's current language (loaded again after a language switch).
        public static HudFont Current
        {
            get
            {
                MyLanguagesEnum language = MyAPIGateway.Session.Config.Language;
                if (current == null || language != currentLanguage)
                {
                    currentLanguage = language;
                    current = Load(FileFor(language));
                }
                return current;
            }
        }

        static string FileFor(MyLanguagesEnum language)
        {
            if (language == MyLanguagesEnum.ChineseChina)
                return @"Fonts\white\FontDataCH.xml";
            if (language == MyLanguagesEnum.Japanese)
                return @"Fonts\white\FontDataJP.xml";
            return @"Fonts\white\FontDataPA.xml";
        }

        static HudFont Load(string file)
        {
            HudFont font = new HudFont();
            try
            {
                string xml;
                using (TextReader reader = MyAPIGateway.Utilities.ReadFileInGameContent(file))
                    xml = reader.ReadToEnd();
                font.Parse(xml);
                Log.Info("HUD font " + file + ": " + font.glyphs.Count + " glyphs");
            }
            catch (Exception e)
            {
                Log.Error("HudFont.Load " + file, e);
            }
            return font;
        }

        // The game's font file (an XNA bitmap font): <font base height>, <bitmap id name size>,
        // <glyph ch bm origin|loc size aw lsb [ho]>, <kernpair left right adjust>.
        void Parse(string xml)
        {
            string head = Element(xml, "<font", 0);
            if (head != null)
            {
                LineHeight = Int(Attr(head, "height"));
                Baseline = Int(Attr(head, "base"));
            }
            Dictionary<int, MyStringId> materials = new Dictionary<int, MyStringId>();
            Dictionary<int, Vector2> sizes = new Dictionary<int, Vector2>();
            for (int at = xml.IndexOf("<bitmap ", StringComparison.Ordinal); at >= 0; at = xml.IndexOf("<bitmap ", at + 1, StringComparison.Ordinal))
            {
                string e = Element(xml, "<bitmap ", at);
                int id = Int(Attr(e, "id"));
                string name = Attr(e, "name") ?? "";
                int dot = name.LastIndexOf('.');
                materials[id] = MyStringId.GetOrCompute("MagRail_Font_" + (dot > 0 ? name.Substring(0, dot) : name));
                string[] size = (Attr(e, "size") ?? "1024x1024").Split('x');
                sizes[id] = new Vector2(Int(size[0]), Int(size[1]));
            }
            for (int at = xml.IndexOf("<glyph ", StringComparison.Ordinal); at >= 0; at = xml.IndexOf("<glyph ", at + 1, StringComparison.Ordinal))
            {
                string e = Element(xml, "<glyph ", at);
                string ch = Decode(Attr(e, "ch"));
                if (string.IsNullOrEmpty(ch))
                    continue;
                int bitmap = Int(Attr(e, "bm"));
                MyStringId material;
                Vector2 texture;
                if (!materials.TryGetValue(bitmap, out material) || !sizes.TryGetValue(bitmap, out texture))
                    continue;
                string[] origin = (Attr(e, "loc") ?? Attr(e, "origin") ?? "0,0").Split(',');
                string[] size = (Attr(e, "size") ?? "0x0").Split('x');
                Glyph g;
                g.Material = material;
                g.Width = Int(size[0]);
                g.Height = Int(size[1]);
                g.UvOffset = new Vector2(Int(origin[0]), Int(origin[1])) / texture;
                g.UvSize = new Vector2(g.Width, g.Height) / texture;
                g.Advance = Int(Attr(e, "aw"));
                g.Bearing = Int(Attr(e, "lsb"));
                g.HeightOffset = Int(Attr(e, "ho"));
                glyphs[ch[0]] = g;
            }
            for (int at = xml.IndexOf("<kernpair ", StringComparison.Ordinal); at >= 0; at = xml.IndexOf("<kernpair ", at + 1, StringComparison.Ordinal))
            {
                string e = Element(xml, "<kernpair ", at);
                string left = Decode(Attr(e, "left")), right = Decode(Attr(e, "right"));
                if (!string.IsNullOrEmpty(left) && !string.IsNullOrEmpty(right))
                    kerning[KernKey(left[0], right[0])] = Int(Attr(e, "adjust"));
            }
            hasReplacement = glyphs.TryGetValue('?', out replacement);
        }

        // A letter's glyph: missing visible letters show as '?', like in the game.
        public bool TryGet(char c, out Glyph glyph)
        {
            if (glyphs.TryGetValue(c, out glyph))
                return true;
            if (!hasReplacement || char.IsWhiteSpace(c) || char.IsControl(c))
                return false;
            glyph = replacement;
            return true;
        }

        public int Kern(char left, char right)
        {
            int adjust;
            return left != '\0' && kerning.TryGetValue(KernKey(left, right), out adjust) ? adjust : 0;
        }

        // Width of one line in pixels at `scale` (the scale the letters are drawn at, RenderScale included).
        public float Measure(string text, int start, int end, float scale)
        {
            float x = 0;
            char last = '\0';
            bool first = true;
            for (int i = start; i < end; i++)
            {
                Glyph g;
                if (!TryGet(text[i], out g))
                    continue;
                if (!first)
                    x += Spacing;
                x += Kern(last, text[i]) + g.Advance;
                last = text[i];
                first = false;
            }
            return x * scale;
        }

        public float Measure(string text, float scale)
        {
            return Measure(text, 0, text.Length, scale);
        }

        // Breaks `text` into lines no wider than `width` pixels: at spaces, at '\n', and between any two
        // Chinese/Japanese letters; a word wider than a line is cut where it overflows.
        public void Wrap(string text, float scale, float width, List<string> lines)
        {
            lines.Clear();
            foreach (string paragraph in text.Split('\n'))
            {
                int start = 0, end = paragraph.Length;
                while (start < end)
                {
                    int fit = start, lastBreak = -1;
                    for (int i = start; i < end; i++)
                    {
                        if (i > start && Measure(paragraph, start, i + 1, scale) > width)
                            break;
                        fit = i + 1;
                        if (paragraph[i] == ' ')
                            lastBreak = i;
                        else if (IsIdeograph(paragraph[i]) && i + 1 < end)
                            lastBreak = i + 1;
                    }
                    int cut = fit >= end || lastBreak <= start ? fit : lastBreak;
                    lines.Add(paragraph.Substring(start, cut - start).TrimEnd());
                    start = cut;
                    while (start < end && paragraph[start] == ' ')
                        start++;
                }
                if (paragraph.Length == 0)
                    lines.Add("");
            }
        }

        static bool IsIdeograph(char c)
        {
            return c >= '⺀' && c < 'ꀀ' || c >= '豈' && c < 'ﬀ' || c >= '＀' && c < '￰';
        }

        static int KernKey(char left, char right)
        {
            return (left << 16) | right;
        }

        // ---- a minimal reader for the game's font files (one element per tag, attributes in double quotes) ----

        static string Element(string xml, string tag, int at)
        {
            at = xml.IndexOf(tag, at, StringComparison.Ordinal);
            if (at < 0)
                return null;
            int end = xml.IndexOf('>', at);
            return end < 0 ? xml.Substring(at) : xml.Substring(at, end - at);
        }

        static string Attr(string element, string name)
        {
            if (element == null)
                return null;
            string key = " " + name + "=\"";
            int at = element.IndexOf(key, StringComparison.Ordinal);
            if (at < 0)
            {
                key = "\t" + name + "=\"";
                at = element.IndexOf(key, StringComparison.Ordinal);
                if (at < 0)
                    return null;
            }
            at += key.Length;
            int end = element.IndexOf('"', at);
            return end < 0 ? null : element.Substring(at, end - at);
        }

        static int Int(string s)
        {
            int value;
            return s != null && int.TryParse(s.Trim(), out value) ? value : 0;
        }

        static string Decode(string s)
        {
            if (s == null || s.IndexOf('&') < 0)
                return s;
            StringBuilder b = new StringBuilder();
            for (int i = 0; i < s.Length; i++)
            {
                int semi = s[i] == '&' ? s.IndexOf(';', i) : -1;
                if (semi < 0)
                {
                    b.Append(s[i]);
                    continue;
                }
                string entity = s.Substring(i + 1, semi - i - 1);
                if (entity == "quot") b.Append('"');
                else if (entity == "amp") b.Append('&');
                else if (entity == "lt") b.Append('<');
                else if (entity == "gt") b.Append('>');
                else if (entity == "apos") b.Append('\'');
                else if (entity.StartsWith("#x") || entity.StartsWith("#X"))
                    b.Append((char)Convert.ToInt32(entity.Substring(2), 16));
                else if (entity.StartsWith("#"))
                    b.Append((char)Int(entity.Substring(1)));
                else
                    b.Append('&').Append(entity).Append(';');
                i = semi;
            }
            return b.ToString();
        }
    }
}
