using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace Bds.Core.NetherNet;

public sealed record TurnServer(string[] Urls, string? Username, string? Password);

public sealed record Signal(string From, string Type, ulong ConnectionId, string Data)
{
    public const string ConnectRequest = "CONNECTREQUEST";
    public const string ConnectResponse = "CONNECTRESPONSE";
    public const string CandidateAdd = "CANDIDATEADD";
    public const string ConnectError = "CONNECTERROR";

    public static Signal? Parse(string from, string message)
    {
        var parts = message.Split(' ', 3);
        if (parts.Length < 3 || !ulong.TryParse(parts[1], out var id)) return null;
        return new Signal(from, parts[0], id, parts[2]);
    }

    public override string ToString() => $"{Type} {ConnectionId} {Data}";
}

/// <summary>Minecraft franchise signaling websocket used to set up NetherNet (WebRTC) connections.</summary>
public sealed class SignalingClient(ILogger log) : IAsyncDisposable
{
    const int TypePing = 0;
    const int TypeSignal = 1;
    const int TypeCredentials = 2;

    ClientWebSocket? _ws;
    CancellationTokenSource? _cts;
    Task? _loops;
    readonly SemaphoreSlim _sendLock = new(1, 1);
    readonly TaskCompletionSource<List<TurnServer>> _credentials = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public event Action<Signal>? SignalReceived;
    public event Action<string>? Closed;

    public async Task<List<TurnServer>> ConnectAsync(ulong networkId, string mcToken, CancellationToken ct)
    {
        _ws = new ClientWebSocket();
        _ws.Options.SetRequestHeader("Authorization", mcToken);
        await _ws.ConnectAsync(new Uri($"wss://signal.franchise.minecraft-services.net/ws/v1.0/signaling/{networkId}"), ct);
        _cts = new CancellationTokenSource();
        _loops = Task.WhenAll(ReceiveLoopAsync(_cts.Token), PingLoopAsync(_cts.Token));

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            return await _credentials.Task.WaitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            log.LogWarning("No TURN credentials received, continuing with STUN only");
            return [new TurnServer(["stun:stun.l.google.com:19302"], null, null)];
        }
    }

    public Task SendSignalAsync(string to, Signal signal, CancellationToken ct) =>
        SendAsync(new JsonObject { ["Type"] = TypeSignal, ["To"] = to, ["Message"] = signal.ToString() }, ct);

    async Task SendAsync(JsonObject msg, CancellationToken ct)
    {
        if (_ws is not { State: WebSocketState.Open }) return;
        await _sendLock.WaitAsync(ct);
        try
        {
            await _ws.SendAsync(Encoding.UTF8.GetBytes(msg.ToJsonString()), WebSocketMessageType.Text, true, ct);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    async Task PingLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
                await SendAsync(new JsonObject { ["Type"] = TypePing }, ct);
        }
        catch (Exception e) when (e is OperationCanceledException or WebSocketException) { }
    }

    async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buf = new byte[16 * 1024];
        var reason = "Signaling closed";
        try
        {
            while (_ws!.State == WebSocketState.Open)
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult res;
                do
                {
                    res = await _ws.ReceiveAsync(buf, ct);
                    if (res.MessageType == WebSocketMessageType.Close) return;
                    ms.Write(buf, 0, res.Count);
                } while (!res.EndOfMessage);
                Handle(JsonNode.Parse(ms.ToArray()));
            }
        }
        catch (OperationCanceledException) { reason = "Stopped"; }
        catch (Exception e) when (e is WebSocketException or System.Text.Json.JsonException)
        {
            reason = e.Message;
        }
        finally
        {
            Closed?.Invoke(reason);
        }
    }

    void Handle(JsonNode? msg)
    {
        if (msg is null) return;
        var type = msg["Type"]?.GetValue<int>();
        var message = msg["Message"]?.GetValue<string>();
        if (type == TypeCredentials && message is not null)
        {
            var creds = JsonNode.Parse(message);
            var servers = creds?["TurnAuthServers"]?.AsArray().Select(s => new TurnServer(
                s!["Urls"]!.AsArray().Select(u => u!.GetValue<string>()).ToArray(),
                s["Username"]?.GetValue<string>(),
                s["Password"]?.GetValue<string>())).ToList() ?? [];
            _credentials.TrySetResult(servers);
        }
        else if (type == TypeSignal && message is not null)
        {
            var from = msg["From"]?.ToString() ?? "";
            if (Signal.Parse(from, message) is { } signal) SignalReceived?.Invoke(signal);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        if (_ws is { State: WebSocketState.Open })
        {
            try { await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None); } catch { }
        }
        if (_loops is not null) { try { await _loops; } catch { } }
        _ws?.Dispose();
        _cts?.Dispose();
    }
}
