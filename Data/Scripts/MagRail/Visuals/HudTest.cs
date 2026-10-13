using System;
using Sandbox.Game.Gui;
using Sandbox.ModAPI;

namespace MagRail
{
    // TEMPORARY (HUD window prototype): /magrail hud ... checks the HUD windows (Hud, HudPanel).
    //   /magrail hud           - the Rail Linker window shows the next sample text (other alphabets, long words), then its own again;
    //   /magrail hud <text>    - shows that text;
    //   /magrail hud depth <m> - the windows' billboards this far in front of the camera (0.1 by default);
    //   /magrail hud compare   - while a game tool help window is up (welder, grinder in hand), our copy of it on its left.
    public static class HudTest
    {
        public static string Text;
        static bool compare;
        static int sample = -1;
        static readonly HudPanel copy = new HudPanel();

        static readonly string[] Samples =
        {
            "Deutsch: Verlegt Magnetschwebebahn-Spannweiten zwischen Gleisblöcken und schaltet sie zwischen Prototyp und Schiene um. Größe, Übergänge: äöüß ÄÖÜ.",
            "Polski: Zażółć gęślą jaźń. Čeština: Příliš žluťoučký kůň úpěl ďábelské ódy. Türkçe: Pijamalı hasta yağız şoföre çabucak güvendi. Română: Șase țânțari. Íslenska: Þórður ðá.",
            "Українська: Чуєш їх, доцю, га? Кумедна ж ти, прощайся без ґольфів! Їжак, ґанок, єнот.",
            "中文 (only with the game in Chinese): 在轨道方块之间铺设磁悬浮跨度，并在原型和轨道之间切换。",
            "日本語 (only with the game in Japanese): 線路ブロックの間にリニアのスパンを敷設します。ひらがなとカタカナ。",
            "Ελληνικά (no Greek in the game font: question marks) + a long word: Donaudampfschifffahrtsgesellschaftskapitänswitwenrentenversicherung",
        };

        // `text`: the chat line, "/magrail hud ...".
        public static void Command(string text)
        {
            string arg = text.Substring(text.IndexOf("hud", StringComparison.OrdinalIgnoreCase) + 3).Trim();
            if (arg.ToLowerInvariant() == "compare")
            {
                compare = !compare;
                MyAPIGateway.Utilities.ShowMessage("MagRail", Loc.T(compare ? "MagRail_Cmd_HudCompareOn" : "MagRail_Cmd_HudCompareOff"));
                return;
            }
            double depth;
            if (arg.StartsWith("depth ", StringComparison.OrdinalIgnoreCase) && double.TryParse(arg.Substring(6).Trim(),
                System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out depth) && depth > 0)
            {
                Hud.Depth = depth;
                MyAPIGateway.Utilities.ShowMessage("MagRail", "HUD depth " + depth.ToString(System.Globalization.CultureInfo.InvariantCulture) + " m");
                return;
            }
            if (arg != "")
            {
                Text = arg.Replace("\\n", "\n");
                return;
            }
            sample++;
            if (sample >= Samples.Length)
            {
                sample = -1;
                Text = null;
            }
            else
                Text = Samples[sample];
            MyAPIGateway.Utilities.ShowMessage("MagRail", Loc.F("MagRail_Cmd_HudSample", sample + 1, Samples.Length));
        }

        // Our copy of the game's tool help window, on its left (Hud.Begin first).
        public static void DrawCompare()
        {
            if (!compare || !Hud.Visible || MyHud.BlockInfo == null || !MyHud.BlockInfo.Visible)
                return;
            copy.Title = MyHud.BlockInfo.BlockName ?? "";
            copy.Clear();
            copy.Add(MyHud.BlockInfo.ContextHelp ?? "", HudPanel.Blue);
            copy.Draw(-Hud.GuiX(0.235f));
        }
    }
}
