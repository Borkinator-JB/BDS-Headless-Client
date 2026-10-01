using System.Net;
using System.Net.Sockets;
using System.Text;
using Bds.Core.Protocol;

namespace Bds.Core.RakNet;

/// <summary>RakNet unconnected ping, enough to read a Bedrock server's status.</summary>
public static class RakNetClient
{
    public static async Task<IPEndPoint> ResolveAsync(string host, int port, CancellationToken ct)
    {
        if (IPAddress.TryParse(host, out var ip)) return new IPEndPoint(ip, port);
        var addresses = await Dns.GetHostAddressesAsync(host, ct);
        var pick = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? addresses.First();
        return new IPEndPoint(pick, port);
    }

    public static async Task<ServerPong> PingAsync(string host, int port, TimeSpan timeout, CancellationToken ct)
    {
        var ep = await ResolveAsync(host, port, ct);
        using var udp = new UdpClient(ep.AddressFamily);
        udp.Connect(ep);
        var w = new PacketWriter();
        w.Byte(RakId.UnconnectedPing).Int64BE(Environment.TickCount64).Bytes(RakBinary.Magic).Int64BE(Random.Shared.NextInt64());
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            await udp.SendAsync(w.ToArray(), timeoutCts.Token);
            using var once = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token);
            once.CancelAfter(TimeSpan.FromMilliseconds(timeout.TotalMilliseconds / 3));
            try
            {
                while (true)
                {
                    var res = await udp.ReceiveAsync(once.Token);
                    if (res.Buffer.Length == 0 || res.Buffer[0] != RakId.UnconnectedPong) continue;
                    var r = new PacketReader(res.Buffer);
                    r.Skip(1 + 8 + 8 + 16);
                    var len = r.UInt16BE();
                    return ServerPong.Parse(Encoding.UTF8.GetString(r.Bytes(len)));
                }
            }
            catch (OperationCanceledException) when (!timeoutCts.IsCancellationRequested) { }
        }
        throw new TimeoutException($"No response from {host}:{port}");
    }
}
