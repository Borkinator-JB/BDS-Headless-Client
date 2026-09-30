using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using Bds.Core.Protocol;

namespace Bds.Core.RakNet;

/// <summary>Minimal RakNet client: enough to talk to Bedrock servers.</summary>
public sealed class RakNetClient : IAsyncDisposable
{
    static readonly int[] MtuSizes = [1492, 1200, 576];
    static readonly TimeSpan ResendAfter = TimeSpan.FromMilliseconds(800);
    static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(15);

    readonly UdpClient _udp;
    readonly IPEndPoint _remote;
    readonly long _guid = Random.Shared.NextInt64();
    readonly Stopwatch _clock = Stopwatch.StartNew();
    readonly CancellationTokenSource _cts = new();
    readonly Channel<byte[]> _incoming = Channel.CreateUnbounded<byte[]>();
    readonly Lock _sendLock = new();
    readonly ConcurrentQueue<int> _pendingAcks = new();
    readonly Dictionary<int, (Frame[] Frames, long SentAt)> _unacked = [];
    readonly HashSet<int> _receivedReliable = [];
    readonly Dictionary<ushort, byte[]?[]> _splits = [];
    readonly Dictionary<int, byte[]> _orderBuffer = [];
    readonly TaskCompletionSource _connected = new(TaskCreationOptions.RunContinuationsAsynchronously);

    int _mtu;
    int _sendSeq;
    int _reliableIndex;
    int _orderIndex;
    ushort _splitId;
    int _expectedOrder;
    int _lowestReliable;
    long _lastReceive;
    Task? _loops;

    public string? DisconnectReason { get; private set; }
    public ChannelReader<byte[]> Incoming => _incoming.Reader;
    public IPEndPoint Remote => _remote;

    RakNetClient(IPEndPoint remote)
    {
        _remote = remote;
        _udp = new UdpClient(remote.AddressFamily);
        _udp.Connect(remote);
    }

    long Now => _clock.ElapsedMilliseconds;

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

    public static async Task<RakNetClient> ConnectAsync(string host, int port, CancellationToken ct)
    {
        var client = new RakNetClient(await ResolveAsync(host, port, ct));
        try
        {
            await client.HandshakeAsync(ct);
            return client;
        }
        catch
        {
            await client.DisposeAsync();
            throw;
        }
    }

    async Task HandshakeAsync(CancellationToken ct)
    {
        foreach (var mtu in MtuSizes)
        {
            for (var attempt = 0; attempt < 3 && _mtu == 0; attempt++)
            {
                var w = new PacketWriter(mtu);
                w.Byte(RakId.OpenConnectionRequest1).Bytes(RakBinary.Magic).Byte(RakBinary.ProtocolVersion);
                w.Bytes(new byte[Math.Max(0, mtu - 28 - w.Length)]);
                await _udp.SendAsync(w.ToArray(), ct);
                var reply = await ReceiveRawAsync(RakId.OpenConnectionReply1, TimeSpan.FromMilliseconds(600), ct);
                if (reply is null) continue;
                var r = new PacketReader(reply);
                r.Skip(1 + 16 + 8);
                int? cookie = r.Bool() ? r.Int32BE() : null;
                _mtu = Math.Min(r.UInt16BE(), mtu);
                await SendRequest2Async(cookie, ct);
            }
            if (_mtu != 0) break;
        }
        if (_mtu == 0) throw new TimeoutException($"Server {_remote} did not answer");

        _lastReceive = Now;
        _loops = Task.WhenAll(ReceiveLoopAsync(_cts.Token), TickLoopAsync(_cts.Token));

        SendInternal(new PacketWriter()
            .Byte(RakId.ConnectionRequest).Int64BE(_guid).Int64BE(Now).Bool(false).ToArray(), Reliability.Reliable);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        await _connected.Task.WaitAsync(timeout.Token);
    }

