using System;
using ProtoBuf;
using Sandbox.ModAPI;

namespace MagRail
{
    public enum TrackKind
    {
        Rail,       // Rail Block 1: fixed rail
        FreeRail,   // Free Rail Block: turning head
        Switch,     // Rail Switch: entry + branches L/C/R
    }

    public enum SwitchBranch
    {
        Center = 0,
        Left = 1,
        Right = 2,
    }

    // Player settings of a track block: kept in the block's Storage and sent over the network.
    [ProtoContract]
    public class TrackSettings
    {
        // 2: the Free Rail head on the lift turns about the middle of its ear pivots (RailPointPivot).
        // 3: the models' standard rail point is 0.90 m over the cell bottom (was 1.00): older settings get LegacyOffset
        // added, so their rail stays where it was built.
        public const int CurrentVersion = 3;
        // The old standard over the new one, m.
        public const float LegacyOffset = 0.10f;

        [ProtoMember(1)] public int Version = CurrentVersion;
        // 2 was the Locked flag (test builds of V0.3.0): do not reuse.
        // Rail point height relative to the model's standard placement, m.
        [ProtoMember(3)] public float HeightOffset;
        // Free Rail head, degrees: + yaw = turn right, + pitch = nose up, + roll = bank right.
        [ProtoMember(4)] public float Yaw;
        [ProtoMember(5)] public float Pitch;
        [ProtoMember(6)] public float Roll;
        // 7 was the angle step (test builds of V0.3.0): do not reuse.
        [ProtoMember(8)] public SwitchBranch Branch;
        // Rail Switch placed upside down: the player's left is the model's right. Branch stays the model's;
        // only the terminal and the actions swap left and right (TrackTerminal.Shown).
        [ProtoMember(9)] public bool Flipped;
        // Free Rail saved before version 2: its head turns about the rail point, as it did when its spans were built
        // (built is built). Cleared on the first change of the head or height (only possible with prototypes).
        [ProtoMember(10)] public bool RailPointPivot;

        public TrackSettings Copy()
        {
            return new TrackSettings
            {
                Version = Version, HeightOffset = HeightOffset,
                Yaw = Yaw, Pitch = Pitch, Roll = Roll, Branch = Branch, Flipped = Flipped, RailPointPivot = RailPointPivot,
            };
        }

        // The rail point and head are where they were (everything but the branch).
        public bool SamePlace(TrackSettings other)
        {
            return HeightOffset == other.HeightOffset && Yaw == other.Yaw && Pitch == other.Pitch && Roll == other.Roll
                && RailPointPivot == other.RailPointPivot;
        }

        // A requested change on top of the current settings: a locked block (a rail span touches it)
        // keeps its height and head angles.
        public static TrackSettings Merge(TrackSettings current, TrackSettings requested, bool locked)
        {
            TrackSettings s = requested.Copy();
            s.Version = CurrentVersion;
            s.RailPointPivot = current.RailPointPivot;
            if (locked)
            {
                s.HeightOffset = current.HeightOffset;
                s.Yaw = current.Yaw;
                s.Pitch = current.Pitch;
                s.Roll = current.Roll;
            }
            else if (!s.SamePlace(current))
            {
                s.RailPointPivot = false;
            }
            return s;
        }

        public string Encode()
        {
            return Convert.ToBase64String(MyAPIGateway.Utilities.SerializeToBinary(this));
        }

        public static TrackSettings Decode(string text)
        {
            TrackSettings s = MyAPIGateway.Utilities.SerializeFromBinary<TrackSettings>(Convert.FromBase64String(text));
            if (s.Version < 2)
                s.RailPointPivot = true;
            if (s.Version < 3)
                s.HeightOffset = (float)Math.Round(s.HeightOffset + LegacyOffset, 2);
            s.Version = CurrentVersion;
            return s;
        }
    }
}
