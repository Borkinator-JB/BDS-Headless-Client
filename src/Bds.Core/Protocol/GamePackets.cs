namespace Bds.Core.Protocol;

/// <summary>
/// Encode/decode for the handful of packets the gateway uses. Layouts follow CloudburstMC Protocol's
/// v2193 codec; values follow MCXboxBroadcast's RedirectPacketHandler.
/// </summary>
public static class GamePackets
{
    // Fields that changed between versions. Bump these when Mojang changes a layout.
    public const int ProtocolTransferReload = 729;
    public const int ProtocolDisconnectFiltered = 712;
    public const int ProtocolTransferGatherings = 2168;

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
            if (protocol >= ProtocolTransferGatherings) w.Bool(false);
        });

    public static byte[] ResourcePacksInfo() =>
        Packet.Encode(PacketId.ResourcePacksInfo, w => w
            .Bool(false).Bool(false).Bool(false)
            .Bool(true) // vibrant visuals force disabled
            .Uuid(Guid.Empty).String("")
            .VarUInt(0));

    public static byte[] ResourcePackStack() =>
        Packet.Encode(PacketId.ResourcePackStack, w => w
            .Bool(false).VarUInt(0).String("*")
            .Int32LE(0) // experiments
            .Bool(false).Bool(false));

    /// <summary>0 refused, 1 send packs, 2 have all packs, 3 completed.</summary>
    public static int ReadResourcePackResponse(Packet p) => (int)p.Reader().VarUInt();

    public static byte[] JigsawStructureData() =>
        Packet.Encode(PacketId.JigsawStructureData, w =>
        {
            // Network NBT: compound with four empty lists.
            w.Byte(10).String("");
            foreach (var name in (string[])["processors", "template_pools", "jigsaws", "structure_sets"])
                w.Byte(9).String(name).Byte(0).VarInt(0);
            w.Byte(0);
        });

    public static byte[] VoxelShapes() =>
        Packet.Encode(PacketId.VoxelShapes, w => w.VarUInt(0).VarUInt(0).UInt16LE(0));

    /// <summary>Minimal creative world in the end, only so the client accepts the Transfer that follows.</summary>
    public static byte[] StartGame(string levelName) =>
        Packet.Encode(PacketId.StartGame, w =>
        {
            w.VarLong(1).VarUInt(1).VarInt(1); // entity ids, creative
            w.FloatLE(0).FloatLE(66).FloatLE(0).FloatLE(1).FloatLE(1);

            // Level settings
            w.Int64LE(0).UInt16LE(0).String("").VarInt(2).VarInt(1).VarInt(1); // seed, biome, dimension, generator, game type
            w.Bool(false).VarInt(0); // hardcore, difficulty
            w.VarInt(0).VarInt(0).VarInt(0); // default spawn
            w.Bool(true); // achievements disabled
            w.VarInt(0).Bool(false).Bool(false).VarInt(0); // editor world type, created/exported in editor, day cycle stop
            w.VarUInt(0).Bool(false).String(""); // edu offers, features, production id
            w.FloatLE(0).FloatLE(0); // rain, lightning
            w.Bool(false).Bool(true).Bool(true); // platform locked content, multiplayer, broadcast to LAN
            w.VarInt(4).VarInt(4); // xbl and platform broadcast: public
            w.Bool(true).Bool(false); // commands, texture packs required
            w.VarUInt(1).String("showcoordinates").Bool(false).VarUInt(1).Bool(false);
            w.Int32LE(0).Bool(false); // experiments, previously toggled
            w.Bool(false).Bool(false).Byte(0).Int32LE(4); // bonus chest, map, permission visitor, chunk tick range
            for (var i = 0; i < 10; i++) w.Bool(false); // pack/template locks, v1 villagers, personas, skins, emote chat
            w.String("*").Int32LE(0).Int32LE(0).Bool(false); // vanilla version, limited world, nether type
            w.String("").String(""); // edu shared uri
            w.Bool(false).Byte(0).Bool(false).VarInt(0).Bool(false); // experimental gameplay, chat restriction, editor fields

            w.String("").String(levelName).String("").Bool(false); // level id, name, premium template, trial
            w.VarInt(0).Bool(false); // rewind history, server block breaking
            w.Int64LE(0).VarInt(0).VarUInt(0); // current tick, enchant seed, block properties
            w.String("").Bool(false); // correlation id, server inventories
            w.String(""); // server engine
            w.Byte(10).String("").Byte(0); // player property data: empty NBT compound
            w.Int64LE(0).Uuid(Guid.Empty); // block registry checksum, world template
            w.Bool(false).Bool(false).Bool(false); // client side generation, hashed block ids, server auth sounds
            w.Bool(false); // server join info
            w.String("").String("").String("").String(""); // server, scenario, world, owner id
        });

    public static byte[] Disconnect(string message, int protocol) =>
        Packet.Encode(PacketId.Disconnect, w =>
        {
            w.VarInt(0).Bool(false).String(message);
            if (protocol >= ProtocolDisconnectFiltered) w.String(message);
        });
}
