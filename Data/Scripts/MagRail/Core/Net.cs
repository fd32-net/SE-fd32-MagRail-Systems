using System;
using System.Collections.Generic;
using ProtoBuf;
using Sandbox.ModAPI;
using VRage.Game.ModAPI;

namespace MagRail
{
    public enum PacketType
    {
        SettingsRequest = 1,    // client -> server: a player changed a track block's settings
        Settings = 2,           // server -> clients: the settings the server applied
    }

    [ProtoContract]
    public class Packet
    {
        [ProtoMember(1)] public PacketType Type;
        [ProtoMember(2)] public long Block;
        [ProtoMember(3)] public TrackSettings Settings;
    }

    // The mod's network channel. The server decides; clients send requests and apply what the
    // server broadcasts.
    public static class Net
    {
        const ushort Channel = 39731;
        static readonly List<IMyPlayer> players = new List<IMyPlayer>();

        public static bool IsServer
        {
            get { return MyAPIGateway.Session.IsServer; }
        }

        public static void Load()
        {
            MyAPIGateway.Multiplayer.RegisterSecureMessageHandler(Channel, Receive);
        }

        public static void Unload()
        {
            MyAPIGateway.Multiplayer.UnregisterSecureMessageHandler(Channel, Receive);
        }

        public static void SendSettingsRequest(TrackBlockLogic logic, TrackSettings settings)
        {
            Packet p = new Packet { Type = PacketType.SettingsRequest, Block = logic.Block.EntityId, Settings = settings };
            MyAPIGateway.Multiplayer.SendMessageToServer(Channel, MyAPIGateway.Utilities.SerializeToBinary(p));
        }

        public static void BroadcastSettings(TrackBlockLogic logic)
        {
            if (!MyAPIGateway.Multiplayer.MultiplayerActive)
                return;
            Packet p = new Packet { Type = PacketType.Settings, Block = logic.Block.EntityId, Settings = logic.Settings };
            MyAPIGateway.Multiplayer.SendMessageToOthers(Channel, MyAPIGateway.Utilities.SerializeToBinary(p));
        }

        static void Receive(ushort channel, byte[] data, ulong sender, bool fromServer)
        {
            try
            {
                Packet p = MyAPIGateway.Utilities.SerializeFromBinary<Packet>(data);
                if (p == null || p.Settings == null)
                    return;
                TrackBlockLogic logic = MagRailSession.Instance.FindTrackBlock(p.Block);
                if (logic == null)
                    return;
                switch (p.Type)
                {
                    case PacketType.SettingsRequest:
                        if (IsServer && HasAccess(logic, sender))
                            logic.ApplyRequest(p.Settings);
                        break;
                    case PacketType.Settings:
                        if (fromServer && !IsServer)
                            logic.SetSettings(p.Settings);
                        break;
                }
            }
            catch (Exception e)
            {
                Log.Error("Net.Receive", e);
            }
        }

        // The same rule as the terminal: the player must have access to the block.
        static bool HasAccess(TrackBlockLogic logic, ulong steamId)
        {
            players.Clear();
            MyAPIGateway.Players.GetPlayers(players, pl => pl.SteamUserId == steamId);
            bool ok = players.Count > 0 && logic.Block.HasPlayerAccess(players[0].IdentityId);
            players.Clear();
            return ok;
        }
    }
}
