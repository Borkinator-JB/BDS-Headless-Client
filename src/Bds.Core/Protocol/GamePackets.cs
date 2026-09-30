namespace Bds.Core.Protocol;

public sealed record NetworkSettings(int Threshold, CompressionAlgorithm Algorithm);

public sealed record PlayerListEntry(Guid Uuid, string Name, string Xuid);

public sealed record PlayerListUpdate(bool Add, List<PlayerListEntry> Entries);

/// <summary>Encode/decode for the handful of packets this client uses.</summary>
public static class GamePackets
{
    // Fields that changed between versions. Bump these when Mojang changes a layout.
    public const int ProtocolPlayerColor = 766;
    public const int ProtocolTransferReload = 729;
    public const int ProtocolDisconnectFiltered = 712;

    public static byte[] RequestNetworkSettings(int protocol) =>
        Packet.Encode(PacketId.RequestNetworkSettings, w => w.Int32BE(protocol));

    public static int ReadRequestNetworkSettings(Packet p) => p.Reader().Int32BE();

    public static byte[] NetworkSettingsPacket(int threshold, CompressionAlgorithm algorithm) =>
        Packet.Encode(PacketId.NetworkSettings, w => w
            .UInt16LE((ushort)threshold)
            .UInt16LE((ushort)algorithm)
            .Bool(false).Byte(0).FloatLE(0));

    public static NetworkSettings ReadNetworkSettings(Packet p)
    {
        var r = p.Reader();
        return new NetworkSettings(r.UInt16LE(), (CompressionAlgorithm)r.UInt16LE());
    }

    public static byte[] Login(int protocol, string identity, string clientData) =>
        Packet.Encode(PacketId.Login, w =>
        {
            var payload = new PacketWriter();
            var id = System.Text.Encoding.UTF8.GetBytes(identity);
            var cd = System.Text.Encoding.UTF8.GetBytes(clientData);
            payload.Int32LE(id.Length).Bytes(id).Int32LE(cd.Length).Bytes(cd);
            w.Int32BE(protocol).ByteArray(payload.Span);
        });

    public static (int Protocol, string Identity, string ClientData) ReadLogin(Packet p)
    {
        var r = p.Reader();
        var protocol = r.Int32BE();
        var payload = new PacketReader(r.ByteArray());
        var identity = System.Text.Encoding.UTF8.GetString(payload.Bytes(payload.Int32LE()));
        var clientData = System.Text.Encoding.UTF8.GetString(payload.Bytes(payload.Int32LE()));
        return (protocol, identity, clientData);
    }

    public static string ReadServerToClientHandshake(Packet p) => p.Reader().String();

    public static byte[] ClientToServerHandshake() => Packet.Encode(PacketId.ClientToServerHandshake);

    public static byte[] PlayStatusPacket(PlayStatus status) =>
        Packet.Encode(PacketId.PlayStatus, w => w.Int32BE((int)status));

    public static PlayStatus ReadPlayStatus(Packet p) => (PlayStatus)p.Reader().Int32BE();

    public static byte[] ResourcePackResponse(byte status) =>
        Packet.Encode(PacketId.ResourcePackClientResponse, w => w.Byte(status).UInt16LE(0));

    public const byte ResourcePackHaveAll = 3;
    public const byte ResourcePackCompleted = 4;

    public static (long UniqueId, ulong RuntimeId) ReadStartGame(Packet p)
    {
        var r = p.Reader();
        return (r.VarLong(), r.VarUInt());
    }

    public static byte[] RequestChunkRadius(int radius) =>
        Packet.Encode(PacketId.RequestChunkRadius, w => w.VarInt(radius).Byte((byte)radius));

    public static byte[] SetLocalPlayerAsInitialized(ulong runtimeId) =>
        Packet.Encode(PacketId.SetLocalPlayerAsInitialized, w => w.VarUInt(runtimeId));

    public static byte[]? NetworkStackLatencyReply(Packet p)
    {
        var r = p.Reader();
        var timestamp = r.Int64LE();
        var needsResponse = r.Bool();
        return needsResponse
            ? Packet.Encode(PacketId.NetworkStackLatency, w => w.Int64LE(timestamp).Bool(false))
            : null;
    }

    public static byte[] Transfer(string address, ushort port, int protocol) =>
        Packet.Encode(PacketId.Transfer, w =>
        {
            w.String(address).UInt16LE(port);
            if (protocol >= ProtocolTransferReload) w.Bool(false);
        });

    public static byte[] Disconnect(string message, int protocol) =>
        Packet.Encode(PacketId.Disconnect, w =>
        {
            w.VarInt(0).Bool(false).String(message);
            if (protocol >= ProtocolDisconnectFiltered) w.String(message);
        });

    public static string ReadDisconnect(Packet p)
    {
        try
        {
            var r = p.Reader();
            r.VarInt();
            return r.Bool() ? "Disconnected" : r.String();
        }
        catch (EndOfStreamException)
        {
            return "Disconnected";
        }
    }

    public static PlayerListUpdate ReadPlayerList(Packet p, int protocol)
    {
        var r = p.Reader();
        var add = r.Byte() == 0;
        var count = checked((int)r.VarUInt());
        var entries = new List<PlayerListEntry>(count);
        for (var i = 0; i < count; i++)
        {
            var uuid = r.Uuid();
            if (!add)
            {
                entries.Add(new PlayerListEntry(uuid, "", ""));
                continue;
            }
            r.VarLong();
            var name = r.String();
            var xuid = r.String();
            r.String();
            r.Int32LE();
            SkipSkin(r);
            r.Bool();
            r.Bool();
            r.Bool();
            if (protocol >= ProtocolPlayerColor) r.Int32LE();
            entries.Add(new PlayerListEntry(uuid, name, xuid));
        }
        return new PlayerListUpdate(add, entries);
    }

    static void SkipSkin(PacketReader r)
    {
        r.String();
        r.String();
        r.String();
        SkipImage(r);
        var animations = r.UInt32LE();
        for (var i = 0; i < animations; i++)
        {
            SkipImage(r);
            r.UInt32LE();
            r.FloatLE();
            r.UInt32LE();
        }
        SkipImage(r);
        for (var i = 0; i < 7; i++) r.String();
        var pieces = r.UInt32LE();
        for (var i = 0; i < pieces; i++)
        {
            r.String(); r.String(); r.String(); r.Bool(); r.String();
        }
        var tints = r.UInt32LE();
        for (var i = 0; i < tints; i++)
        {
            r.String();
            var colors = r.UInt32LE();
            for (var c = 0; c < colors; c++) r.String();
        }
        for (var i = 0; i < 5; i++) r.Bool();
    }

    static void SkipImage(PacketReader r)
    {
        r.UInt32LE();
        r.UInt32LE();
        r.ByteArray();
    }
}
