using System.Collections.Concurrent;
using System.Security.Cryptography;
using Bds.Core.Auth;
using Bds.Core.NetherNet;
using Bds.Core.Protocol;
using Bds.Core.RakNet;
using Bds.Core.Storage;
using Bds.Core.Util;
using Microsoft.Extensions.Logging;

namespace Bds.Core.Services;

public enum BotState
{
    Idle,
    Connecting,
    Online,
    PingOnly,
    Offline,
}

public sealed record ServerTarget(string Name, string Host, int Port, string TransferHost, int TransferPort)
{
    public static ServerTarget From(ServerEntry s) => new(s.Name, s.Host, s.Port,
        string.IsNullOrWhiteSpace(s.PublicHost) ? s.Host : s.PublicHost,
        s.PublicPort ?? s.Port);
}

/// <summary>Joins the selected server as the signed in account and tracks who is online.</summary>
public sealed class ServerBot(XboxAccount account, ILogger<ServerBot> log)
{
    static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(30);

    readonly ConcurrentDictionary<Guid, PlayerListEntry> _players = new();
    bool _netherNet;

    public BotState State { get; private set; } = BotState.Idle;
    public string? Status { get; private set; }
    public ServerPong? Pong { get; private set; }
    public ServerTarget? Target { get; private set; }
    public IReadOnlyCollection<PlayerListEntry> Players => _players.Values.OrderBy(p => p.Name).ToList();

    public event Action? Changed;

