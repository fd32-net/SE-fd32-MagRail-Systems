using System;
using System.Collections.Generic;
using ProtoBuf;
using Sandbox.ModAPI;

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
        public bool CurveInGrid;

        public Span(TrackBlockLogic owner, SpanRecord record)
        {
            Owner = owner;
            Record = record;
        }

        public bool Bound
        {
            get { return Partner != null; }
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
