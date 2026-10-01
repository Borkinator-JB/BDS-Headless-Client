namespace Bds.Core.Protocol;

/// <summary>Encode/decode for the handful of packets the gateway uses.</summary>
public static class GamePackets
{
    // Fields that changed between versions. Bump these when Mojang changes a layout.
    public const int ProtocolTransferReload = 729;
    public const int ProtocolDisconnectFiltered = 712;

    public static int ReadRequestNetworkSettings(Packet p) => p.Reader().Int32BE();

    public static byte[] NetworkSettingsPacket(int threshold, CompressionAlgorithm algorithm) =>
        Packet.Encode(PacketId.NetworkSettings, w => w
            .UInt16LE((ushort)threshold)
            .UInt16LE((ushort)algorithm)
            .Bool(false).Byte(0).FloatLE(0));

    public static (int Protocol, string Identity, string ClientData) ReadLogin(Packet p)
    {
        var r = p.Reader();
        var protocol = r.Int32BE();
        var payload = new PacketReader(r.ByteArray());
        var identity = System.Text.Encoding.UTF8.GetString(payload.Bytes(payload.Int32LE()));
        var clientData = System.Text.Encoding.UTF8.GetString(payload.Bytes(payload.Int32LE()));
        return (protocol, identity, clientData);
    }

    public static byte[] PlayStatusPacket(PlayStatus status) =>
        Packet.Encode(PacketId.PlayStatus, w => w.Int32BE((int)status));

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
}
