using System;
using System.Collections.Generic;
using Sandbox.ModAPI;
using VRage;
using VRage.Game.Entity;
using VRage.Game.ModAPI;
using VRage.Library.Utils;
using VRage.ModAPI;
using VRageMath;

namespace MagRail
{
    // The display of the Rail Linker (model ModelSource/blocks/RailLinker.py; drawings ModelSource/display_svg.py,
    // textures display_textures.py): glyphs on the screen, each a subpart G_<name> of the tool - a quad over its piece of
    // an atlas, a cut-out sign that takes the colour of its subpart and glows. Shown or hidden (Render.Visible), coloured
    // (SetEmissiveParts) and, the digits, marks and hints, moved over the screen (dummy `screen`: its top left corner,
    // x right, z down the screen, 1 unit = 0.1 mm) - every frame, clients only, visual. Being parts of the tool they are
    // drawn with it in first person (the game draws a first person tool over every billboard).
    // The screen (960 x 360 units, ModelSource/display/linker_screens.svg): a rail stub at each side (the left one shows
    // its end face), port dots, the line between them in SEGMENTS segments (dashes while laying, bars once linked; each
    // its own colour), the rail between the stubs once built; over them the mouse (always) with what its buttons do: a
    // button that does something is lit, its hint next to the mouse (left button's at its left, the right one's at its
    // right, the wheel's over it); the length in whole metres in the top right corner, the profile in the top left one;
    // a switch's branch (L / C / R) over the stub of its port; under the line the ground (a mountain ridge: the span
    // follows the ground - lit, else dim) and Shift left of it (a key with the up arrow: while laying, Shift lays along
    // the ground).
    // What it shows comes from RailLinker.Screen; a click's answer flashes in the clicked button's hint's place (Flash:
    // a small check - done, a red cross - cannot, for FlashSeconds). The line's segments take the colours of the
    // world's span lines (Draw.ByShape).
    // The button on the front (the tool's material Emissive) lights in the state colour while a mouse button is pressed,
    // at least PressSeconds - the answer to a click. Other players' Linkers show nothing and their button off.
    // TEST: /magrail emi (Commands) lights any material of the held tool.
    public static class LinkerDisplay
    {
        const string ButtonMaterial = "Emissive", GlyphMaterial = "MagRail_Glyphs", ProfileMaterial = "MagRail_Profiles";
        const string ScreenDummy = "screen", Prefix = "G_";
        const float GlowEmissivity = 1;
        const double PressSeconds = 0.15;
        static readonly Color ButtonOff = new Color(40, 40, 40);
        // The glow is set again this often even without a change: a part whose render object is not there yet ignores it
        // (seen: a part of the tool stayed dark).
        const double RelightSeconds = 0.5;
        const double OthersRange = 100;     // other players' Linkers are drawn this close to the camera
        const int PlayersRefresh = 60;      // frames

        // Screen layout (ModelSource/display/linker_layout.json, written by display_svg.py): screen units.
        public const int Segments = 12;
        const float Unit = 1e-4f;           // metres
        const float TextRight = 940, TextTop = 46, TextGap = 6;
        const float DigitWidth = 43, MWidth = 56;
        const int DigitCopies = 5;         // subparts per digit (RailLinker.py DIGIT_COPIES)
        static readonly Vector2[] hintAt = { new Vector2(346, 137), new Vector2(534, 137), new Vector2(440, 34) };   // by Button
        const float AnswerIn = 20;          // a click's answer (40 units) in its hint's place (80)
        static readonly Vector2 RefusedAt = new Vector2(834, 150);  // the red cross over the right stub

        public static readonly Color White = new Color(255, 255, 255);
        static readonly Color Dim = new Color(32, 32, 32);      // a sign that is off (display_svg.py BADGE_DIM)
        const double FlashSeconds = 0.5;

        public enum LineKind { None, Dash, Bar }
        public enum Button { Left, Right, Wheel }
        // What a mouse button does: grab (start laying from a port, pick a prototype's end up), delete a prototype, make
        // a prototype, make a rail, paint, cancel.
        public enum HintKind { None, Grab, Break, Prototype, Rail, Paint, Cancel }

        // What the screen shows.
        public class Screen
        {
            public bool StubLeft, CapLeft, PortLeft, StubRight, PortRight, Rail, Arrow;
            public Color Left = White, Right = White;       // left stub, cap and port; right stub and port
            public LineKind Line;
            public readonly Color[] Grades = new Color[Segments];   // each segment's colour
            public string Text;                             // digits and 'm'
            public Color TextColor = White;
            public TrackStyle Profile;
            public bool Refused;                            // a red cross over the right stub
            public char BranchLeft, BranchRight;            // a switch's branch over the stub of its port: 'L', 'C', 'R' or none
            public readonly HintKind[] Hints = new HintKind[3]; // by Button
            public bool ShiftKey;                           // Shift under the line
            public bool GroundShown, Ground;                // the ground under the line; lit or dim

