using System;
using System.Collections.Generic;
using ProtoBuf;
using Sandbox.ModAPI;
using VRageMath;

namespace MagRail
{
    // A span as stored in the Storage of its owner block (the block it was laid from). Ports are named
    // (model dummy names), so a model with more ports keeps old spans.
    [ProtoContract]
    public class SpanRecord
    {
        [ProtoMember(1)] public string OwnerPort;
        [ProtoMember(2)] public long PartnerId;
        [ProtoMember(3)] public string PartnerPort;
        // Partner on the owner's grid at save time: a copy of the grid finds it by its position.
        [ProtoMember(4)] public bool SameGrid;
        [ProtoMember(5)] public int PartnerX;
        [ProtoMember(6)] public int PartnerY;
        [ProtoMember(7)] public int PartnerZ;
        // Desired mode, switched with the Rail Linker: rail (true) or prototype.
        [ProtoMember(8)] public bool WantRail;
        // Paint (Rail Linker, MMB): a colour mask HSV and an armour skin (subtype, "" = none) like a block's.
        // Not painted: the owner block's colour and skin.
        [ProtoMember(9)] public bool Painted;
        [ProtoMember(10)] public float PaintH;
        [ProtoMember(11)] public float PaintS;
        [ProtoMember(12)] public float PaintV;
        [ProtoMember(13)] public string PaintSkin;
        // Lift over planet ground (TerrainLift), m, evenly over the span: measured when the span was switched to rail
        // and kept while it is one. null: none (also rails laid before the lift existed).
        [ProtoMember(14)] public float[] Lift;
        // The span ignores the ground (no TerrainLift): laid with the Rail Linker without Shift (the default). Spans
        // laid before this existed have false: they rest on the ground.
        [ProtoMember(15)] public bool NoLift;
        // A rail still being built (survival, SpanWeld): how far each segment is welded. null: built (switched to rail
        // in creative or with the admin creative tools on, or welded to the end; also every rail laid before welding existed).
        [ProtoMember(16)] public SpanWeld Weld;
    }

    [ProtoContract]
    public class SpanStore
    {
        public const int CurrentVersion = 1;

        [ProtoMember(1)] public int Version = CurrentVersion;
        // EntityId of the owner when saved: a different one means this block is a copy.
        [ProtoMember(2)] public long SelfId;
        [ProtoMember(3)] public List<SpanRecord> Spans = new List<SpanRecord>();

        public string Encode()
        {
            return Convert.ToBase64String(MyAPIGateway.Utilities.SerializeToBinary(this));
        }

        public static SpanStore Decode(string text)
        {
            return MyAPIGateway.Utilities.SerializeFromBinary<SpanStore>(Convert.FromBase64String(text));
        }
    }

    // Runtime span: a stored record, bound to both blocks once both are loaded.
    public class Span
    {
        public readonly TrackBlockLogic Owner;
        public SpanRecord Record { get; private set; }
        public int OwnerPort = -1;
        public TrackBlockLogic Partner;     // null while the partner is not loaded (or gone)
        public int PartnerPort = -1;

        // Why the span cannot be a rail now (null = it can): a Loc key.
        public string Problem;
        // Cache of TrackNetwork.CurveOf.
        public SpanCurve Curve;
        public int CurveOwnerRevision = -1, CurvePartnerRevision = -1, CurveTick;
        public object CurveGrid;
        // Cache of TrackNetwork.LayoutOf (for the current Curve).
        public TrackLayout Layout;
        // What the layout's FanLevel was built from (FanLevel.KeyChanged); null = nothing.
        public List<object> FanKey;

        public Span(TrackBlockLogic owner, SpanRecord record)
        {
            Owner = owner;
            Record = record;
        }

        public bool Bound
        {
            get { return Partner != null; }
        }

        // Colour mask HSV and skin of the span's track pieces.
        public Vector3 Color
        {
            get { return Record.Painted ? new Vector3(Record.PaintH, Record.PaintS, Record.PaintV) : Owner.Block.SlimBlock.ColorMaskHSV; }
        }

        public string Skin
        {
            get { return Record.Painted ? Record.PaintSkin ?? "" : Owner.Block.SlimBlock.SkinSubtypeId.String; }
        }

        public bool IsRail
        {
            get { return Record.WantRail && Problem == null; }
        }

