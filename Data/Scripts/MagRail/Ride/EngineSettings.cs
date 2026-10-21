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
        public const float MaxSideOffset = 2.5f;
        public const float LargeHeight = 2.5f, SmallHeight = 1.2f;
        public const float MinGap = 0f, MaxGap = 3f, DefaultGap = 1f;
        // The platform's offset: each model narrows it (EnginePlatform.ReadRest), this only bounds requests.
        public const float MinOffset = 0f, MaxOffset = 9.5f;

        [ProtoMember(1)] public int Version = CurrentVersion;
        // Height of the guide point over the rail point, m.
        [ProtoMember(2)] public float HoverHeight = LargeHeight;
        // Stiffness of the hold (1 = standard): how hard the engine pulls its point back over the rail.
        [ProtoMember(3)] public float HoldStrength = 1;
        // The guide point (the point held over the rail) from the block centre along the block's Right, m.
        // (5 and 6 were the forward and up offsets: removed, old saves just drop them.)
        [ProtoMember(4)] public float SideOffset;
        // Engines with a platform (EnginePlatform): the gap between the rail's top and the platform's bottom, m
        // (0 = the platform lies on the rail). It sets their hover height instead of HoverHeight.
        [ProtoMember(7)] public float PlatformGap = DefaultGap;
        // Engines whose model lets the platform hang lower or higher: its offset from the base (the base's lowest point
        // to the platform's top), m, within the model's range.
        [ProtoMember(8)] public float PlatformOffset;

        public EngineSettings Copy()
        {
            return new EngineSettings
            {
                Version = Version, HoverHeight = HoverHeight, HoldStrength = HoldStrength,
                SideOffset = SideOffset, PlatformGap = PlatformGap, PlatformOffset = PlatformOffset,
            };
        }

        // Within the limits (requests from clients and programmable blocks too).
        public EngineSettings Clamped()
        {
            EngineSettings s = Copy();
            s.Version = CurrentVersion;
            s.HoverHeight = MathHelper.Clamp(s.HoverHeight, MinHeight, MaxHeight);
            s.HoldStrength = MathHelper.Clamp(s.HoldStrength, MinHold, MaxHold);
            s.SideOffset = MathHelper.Clamp(s.SideOffset, -MaxSideOffset, MaxSideOffset);
            s.PlatformGap = MathHelper.Clamp(s.PlatformGap, MinGap, MaxGap);
            s.PlatformOffset = MathHelper.Clamp(s.PlatformOffset, MinOffset, MaxOffset);
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