    public async Task RunAsync(ServerTarget target, CancellationToken ct)
    {
        Target = target;
        _players.Clear();
        var backoff = new Backoff(TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(5));
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    Set(BotState.Connecting, "Pinging server");
                    Pong = await PingAsync(target, ct);
                    Set(BotState.Connecting, "Joining server");
                    await SessionAsync(target, Pong, backoff, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (JoinRejectedException e)
                {
                    log.LogWarning("Bot can't join: {Reason}", e.Message);
                    await PingOnlyAsync(target, e.Message, ct);
                    continue;
                }
                catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    log.LogWarning("Server connection failed: {Message}", e.Message);
                    Set(BotState.Offline, e.Message);
                }
                _players.Clear();
                await Task.Delay(backoff.Next(), ct);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            _players.Clear();
            Pong = null;
            Target = null;
            Set(BotState.Idle, null);
        }
    }

    async Task PingOnlyAsync(ServerTarget target, string reason, CancellationToken ct)
    {
        _players.Clear();
        var until = DateTimeOffset.UtcNow.AddMinutes(10);
        while (DateTimeOffset.UtcNow < until)
        {
            try
            {
                Pong = await PingAsync(target, ct);
                Set(BotState.PingOnly, $"Ping only: {reason}");
            }
            catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                Set(BotState.Offline, "Server not responding");
            }
            await Task.Delay(PingInterval, ct);
        }
    }

    /// <summary>Tries the transport that worked last, then the other one (servers with transport=nethernet don't answer RakNet).</summary>
    async Task<ServerPong> PingAsync(ServerTarget target, CancellationToken ct)
    {
        try
        {
            return await PingAsync(target, _netherNet, ct);
        }
        catch (Exception first) when (first is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            try
            {
                var pong = await PingAsync(target, !_netherNet, ct);
                _netherNet = !_netherNet;
                log.LogInformation("Server uses {Transport}", _netherNet ? "NetherNet" : "RakNet");
                return pong;
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                throw first;
            }
        }
    }

    static Task<ServerPong> PingAsync(ServerTarget target, bool netherNet, CancellationToken ct) => netherNet
        ? NetherNetClient.PingAsync(target.Host, target.Port, TimeSpan.FromSeconds(5), ct)
        : RakNetClient.PingAsync(target.Host, target.Port, TimeSpan.FromSeconds(5), ct);

    async Task SessionAsync(ServerTarget target, ServerPong pong, Backoff backoff, CancellationToken ct)
    {
        var protocol = pong.Protocol;
        using var identity = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        using var ecdh = ECDiffieHellman.Create(identity.ExportParameters(true));

        var xsts = await account.GetXstsAsync(AuthConstants.MinecraftRelyingParty, ct);
        var chain = await account.Minecraft.GetChainAsync(xsts, identity, ct);
        var netherNet = _netherNet;
        string? token = null;
        if (netherNet || protocol >= LoginBuilder.ProtocolTokenLogin)
        {
            try
            {
                var mc = await account.GetMcTokenAsync(pong.Version, ct);
                token = await account.Minecraft.GetMultiplayerTokenAsync(mc, identity, ct);
            }
            catch (Exception e) when (!netherNet && e is AuthException or HttpRequestException)
            {
                log.LogDebug("Multiplayer token unavailable: {Message}", e.Message);
            }
        }

        string serverAddress;
        IGameTransport transport;
        if (netherNet)
        {
            serverAddress = NetherNetClient.ServerAddress(await RakNetClient.ResolveAsync(target.Host, target.Port, ct));
            transport = await NetherNetClient.ConnectAsync(target.Host, target.Port, identity, token!, log, ct);
        }
        else
        {
            serverAddress = $"{target.Host}:{target.Port}";
            transport = await RakNetClient.ConnectAsync(target.Host, target.Port, ct);
        }
        await using var _ = transport;
        using var codec = new BatchCodec();
        void Send(params byte[][] packets) => transport.Send(codec.Encode(packets));

        Send(GamePackets.RequestNetworkSettings(protocol));
        ulong runtimeId = 0;

        await foreach (var batch in transport.Incoming.ReadAllAsync(ct))
        {
            foreach (var packet in codec.Decode(batch))
            {
                switch (packet.Id)
                {
                    case PacketId.NetworkSettings:
                    {
                        var settings = GamePackets.ReadNetworkSettings(packet);
                        log.LogInformation("Connected, logging in (protocol {Protocol}, compression {Algorithm})", protocol, settings.Algorithm);
                        codec.EnableCompression(settings.Algorithm, settings.Threshold);
                        Send(GamePackets.Login(protocol,
                            LoginBuilder.Identity(chain, identity, token, protocol),
                            LoginBuilder.ClientData(identity, account.Gamertag ?? "", pong.Version, serverAddress, account.DeviceId)));
                        break;
                    }
                    case PacketId.ServerToClientHandshake when !transport.Encrypted:
                        // NetherNet is already DTLS encrypted; just acknowledge.
                        Send(GamePackets.ClientToServerHandshake());
                        break;
                    case PacketId.ServerToClientHandshake:
                    {
                        var jwt = GamePackets.ReadServerToClientHandshake(packet);
                        var (header, payload) = Jwt.DecodeUnverified(jwt);
                        var serverKey = header["x5u"]!.GetValue<string>();
                        using (var verify = Jwt.ImportX5u(serverKey))
                            if (!Jwt.VerifyEs384(jwt, verify)) throw new InvalidDataException("Bad server handshake signature");
                        var salt = Convert.FromBase64String(payload["salt"]!.GetValue<string>());
                        codec.EnableEncryption(BedrockCipher.DeriveKey(salt, LoginBuilder.SharedSecret(ecdh, serverKey)));
                        Send(GamePackets.ClientToServerHandshake());
                        break;
                    }
                    case PacketId.PlayStatus:
                    {
                        var status = GamePackets.ReadPlayStatus(packet);
                        log.LogInformation("Play status {Status}", status);
                        if (status == PlayStatus.LoginSuccess)
                        {
                            Send(GamePackets.ClientCacheStatus(false));
                        }
                        else if (status == PlayStatus.PlayerSpawn)
                        {
                            Send(GamePackets.SetLocalPlayerAsInitialized(runtimeId));
                            backoff.Reset();
                            Set(BotState.Online, null);
                        }
                        else if (status != PlayStatus.LoginSuccess)
                        {
                            throw status is PlayStatus.FailedServerFull
                                ? new IOException("Server is full")
                                : new JoinRejectedException($"Login refused ({status})");
                        }
                        break;
                    }
                    case PacketId.ResourcePacksInfo:
                        Send(GamePackets.ResourcePackResponse(false, protocol));
                        break;
                    case PacketId.ResourcePackStack:
                        Send(GamePackets.ResourcePackResponse(true, protocol));
                        break;
                    case PacketId.StartGame:
                        log.LogInformation("Start game received");
                        runtimeId = GamePackets.ReadStartGame(packet).RuntimeId;
                        Send(GamePackets.RequestChunkRadius(4));
                        break;
                    case PacketId.PlayerList:
                        ApplyPlayerList(packet, protocol);
                        break;
                    case PacketId.NetworkStackLatency:
                        if (GamePackets.NetworkStackLatencyReply(packet) is { } reply) Send(reply);
                        break;
                    case PacketId.Disconnect:
                    {
                        var reason = GamePackets.ReadDisconnect(packet);
                        if (reason.Contains("whitelist", StringComparison.OrdinalIgnoreCase)
                            || reason.Contains("allowlist", StringComparison.OrdinalIgnoreCase)
                            || reason.Contains("not authenticated", StringComparison.OrdinalIgnoreCase)
                            || reason.Contains("outdated", StringComparison.OrdinalIgnoreCase))
                            throw new JoinRejectedException(reason);
                        throw new IOException($"Kicked: {reason}");
                    }
                    default:
                        log.LogTrace("Ignoring packet {Id}", packet.Id);
                        break;
                }
            }
        }
        throw new IOException(transport.DisconnectReason ?? "Connection closed");
    }

    void ApplyPlayerList(Packet packet, int protocol)
    {
        try
        {
            var update = GamePackets.ReadPlayerList(packet, protocol);
            foreach (var e in update.Entries)
            {
                if (update.Add) _players[e.Uuid] = e;
                else _players.TryRemove(e.Uuid, out _);
            }
            Changed?.Invoke();
        }
        catch (EndOfStreamException)
        {
            log.LogWarning("Could not read player list; protocol layout may have changed");
        }
    }

    void Set(BotState state, string? status)
    {
        State = state;
        Status = status;
        Changed?.Invoke();
    }

    sealed class JoinRejectedException(string message) : Exception(message);
}
