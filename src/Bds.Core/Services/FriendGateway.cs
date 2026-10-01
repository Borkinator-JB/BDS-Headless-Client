using System.Collections.Concurrent;
using Bds.Core.Auth;
using Bds.Core.NetherNet;
using Bds.Core.Protocol;
using Bds.Core.Util;
using Bds.Core.Xbox;
using Microsoft.Extensions.Logging;

namespace Bds.Core.Services;

public enum GatewayState
{
    Stopped,
    Starting,
    Broadcasting,
    Error,
}

public sealed record JoinedFriend(string Name, string? Xuid, DateTimeOffset Time);

/// <summary>
/// Publishes a joinable Xbox session for the bot's server. Friends connect over NetherNet
/// and get a Transfer packet to that server.
/// </summary>
public sealed class FriendGateway(XboxAccount account, XboxHttp xbox, ServerBot bot, ILoggerFactory logs)
{
    static readonly TimeSpan SessionRefresh = TimeSpan.FromSeconds(60);
    const int MaxRecentJoins = 50;

    readonly ILogger _log = logs.CreateLogger<FriendGateway>();
    readonly SessionDirectoryClient _sessions = new(xbox);
    readonly PresenceClient _presence = new(xbox);
    readonly ConcurrentDictionary<ulong, NetherNetConnection> _connections = new();
    readonly ConcurrentQueue<JoinedFriend> _recent = new();

    public GatewayState State { get; private set; } = GatewayState.Stopped;
    public string? Status { get; private set; }
    public IReadOnlyList<JoinedFriend> RecentJoins => _recent.Reverse().ToList();

    public event Action? Changed;
    public event Action<JoinedFriend>? FriendJoined;
    public event Action? SocialChanged;

    public async Task RunAsync(CancellationToken ct)
    {
        var backoff = new Backoff(TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(5));
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (bot.Pong is null || bot.Target is null)
                {
                    Set(GatewayState.Starting, "Waiting for server");
                    await Task.Delay(TimeSpan.FromSeconds(2), ct);
                    continue;
                }
                try
                {
                    await BroadcastAsync(backoff, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception e)
                {
                    _log.LogWarning("Gateway failed: {Message}", e.Message);
                    Set(GatewayState.Error, e.Message);
                }
                await Task.Delay(backoff.Next(), ct);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            Set(GatewayState.Stopped, null);
        }
    }

