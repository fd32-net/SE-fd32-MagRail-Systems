using System;
using Sandbox.ModAPI;
using VRageMath;

namespace MagRail
{
    // The whole span under the welder's / grinder's window while the window shows one of its segments (SpanWelding): a
    // window of its own in the game's style right under the tool's block window, the block window's colours
    // (GetDefaultStyle). The game's windows there (MyGuiScreenHudSpace): the help at 0.28 (without hints 0.38), 0.32 high
    // (0.1: title only, MyGuiControlContextHelp), the block window 0.006 under it, 0.12 + 0.037 per component over two
    // high (MyGuiControlBlockInfo in progress mode; ours has one component). In it: how far the span is welded (the segments' average, as the block window's integrity), a bar with every segment (blue welded full, red
    // working but not full, black not working; a pointer over the aimed one), its gaps (stretches of unfinished segments:
    // trains stop there, Span.Ride) and how many segments are finished.
    public static class WeldStrip
    {
        static readonly Vector4 Background = new Vector4(13f / 85f, 0.20392157f, 0.23137255f, 0.9f);
        static readonly Vector4 BarBackground = new Vector4(4f / 15f, 0.3019608f, 0.3372549f, 0.9f);
        static readonly Vector4 Unfinished = new Vector4(23f / 51f, 23f / 85f, 16f / 51f, 1f);
        static readonly Vector4 Finished = new Vector4(0.47843137f, 28f / 51f, 0.6039216f, 1f);
        static readonly Vector4 Black = new Vector4(0, 0, 0, 1);

        // GUI units (across: GuiX, down: GuiY).
        const float Width = 0.225f, HudRight = 0.99f, Below = 0.006f;
        const float HelpTop = 0.28f, HelpHeight = 0.32f, HelpTopNoHints = 0.38f, HelpHeightNoHints = 0.1f;
        const float BlockHeight = 0.12f - 0.037f;
        const float Margin = 0.01f, BarHeight = 0.02f, BarGap = 0.007f, PointerGap = 0.012f;
        const float TitleScale = 0.75f, TextScale = 0.68f;
        // Segments narrower than this many pixels get no seams between them; a red or black stretch is at least MinGap
        // pixels wide (at 1080 px); the pointer over the aimed segment is PointerRows pixels high.
        const float SeamCells = 4, MinGap = 3, PointerRows = 5;

        // The texts, made again only when the span's welding changed.
        static TrackLayout textLayout;
        static float[] textDone;
        static bool textBuilt;
        static string title, percent, gaps, finished;
        static bool anyGap;

        // Hud.Begin first. done: the span's SpanWeld progress, null: built.
        public static void Draw(TrackLayout layout, float[] done, int aimed)
        {
            int state = MyAPIGateway.Session.Config.HudState;
            HudFont font = HudFont.Current;
            if (state == 0 || MyAPIGateway.Session.Config.MinimalHud || MyAPIGateway.Gui.IsCursorVisible || Hud.Height <= 0
                || font.LineHeight == 0)
                return;
            int n = layout.Segments;
            float working = layout.Style.Working;
            Texts(layout, done);

            float titleHeight = Hud.LineHeight(font, TitleScale), line = Hud.LineHeight(font, TextScale);
            float barHeight = Hud.GuiY(BarHeight), barGap = Hud.GuiY(BarGap), margin = Hud.GuiY(Margin);
            float width = Hud.GuiX(Width), right = Hud.HudPoint(HudRight, 0).X, left = right - width;
            float height = margin + titleHeight + Hud.GuiY(PointerGap) + barHeight + barGap + line + margin;
            float top = Hud.GuiY((state == 2 ? HelpTopNoHints + HelpHeightNoHints : HelpTop + HelpHeight) + Below + BlockHeight + Below);
            float opacity = MyAPIGateway.Session.Config.HUDBkOpacity;
            float x = left + Hud.GuiX(Margin), inner = width - 2 * Hud.GuiX(Margin);

            // The game multiplies the whole colour by the HUD background opacity (as HudPanel).
            Hud.Rect(left, top, width, height, Background * opacity);
            Hud.NextLayer();

            float y = top + margin;
            float titleScale = TitleScale * Hud.Scale * HudFont.RenderScale;
            float percentWidth = font.Measure(percent, titleScale);
            Hud.Text(font, HudPanel.Cut(font, title, titleScale, inner - percentWidth - Hud.GuiX(Margin)), x, y, TitleScale,
                HudPanel.Blue);
            Hud.Text(font, percent, x + inner - percentWidth, y, TitleScale, HudPanel.White);

            // The bar: whole pixels, every segment in full height: blue welded full, red working but not full (welded
            // under 100 % or damaged), black not working (a gap: trains stop there). A red or black stretch is at least
            // MinGap pixels wide, its neighbours give way (black over red over blue).
            y = (float)Math.Round(y + titleHeight + Hud.GuiY(PointerGap));
            float barLeft = (float)Math.Round(x), barWidth = (float)Math.Round(inner), barRight = barLeft + barWidth;
            barHeight = (float)Math.Round(barHeight);
            Hud.Rect(barLeft, y, barWidth, barHeight, BarBackground * opacity);
            Hud.NextLayer();
            float cell = barWidth / n, minGap = Math.Max(MinGap, (float)Math.Round(MinGap * Hud.Scale));
            Stretches(done, n, working, Full, 0, barLeft, barRight, cell, y, barHeight, Finished);
            Hud.NextLayer();
            Stretches(done, n, working, Short, minGap, barLeft, barRight, cell, y, barHeight, Unfinished);
            Hud.NextLayer();
            Stretches(done, n, working, Down, minGap, barLeft, barRight, cell, y, barHeight, Black);
            Hud.NextLayer();
            if (cell >= SeamCells)
                for (int g = 1; g < n; g++)
                    Hud.Rect((float)Math.Round(barLeft + g * cell), y, 1, barHeight, Background * opacity);
            // The aimed segment: a white pointer over the bar, its tip at the segment's middle.
            if (aimed >= 0 && aimed < n)
            {
                float middle = (float)Math.Floor(barLeft + (aimed + 0.5f) * cell);
                int rows = Math.Max(3, (int)Math.Round(PointerRows * Hud.Scale));
                for (int r = 0; r < rows; r++)
                    Hud.Rect(middle - r, y - 2 - r, 2 * r + 1, 1, HudPanel.White);
            }

            y += barHeight + barGap;
            float textScale = TextScale * Hud.Scale * HudFont.RenderScale;
            float finishedWidth = font.Measure(finished, textScale);
            Hud.Text(font, HudPanel.Cut(font, gaps, textScale, inner - finishedWidth - Hud.GuiX(Margin)), x, y, TextScale,
                anyGap ? HudPanel.Red : HudPanel.Blue);
            Hud.Text(font, finished, x + inner - finishedWidth, y, TextScale, HudPanel.Blue);
        }

