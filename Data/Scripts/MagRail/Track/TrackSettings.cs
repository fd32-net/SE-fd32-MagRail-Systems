using System;
using ProtoBuf;
using Sandbox.ModAPI;

namespace MagRail
{
    public enum TrackKind
    {
        Rail,       // Rail Block 1 / 5: fixed rail
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
        public const int CurrentVersion = 1;

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

        public TrackSettings Copy()
        {
            return new TrackSettings
            {
                Version = Version, HeightOffset = HeightOffset,
                Yaw = Yaw, Pitch = Pitch, Roll = Roll, Branch = Branch,
            };
        }

        // A requested change on top of the current settings: a locked block (a rail span touches it)
        // keeps its height and head angles.
        public static TrackSettings Merge(TrackSettings current, TrackSettings requested, bool locked)
        {
            TrackSettings s = requested.Copy();
            s.Version = CurrentVersion;
            if (locked)
            {
                s.HeightOffset = current.HeightOffset;
                s.Yaw = current.Yaw;
                s.Pitch = current.Pitch;
                s.Roll = current.Roll;
            }
            return s;
        }

        public string Encode()
        {
            return Convert.ToBase64String(MyAPIGateway.Utilities.SerializeToBinary(this));
        }

        public static TrackSettings Decode(string text)
        {
            return MyAPIGateway.Utilities.SerializeFromBinary<TrackSettings>(Convert.FromBase64String(text));
        }
    }
}