    async Task BroadcastAsync(Backoff backoff, CancellationToken ct)
    {
        var xuid = account.Xuid ?? throw new AuthException("Not signed in");
        var pong = bot.Pong!;
        var networkId = (ulong)Random.Shared.NextInt64(1, long.MaxValue);
        var sessionId = Guid.NewGuid();

        Set(GatewayState.Starting, "Connecting to Minecraft services");
        var mcToken = await account.GetMcTokenAsync(pong.Version, ct);

        await using var signaling = new SignalingClient(_log);
        var turn = await signaling.ConnectAsync(networkId, mcToken.AuthorizationHeader, ct);

        await using var rta = new RtaClient(account, _log);
        var connectionId = await rta.ConnectAsync(xuid, ct);
        rta.SocialChanged += () => SocialChanged?.Invoke();

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        signaling.Closed += reason => linked.Cancel();
        rta.Closed += reason => linked.Cancel();
        signaling.SignalReceived += signal => _ = HandleSignalAsync(signaling, signal, turn, linked.Token);

        try
        {
            await _sessions.CreateOrUpdateAsync(sessionId, xuid, connectionId, Info(networkId), ct);
            await _sessions.SetActivityAsync(sessionId, ct);
            await SetPresenceAsync(xuid, ct);
            backoff.Reset();
            Set(GatewayState.Broadcasting, null);
            _log.LogInformation("Broadcasting session {Session}", sessionId);

            var lastTarget = bot.Target;
            using var timer = new PeriodicTimer(SessionRefresh);
            while (await timer.WaitForNextTickAsync(linked.Token))
            {
                if (bot.Pong is null || bot.Target != lastTarget) return;
                await _sessions.CreateOrUpdateAsync(sessionId, xuid, connectionId, Info(networkId), linked.Token);
                await SetPresenceAsync(xuid, linked.Token);
                await LogFriendSessionsAsync(xuid, linked.Token);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new IOException("Xbox or signaling connection closed");
        }
        finally
        {
            foreach (var c in _connections.Values) await c.DisposeAsync();
            _connections.Clear();
            await _sessions.LeaveAsync(sessionId, CancellationToken.None);
        }
    }

    readonly HashSet<string> _loggedFriendSessions = [];

    // Diagnostics: logs sessions published by real Minecraft clients so ours can match them.
    async Task LogFriendSessionsAsync(string xuid, CancellationToken ct)
    {
        try
        {
            foreach (var handle in await _sessions.QueryFriendHandlesAsync(xuid, ct))
            {
                var name = handle?["sessionRef"]?["name"]?.GetValue<string>();
                if (handle is null || name is null || handle["ownerXuid"]?.GetValue<string>() == xuid || !_loggedFriendSessions.Add(name)) continue;
                _log.LogInformation("Friend session: {Json}", handle.ToJsonString());
            }
        }
        catch (Exception e) when (e is XboxApiException or System.Text.Json.JsonException or InvalidOperationException)
        {
            _log.LogWarning("Friend session query failed: {Message}", e.Message);
        }
    }

    async Task SetPresenceAsync(string xuid, CancellationToken ct)
    {
        try
        {
            await _presence.SetActiveAsync(xuid, ct);
        }
        catch (XboxApiException e)
        {
            _log.LogWarning("Presence update failed: {Message}", e.Message);
        }
    }

    SessionInfo Info(ulong networkId)
    {
        var pong = bot.Pong!;
        return new SessionInfo(
            string.IsNullOrWhiteSpace(pong.Motd) ? account.Gamertag ?? "Server" : pong.Motd,
            string.IsNullOrWhiteSpace(pong.LevelName) ? pong.Motd : pong.LevelName,
            pong.Version,
            pong.Protocol,
            pong.Players,
            pong.MaxPlayers,
            networkId);
    }

    async Task HandleSignalAsync(SignalingClient signaling, Signal signal, List<TurnServer> turn, CancellationToken ct)
    {
        try
        {
            switch (signal.Type)
            {
                case Signal.ConnectRequest:
                {
                    var conn = new NetherNetConnection(signal.ConnectionId, signal.From, turn, _log);
                    if (!_connections.TryAdd(signal.ConnectionId, conn))
                    {
                        await conn.DisposeAsync();
                        return;
                    }
                    conn.LocalCandidate += c => _ = signaling.SendSignalAsync(signal.From,
                        new Signal(signal.From, Signal.CandidateAdd, signal.ConnectionId, c), ct);
                    var answer = await conn.AnswerAsync(signal.Data);
                    await signaling.SendSignalAsync(signal.From, new Signal(signal.From, Signal.ConnectResponse, signal.ConnectionId, answer), ct);
                    _ = RedirectAsync(conn, ct);
                    break;
                }
                case Signal.CandidateAdd:
                    if (_connections.TryGetValue(signal.ConnectionId, out var existing)) existing.AddRemoteCandidate(signal.Data);
                    break;
                case Signal.ConnectError:
                    if (_connections.TryRemove(signal.ConnectionId, out var failed)) await failed.DisposeAsync();
                    break;
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _log.LogDebug("Signal handling failed: {Message}", e.Message);
        }
    }

    async Task RedirectAsync(NetherNetConnection conn, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var codec = new BatchCodec();
        try
        {
            await conn.Opened.WaitAsync(timeout.Token);
            var protocol = bot.Pong?.Protocol ?? 0;
            await foreach (var batch in conn.Incoming.ReadAllAsync(timeout.Token))
            {
                foreach (var packet in codec.Decode(batch))
                {
                    if (packet.Id == PacketId.RequestNetworkSettings)
                    {
                        protocol = GamePackets.ReadRequestNetworkSettings(packet);
                        conn.Send(codec.Encode([GamePackets.NetworkSettingsPacket(1, CompressionAlgorithm.Zlib)]));
                        codec.EnableCompression(CompressionAlgorithm.Zlib, 1);
                    }
                    else if (packet.Id == PacketId.Login)
                    {
                        var (_, identity, _) = GamePackets.ReadLogin(packet);
                        var (name, xuid) = LoginBuilder.ReadIdentity(identity);
                        var target = bot.Target;
                        if (target is null)
                        {
                            conn.Send(codec.Encode([GamePackets.Disconnect("Server is offline", protocol)]));
                        }
                        else
                        {
                            conn.Send(codec.Encode([
                                GamePackets.PlayStatusPacket(PlayStatus.LoginSuccess),
                                GamePackets.Transfer(target.TransferHost, (ushort)target.TransferPort, protocol),
                            ]));
                            var joined = new JoinedFriend(name ?? "Unknown", xuid, DateTimeOffset.UtcNow);
                            _recent.Enqueue(joined);
                            while (_recent.Count > MaxRecentJoins) _recent.TryDequeue(out _);
                            _log.LogInformation("Transferred {Name}", joined.Name);
                            FriendJoined?.Invoke(joined);
                            Changed?.Invoke();
                        }
                        await Task.Delay(TimeSpan.FromSeconds(2), timeout.Token);
                        return;
                    }
                }
            }
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or InvalidDataException or EndOfStreamException or InvalidOperationException)
        {
            _log.LogDebug("Friend connection ended: {Message}", e.Message);
        }
        finally
        {
            if (_connections.TryRemove(conn.ConnectionId, out _)) await conn.DisposeAsync();
        }
    }

    void Set(GatewayState state, string? status)
    {
        if (State == state && Status == status) return;
        State = state;
        Status = status;
        Changed?.Invoke();
    }
}