        // Where trains ride: the finished stretch (its segments' integrity finished, SpanWeld.Stage, TrackStyle.Working;
        // a built rail: all of it) at arc length s, lo..hi. s up to `reach` off a finished segment (onto one not finished)
        // still finds the stretch next to it. False: not bound, no rail, or no finished segment there.
        public bool Ride(TrackNetwork network, double s, double reach, out double lo, out double hi)
        {
            lo = hi = 0;
            if (!Bound || !IsRail)
                return false;
            if (Record.Weld == null)
            {
                hi = network.CurveOf(this).Length;
                return true;
            }
            TrackLayout layout = network.LayoutOf(this);
            float[] done = Integrity(layout);
            if (stretchOf != done)
                Stretches(done, layout.Style.Working);
            int g = layout.SegmentAt(s);
            if (stretchFirst[g] < 0)
            {
                // The nearer finished neighbour within reach (a gap of one segment: they hand over in its middle).
                double back = g > 0 && stretchFirst[g - 1] >= 0 ? s - layout.SegmentStart(g) : double.MaxValue;
                double ahead = g < done.Length - 1 && stretchFirst[g + 1] >= 0 ? layout.SegmentEnd(g) - s : double.MaxValue;
                if (Math.Min(back, ahead) > reach)
                    return false;
                g += back <= ahead ? -1 : 1;
            }
            lo = layout.SegmentStart(stretchFirst[g]);
            hi = layout.SegmentEnd(stretchLast[g]);
            return true;
        }

        // By segment: the first and last segment of the finished stretch it is in, -1 if not finished (for stretchOf).
        int[] stretchFirst, stretchLast;
        float[] stretchOf;

        void Stretches(float[] done, float working)
        {
            stretchOf = done;
            stretchFirst = new int[done.Length];
            stretchLast = new int[done.Length];
            for (int g = 0; g < done.Length; g++)
            {
                bool finished = SpanWeld.Stage(done[g], working) == SpanWeld.Finished;
                stretchFirst[g] = !finished ? -1 : g > 0 && stretchFirst[g - 1] >= 0 ? stretchFirst[g - 1] : g;
            }
            for (int g = done.Length - 1; g >= 0; g--)
                stretchLast[g] = stretchFirst[g] < 0 ? -1 : g < done.Length - 1 && stretchLast[g + 1] >= 0 ? stretchLast[g + 1] : g;
        }

        // Changes whenever the welding does (track pieces and collision follow it).
        public int WeldRevision { get; private set; }
        SpanWeld weldOf;
        float[] weld;

        // How far each of the layout's segments is welded (SpanWeld), or null: built. Do not change the array.
        public float[] Weld(TrackLayout layout)
        {
            if (Record.Weld == null)
                return null;
            if (weld == null || weldOf != Record.Weld || weld.Length != layout.Segments)
            {
                weldOf = Record.Weld;
                weld = Record.Weld.Expand(layout.Segments);
            }
            return weld;
        }

        float[] integrity;
        SpanWeld integrityOf;

        // What is left of each segment (Weld less the damage, SpanWeld.Lost): what it works by. null: built. Do not
        // change the array.
        public float[] Integrity(TrackLayout layout)
        {
            float[] done = Weld(layout);
            if (done == null || Record.Weld.Damaged == null)
                return done;
            if (integrity == null || integrityOf != Record.Weld || integrity.Length != done.Length)
            {
                integrityOf = Record.Weld;
                float[] lost = Record.Weld.ExpandLost(done.Length);
                integrity = new float[done.Length];
                for (int g = 0; g < done.Length; g++)
                    integrity[g] = Math.Max(0, done[g] - lost[g]);
            }
            return integrity;
        }

        // Server: the segments' new progress, stock and lost integrity (SpanWeld.Lost, null: none; stored; null or all
        // welded full and whole with nothing in stock: built).
        public void SetWeld(float[] done, int stock, float[] lost = null)
        {
            Record.Weld = done != null ? SpanWeld.Pack(done, stock, lost) : null;
            WeldRevision++;
        }

        // Components in the span not welded in yet (SpanWeld.Stock).
        public int Stock
        {
            get { return Record.Weld != null ? Record.Weld.Stock : 0; }
        }

        // Server: switched to rail with nothing welded yet (building), or built / back to prototype.
        public void BeginWeld(bool building)
        {
            Record.Weld = building ? new SpanWeld() : null;
            WeldRevision++;
        }

        // Client: the server's copy of this span's record, the same span otherwise (TrackNetwork.ReplaceOwnerSpans).
        public void Adopt(SpanRecord record)
        {
            if (record.Weld != null || Record.Weld != null)
                WeldRevision++;
            Record = record;
        }

        public TrackBlockLogic Other(TrackBlockLogic block)
        {
            return block == Owner ? Partner : Owner;
        }
    }
}
