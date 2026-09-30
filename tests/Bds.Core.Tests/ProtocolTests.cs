using System.Security.Cryptography;
using Bds.Core.Protocol;

namespace Bds.Core.Tests;

public class ProtocolTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(-1)]
    [InlineData(int.MaxValue)]
    [InlineData(int.MinValue)]
    public void VarInt_RoundTrips(int value)
    {
        var w = new PacketWriter().VarInt(value).VarLong(value * 3L);
        var r = new PacketReader(w.ToArray());
        Assert.Equal(value, r.VarInt());
        Assert.Equal(value * 3L, r.VarLong());
    }

    [Fact]
    public void String_And_Uuid_RoundTrip()
    {
        var id = Guid.NewGuid();
        var w = new PacketWriter().String("héllo").Uuid(id).UInt24LE(0x123456);
        var r = new PacketReader(w.ToArray());
        Assert.Equal("héllo", r.String());
        Assert.Equal(id, r.Uuid());
        Assert.Equal(0x123456, r.UInt24LE());
    }

    [Fact]
    public void Cipher_RoundTrips_Across_Packets()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        using var enc = new BedrockCipher(key);
        using var dec = new BedrockCipher(key);
        for (var i = 0; i < 5; i++)
        {
            var data = RandomNumberGenerator.GetBytes(37 * (i + 1));
            Assert.Equal(data, dec.Decrypt(enc.Encrypt(data)));
        }
    }

    [Fact]
    public void Cipher_Rejects_Tampered_Data()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        using var enc = new BedrockCipher(key);
        using var dec = new BedrockCipher(key);
        var bytes = enc.Encrypt([1, 2, 3, 4]);
        bytes[0] ^= 1;
        Assert.Throws<InvalidDataException>(() => dec.Decrypt(bytes));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Batch_RoundTrips(bool compress, bool encrypt)
    {
        using var a = new BatchCodec();
        using var b = new BatchCodec();
        if (compress)
        {
            a.EnableCompression(CompressionAlgorithm.Zlib, 1);
            b.EnableCompression(CompressionAlgorithm.Zlib, 1);
        }
        if (encrypt)
        {
            var key = RandomNumberGenerator.GetBytes(32);
            a.EnableEncryption(key);
            b.EnableEncryption(key);
        }

        var packets = b.Decode(a.Encode([GamePackets.PlayStatusPacket(PlayStatus.PlayerSpawn), GamePackets.RequestChunkRadius(8)]));

        Assert.Equal(2, packets.Count);
        Assert.Equal(PacketId.PlayStatus, packets[0].Id);
        Assert.Equal(PlayStatus.PlayerSpawn, GamePackets.ReadPlayStatus(packets[0]));
        Assert.Equal(PacketId.RequestChunkRadius, packets[1].Id);
    }

    [Fact]
    public void Login_RoundTrips()
    {
        var packet = Packet.Decode(GamePackets.Login(800, "{\"chain\":[]}", "a.b.c"));
        var (protocol, identity, clientData) = GamePackets.ReadLogin(packet);
        Assert.Equal(800, protocol);
        Assert.Equal("{\"chain\":[]}", identity);
        Assert.Equal("a.b.c", clientData);
    }

    [Fact]
    public void PlayerList_Reads_Names()
    {
        var uuid = Guid.NewGuid();
        var body = Packet.Encode(PacketId.PlayerList, w =>
        {
            w.Byte(0).VarUInt(1).Uuid(uuid).VarLong(5).String("Steve").String("123").String("").Int32LE(7);
            w.String("skin").String("").String("{}");
            WriteImage(w, 64, 64);
            w.Int32LE(1);
            WriteImage(w, 32, 32);
            w.Int32LE(0).FloatLE(1).Int32LE(0);
            WriteImage(w, 0, 0);
            for (var i = 0; i < 7; i++) w.String("");
            w.Int32LE(1).String("p").String("t").String("pk").Bool(true).String("prod");
            w.Int32LE(1).String("t").Int32LE(2).String("#fff").String("#000");
            for (var i = 0; i < 5; i++) w.Bool(false);
            w.Bool(false).Bool(false).Bool(false);
            w.Int32LE(-1);
            w.Bool(true);
        });

        var list = GamePackets.ReadPlayerList(Packet.Decode(body), GamePackets.ProtocolPlayerColor);

        var entry = Assert.Single(list.Entries);
        Assert.True(list.Add);
        Assert.Equal(uuid, entry.Uuid);
        Assert.Equal("Steve", entry.Name);
        Assert.Equal("123", entry.Xuid);
    }

    [Fact]
    public void PlayerList_Reads_126_Layout()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        // Layout captured from a 1.26.52 (protocol 2193) server.
        void Entry(PacketWriter w, Guid uuid, string name)
        {
            w.Uuid(uuid).VarLong(5).String(name).String("2535400000000000").String("").Int32LE(1);
            w.String("Standard_Custom").String("D660D7EAE7CC1F7B").String("{\"geometry\":{}}");
            WriteImage(w, 64, 64);
            w.VarUInt(0);
            WriteImage(w, 0, 0);
            w.String("null\n").String("0.0.0").String("").String("CAPEID").String("Standard_CustomCAPEID");
            w.Byte(0).Int32LE(0x00abcdef);
            w.VarUInt(0).VarUInt(0);
            for (var i = 0; i < 5; i++) w.Bool(false);
            w.String("false").Bytes([0, 0, 0, 0]).Int32LE(unchecked((int)0xffededed));
        }
        var body = Packet.Encode(PacketId.PlayerList, w =>
        {
            w.Byte(1).VarUInt(2);
            Entry(w, a, "Alex");
            Entry(w, b, "Steve");
        });

        var list = GamePackets.ReadPlayerList(Packet.Decode(body), 2193);

        Assert.True(list.Add);
        Assert.Equal(["Alex", "Steve"], list.Entries.Select(e => e.Name));
        Assert.Equal(b, list.Entries[1].Uuid);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void PlayerList_Detects_Remove(byte action)
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var body = Packet.Encode(PacketId.PlayerList, w => w.Byte(action).VarUInt(2).Uuid(a).Uuid(b));

        var list = GamePackets.ReadPlayerList(Packet.Decode(body), 2193);

        Assert.False(list.Add);
        Assert.Equal([a, b], list.Entries.Select(e => e.Uuid));
    }

    [Fact]
    public void ResourcePackResponse_Uses_Named_Layout_On_New_Protocols()
    {
        var r = Packet.Decode(GamePackets.ResourcePackResponse(true, 2193)).Reader();
        Assert.Equal(3ul, r.VarUInt());
        Assert.Equal("resourcepackstackfinished", r.String());

        var old = Packet.Decode(GamePackets.ResourcePackResponse(true, 800)).Reader();
        Assert.Equal(4, old.Byte());
        Assert.Equal(0, old.UInt16LE());
    }

    static void WriteImage(PacketWriter w, int width, int height) =>
        w.Int32LE(width).Int32LE(height).ByteArray(new byte[width * height * 4]);
}
