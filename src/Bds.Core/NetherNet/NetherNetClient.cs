using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using Bds.Core.RakNet;

namespace Bds.Core.NetherNet;

/// <summary>
/// Status of a server running <c>transport=nethernet</c>, from <c>GET http://host:port/v1/join</c>.
/// </summary>
public static class NetherNetClient
{
    sealed record Status(string? Name, int Protocol, string? Version, string? Level, int Players, int MaxPlayers, int GameType);

    /// <summary>NetherNet's replacement for the RakNet ping.</summary>
    public static async Task<ServerPong> PingAsync(string host, int port, TimeSpan timeout, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = timeout };
        var status = await http.GetFromJsonAsync<Status>(JoinUrl(await RakNetClient.ResolveAsync(host, port, ct)), ct)
            ?? throw new IOException("Empty NetherNet status");
        return new ServerPong(status.Name ?? "", status.Protocol, status.Version ?? "", status.Players, status.MaxPlayers,
            status.Level ?? "", status.GameType.ToString());
    }

    static string JoinUrl(IPEndPoint server)
    {
        var host = server.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{server.Address}]" : server.Address.ToString();
        return $"http://{host}:{server.Port}/v1/join";
    }
}
