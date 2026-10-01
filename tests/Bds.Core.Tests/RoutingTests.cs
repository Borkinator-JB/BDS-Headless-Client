using Bds.Core.Services;
using Bds.Core.Storage;

namespace Bds.Core.Tests;

public class RoutingTests
{
    static readonly ServerTarget Default = new("Default", "default.example", 19132, "default.example", 19132);
    static readonly ServerTarget Other = new("Other", "other.example", 19133, "other.example", 19133);
    static readonly Dictionary<int, ServerTarget> Servers = new() { [2] = Other };

    [Fact]
    public void Route_Found() =>
        Assert.Same(Other, FriendRoutes.Resolve("123", new Dictionary<string, int> { ["123"] = 2 }, Servers, Default));

    [Fact]
    public void No_Route_Uses_Default() =>
        Assert.Same(Default, FriendRoutes.Resolve("123", new Dictionary<string, int>(), Servers, Default));

    [Fact]
    public void Deleted_Server_Uses_Default() =>
        Assert.Same(Default, FriendRoutes.Resolve("123", new Dictionary<string, int> { ["123"] = 9 }, Servers, Default));

    [Fact]
    public void Null_Xuid_Uses_Default() =>
        Assert.Same(Default, FriendRoutes.Resolve(null, new Dictionary<string, int> { ["123"] = 2 }, Servers, Default));

    [Fact]
    public void Target_Uses_Public_Address()
    {
        var t = ServerTarget.From(new ServerEntry { Name = "A", Host = "127.0.0.1", Port = 19132, PublicHost = "play.example", PublicPort = 19200 });
        Assert.Equal(("127.0.0.1", 19132, "play.example", 19200), (t.Host, t.Port, t.TransferHost, t.TransferPort));
    }
}
