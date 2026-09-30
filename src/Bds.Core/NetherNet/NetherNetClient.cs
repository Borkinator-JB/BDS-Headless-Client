using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using Bds.Core.Protocol;
using Bds.Core.RakNet;
using Bds.Core.Util;
using Microsoft.Extensions.Logging;
using SIPSorcery.Net;

namespace Bds.Core.NetherNet;

/// <summary>
/// Client side of a server running <c>transport=nethernet</c>. The server answers
/// <c>GET http://host:port/v1/join</c> with its status, and the SDP offer is POSTed to
/// <c>/v1/join/{networkId}</c> with the SDP answer as the response body.
/// Game batches then flow over the reliable data channel.
/// </summary>
public sealed class NetherNetClient : IGameTransport
{
    const string ReliableLabel = "ReliableDataChannel";
    const string UnreliableLabel = "UnreliableDataChannel";
    const string DefaultIssuer = "https://authorization.franchise.minecraft-services.net";

    readonly RTCPeerConnection _pc = new(new RTCConfiguration());
    readonly Segmenter _segmenter = new();
    readonly Channel<byte[]> _incoming = Channel.CreateUnbounded<byte[]>();
    readonly Lock _sendLock = new();
    readonly ILogger _log;
    RTCDataChannel? _reliable;
    int _closed;

    NetherNetClient(ILogger log) => _log = log;

    public ChannelReader<byte[]> Incoming => _incoming.Reader;
    public string? DisconnectReason { get; private set; }
    public bool Encrypted => false;

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

    public static async Task<NetherNetClient> ConnectAsync(string host, int port, ECDsa identity, string multiplayerToken,
        ILogger log, CancellationToken ct)
    {
        var client = new NetherNetClient(log);
        try
        {
            await client.NegotiateAsync(await RakNetClient.ResolveAsync(host, port, ct), identity, multiplayerToken, ct);
            return client;
        }
        catch
        {
            await client.DisposeAsync();
            throw;
        }
    }

    /// <summary>NetherNet clients report the server as <c>http://host:port/v1:port</c> in the Login client data.</summary>
    public static string ServerAddress(IPEndPoint server) => $"{JoinUrl(server).Replace("/v1/join", "")}:{server.Port}";

    public static string JoinUrl(IPEndPoint server)
    {
        var host = server.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{server.Address}]" : server.Address.ToString();
        return $"http://{host}:{server.Port}/v1/join";
    }

    async Task NegotiateAsync(IPEndPoint server, ECDsa identity, string multiplayerToken, CancellationToken ct)
    {
        _reliable = await _pc.createDataChannel(ReliableLabel, new RTCDataChannelInit { ordered = true });
        await _pc.createDataChannel(UnreliableLabel, new RTCDataChannelInit { ordered = false, maxRetransmits = 0 });

        var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _reliable.onopen += () => opened.TrySetResult();
        _reliable.onmessage += (_, _, data) =>
        {
            try
            {
                if (_segmenter.Push(data) is { } packet) _incoming.Writer.TryWrite(packet);
            }
            catch (InvalidDataException e)
            {
                _log.LogDebug("Bad NetherNet message: {Message}", e.Message);
            }
        };
        _reliable.onclose += () => Close("Server closed the connection");
        _pc.onconnectionstatechange += state =>
        {
            if (state is RTCPeerConnectionState.failed or RTCPeerConnectionState.closed or RTCPeerConnectionState.disconnected)
            {
                opened.TrySetException(new IOException($"WebRTC connection {state}"));
                Close($"WebRTC connection {state}");
            }
        };

        var offer = _pc.createOffer();
        await _pc.setLocalDescription(offer);
        var issuer = Jwt.DecodeUnverified(multiplayerToken).Payload["iss"]?.GetValue<string>() ?? DefaultIssuer;
        var offerSdp = IdentityAssertion.Add(_pc.localDescription.sdp.ToString(), identity, multiplayerToken, issuer);

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        using var content = new StringContent(offerSdp, Encoding.UTF8, "application/sdp");
        var networkId = (ulong)Random.Shared.NextInt64(1, long.MaxValue);
        using var res = await http.PostAsync($"{JoinUrl(server)}/{networkId}", content, ct);
        var answer = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
            throw new IOException($"NetherNet signaling failed ({(int)res.StatusCode}): {Trim(answer)}");
        if (!answer.StartsWith("v=0", StringComparison.Ordinal))
            throw new IOException($"NetherNet signaling returned no SDP answer: {Trim(answer)}");

        var result = _pc.setRemoteDescription(new RTCSessionDescriptionInit { type = RTCSdpType.answer, sdp = answer });
        if (result != SetDescriptionResultEnum.OK) throw new IOException($"Server's SDP answer was rejected: {result}");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            await opened.Task.WaitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"NetherNet data channel did not open (ICE {_pc.iceConnectionState})");
        }
    }

    static string Trim(string text) => text.Length == 0 ? "no details" : text.Length > 300 ? text[..300] : text;

    public void Send(byte[] gamePayload)
    {
        if (Volatile.Read(ref _closed) != 0 || _reliable is null) return;
        lock (_sendLock)
            foreach (var segment in Segmenter.Split(gamePayload)) _reliable.send(segment);
    }

    void Close(string reason)
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        DisconnectReason = reason;
        _incoming.Writer.TryComplete();
    }

    public ValueTask DisposeAsync()
    {
        Close("Closed");
        _pc.close();
        _pc.Dispose();
        return ValueTask.CompletedTask;
    }
}
