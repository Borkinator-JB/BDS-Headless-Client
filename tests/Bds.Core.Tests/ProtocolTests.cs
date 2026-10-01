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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Batch_RoundTrips(bool compress)
    {
        var a = new BatchCodec();
        var b = new BatchCodec();
        if (compress)
        {
            a.EnableCompression(CompressionAlgorithm.Zlib, 1);
            b.EnableCompression(CompressionAlgorithm.Zlib, 1);
        }

        var packets = b.Decode(a.Encode([GamePackets.PlayStatusPacket(PlayStatus.LoginSuccess), GamePackets.Transfer("a.example", 19132, 800)]));

        Assert.Equal(2, packets.Count);
        Assert.Equal(PacketId.PlayStatus, packets[0].Id);
        Assert.Equal((int)PlayStatus.LoginSuccess, packets[0].Reader().Int32BE());
        Assert.Equal(PacketId.Transfer, packets[1].Id);
        Assert.Equal("a.example", packets[1].Reader().String());
    }

    [Fact]
    public void StartGame_Matches_v2193_Layout()
    {
        // 194 bytes with "MCXboxBroadcast" (15 chars) as traced from CloudburstMC's v2193 serializers.
        var p = Packet.Decode(GamePackets.StartGame("MCXboxBroadcast"));
        Assert.Equal(PacketId.StartGame, p.Id);
        Assert.Equal(194, p.Body.Length);
        Assert.Equal(66f, new PacketReader(p.Body[7..]).FloatLE());
    }

    [Fact]
    public void Transfer_Has_Gatherings_Flag_On_New_Protocols()
    {
        Assert.Equal(1 + 9 + 2 + 2, Packet.Decode(GamePackets.Transfer("a.example", 19132, 2193)).Body.Length);
        Assert.Equal(1 + 9 + 2 + 1, Packet.Decode(GamePackets.Transfer("a.example", 19132, 800)).Body.Length);
    }

    [Fact]
    public void Login_Is_Read()
    {
        var packet = Packet.Decode(Packet.Encode(PacketId.Login, w =>
        {
            var payload = new PacketWriter();
            payload.Int32LE(12).Bytes("{\"chain\":[]}"u8.ToArray()).Int32LE(5).Bytes("a.b.c"u8.ToArray());
            w.Int32BE(800).ByteArray(payload.Span);
        }));
        var (protocol, identity, clientData) = GamePackets.ReadLogin(packet);
        Assert.Equal(800, protocol);
        Assert.Equal("{\"chain\":[]}", identity);
        Assert.Equal("a.b.c", clientData);
    }

    [Fact]
    public void Identity_Is_Read_From_Chain_And_Token()
    {
        static string Part(string json) => Bds.Core.Util.Base64Url.Encode(System.Text.Encoding.UTF8.GetBytes(json));
        var chainJwt = $"{Part("{}")}.{Part("{\"extraData\":{\"displayName\":\"Bot\",\"XUID\":\"42\"}}")}.sig";
        var tokenJwt = $"{Part("{}")}.{Part("{\"xname\":\"Bot\",\"xid\":\"42\"}")}.sig";
        var chain = $"{{\"chain\":[\"{chainJwt}\"]}}";

        Assert.Equal(("Bot", "42"), LoginBuilder.ReadIdentity(chain));
        Assert.Equal(("Bot", "42"), LoginBuilder.ReadIdentity(
            new System.Text.Json.Nodes.JsonObject { ["Certificate"] = chain, ["Token"] = "" }.ToJsonString()));
        Assert.Equal(("Bot", "42"), LoginBuilder.ReadIdentity(
            new System.Text.Json.Nodes.JsonObject { ["Certificate"] = "{\"chain\":[]}", ["Token"] = tokenJwt }.ToJsonString()));
        Assert.Equal((null, null), LoginBuilder.ReadIdentity("not json"));
    }
}