    async Task SendRequest2Async(int? cookie, CancellationToken ct)
    {
        var w = new PacketWriter();
        w.Byte(RakId.OpenConnectionRequest2).Bytes(RakBinary.Magic);
        // Servers with RakNet security echo the cookie back; we send no challenge.
        if (cookie is { } c) w.Int32BE(c).Bool(false);
        RakBinary.WriteAddress(w, _remote);
        w.UInt16BE((ushort)_mtu).Int64BE(_guid);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            await _udp.SendAsync(w.ToArray(), ct);
            if (await ReceiveRawAsync(RakId.OpenConnectionReply2, TimeSpan.FromMilliseconds(800), ct) is not null) return;
        }
        throw new TimeoutException("No OpenConnectionReply2");
    }

    async Task<byte[]?> ReceiveRawAsync(byte id, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            while (true)
            {
                var res = await _udp.ReceiveAsync(cts.Token);
                if (res.Buffer.Length > 0 && res.Buffer[0] == RakId.IncompatibleProtocol)
                    throw new InvalidOperationException("Server uses an incompatible RakNet version");
                if (res.Buffer.Length > 0 && res.Buffer[0] == id) return res.Buffer;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }

    public void Send(byte[] gamePayload)
    {
        var buf = new byte[gamePayload.Length + 1];
        buf[0] = RakId.Game;
        gamePayload.CopyTo(buf, 1);
        SendInternal(buf, Reliability.ReliableOrdered);
    }

    void SendInternal(byte[] body, Reliability reliability)
    {
        lock (_sendLock)
        {
            // IP/UDP 28, datagram header 4, max frame header 23.
            var maxBody = _mtu - 28 - 4 - 23;
            var order = reliability.IsOrdered() ? _orderIndex++ : 0;
            if (body.Length <= maxBody)
            {
                SendFrames([NewFrame(body, reliability, order)]);
                return;
            }

            var count = (body.Length + maxBody - 1) / maxBody;
            var id = _splitId++;
            for (var i = 0; i < count; i++)
            {
                var f = NewFrame(body.AsSpan(i * maxBody, Math.Min(maxBody, body.Length - i * maxBody)).ToArray(),
                    reliability.IsReliable() ? reliability : Reliability.Reliable, order);
                f.Split = true;
                f.SplitCount = count;
                f.SplitId = id;
                f.SplitIndex = i;
                SendFrames([f]);
            }
        }
    }

    Frame NewFrame(byte[] body, Reliability reliability, int order) => new()
    {
        Body = body,
        Reliability = reliability,
        ReliableIndex = reliability.IsReliable() ? _reliableIndex++ : 0,
        OrderIndex = order,
    };

    void SendFrames(Frame[] frames)
    {
        var seq = _sendSeq++ & 0xFFFFFF;
        var w = new PacketWriter(_mtu);
        w.Byte(0x84).UInt24LE(seq);
        foreach (var f in frames) f.Write(w);
        if (frames.Any(f => f.Reliability.IsReliable())) _unacked[seq] = (frames, Now);
        _ = _udp.SendAsync(w.ToArray()).AsTask().ContinueWith(_ => { }, TaskContinuationOptions.OnlyOnFaulted);
    }

    async Task ReceiveLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var res = await _udp.ReceiveAsync(ct);
                _lastReceive = Now;
                try
                {
                    HandleDatagram(res.Buffer);
                }
                catch (Exception e) when (e is EndOfStreamException or InvalidDataException or FormatException)
                {
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (SocketException e)
        {
            Close($"Connection lost: {e.Message}");
        }
    }

    void HandleDatagram(byte[] data)
    {
        if (data.Length == 0 || (data[0] & 0x80) == 0) return;
        var r = new PacketReader(data);
        var flags = r.Byte();
        if ((flags & 0x40) != 0)
        {
            lock (_sendLock)
                foreach (var s in AckCodec.Decode(r)) _unacked.Remove(s);
            return;
        }
        if ((flags & 0x20) != 0)
        {
            lock (_sendLock)
                foreach (var s in AckCodec.Decode(r))
                    if (_unacked.Remove(s, out var entry)) SendFrames(entry.Frames);
            return;
        }

        _pendingAcks.Enqueue(r.UInt24LE());
        while (r.Remaining > 0) HandleFrame(Frame.Read(r));
    }

    void HandleFrame(Frame f)
    {
        if (f.Reliability.IsReliable())
        {
            if (f.ReliableIndex < _lowestReliable || !_receivedReliable.Add(f.ReliableIndex)) return;
            while (_receivedReliable.Remove(_lowestReliable)) _lowestReliable++;
        }

        var body = f.Body;
        if (f.Split)
        {
            if (f.SplitCount is <= 0 or > 8192 || f.SplitIndex >= f.SplitCount) return;
            if (!_splits.TryGetValue(f.SplitId, out var parts))
                _splits[f.SplitId] = parts = new byte[]?[f.SplitCount];
            parts[f.SplitIndex] = f.Body;
            if (parts.Any(p => p is null)) return;
            _splits.Remove(f.SplitId);
            body = parts.SelectMany(p => p!).ToArray();
        }

        if (f.Reliability == Reliability.ReliableOrdered)
        {
            if (f.OrderIndex < _expectedOrder) return;
            _orderBuffer[f.OrderIndex] = body;
            while (_orderBuffer.Remove(_expectedOrder, out var next))
            {
                _expectedOrder++;
                HandleMessage(next);
            }
            return;
        }
        HandleMessage(body);
    }

    void HandleMessage(byte[] body)
    {
        if (body.Length == 0) return;
        switch (body[0])
        {
            case RakId.Game:
                _incoming.Writer.TryWrite(body[1..]);
                break;
            case RakId.ConnectedPing:
            {
                var time = new PacketReader(body.AsMemory(1)).Int64BE();
                SendInternal(new PacketWriter().Byte(RakId.ConnectedPong).Int64BE(time).Int64BE(Now).ToArray(), Reliability.Unreliable);
                break;
            }
            case RakId.ConnectionRequestAccepted:
            {
                var r = new PacketReader(body.AsMemory(1));
                RakBinary.ReadAddress(r);
                r.UInt16BE();
                long requestTime = 0;
                // 20 internal addresses we don't need; request time is 16 bytes before the end.
                if (body.Length >= 17) requestTime = new PacketReader(body.AsMemory(body.Length - 16)).Int64BE();
                var w = new PacketWriter().Byte(RakId.NewIncomingConnection);
                RakBinary.WriteAddress(w, _remote);
                var local = new IPEndPoint(IPAddress.Any, 0);
                for (var i = 0; i < 20; i++) RakBinary.WriteAddress(w, local);
                w.Int64BE(requestTime).Int64BE(Now);
                SendInternal(w.ToArray(), Reliability.ReliableOrdered);
                _connected.TrySetResult();
                break;
            }
            case RakId.DisconnectionNotification:
                Close("Server closed the connection");
                break;
        }
    }

    async Task TickLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(50));
        var lastPing = Now;
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                var acks = new List<int>();
                while (_pendingAcks.TryDequeue(out var s)) acks.Add(s);
                if (acks.Count > 0) await _udp.SendAsync(AckCodec.Encode(AckCodec.AckFlag, acks), ct);

                lock (_sendLock)
                {
                    var now = Now;
                    foreach (var (seq, entry) in _unacked.Where(e => now - e.Value.SentAt > ResendAfter.TotalMilliseconds).ToList())
                    {
                        _unacked.Remove(seq);
                        SendFrames(entry.Frames);
                    }
                }

                if (Now - lastPing > 2000)
                {
                    lastPing = Now;
                    SendInternal(new PacketWriter().Byte(RakId.ConnectedPing).Int64BE(Now).ToArray(), Reliability.Unreliable);
                }

                if (Now - _lastReceive > IdleTimeout.TotalMilliseconds)
                {
                    Close("Timed out");
                    return;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (SocketException e)
        {
            Close($"Connection lost: {e.Message}");
        }
    }

    void Close(string reason)
    {
        DisconnectReason ??= reason;
        _connected.TrySetException(new IOException(reason));
        _incoming.Writer.TryComplete();
        _cts.Cancel();
    }

    public async ValueTask DisposeAsync()
    {
        if (!_cts.IsCancellationRequested && _connected.Task.IsCompletedSuccessfully)
        {
            try
            {
                SendInternal([RakId.DisconnectionNotification], Reliability.Reliable);
                await Task.Delay(50);
            }
            catch { }
        }
        Close("Closed");
        if (_loops is not null)
        {
            try { await _loops; } catch { }
        }
        _udp.Dispose();
        _cts.Dispose();
    }
}