            public void Clear()
            {
                StubLeft = CapLeft = PortLeft = StubRight = PortRight = Rail = Arrow = false;
                ShiftKey = GroundShown = Ground = false;
                Left = Right = TextColor = White;
                Line = LineKind.None;
                for (int i = 0; i < Segments; i++)
                    Grades[i] = White;
                Text = null;
                Profile = null;
                Refused = false;
                BranchLeft = BranchRight = '\0';
                Array.Clear(Hints, 0, Hints.Length);
            }
        }

        // The glyph subparts (found by name, RailLinker.py glyph_subparts): the single ones, the line segments, the
        // digits, the profiles (one per TrackStyle, in the order of TrackStyle.All: G_profile_<name>).
        enum G
        {
            StubLeft, CapLeft, StubRight, Rail, PortLeft, PortRight, Arrow, CheckSmall, CrossSmall, Cross, M, Mouse,
            MouseLeft, MouseRight, MouseWheel, HintGrab, HintBreak, HintPrototype, HintRail, HintPaint, HintCancel,
            KeyShift, Terrain, BranchLeft, BranchRight = BranchLeft + 3, Dash0 = BranchRight + 3   // each L, C, R
        }
        const int Bar0 = (int)G.Dash0 + Segments, Digit0 = Bar0 + Segments, Profile0 = Digit0 + 10 * DigitCopies;
        // Before the arrays sized by it.
        static readonly int GlyphCount = Profile0 + TrackStyle.All.Length;
        static readonly string[] names = Names();

        static string[] Names()
        {
            string[] n = new string[GlyphCount];
            string[] single = { "stub_left", "cap_left", "stub_right", "rail", "port_left", "port_right", "arrow", "check_s",
                "cross_s", "cross", "m", "mouse", "mouse_l", "mouse_r", "mouse_w", "hint_grab", "hint_break", "hint_proto",
                "hint_rail", "hint_paint", "cross_hint", "key_shift", "terrain", "branch_l_left", "branch_c_left", "branch_r_left", "branch_l_right",
                "branch_c_right", "branch_r_right" };
            for (int i = 0; i < single.Length; i++)
                n[i] = Prefix + single[i];
            for (int k = 0; k < Segments; k++)
            {
                n[(int)G.Dash0 + k] = Prefix + "dash" + k;
                n[Bar0 + k] = Prefix + "bar" + k;
            }
            for (int d = 0; d < 10; d++)
                for (int c = 0; c < DigitCopies; c++)
                    n[Digit0 + d * DigitCopies + c] = Prefix + "d" + d + "_" + c;
            for (int k = 0; k < TrackStyle.All.Length; k++)
                n[Profile0 + k] = Prefix + "profile_" + TrackStyle.All[k].Name.ToLowerInvariant();
            return n;
        }

        // A tool entity's glyphs and what they were last set to (a tool entity lives while it is in hand).
        class Glyphs
        {
            public readonly MyEntitySubpart[] Parts = new MyEntitySubpart[GlyphCount];
            public readonly bool[] Shown = new bool[GlyphCount];
            public readonly Color[] Colors = new Color[GlyphCount];
            public readonly Vector2[] At = new Vector2[GlyphCount];
            public Matrix Frame;            // the dummy `screen`
            public double LitAt = -1;
            public Color Button;
            public bool ButtonSet;
        }

        static readonly Dictionary<long, Glyphs> tools = new Dictionary<long, Glyphs>();
        // Wanted this frame.
        static readonly bool[] want = new bool[GlyphCount];
        static readonly Color[] wantColor = new Color[GlyphCount];
        static readonly Vector2[] wantAt = new Vector2[GlyphCount];
        static readonly bool[] moves = new bool[GlyphCount];
        static readonly int[] used = new int[10];
        static readonly Screen empty = new Screen();

        static double pressedUntil;         // the local player's button is lit until then
        static bool flashDone;              // the local player's last click's answer: done or not, which button, until when
        static Button flashButton;
        static double flashUntil;
        static readonly List<IMyPlayer> players = new List<IMyPlayer>();
        static int playersAge = PlayersRefresh;
        static readonly MyGameTimer clock = new MyGameTimer();
        // A click's answer on the local player's screen, in the clicked button's hint's place: a small check (done:
        // laid, picked up, deleted, cancelled, switched, painted) or a small red cross (it cannot be done).
        public static void Flash(Button button, bool done)
        {
            flashButton = button;
            flashDone = done;
            flashUntil = clock.Elapsed.Seconds + FlashSeconds;
        }

