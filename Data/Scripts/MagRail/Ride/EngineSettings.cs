using System;
using ProtoBuf;
using Sandbox.ModAPI;
using VRageMath;

namespace MagRail
{
    // Player settings of a hover engine: kept in the block's Storage and sent over the network.
    [ProtoContract]
    public class EngineSettings
    {
        public const int CurrentVersion = 1;
        // Limits (m): ranges chosen so Build Vision steps them by 1 cm (see TrackTerminal).
        public const float MinHeight = 0.5f, MaxHeight = 9.5f;
        public const float MinHold = 0.5f, MaxHold = 3f;
        public const float MaxOffsetRight = 2.5f, MaxOffsetForward = 3f, MaxOffsetUp = 2f;
        public const float LargeHeight = 2.5f, SmallHeight = 1.2f;

        [ProtoMember(1)] public int Version = CurrentVersion;
        // Height of the guide point over the rail point, m.
        [ProtoMember(2)] public float HoverHeight = LargeHeight;
        // Stiffness of the hold (1 = standard): how hard the engine pulls its point back over the rail.
        [ProtoMember(3)] public float HoldStrength = 1;
        // The guide point (the point held over the rail) from the block centre along the block's Right,
        // Forward and Up, m.
        [ProtoMember(4)] public float OffsetRight;
        [ProtoMember(5)] public float OffsetForward;
        [ProtoMember(6)] public float OffsetUp;

        public EngineSettings Copy()
        {
            return new EngineSettings
            {
                Version = Version, HoverHeight = HoverHeight, HoldStrength = HoldStrength,
                OffsetRight = OffsetRight, OffsetForward = OffsetForward, OffsetUp = OffsetUp,
            };
        }

        // Within the limits (requests from clients and programmable blocks too).
        public EngineSettings Clamped()
        {
            EngineSettings s = Copy();
            s.Version = CurrentVersion;
            s.HoverHeight = MathHelper.Clamp(s.HoverHeight, MinHeight, MaxHeight);
            s.HoldStrength = MathHelper.Clamp(s.HoldStrength, MinHold, MaxHold);
            s.OffsetRight = MathHelper.Clamp(s.OffsetRight, -MaxOffsetRight, MaxOffsetRight);
            s.OffsetForward = MathHelper.Clamp(s.OffsetForward, -MaxOffsetForward, MaxOffsetForward);
            s.OffsetUp = MathHelper.Clamp(s.OffsetUp, -MaxOffsetUp, MaxOffsetUp);
            return s;
        }

        public string Encode()
        {
            return Convert.ToBase64String(MyAPIGateway.Utilities.SerializeToBinary(this));
        }

        public static EngineSettings Decode(string text)
        {
            return MyAPIGateway.Utilities.SerializeFromBinary<EngineSettings>(Convert.FromBase64String(text));
        }
    }
}
