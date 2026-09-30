using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using Bds.Core.Auth;
using Microsoft.Extensions.Logging;

namespace Bds.Core.Xbox;

/// <summary>Xbox real time activity socket. MPSD sessions stay alive only while this is connected.</summary>
public sealed class RtaClient(XboxAccount account, ILogger log) : IAsyncDisposable
{
    const string ConnectionsUri = "https://sessiondirectory.xboxlive.com/connections/";

    ClientWebSocket? _ws;
    CancellationTokenSource? _cts;
    Task? _loop;
    readonly TaskCompletionSource<string> _connectionId = new(TaskCreationOptions.RunContinuationsAsynchronously);
    int _nextSequence = 1;

    public event Action<string>? Closed;
    public event Action? SocialChanged;

    public async Task<string> ConnectAsync(string xuid, CancellationToken ct)
    {
        var xsts = await account.GetXstsAsync(AuthConstants.XboxLiveRelyingParty, ct);
        _ws = new ClientWebSocket();
        _ws.Options.SetRequestHeader("Authorization", xsts.Header);
        _ws.Options.AddSubProtocol("rta.xboxlive.com.V2");
        await _ws.ConnectAsync(new Uri("wss://rta.xboxlive.com/connect"), ct);
        _cts = new CancellationTokenSource();
        _loop = ReceiveLoopAsync(_cts.Token);

        await SubscribeAsync(ConnectionsUri, ct);
        await SubscribeAsync($"http://social.xboxlive.com/users/xuid({xuid})/friends", ct);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        return await _connectionId.Task.WaitAsync(timeout.Token);
    }

    Task SubscribeAsync(string resource, CancellationToken ct)
    {
        var msg = new JsonArray(1, _nextSequence++, resource).ToJsonString();
        return _ws!.SendAsync(Encoding.UTF8.GetBytes(msg), WebSocketMessageType.Text, true, ct);
    }

    async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buf = new byte[16 * 1024];
        var reason = "RTA closed";
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
                        reason = $"RTA closed ({res.CloseStatus} {res.CloseStatusDescription})";
                        return;
                    }
                    ms.Write(buf, 0, res.Count);
                } while (!res.EndOfMessage);
                Handle(JsonNode.Parse(ms.ToArray())?.AsArray());
            }
        }
        catch (OperationCanceledException) { reason = "Stopped"; }
        catch (Exception e) when (e is WebSocketException or System.Text.Json.JsonException)
        {
            reason = e.Message;
            log.LogWarning("RTA error: {Message}", e.Message);
        }
        finally
        {
            _connectionId.TrySetException(new IOException(reason));
            Closed?.Invoke(reason);
        }
    }

    void Handle(JsonArray? msg)
    {
        if (msg is null || msg.Count < 2) return;
        // [1, seq, code, ...] with a non-zero code is a failed subscribe.
        if (msg[0]!.GetValue<int>() == 1 && msg.Count >= 3 && msg[2]!.GetValue<int>() != 0)
            log.LogWarning("RTA subscribe failed: {Message}", msg.ToJsonString());
        var type = msg[0]!.GetValue<int>();
        // [1, seq, status, {ConnectionId}] subscribe reply; [3, subId, payload] event.
        if (type == 1 && msg.Count >= 4 && msg[3]?["ConnectionId"]?.GetValue<string>() is { } id)
            _connectionId.TrySetResult(id);
        else if (type == 3)
            SocialChanged?.Invoke();
    }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        if (_ws is { State: WebSocketState.Open })
        {
            try { await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None); } catch { }
        }
        if (_loop is not null) { try { await _loop; } catch { } }
        _ws?.Dispose();
        _cts?.Dispose();
    }
}