        // The line's segments in the colours of `curve`'s parts (each the worst of its part, as the span lines colour it).
        public static void Line(Screen s, LineKind kind, SpanCurve curve)
        {
            s.Line = kind;
            int last = curve.Pos.Count - 1;
            for (int k = 0; k < Segments; k++)
            {
                double bend = 0;
                for (int n = k * last / Segments; n <= (k + 1) * last / Segments; n++)
                    bend = Math.Max(bend, curve.Curvature[n]);
                s.Grades[k] = MagRail.Draw.ByShape(bend, curve.TwistRate);
            }
        }

        // A length for the screen: whole metres, up to 99999.
        public static string Length(double metres)
        {
            return Math.Min(99999, Math.Round(metres)).ToString("0", System.Globalization.CultureInfo.InvariantCulture) + "m";
        }

        // The local player's mouse buttons (any held), every tick while the Linker is in hand.
        public static void Press(bool down)
        {
            if (down)
                pressedUntil = clock.Elapsed.Seconds + PressSeconds;
        }

        // A Linker in hand: `buttonColor` - the state colour its button lights in when pressed; `own` - the local
        // player's (its button answers the mouse, its screen shows `screen`; others show nothing).
        public static void Draw(IMyEntity tool, Color buttonColor, Screen screen, bool own)
        {
            MyEntity e = tool as MyEntity;
            if (e == null || tool.Model == null)
                return;
            double now = clock.Elapsed.Seconds;
            Glyphs g;
            if (!tools.TryGetValue(tool.EntityId, out g))
            {
                if (tools.Count > 64)
                    tools.Clear();
                g = Find(e);
                tools[tool.EntityId] = g;
            }
            bool relight = now - g.LitAt >= RelightSeconds;
            if (relight)
                g.LitAt = now;
            Color button = own && now < pressedUntil ? buttonColor : ButtonOff;
            if (relight || !g.ButtonSet || button != g.Button)
            {
                tool.SetEmissiveParts(ButtonMaterial, button, button == ButtonOff ? 0 : GlowEmissivity);
                g.Button = button;
                g.ButtonSet = true;
            }
            bool mine = own && screen != null;
            Want(mine ? screen : empty, mine, mine && now < flashUntil);
            Apply(g, relight);
        }

        static Glyphs Find(MyEntity e)
        {
            Glyphs g = new Glyphs();
            for (int i = 0; i < GlyphCount; i++)
            {
                MyEntitySubpart part;
                if (e.TryGetSubpart(names[i], out part))
                {
                    g.Parts[i] = part;
                    g.Shown[i] = true;          // as the model loads them
                }
            }
            Dictionary<string, IMyModelDummy> dummies = new Dictionary<string, IMyModelDummy>();
            ((IMyEntity)e).Model.GetDummies(dummies);
            IMyModelDummy frame;
            g.Frame = dummies.TryGetValue(ScreenDummy, out frame) ? Matrix.Normalize(frame.Matrix) : Matrix.Identity;
            return g;
        }

        // `mouse`: the mouse is shown (the local player's screen); `answer`: a click's answer is shown.
        static void Want(Screen s, bool mouse, bool answer)
        {
            for (int i = 0; i < GlyphCount; i++)
            {
                want[i] = false;
                moves[i] = false;
            }
            Set(G.StubLeft, s.StubLeft, s.Left);
            Set(G.CapLeft, s.CapLeft, s.Left);
            Set(G.PortLeft, s.PortLeft, s.Left);
            Set(G.StubRight, s.StubRight, s.Right);
            Set(G.PortRight, s.PortRight, s.Right);
            Set(G.Rail, s.Rail, White);
            if (s.Line != LineKind.None)
                for (int k = 0; k < Segments; k++)
                {
                    int i = (s.Line == LineKind.Dash ? (int)G.Dash0 : Bar0) + k;
                    want[i] = true;
                    wantColor[i] = s.Grades[k];
                }
            Set(G.Arrow, s.Arrow, s.Grades[Segments - 1]);
            Branch(G.BranchLeft, s.BranchLeft, s.Left);
            Branch(G.BranchRight, s.BranchRight, s.Right);
            Set(G.KeyShift, s.ShiftKey, White);
            Set(G.Terrain, s.GroundShown, s.Ground ? White : Dim);
            for (int k = 0; k < TrackStyle.All.Length; k++)
            {
                want[Profile0 + k] = s.Profile == TrackStyle.All[k];
                wantColor[Profile0 + k] = White;
            }
            if (s.Refused)
                Move(G.Cross, MagRail.Draw.Red, RefusedAt);
            Set(G.Mouse, mouse, White);
            for (int b = 0; b < 3; b++)
            {
                G lit = b == (int)Button.Left ? G.MouseLeft : b == (int)Button.Right ? G.MouseRight : G.MouseWheel;
                if (answer && b == (int)flashButton)
                {
                    Set(lit, true, White);
                    Move(flashDone ? G.CheckSmall : G.CrossSmall, flashDone ? White : MagRail.Draw.Red,
                        hintAt[b] + new Vector2(AnswerIn, AnswerIn));
                }
                else
                    Hint(s.Hints[b], lit, hintAt[b]);
            }
            if (!string.IsNullOrEmpty(s.Text))
                Text(s.Text, s.TextColor);
        }

