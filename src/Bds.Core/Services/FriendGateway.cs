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

public sealed record JoinedFriend(string Name, string? Xuid, string Server, DateTimeOffset Time);

/// <summary>
/// Publishes a joinable Xbox session for the bot's server. Friends connect over NetherNet
/// and get a Transfer packet to their routed server, or the bot's server.
/// </summary>
public sealed class FriendGateway(XboxAccount account, XboxHttp xbox, ServerBot bot, FriendRoutes routes, ILoggerFactory logs)
{
    static readonly TimeSpan SessionRefresh = TimeSpan.FromSeconds(60);
    const int MaxRecentJoins = 50;

    readonly ILogger _log = logs.CreateLogger<FriendGateway>();
    readonly SessionDirectoryClient _sessions = new(xbox);
    readonly ConcurrentDictionary<string, string> _nonces = new();
    readonly SemaphoreSlim _nonceLock = new(1, 1);
    readonly PresenceClient _presence = new(xbox);
    readonly ServerIdentity _identity = new();
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
        _nonces.Clear();
        var pmsgId = signaling.PmsgId!;

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        string? closed = null;
        signaling.Closed += reason => { closed ??= $"Signaling: {reason}"; linked.Cancel(); };
        rta.Closed += reason => { closed ??= $"Xbox RTA: {reason}"; linked.Cancel(); };
        signaling.SignalReceived += signal => _ = HandleSignalAsync(signaling, signal, turn, linked.Token);

