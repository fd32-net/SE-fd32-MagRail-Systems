using System;
using System.Linq;
using VRage.Utils;

namespace MagRail
{
    // A rail style: the section its blocks and spans are made of, and the names of its track piece models.
    // Narrow: ModelSource/rail_profile.py (blocks/TrackPieces.py, TrackBends.py); wide: rail_profile_wide.py
    // (blocks/WideTrack.py); both sections: rail_section.py. A block's style comes from its subtype; spans only
    // connect blocks of one style, joints (end to end) join both.
    // Both styles repeat their texture every module, so every piece fits anywhere and a left bend is the right one
    // turned round. The copper is a plate per module; the 0.625 m module and the bends also come with a continuous
    // strip instead (strip: where the branches of a Rail Switch overlap, see TrackLayout).
    public class TrackStyle
    {
        public static readonly TrackStyle Narrow = new TrackStyle("MagRail_", 0.45f, 0.74, "Narrow", 0.5f, -0.455f, 0.545f);
        public static readonly TrackStyle Wide = new TrackStyle("MagRail_Wide", 0.45f, 2.0, "Wide", 1.2f, -0.555f, 0.645f);

        // Crest top over the rail point (CREST_Z), m.
        public const double CrestHeight = 0.33;

        readonly string prefix;
        // Lowest rail point over the bottom of the cell (terminal height), m: the feet of both sections hang 0.24 m
        // below the rail point and stay clear of the platform.
        public readonly float MinHeight;
        // Width of the section (BODY_W), m.
        public readonly double Width;
        // The end face overlay (Draw.Face): its texture's box from the rail point, m (FACES in
        // ModelSource/overlay_textures.py, which draws the section's outline into it).
        public readonly float FaceHalfWidth, FaceBottom, FaceTop;
        readonly MyStringId[] faceLines, faceFills;

        TrackStyle(string prefix, float minHeight, double width, string face, float faceHalfWidth, float faceBottom, float faceTop)
        {
            this.prefix = prefix;
            MinHeight = minHeight;
            Width = width;
            FaceHalfWidth = faceHalfWidth;
            FaceBottom = faceBottom;
            FaceTop = faceTop;
            string[] parts = { "", "C", "L", "R" };
            faceLines = parts.Select(s => MyStringId.GetOrCompute("MagRail_Face" + face + s + "Line")).ToArray();
            faceFills = parts.Select(s => MyStringId.GetOrCompute("MagRail_Face" + face + s + "Fill")).ToArray();
        }

        // The whole end face (part -1), or the third of a Rail Switch's end face for that SwitchBranch: its outline,
        // or its inside (fill).
        public MyStringId FaceMaterial(int part, bool fill)
        {
            return (fill ? faceFills : faceLines)[part + 1];
        }

        public static TrackStyle Of(string subtype)
        {
            return subtype.StartsWith("MagRail_Wide") ? Wide : Narrow;
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