        static void Set(G glyph, bool on, Color color)
        {
            want[(int)glyph] = on;
            wantColor[(int)glyph] = color;
        }

        // A switch's branch letter over a stub: `first` - its L.
        static void Branch(G first, char letter, Color color)
        {
            int k = letter == 0 ? -1 : "LCR".IndexOf(letter);
            if (k >= 0)
                Set(first + k, true, color);
        }

        static void Move(G glyph, Color color, Vector2 at)
        {
            int i = (int)glyph;
            want[i] = true;
            wantColor[i] = color;
            moves[i] = true;
            wantAt[i] = at;
        }

        // A mouse button that does something: lit on the mouse, its hint at `at`.
        static void Hint(HintKind hint, G button, Vector2 at)
        {
            if (hint == HintKind.None)
                return;
            Set(button, true, White);
            G glyph = hint == HintKind.Grab ? G.HintGrab : hint == HintKind.Break ? G.HintBreak
                : hint == HintKind.Prototype ? G.HintPrototype : hint == HintKind.Rail ? G.HintRail : hint == HintKind.Paint ? G.HintPaint
                : G.HintCancel;
            Move(glyph, White, at);
        }

        // A line of digits and 'm', right aligned in the top right corner (display_svg.text); digits past DigitCopies of
        // one kind are left out.
        static void Text(string text, Color color)
        {
            float width = 0;
            int count = 0;
            foreach (char c in text)
            {
                float w = Width(c);
                if (w > 0)
                {
                    width += w;
                    count++;
                }
            }
            float x = TextRight - (width + TextGap * (count - 1));
            Array.Clear(used, 0, used.Length);
            foreach (char c in text)
            {
                float w = Width(c);
                if (w <= 0)
                    continue;
                int i = -1;
                if (c == 'm')
                    i = (int)G.M;
                else if (used[c - '0'] < DigitCopies)
                    i = Digit0 + (c - '0') * DigitCopies + used[c - '0']++;
                if (i >= 0 && !want[i])
                {
                    want[i] = true;
                    wantColor[i] = color;
                    moves[i] = true;
                    wantAt[i] = new Vector2(x, TextTop);
                }
                x += w + TextGap;
            }
        }

        static float Width(char c)
        {
            return c >= '0' && c <= '9' ? DigitWidth : c == 'm' ? MWidth : 0;
        }

        static void Apply(Glyphs g, bool relight)
        {
            for (int i = 0; i < GlyphCount; i++)
            {
                MyEntitySubpart part = g.Parts[i];
                if (part == null)
                    continue;
                bool shown = want[i];
                if (shown != g.Shown[i])
                {
                    part.Render.Visible = shown;
                    g.Shown[i] = shown;
                    if (shown)
                        g.Colors[i] = Color.Transparent;    // light it again: a hidden part may have lost its glow
                }
                if (!shown)
                    continue;
                if (moves[i] && wantAt[i] != g.At[i])
                {
                    g.At[i] = wantAt[i];
                    Matrix local = Matrix.CreateTranslation(wantAt[i].X * Unit, 0, wantAt[i].Y * Unit) * g.Frame;
                    part.PositionComp.SetLocalMatrix(ref local);
                }
                if (relight || wantColor[i] != g.Colors[i])
                {
                    g.Colors[i] = wantColor[i];
                    part.SetEmissiveParts(i >= Profile0 ? ProfileMaterial : GlyphMaterial,
                        wantColor[i], GlowEmissivity);
                }
            }
        }

        // Other players' Linkers near the camera: nothing on the screen, the button off.
        public static void DrawOthers(Vector3D camera)
        {
            if (++playersAge >= PlayersRefresh)
            {
                playersAge = 0;
                players.Clear();
                MyAPIGateway.Players.GetPlayers(players);
            }
            IMyCharacter own = MyAPIGateway.Session.Player != null ? MyAPIGateway.Session.Player.Character : null;
            foreach (IMyPlayer p in players)
            {
                IMyCharacter c = p.Character;
                if (c == null || c == own || c.MarkedForClose || !RailLinker.IsHolding(c)
                    || Vector3D.DistanceSquared(c.WorldMatrix.Translation, camera) > OthersRange * OthersRange)
                    continue;
                Draw(c.EquippedTool, ButtonOff, null, false);
            }
        }
    }
}
