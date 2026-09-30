using System.Collections.Concurrent;
using System.Security.Cryptography;
using Bds.Core.Auth;
using Bds.Core.Protocol;
using Bds.Core.RakNet;
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

public sealed record ServerTarget(string Host, int Port, string TransferHost, int TransferPort);

/// <summary>Joins the selected server as the signed in account and tracks who is online.</summary>
public sealed class ServerBot(XboxAccount account, ILogger<ServerBot> log)
{
    static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(30);

    readonly ConcurrentDictionary<Guid, PlayerListEntry> _players = new();

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
                    Pong = await RakNetClient.PingAsync(target.Host, target.Port, TimeSpan.FromSeconds(5), ct);
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
                Pong = await RakNetClient.PingAsync(target.Host, target.Port, TimeSpan.FromSeconds(5), ct);
                Set(BotState.PingOnly, $"Ping only: {reason}");
            }
            catch (TimeoutException)
            {
                Set(BotState.Offline, "Server not responding");
            }
            await Task.Delay(PingInterval, ct);
        }
    }

    async Task SessionAsync(ServerTarget target, ServerPong pong, Backoff backoff, CancellationToken ct)
    {
        var protocol = pong.Protocol;
        using var identity = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        using var ecdh = ECDiffieHellman.Create(identity.ExportParameters(true));

        var xsts = await account.GetXstsAsync(AuthConstants.MinecraftRelyingParty, ct);
        var chain = await account.Minecraft.GetChainAsync(xsts, identity, ct);
        string? token = null;
        if (protocol >= LoginBuilder.ProtocolTokenLogin)
        {
            try
            {
                var mc = await account.GetMcTokenAsync(pong.Version, ct);
                token = await account.Minecraft.GetMultiplayerTokenAsync(mc, identity, ct);
            }
            catch (Exception e) when (e is AuthException or HttpRequestException)
            {
                log.LogDebug("Multiplayer token unavailable: {Message}", e.Message);
            }
        }

        await using var rak = await RakNetClient.ConnectAsync(target.Host, target.Port, ct);
        using var codec = new BatchCodec();
        void Send(params byte[][] packets) => rak.Send(codec.Encode(packets));

        Send(GamePackets.RequestNetworkSettings(protocol));
        ulong runtimeId = 0;

        await foreach (var batch in rak.Incoming.ReadAllAsync(ct))
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
                            LoginBuilder.ClientData(identity, account.Gamertag ?? "", pong.Version, $"{target.Host}:{target.Port}", account.DeviceId)));
                        break;
                    }
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
                        if (status == PlayStatus.PlayerSpawn)
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
                        Send(GamePackets.ResourcePackResponse(GamePackets.ResourcePackHaveAll));
                        break;
                    case PacketId.ResourcePackStack:
                        Send(GamePackets.ResourcePackResponse(GamePackets.ResourcePackCompleted));
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
                }
            }
        }
        throw new IOException(rak.DisconnectReason ?? "Connection closed");
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