        try
        {
            await _sessions.CreateOrUpdateAsync(sessionId, xuid, connectionId, Info(networkId, pmsgId), ct);
            rta.SessionChanged += () => _ = SyncNoncesAsync(sessionId, xuid, linked.Token);
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
                await _sessions.CreateOrUpdateAsync(sessionId, xuid, connectionId, Info(networkId, pmsgId), linked.Token);
                await SetPresenceAsync(xuid, linked.Token);
                await LogFriendSessionsAsync(xuid, linked.Token);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new IOException(closed ?? "Xbox or signaling connection closed");
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
            var own = await _sessions.QueryOwnHandlesAsync(xuid, ct);
            var friends = await _sessions.QueryFriendHandlesAsync(xuid, ct);
            _log.LogDebug("Session check: own handles {Own}, friend handles {Friends}", own.Count, friends.Count);
            foreach (var handle in friends)
            {
                var name = handle?["sessionRef"]?["name"]?.GetValue<string>();
                if (handle is null || name is null || handle["ownerXuid"]?.GetValue<string>() == xuid || !_loggedFriendSessions.Add(name)) continue;
                _log.LogDebug("Friend session: {Json}", handle.ToJsonString());
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

    SessionInfo Info(ulong networkId, string pmsgId)
    {
        var pong = bot.Pong!;
        return new SessionInfo(
            string.IsNullOrWhiteSpace(pong.Motd) ? account.Gamertag ?? "Server" : pong.Motd,
            string.IsNullOrWhiteSpace(pong.LevelName) ? pong.Motd : pong.LevelName,
            pong.Version,
            pong.Protocol,
            pong.Players,
            pong.MaxPlayers,
            networkId,
            pmsgId,
            new Dictionary<string, string>(_nonces));
    }

    // Joining friends add themselves to the session and expect a nonce under their xuid.
    async Task SyncNoncesAsync(Guid sessionId, string ownXuid, CancellationToken ct)
    {
        try
        {
            await _nonceLock.WaitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        try
        {
            var active = (await _sessions.GetMemberXuidsAsync(sessionId, ct)).Where(x => x != ownXuid).ToHashSet();
            var changed = false;
            foreach (var stale in _nonces.Keys.Where(x => !active.Contains(x)).ToList())
                changed |= _nonces.TryRemove(stale, out _);
            foreach (var x in active.Where(x => !_nonces.ContainsKey(x)))
            {
                _nonces[x] = Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(8));
                changed = true;
                _log.LogInformation("Friend {Xuid} is joining", x);
            }
            if (changed) await _sessions.UpdateNoncesAsync(sessionId, new Dictionary<string, string>(_nonces), ct);
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            _log.LogWarning("Nonce update failed: {Message}", e.Message);
        }
        catch (Exception) when (ct.IsCancellationRequested) { }
        finally
        {
            _nonceLock.Release();
        }
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
                    conn.LocalCandidate += c => signaling.SendSignalAsync(signal.From,
                        new Signal(signal.From, Signal.CandidateAdd, signal.ConnectionId, c), ct)
                        .ContinueWith(t => _log.LogDebug("Candidate not sent: {Message}", t.Exception?.GetBaseException().Message),
                            TaskContinuationOptions.OnlyOnFaulted);
                    var answer = _identity.Sign(await conn.AnswerAsync(signal.Data));
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
            _log.LogWarning("Signal handling failed: {Message}", e.Message);
        }
    }

    async Task RedirectAsync(NetherNetConnection conn, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var codec = new BatchCodec();
        try
        {
            await conn.Opened.WaitAsync(timeout.Token);
            var protocol = bot.Pong?.Protocol ?? 0;
            string? name = null, xuid = null;
            await foreach (var batch in conn.Incoming.ReadAllAsync(timeout.Token))
            {
                foreach (var packet in codec.Decode(batch))
                {
                    if (packet.Id == PacketId.RequestNetworkSettings)
                    {
                        protocol = GamePackets.ReadRequestNetworkSettings(packet);
                        conn.Send(codec.Encode([GamePackets.NetworkSettingsPacket(0, CompressionAlgorithm.Zlib)]));
                        codec.EnableCompression(CompressionAlgorithm.Zlib, 0);
                    }
                    else if (packet.Id == PacketId.Login)
                    {
                        var (_, identity, _) = GamePackets.ReadLogin(packet);
                        (name, xuid) = LoginBuilder.ReadIdentity(identity);
                        if (bot.Target is null && routes.Resolve(xuid, null) is null)
                        {
                            conn.Send(codec.Encode([GamePackets.Disconnect("Server is offline", protocol)]));
                            await Task.Delay(TimeSpan.FromSeconds(2), timeout.Token);
                            return;
                        }
                        conn.Send(codec.Encode([GamePackets.PlayStatusPacket(PlayStatus.LoginSuccess), GamePackets.ResourcePacksInfo()]));
                    }
                    else if (packet.Id == PacketId.ResourcePackClientResponse)
                    {
                        switch (GamePackets.ReadResourcePackResponse(packet))
                        {
                            case 2:
                                conn.Send(codec.Encode([GamePackets.ResourcePackStack()]));
                                break;
                            case 3:
                                await TransferAsync(conn, codec, protocol, name, xuid, timeout.Token);
                                return;
                            default:
                                conn.Send(codec.Encode([GamePackets.Disconnect("disconnectionScreen.resourcePack", protocol)]));
                                await Task.Delay(TimeSpan.FromSeconds(2), timeout.Token);
                                return;
                        }
                    }
                }
            }
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or InvalidDataException or EndOfStreamException or InvalidOperationException)
        {
            _log.LogInformation("Friend connection ended: {Message}", e.Message);
        }
        finally
        {
            if (_connections.TryRemove(conn.ConnectionId, out _)) await conn.DisposeAsync();
        }
    }

    // Same order as MCXboxBroadcast: clients ignore a Transfer before StartGame.
    async Task TransferAsync(NetherNetConnection conn, BatchCodec codec, int protocol, string? name, string? xuid, CancellationToken ct)
    {
        var target = routes.Resolve(xuid, bot.Target);
        if (target is null)
        {
            conn.Send(codec.Encode([GamePackets.Disconnect("Server is offline", protocol)]));
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
            return;
        }
        var level = bot.Pong?.LevelName is { Length: > 0 } l ? l : account.Gamertag ?? "Server";
        conn.Send(codec.Encode([
            GamePackets.JigsawStructureData(),
            GamePackets.VoxelShapes(),
            GamePackets.StartGame(level),
            GamePackets.Transfer(target.TransferHost, (ushort)target.TransferPort, protocol),
        ]));
        var joined = new JoinedFriend(name ?? "Unknown", xuid, target.Name, DateTimeOffset.UtcNow);
        _recent.Enqueue(joined);
        while (_recent.Count > MaxRecentJoins) _recent.TryDequeue(out _);
        _log.LogInformation("Transferred {Name} to {Server}", joined.Name, joined.Server);
        FriendJoined?.Invoke(joined);
        Changed?.Invoke();
        await Task.Delay(TimeSpan.FromSeconds(2), ct);
    }

    void Set(GatewayState state, string? status)
    {
        if (State == state && Status == status) return;
        State = state;
        Status = status;
        Changed?.Invoke();
    }
}