        static float Done(float[] done, int g)
        {
            return done != null ? done[g] : 1;
        }

        // A segment's kind on the bar.
        const int Down = 0, Short = 1, Full = 2;

        static int Kind(float[] done, int g, float working)
        {
            float d = Done(done, g);
            if (SpanWeld.Stage(d, working) != SpanWeld.Finished)
                return Down;
            return d < 1 ? Short : Full;
        }

        // Every stretch of segments of this kind, at its pixels, widened about its middle to at least minWidth within
        // the bar.
        static void Stretches(float[] done, int n, float working, int kind, float minWidth, float barLeft, float barRight,
            float cell, float y, float height, Vector4 color)
        {
            int g0 = 0;
            while (g0 < n)
            {
                int k = Kind(done, g0, working), g1 = g0 + 1;
                while (g1 < n && Kind(done, g1, working) == k)
                    g1++;
                if (k == kind)
                {
                    float a = (float)Math.Round(barLeft + g0 * cell), b = (float)Math.Round(barLeft + g1 * cell);
                    if (b - a < minWidth)
                    {
                        a = (float)Math.Round((a + b - minWidth) * 0.5f);
                        a = Math.Max(barLeft, Math.Min(a, barRight - minWidth));
                        b = a + minWidth;
                    }
                    Hud.Rect(a, y, b - a, height, color);
                }
                g0 = g1;
            }
        }

        static void Texts(TrackLayout layout, float[] done)
        {
            if (layout == textLayout && done == textDone && (done != null || textBuilt))
                return;
            textLayout = layout;
            textDone = done;
            textBuilt = done == null;
            int n = layout.Segments, count = 0, runs = 0;
            float working = layout.Style.Working, sum = 0;
            bool before = true;     // the segment before is finished
            for (int g = 0; g < n; g++)
            {
                float d = done != null ? done[g] : 1;
                sum += d;
                bool ok = SpanWeld.Stage(d, working) == SpanWeld.Finished;
                if (ok)
                    count++;
                else if (before)
                    runs++;
                before = ok;
            }
            title = Loc.T("MagRail_Weld_Span").ToUpperInvariant();
            // Rounded down: 100.00 % only once all of it is welded.
            percent = (Math.Floor(sum / n * 10000) / 100).ToString("0.00") + "%";
            anyGap = runs > 0;
            gaps = anyGap ? Loc.F("MagRail_Weld_Gaps", runs) : Loc.T("MagRail_Weld_NoGaps");
            finished = Loc.F("MagRail_Weld_Finished", count, n);
        }
    }
}
