using System;
using System.Collections.Generic;
using System.Linq;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Definitions;
using Sandbox.ModAPI;
using VRage.Game;
using VRage.Utils;

namespace MagRail
{
    // A rail style: the section its blocks and spans are made of, the names of its track piece models, and its blocks.
    // Narrow: ModelSource/rail_profile.py (blocks/TrackPieces.py, TrackBends.py); wide: rail_profile_wide.py
    // (blocks/WideTrack.py); both sections: rail_section.py. The styles are a registry: every track block subtype is
    // listed under its style with its kind (a subtype listed nowhere is an error, never a default style). Spans only
    // connect blocks of one style; joints (end to end) join any styles: a train would drive across anyway.
    // Both styles repeat their body texture every module, so every piece fits anywhere and a left bend is the right
    // one turned round. The copper is a strip with a gap every TrackLayout.GapModules modules (ModelSource/
    // copper_texture.py), half of each gap on each piece: a piece has a gap half at its start (turned round: its end)
    // and every GapModules on; the 0.625 m module and the bends also come without (strip models), the yaw bends also
    // with it at the end (a right bend turned round is a left one), the joint without and with (variant models).
    // Without the whole set the gaps are off and the switch branches are laid as they are, overlapping (where they
    // overlap, they are laid level and a millimetre apart without gaps: FanLevel and TrackLayout).
    // A new style: an entry here (and in All), its subtypes in TrackBlockLogic's descriptor, its data in
    // Data/RailType_<Name>.sbc, its end face textures and Linker glyph: the checklist "Adding a rail type" in
    // ModelSource/README.md.
    public class TrackStyle
    {
        // Before the styles: they fill them as they are made.
        static readonly Dictionary<string, TrackStyle> styleOf = new Dictionary<string, TrackStyle>();
        static readonly Dictionary<string, TrackKind> kindOf = new Dictionary<string, TrackKind>();
        static readonly HashSet<string> lifted = new HashSet<string>();
        static readonly HashSet<string> fixedHeight = new HashSet<string>();

        public static readonly TrackStyle Narrow = new TrackStyle("Narrow", "MagRail_", minHeight: 0.45f, width: 0.74,
                crestHeight: 0.33, top: 0.35, feet: 0.24, faceHalfWidth: 0.5f, faceBottom: -0.455f, faceTop: 0.545f)
            .Blocks(TrackKind.Rail, "MagRail_RailBlock1", "MagRail_EmbeddedRail", "MagRail_EmbeddedRailHalf")
            .Blocks(TrackKind.FreeRail, "MagRail_FreeRail")
            .Blocks(TrackKind.Switch, "MagRail_Switch1")
            .Lifts("MagRail_RailBlock1", "MagRail_FreeRail", "MagRail_Switch1")
            .FixedHeight("MagRail_EmbeddedRail", "MagRail_EmbeddedRailHalf");

        public static readonly TrackStyle Wide = new TrackStyle("Wide", "MagRail_Wide", minHeight: 0.45f, width: 2.0,
                crestHeight: 0.33, top: 0.35, feet: 0.24, faceHalfWidth: 1.2f, faceBottom: -0.555f, faceTop: 0.645f)
            .Blocks(TrackKind.Rail, "MagRail_WideRailBlock1", "MagRail_WideEmbeddedRail", "MagRail_WideEmbeddedRailHalf")
            .Blocks(TrackKind.FreeRail, "MagRail_WideFreeRail")
            .Blocks(TrackKind.Switch, "MagRail_WideSwitch1")
            .Lifts("MagRail_WideRailBlock1", "MagRail_WideFreeRail", "MagRail_WideSwitch1")
            .FixedHeight("MagRail_WideEmbeddedRail", "MagRail_WideEmbeddedRailHalf");

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
        // Players can walk on it: its spans get collision pieces (TrackCollision) and its blocks' rail parts physics
        // (TrackBlockLogic). Without it (an energy beam) a player falls through; trains ride as always (they do not
        // touch collision). The blocks themselves keep theirs.
        public readonly bool Solid;
        // It has the whole set of strip and variant models (VariantModels; found at session load, Check).
        public bool HasStrips { get; private set; }

        TrackStyle(string name, string prefix, float minHeight, double width, double crestHeight, double top, double feet,
            float faceHalfWidth, float faceBottom, float faceTop, bool solid = true)
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
            Solid = solid;
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

