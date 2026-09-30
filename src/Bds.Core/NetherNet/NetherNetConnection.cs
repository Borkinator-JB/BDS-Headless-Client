using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using SIPSorcery.Net;

namespace Bds.Core.NetherNet;

/// <summary>One incoming WebRTC peer. Carries Bedrock batches over the reliable data channel.</summary>
public sealed class NetherNetConnection : IAsyncDisposable
{
    const string ReliableLabel = "ReliableDataChannel";

    readonly RTCPeerConnection _pc;
    readonly Segmenter _segmenter = new();
    readonly Channel<byte[]> _incoming = Channel.CreateUnbounded<byte[]>();
    readonly TaskCompletionSource _open = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly ILogger _log;
    RTCDataChannel? _reliable;

    public ulong ConnectionId { get; }
    public string RemoteId { get; }
    public ChannelReader<byte[]> Incoming => _incoming.Reader;
    public Task Opened => _open.Task;

    public event Action<string>? LocalCandidate;

    public NetherNetConnection(ulong connectionId, string remoteId, IEnumerable<TurnServer> servers, ILogger log)
    {
        ConnectionId = connectionId;
        RemoteId = remoteId;
        _log = log;
        _pc = new RTCPeerConnection(new RTCConfiguration
        {
            iceServers = servers.SelectMany(s => s.Urls.Select(u => new RTCIceServer
            {
                urls = u,
                username = s.Username,
                credential = s.Password,
            })).ToList(),
        });
        _pc.ondatachannel += OnDataChannel;
        _pc.onicecandidate += c =>
        {
            if (c is not null) LocalCandidate?.Invoke("candidate:" + c.candidate.Replace("candidate:", ""));
        };
        _pc.onconnectionstatechange += state =>
        {
            if (state is RTCPeerConnectionState.failed or RTCPeerConnectionState.closed or RTCPeerConnectionState.disconnected)
                Close();
        };
    }

    public async Task<string> AnswerAsync(string offerSdp)
    {
        var result = _pc.setRemoteDescription(new RTCSessionDescriptionInit { type = RTCSdpType.offer, sdp = offerSdp });
        if (result != SetDescriptionResultEnum.OK) throw new InvalidOperationException($"Bad offer: {result}");
        var answer = _pc.createAnswer(null);
        await _pc.setLocalDescription(answer);
        return answer.sdp;
    }

    public void AddRemoteCandidate(string candidate)
    {
        try
        {
            _pc.addIceCandidate(new RTCIceCandidateInit { candidate = candidate, sdpMid = "0", sdpMLineIndex = 0 });
        }
        catch (Exception e)
        {
            _log.LogDebug("Ignoring ICE candidate: {Message}", e.Message);
        }
    }

    void OnDataChannel(RTCDataChannel dc)
    {
        if (dc.label != ReliableLabel) return;
        _reliable = dc;
        dc.onmessage += (_, _, data) =>
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
        dc.onclose += Close;
        _open.TrySetResult();
    }

    public void Send(byte[] packet)
    {
        if (_reliable is null) throw new InvalidOperationException("Channel not open");
        foreach (var segment in Segmenter.Split(packet)) _reliable.send(segment);
    }

    void Close()
    {
        _open.TrySetException(new IOException("Connection closed"));
        _incoming.Writer.TryComplete();
    }

    public ValueTask DisposeAsync()
    {
        Close();
        _pc.close();
        _pc.Dispose();
        return ValueTask.CompletedTask;
    }
}
