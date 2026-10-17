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
        public readonly SpanRecord Record;
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

        public TrackBlockLogic Other(TrackBlockLogic block)
        {
            return block == Owner ? Partner : Owner;
        }
    }
}
