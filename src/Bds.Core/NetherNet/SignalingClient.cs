using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using Bds.Core.Util;
using Microsoft.Extensions.Logging;

namespace Bds.Core.NetherNet;

public sealed record TurnServer(string[] Urls, string? Username, string? Password);

/// <summary>A WebRTC signal. From/To is the peer's PmsgId.</summary>
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

/// <summary>Minecraft JSON-RPC signaling (ConnectionType 7). Peers are addressed by PmsgId.</summary>
public sealed class SignalingClient(ILogger log) : IAsyncDisposable
{
    const string Url = "wss://signal.franchise.minecraft-services.net/ws/v1.0/messaging/connect";
    const string TurnAuthMethod = "Signaling_TurnAuth_v1_0";
    const string ReceiveMessageMethod = "Signaling_ReceiveMessage_v1_0";
    const string SendClientMessageMethod = "Signaling_SendClientMessage_v1_0";
    const string WebRtcMethod = "Signaling_WebRtc_v1_0";
    const string PingMethod = "System_Ping_v1_0";

    ClientWebSocket? _ws;
    CancellationTokenSource? _cts;
    Task? _loops;
    ulong _networkId;
    readonly SemaphoreSlim _sendLock = new(1, 1);
    readonly ConcurrentDictionary<string, TaskCompletionSource<JsonNode?>> _pending = new();

    /// <summary>Our id in this signaling system, from the MCToken. Goes into the session's PmsgId.</summary>
    public string? PmsgId { get; private set; }

    public event Action<Signal>? SignalReceived;
    public event Action<string>? Closed;

    public async Task<List<TurnServer>> ConnectAsync(ulong networkId, string mcToken, CancellationToken ct)
    {
        _networkId = networkId;
        PmsgId = ReadPmsgId(mcToken) ?? throw new InvalidOperationException("MCToken has no pmid claim");

        _ws = new ClientWebSocket();
        _ws.Options.SetRequestHeader("Authorization", mcToken);
        _ws.Options.SetRequestHeader("session-id", Guid.NewGuid().ToString());
        _ws.Options.SetRequestHeader("request-id", Guid.NewGuid().ToString());
        await _ws.ConnectAsync(new Uri(Url), ct);
        _cts = new CancellationTokenSource();
        _loops = Task.WhenAll(ReceiveLoopAsync(_cts.Token), PingLoopAsync(_cts.Token));

        var result = await RequestAsync(TurnAuthMethod, new JsonObject(), ct);
        return result?["TurnAuthServers"]?.AsArray().Select(s => new TurnServer(
            s!["Urls"]!.AsArray().Select(u => u!.GetValue<string>()).ToArray(),
            s["Username"]?.GetValue<string>(),
            s["Password"]?.GetValue<string>())).ToList() ?? [];
    }

    public Task SendSignalAsync(string toPmsgId, Signal signal, CancellationToken ct)
    {
        var inner = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = WebRtcMethod,
            ["params"] = new JsonObject
            {
                ["netherNetId"] = _networkId.ToString(),
                ["message"] = signal.ToString(),
            },
        };
        return RequestAsync(SendClientMessageMethod, new JsonObject
        {
            ["toPlayerId"] = toPmsgId,
            ["messageId"] = Guid.NewGuid().ToString(),
            ["message"] = inner.ToJsonString(),
        }, ct);
    }

    async Task<JsonNode?> RequestAsync(string method, JsonNode @params, CancellationToken ct)
    {
        var id = Guid.NewGuid().ToString();
        var tcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        try
        {
            await SendAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = @params }, ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            return await tcs.Task.WaitAsync(timeout.Token);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    async Task SendAsync(JsonObject msg, CancellationToken ct)
    {
        if (_ws is not { State: WebSocketState.Open }) throw new IOException("Signaling not connected");
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
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                try
                {
                    await RequestAsync(PingMethod, new JsonArray(), ct);
                }
                catch (Exception e) when (e is TimeoutException or OperationCanceledException && !ct.IsCancellationRequested)
                {
                    log.LogDebug("Signaling ping timed out");
                }
            }
        }
        catch (Exception e) when (e is OperationCanceledException or WebSocketException or IOException) { }
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
                    if (res.MessageType == WebSocketMessageType.Close)
                    {
                        reason = $"Signaling closed ({res.CloseStatus} {res.CloseStatusDescription})";
                        return;
                    }
                    ms.Write(buf, 0, res.Count);
                } while (!res.EndOfMessage);

                var node = JsonNode.Parse(ms.ToArray());
                if (node is JsonArray batch)
                    foreach (var item in batch) await HandleAsync(item, ct);
                else
                    await HandleAsync(node, ct);
            }
        }
        catch (OperationCanceledException) { reason = "Stopped"; }
        catch (Exception e) when (e is WebSocketException or System.Text.Json.JsonException)
        {
            reason = e.Message;
        }
        finally
        {
            foreach (var p in _pending.Values) p.TrySetException(new IOException(reason));
            Closed?.Invoke(reason);
        }
    }

    async Task HandleAsync(JsonNode? msg, CancellationToken ct)
    {
        if (msg is not JsonObject obj) return;
        var id = obj["id"]?.ToString();

        // Response to one of our requests.
        if (obj["method"] is null)
        {
            if (id is not null && _pending.TryGetValue(id, out var tcs))
            {
                if (obj["error"] is { } error) tcs.TrySetException(new IOException($"Signaling error: {error.ToJsonString()}"));
                else tcs.TrySetResult(obj["result"]);
            }
            return;
        }

        if (obj["method"]!.GetValue<string>() == ReceiveMessageMethod)
        {
            var items = obj["params"] is JsonArray arr ? arr : new JsonArray(obj["params"]?.DeepClone());
            foreach (var item in items) HandleIncoming(item);
        }

        // Every server request needs a response.
        if (id is not null)
            await SendAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = null }, ct);
    }

    void HandleIncoming(JsonNode? item)
    {
        try
        {
            var from = item?["From"]?.ToString();
            var inner = item?["Message"]?.GetValue<string>() is { } m ? JsonNode.Parse(m) : null;
            if (from is null || inner?["method"]?.GetValue<string>() != WebRtcMethod) return;
            var text = inner["params"]?["message"]?.GetValue<string>();
            if (text is not null && Signal.Parse(from, text) is { } signal) SignalReceived?.Invoke(signal);
        }
        catch (Exception e) when (e is System.Text.Json.JsonException or InvalidOperationException)
        {
            log.LogDebug("Bad signaling message: {Message}", e.Message);
        }
    }

    internal static string? ReadPmsgId(string mcToken)
    {
        var jwt = mcToken.Split(' ').LastOrDefault();
        if (jwt is null || jwt.Split('.').Length != 3) return null;
        try
        {
            return Jwt.DecodeUnverified(jwt).Payload["pmid"]?.GetValue<string>();
        }
        catch (Exception e) when (e is FormatException or System.Text.Json.JsonException or InvalidOperationException)
        {
            return null;
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
