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
        LinkRequest = 3,        // client -> server: lay a span Block/Port -> Block2/Port2
        UnlinkRequest = 4,      // client -> server: delete the span on owner Block's Port
        ModeRequest = 5,        // client -> server: span on owner Block's Port to rail (Flag) / prototype
        Spans = 6,              // server -> clients: an owner block's spans
    }

    [ProtoContract]
    public class Packet
    {
        [ProtoMember(1)] public PacketType Type;
        [ProtoMember(2)] public long Block;
        [ProtoMember(3)] public TrackSettings Settings;
        [ProtoMember(4)] public string Port;
        [ProtoMember(5)] public long Block2;
        [ProtoMember(6)] public string Port2;
        [ProtoMember(7)] public bool Flag;
        [ProtoMember(8)] public SpanStore Spans;
    }

    // The mod's network channel. The server decides; clients send requests and apply what the
    // server broadcasts. Spans and settings also travel in the blocks' Storage when a grid streams
    // to a client, so a joining client needs no extra sync.
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

        static void ToServer(Packet p)
        {
            MyAPIGateway.Multiplayer.SendMessageToServer(Channel, MyAPIGateway.Utilities.SerializeToBinary(p));
        }

        static void ToClients(Packet p)
        {
            if (MyAPIGateway.Multiplayer.MultiplayerActive)
                MyAPIGateway.Multiplayer.SendMessageToOthers(Channel, MyAPIGateway.Utilities.SerializeToBinary(p));
        }

        public static void SendSettingsRequest(TrackBlockLogic logic, TrackSettings settings)
        {
            ToServer(new Packet { Type = PacketType.SettingsRequest, Block = logic.Block.EntityId, Settings = settings });
        }

        public static void BroadcastSettings(TrackBlockLogic logic)
        {
            ToClients(new Packet { Type = PacketType.Settings, Block = logic.Block.EntityId, Settings = logic.Settings });
        }

        public static void SendLinkRequest(TrackBlockLogic a, string aPort, TrackBlockLogic b, string bPort)
        {
            ToServer(new Packet { Type = PacketType.LinkRequest, Block = a.Block.EntityId, Port = aPort, Block2 = b.Block.EntityId, Port2 = bPort });
        }

        public static void SendUnlinkRequest(TrackBlockLogic owner, string port)
        {
            ToServer(new Packet { Type = PacketType.UnlinkRequest, Block = owner.Block.EntityId, Port = port });
        }

        public static void SendModeRequest(TrackBlockLogic owner, string port, bool wantRail)
        {
            ToServer(new Packet { Type = PacketType.ModeRequest, Block = owner.Block.EntityId, Port = port, Flag = wantRail });
        }

        public static void BroadcastSpans(TrackBlockLogic owner)
        {
            ToClients(new Packet { Type = PacketType.Spans, Block = owner.Block.EntityId, Spans = owner.SpanStore });
        }

        static void Receive(ushort channel, byte[] data, ulong sender, bool fromServer)
        {
            try
            {
                Packet p = MyAPIGateway.Utilities.SerializeFromBinary<Packet>(data);
                if (p == null)
                    return;
                TrackNetwork network = MagRailSession.Instance.Network;
                TrackBlockLogic logic = network.Find(p.Block);
                if (logic == null)
                    return;
                if (IsServer)
                    ReceiveOnServer(p, logic, network, sender);
                else if (fromServer)
                    ReceiveOnClient(p, logic, network);
            }
            catch (Exception e)
            {
                Log.Error("Net.Receive", e);
            }
        }

        static void ReceiveOnServer(Packet p, TrackBlockLogic logic, TrackNetwork network, ulong sender)
        {
            IMyPlayer player = FindPlayer(sender);
            if (player == null || !logic.Block.HasPlayerAccess(player.IdentityId))
                return;
            switch (p.Type)
            {
                case PacketType.SettingsRequest:
                    if (p.Settings != null)
                        logic.ApplyRequest(p.Settings);
                    break;
                case PacketType.LinkRequest:
                {
                    TrackBlockLogic other = network.Find(p.Block2);
                    if (other == null || !other.Block.HasPlayerAccess(player.IdentityId) || !RailLinker.IsHolding(player.Character)
                        || !logic.PortsKnown || !other.PortsKnown)
                        return;
                    int aPort = logic.PortIndex(p.Port), bPort = other.PortIndex(p.Port2);
                    if (aPort >= 0 && bPort >= 0)
                        network.Link(logic, aPort, other, bPort);
                    break;
                }
                case PacketType.UnlinkRequest:
                case PacketType.ModeRequest:
                {
                    Span s = network.FindOwnedSpan(logic, p.Port);
                    if (s == null || !RailLinker.IsHolding(player.Character)
                        || (s.Partner != null && !s.Partner.Block.HasPlayerAccess(player.IdentityId)))
                        return;
                    if (p.Type == PacketType.UnlinkRequest)
                        network.Unlink(s);
                    else
                        network.SetMode(s, p.Flag);
                    break;
                }
            }
        }

        static void ReceiveOnClient(Packet p, TrackBlockLogic logic, TrackNetwork network)
        {
            switch (p.Type)
            {
                case PacketType.Settings:
                    if (p.Settings != null)
                        logic.SetSettings(p.Settings);
                    break;
                case PacketType.Spans:
                    network.ReplaceOwnerSpans(logic, p.Spans ?? new SpanStore { SelfId = logic.Block.EntityId });
                    break;
            }
        }

        static IMyPlayer FindPlayer(ulong steamId)
        {
            players.Clear();
            MyAPIGateway.Players.GetPlayers(players, pl => pl.SteamUserId == steamId);
            IMyPlayer player = players.Count > 0 ? players[0] : null;
            players.Clear();
            return player;
        }
    }
}
