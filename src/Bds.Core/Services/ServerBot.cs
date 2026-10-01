using Bds.Core.NetherNet;
using Bds.Core.RakNet;
using Bds.Core.Storage;
using Microsoft.Extensions.Logging;

namespace Bds.Core.Services;

public enum BotState
{
    Idle,
    Online,
    Offline,
}

public sealed record ServerTarget(string Name, string Host, int Port, string TransferHost, int TransferPort)
{
    public static ServerTarget From(ServerEntry s) => new(s.Name, s.Host, s.Port,
        string.IsNullOrWhiteSpace(s.PublicHost) ? s.Host : s.PublicHost,
        s.PublicPort ?? s.Port);
}

/// <summary>Pings the selected server. The gateway mirrors its status in the Friends tab.</summary>
public sealed class ServerBot(ILogger<ServerBot> log)
{
    static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(30);
    static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(5);
    const int MaxMisses = 3;

    bool _netherNet;

    public BotState State { get; private set; } = BotState.Idle;
    public string? Status { get; private set; }
    public ServerPong? Pong { get; private set; }
    public ServerTarget? Target { get; private set; }

    public event Action? Changed;

    public async Task RunAsync(ServerTarget target, CancellationToken ct)
    {
        Target = target;
        var misses = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    Pong = await PingAsync(target, ct);
                    misses = 0;
                    Set(BotState.Online, null);
                }
                catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    // UDP pings get lost; only go offline after a few misses in a row.
                    if (++misses >= MaxMisses || Pong is null)
                    {
                        if (State != BotState.Offline) log.LogWarning("Server not responding: {Message}", e.Message);
                        Pong = null;
                        Set(BotState.Offline, e.Message);
                    }
                }
                await Task.Delay(misses > 0 && Pong is not null ? RetryInterval : PingInterval, ct);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            Pong = null;
            Target = null;
            Set(BotState.Idle, null);
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

    void Set(BotState state, string? status)
    {
        State = state;
        Status = status;
        Changed?.Invoke();
    }
}
