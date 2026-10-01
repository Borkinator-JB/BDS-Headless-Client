using Bds.Core.NetherNet;
using Bds.Core.RakNet;

namespace Bds.Core.Tests;

public class TransportTests
{
    [Fact]
    public void Segmenter_Splits_And_Joins()
    {
        var packet = new byte[25_000];
        Random.Shared.NextBytes(packet);
        var segments = Segmenter.Split(packet);
        Assert.Equal(3, segments.Count);
        Assert.Equal(2, segments[0][0]);

        var s = new Segmenter();
        Assert.Null(s.Push(segments[0]));
        Assert.Null(s.Push(segments[1]));
        Assert.Equal(packet, s.Push(segments[2]));
    }

    [Fact]
    public void Segmenter_Single_Message()
    {
        var s = new Segmenter();
        Assert.Equal(new byte[] { 9, 8 }, s.Push([0, 9, 8]));
    }

    [Fact]
    public void Pong_Parses()
    {
        var pong = ServerPong.Parse("MCPE;My Server;800;1.21.80;3;10;123;Bedrock level;Survival;1;19132;19133;");
        Assert.Equal("My Server", pong.Motd);
        Assert.Equal(800, pong.Protocol);
        Assert.Equal("1.21.80", pong.Version);
        Assert.Equal(3, pong.Players);
        Assert.Equal(10, pong.MaxPlayers);
        Assert.Equal("Bedrock level", pong.LevelName);
    }

    [Fact]
    public void PmsgId_Is_Read_From_McToken()
    {
        static string Part(string json) => Bds.Core.Util.Base64Url.Encode(System.Text.Encoding.UTF8.GetBytes(json));
        var token = $"MCToken {Part("{\"alg\":\"none\"}")}.{Part("{\"pmid\":\"abc-123\"}")}.sig";
        Assert.Equal("abc-123", SignalingClient.ReadPmsgId(token));
        Assert.Null(SignalingClient.ReadPmsgId("MCToken nope"));
    }

    [Fact]
    public void Signal_Parses_And_Formats()
    {
        var s = Signal.Parse("123", "CONNECTREQUEST 99 v=0\r\no=- 1 2 IN IP4 127.0.0.1");
        Assert.NotNull(s);
        Assert.Equal(Signal.ConnectRequest, s.Type);
        Assert.Equal(99UL, s.ConnectionId);
        Assert.StartsWith("v=0", s.Data);
        Assert.Equal("CONNECTREQUEST 99 v=0\r\no=- 1 2 IN IP4 127.0.0.1", s.ToString());
    }
}