        // Blocks on the lift (RailLift: Truss Pillar base, ears under the rail ends, struts) instead of platforms.
        TrackStyle Lifts(params string[] subtypes)
        {
            foreach (string subtype in subtypes)
                lifted.Add(subtype);
            return this;
        }

        public static bool HasLift(string subtype)
        {
            return lifted.Contains(subtype);
        }

        // Blocks whose rail stays where the model puts it (embedded rails: the copper flush with the block top): no
        // height setting.
        TrackStyle FixedHeight(params string[] subtypes)
        {
            foreach (string subtype in subtypes)
                fixedHeight.Add(subtype);
            return this;
        }

        public static bool HasFixedHeight(string subtype)
        {
            return fixedHeight.Contains(subtype);
        }

        // The style and kind of a track block subtype; false for one not in the registry.
        public static bool TryGet(string subtype, out TrackStyle style, out TrackKind kind)
        {
            kind = TrackKind.Rail;
            return styleOf.TryGetValue(subtype, out style) && kindOf.TryGetValue(subtype, out kind);
        }

        // Session load: every registered subtype must be a block the game knows (a typo here or in the .sbc).
        // A subtype in the registry but not in TrackBlockLogic's descriptor cannot be seen from here: such a block
        // gets no logic at all (no rail part, no ports). Which styles have their strip and variant models: all of them,
        // or none used (a part of the set is an error).
        public static void Check(MyObjectBuilder_Checkpoint.ModItem mod)
        {
            foreach (string subtype in styleOf.Keys)
            {
                MyCubeBlockDefinition def;
                if (!MyDefinitionManager.Static.TryGetCubeBlockDefinition(
                        new MyDefinitionId(typeof(MyObjectBuilder_UpgradeModule), subtype), out def))
                    Log.Error("TrackStyle.Check", "track block " + subtype + " is registered but has no block definition");
            }
            foreach (TrackStyle style in All)
            {
                List<string> missing = style.VariantModels()
                    .Where(m => !MyAPIGateway.Utilities.FileExistsInModLocation(PieceEntities.ModelFile(m), mod)).ToList();
                style.HasStrips = missing.Count == 0;
                if (missing.Count > 0 && missing.Count < style.VariantModels().Count())
                    Log.Error("TrackStyle.Check", style.Name + ": strip models missing (" + string.Join(", ", missing)
                        + "), no gaps, switch branches are laid without strips");
                Log.Info("Rail type " + style.Name + (style.HasStrips ? ": strip models found" : ": no strip models"));
            }
        }

        // The strip variants of the module and of every bend, the gap variants of the yaw bends and of the joint.
        IEnumerable<string> VariantModels()
        {
            yield return Straight(1, true);
            yield return JointGap;
            for (int steps = 1; steps <= TrackLayout.BendSteps; steps++)
            {
                yield return BendEnd(steps);
                yield return Bend(steps, 0, 0, true);
                yield return Bend(0, steps, 0, true);
                yield return Bend(0, -steps, 0, true);
                yield return Bend(0, 0, steps, true);
                yield return Bend(0, 0, -steps, true);
            }
        }

        // The whole end face (part -1), or the third of a Rail Switch's end face for that SwitchBranch: its outline,
        // or its inside (fill).
        public MyStringId FaceMaterial(int part, bool fill)
        {
            return (fill ? faceFills : faceLines)[part + 1];
        }

        // strip: without a gap, one module only.
        public string Straight(int modules, bool strip = false)
        {
            return prefix + (strip ? "TrackStrip_" : "Track_") + (modules * 625);
        }

        // One axis only: yaw (to the right, +; a left bend is a right one turned round: the section is symmetric),
        // pitch (up, +) or roll (the up leans to the right going forward, +; not mirrored: turned round, a twist
        // twists the same way), in bend steps. strip: without a gap.
        public string Bend(int yaw, int pitch, int roll, bool strip = false)
        {
            int steps = yaw != 0 ? yaw : pitch != 0 ? pitch : roll;
            string axis = yaw != 0 ? "Y" : pitch != 0 ? "P" : "R";
            return prefix + (strip ? "BendStrip_" : "Bend_") + axis + (steps < 0 ? "m" : "") + Math.Abs(steps);
        }

        // A yaw bend to the right with the gap half at its end.
        public string BendEnd(int yaw)
        {
            return prefix + "BendEnd_Y" + yaw;
        }

        // Without a gap.
        public string Joint
        {
            get { return prefix + "Track_Joint"; }
        }

        // With the gap half at its start.
        public string JointGap
        {
            get { return prefix + "Track_JointGap"; }
        }
    }
}
