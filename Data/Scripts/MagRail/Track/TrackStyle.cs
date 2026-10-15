using System;
using System.Collections.Generic;
using System.Linq;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Definitions;
using VRage.Game;
using VRage.Utils;

namespace MagRail
{
    // A rail style: the section its blocks and spans are made of, the names of its track piece models, and its blocks.
    // Narrow: ModelSource/rail_profile.py (blocks/TrackPieces.py, TrackBends.py); wide: rail_profile_wide.py
    // (blocks/WideTrack.py); both sections: rail_section.py. The styles are a registry: every track block subtype is
    // listed under its style with its kind (a subtype listed nowhere is an error, never a default style). Spans only
    // connect blocks of one style; joints (end to end) join any styles: a train would drive across anyway.
    // Both styles repeat their texture every module, so every piece fits anywhere and a left bend is the right one
    // turned round. The copper is a plate per module; the 0.625 m module and the bends also come with a continuous
    // strip instead (strip: where the branches of a Rail Switch overlap, see TrackLayout).
    // A new style: an entry here (and in All), its subtypes in TrackBlockLogic's descriptor, its end face materials
    // (MagRail_Face<Name>...), its profile glyph on the Rail Linker (subpart G_profile_<name>, LinkerDisplay).
    public class TrackStyle
    {
        // Before the styles: they fill them as they are made.
        static readonly Dictionary<string, TrackStyle> styleOf = new Dictionary<string, TrackStyle>();
        static readonly Dictionary<string, TrackKind> kindOf = new Dictionary<string, TrackKind>();

        public static readonly TrackStyle Narrow = new TrackStyle("Narrow", "MagRail_", minHeight: 0.45f, width: 0.74,
                crestHeight: 0.33, top: 0.35, feet: 0.24, faceHalfWidth: 0.5f, faceBottom: -0.455f, faceTop: 0.545f)
            .Blocks(TrackKind.Rail, "MagRail_RailBlock1", "MagRail_RailBlock5", "MagRail_RailBlock10", "MagRail_RailBlock20")
            .Blocks(TrackKind.FreeRail, "MagRail_FreeRail")
            .Blocks(TrackKind.Switch, "MagRail_Switch1");

        public static readonly TrackStyle Wide = new TrackStyle("Wide", "MagRail_Wide", minHeight: 0.45f, width: 2.0,
                crestHeight: 0.33, top: 0.35, feet: 0.24, faceHalfWidth: 1.2f, faceBottom: -0.555f, faceTop: 0.645f)
            .Blocks(TrackKind.Rail, "MagRail_WideRailBlock1", "MagRail_WideRailBlock5")
            .Blocks(TrackKind.FreeRail, "MagRail_WideFreeRail")
            .Blocks(TrackKind.Switch, "MagRail_WideSwitch1");

        public static readonly TrackStyle[] All = { Narrow, Wide };

        // Its name in material and glyph names (MagRail_Face<Name>..., G_profile_<name>).
        public readonly string Name;
        // Its track piece models' names start with this.
        readonly string prefix;
        // Lowest rail point over the bottom of the cell (terminal height), m: the feet of the section stay clear of the
        // platform.
        public readonly float MinHeight;
        // Width of the section (BODY_W), m.
        public readonly double Width;
        // Over the rail point, m: the crest top (CREST_Z, the span lines' crest), the copper's top (CREST_Z + COPPER_UP);
        // under it: the bottom of the feet (-FEET_Z) (rail_section.py).
        public readonly double CrestHeight, Top, Feet;
        // The end face overlay (Draw.Face): its texture's box from the rail point, m (FACES in
        // ModelSource/overlay_textures.py, which draws the section's outline into it).
        public readonly float FaceHalfWidth, FaceBottom, FaceTop;
        readonly MyStringId[] faceLines, faceFills;

        TrackStyle(string name, string prefix, float minHeight, double width, double crestHeight, double top, double feet,
            float faceHalfWidth, float faceBottom, float faceTop)
        {
            Name = name;
            this.prefix = prefix;
            MinHeight = minHeight;
            Width = width;
            CrestHeight = crestHeight;
            Top = top;
            Feet = feet;
            FaceHalfWidth = faceHalfWidth;
            FaceBottom = faceBottom;
            FaceTop = faceTop;
            string[] parts = { "", "C", "L", "R" };
            faceLines = parts.Select(s => MyStringId.GetOrCompute("MagRail_Face" + name + s + "Line")).ToArray();
            faceFills = parts.Select(s => MyStringId.GetOrCompute("MagRail_Face" + name + s + "Fill")).ToArray();
        }

        TrackStyle Blocks(TrackKind kind, params string[] subtypes)
        {
            foreach (string subtype in subtypes)
            {
                styleOf.Add(subtype, this);
                kindOf.Add(subtype, kind);
            }
            return this;
        }

        // The style and kind of a track block subtype; false for one not in the registry.
        public static bool TryGet(string subtype, out TrackStyle style, out TrackKind kind)
        {
            kind = TrackKind.Rail;
            return styleOf.TryGetValue(subtype, out style) && kindOf.TryGetValue(subtype, out kind);
        }

        // Session load: every registered subtype must be a block the game knows (a typo here or in the .sbc).
        // A subtype in the registry but not in TrackBlockLogic's descriptor cannot be seen from here: such a block
        // gets no logic at all (no rail part, no ports).
        public static void Check()
        {
            foreach (string subtype in styleOf.Keys)
            {
                MyCubeBlockDefinition def;
                if (!MyDefinitionManager.Static.TryGetCubeBlockDefinition(
                        new MyDefinitionId(typeof(MyObjectBuilder_UpgradeModule), subtype), out def))
                    Log.Error("TrackStyle.Check", "track block " + subtype + " is registered but has no block definition");
            }
        }

        // The whole end face (part -1), or the third of a Rail Switch's end face for that SwitchBranch: its outline,
        // or its inside (fill).
        public MyStringId FaceMaterial(int part, bool fill)
        {
            return (fill ? faceFills : faceLines)[part + 1];
        }

        // strip: one module only.
        public string Straight(int modules, bool strip = false)
        {
            return prefix + (strip ? "TrackStrip_" : "Track_") + (modules * 625);
        }

        // One axis only: yaw (to the right, +; a left bend is a right one turned round: the section is symmetric),
        // pitch (up, +) or roll (the up leans to the right going forward, +; not mirrored: turned round, a twist
        // twists the same way), in bend steps.
        public string Bend(int yaw, int pitch, int roll, bool strip = false)
        {
            int steps = yaw != 0 ? yaw : pitch != 0 ? pitch : roll;
            string axis = yaw != 0 ? "Y" : pitch != 0 ? "P" : "R";
            return prefix + (strip ? "BendStrip_" : "Bend_") + axis + (steps < 0 ? "m" : "") + Math.Abs(steps);
        }

        public string Joint
        {
            get { return prefix + "Track_Joint"; }
        }
    }
}
