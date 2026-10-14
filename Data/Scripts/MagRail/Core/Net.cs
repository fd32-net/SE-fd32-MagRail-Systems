using System;
using System.Collections.Generic;
using ProtoBuf;
using Sandbox.ModAPI;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRageMath;

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
        EngineSettingsRequest = 7,  // client -> server: a player changed a hover engine's settings
        EngineSettings = 8,         // server -> clients: the engine settings the server applied
        PowerDemand = 9,            // server -> clients: hover engines' power demand (Ids, Values)
        PaintRequest = 10,          // client -> server: paint the span on owner Block's Port: colour mask HSV in Values, Skin
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
        [ProtoMember(9)] public EngineSettings Engine;
        [ProtoMember(10)] public long[] Ids;
        [ProtoMember(11)] public float[] Values;
        [ProtoMember(12)] public string Skin;
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

        public static void SendEngineSettingsRequest(HoverEngineLogic engine, EngineSettings settings)
        {
            ToServer(new Packet { Type = PacketType.EngineSettingsRequest, Block = engine.Block.EntityId, Engine = settings });
        }

        public static void BroadcastEngineSettings(HoverEngineLogic engine)
        {
            ToClients(new Packet { Type = PacketType.EngineSettings, Block = engine.Block.EntityId, Engine = engine.Settings });
        }

        // Server: the engines' power demand, which only the server's drive knows. Clients run their own copy of
        // each grid's power network (power screen, load, battery time; the charge itself comes from the server),
        // which needs the demand too. Changed demands go out 10 times a second in one packet; every 5 s all
        // non-zero ones again, for clients that joined or streamed the grid in since.
        const int PowerTicks = 6, PowerRefreshTicks = 300;
        static readonly List<long> powerIds = new List<long>();
        static readonly List<float> powerValues = new List<float>();

        public static void BroadcastPower(List<HoverEngineLogic> engines, int tick)
        {
            if (tick % PowerTicks != 0 || !MyAPIGateway.Multiplayer.MultiplayerActive)
                return;
            bool refresh = tick % PowerRefreshTicks == 0;
            for (int i = 0; i < engines.Count; i++)
            {
                HoverEngineLogic e = engines[i];
                if (e.Demand == e.SentDemand && !(refresh && e.Demand > 0f))
                    continue;
                e.SentDemand = e.Demand;
                powerIds.Add(e.Block.EntityId);
                powerValues.Add(e.Demand);
            }
            if (powerIds.Count > 0)
                ToClients(new Packet { Type = PacketType.PowerDemand, Ids = powerIds.ToArray(), Values = powerValues.ToArray() });
            powerIds.Clear();
            powerValues.Clear();
        }

        static void ReceivePower(Packet p)
        {
            if (p.Ids == null || p.Values == null)
                return;
            for (int i = 0; i < p.Ids.Length && i < p.Values.Length; i++)
            {
                IMyEntity entity;
                HoverEngineLogic engine = MyAPIGateway.Entities.TryGetEntityById(p.Ids[i], out entity) && entity.GameLogic != null
                    ? entity.GameLogic.GetAs<HoverEngineLogic>() : null;
                if (engine != null)
                    engine.SetDemand(p.Values[i]);
            }
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

        public static void SendPaintRequest(TrackBlockLogic owner, string port, Vector3 hsv, string skin)
        {
            ToServer(new Packet { Type = PacketType.PaintRequest, Block = owner.Block.EntityId, Port = port, Values = new[] { hsv.X, hsv.Y, hsv.Z },
                Skin = skin });
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
                if (p.Type == PacketType.PowerDemand)
                {
                    if (!IsServer && fromServer)
                        ReceivePower(p);
                    return;
                }
                if (p.Type == PacketType.EngineSettingsRequest || p.Type == PacketType.EngineSettings)
                {
                    ReceiveEngine(p, sender, fromServer);
                    return;
                }
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
                case PacketType.PaintRequest:
                {
                    Span s = network.FindOwnedSpan(logic, p.Port);
                    if (s == null || !RailLinker.IsHolding(player.Character)
                        || (s.Partner != null && !s.Partner.Block.HasPlayerAccess(player.IdentityId)))
                        return;
                    // A rail is deleted only as a prototype (the Linker switches it first).
                    if (p.Type == PacketType.UnlinkRequest)
                    {
                        if (!s.Record.WantRail)
                            network.Unlink(s);
                    }
                    else if (p.Type == PacketType.ModeRequest)
                        network.SetMode(s, p.Flag);
                    else if (p.Values != null && p.Values.Length == 3 && s.Record.WantRail)     // prototypes are not painted
                        network.SetPaint(s, new Vector3(p.Values[0], p.Values[1], p.Values[2]), p.Skin ?? "");
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

        static void ReceiveEngine(Packet p, ulong sender, bool fromServer)
        {
            IMyEntity entity;
            HoverEngineLogic engine = MyAPIGateway.Entities.TryGetEntityById(p.Block, out entity) && entity.GameLogic != null
                ? entity.GameLogic.GetAs<HoverEngineLogic>() : null;
            if (engine == null || engine.Settings == null || p.Engine == null)
                return;
            if (IsServer)
            {
                IMyPlayer player = FindPlayer(sender);
                if (p.Type == PacketType.EngineSettingsRequest && player != null && engine.Block.HasPlayerAccess(player.IdentityId))
                    engine.ApplyRequest(p.Engine);
            }
            else if (fromServer && p.Type == PacketType.EngineSettings)
            {
                engine.SetSettings(p.Engine.Clamped());
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
