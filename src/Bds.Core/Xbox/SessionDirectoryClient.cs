using System.Text.Json.Nodes;

namespace Bds.Core.Xbox;

public sealed record SessionInfo(
    string HostName,
    string WorldName,
    string Version,
    int Protocol,
    int Players,
    int MaxPlayers,
    ulong NetherNetId,
    string PmsgId,
    IReadOnlyDictionary<string, string> Nonces);

/// <summary>Multiplayer session directory: the joinable "MinecraftLobby" session friends see.</summary>
public sealed class SessionDirectoryClient(XboxHttp xbox)
{
    public const string Scid = "4fc10100-5f7a-4470-899b-280835760c07";
    const string Template = "MinecraftLobby";

    public string SessionUrl(Guid sessionId) =>
        $"https://sessiondirectory.xboxlive.com/serviceconfigs/{Scid}/sessionTemplates/{Template}/sessions/{sessionId}";

    public async Task CreateOrUpdateAsync(Guid sessionId, string xuid, string rtaConnectionId, SessionInfo info, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["properties"] = new JsonObject
            {
                ["system"] = new JsonObject
                {
                    ["joinRestriction"] = "followed",
                    ["readRestriction"] = "followed",
                    ["closed"] = false,
                },
                ["custom"] = Custom(xuid, info),
            },
            ["members"] = new JsonObject
            {
                ["me"] = new JsonObject
                {
                    ["constants"] = new JsonObject
                    {
                        ["system"] = new JsonObject { ["xuid"] = xuid, ["initialize"] = true },
                    },
                    ["properties"] = new JsonObject
                    {
                        ["system"] = new JsonObject
                        {
                            ["active"] = true,
                            ["connection"] = rtaConnectionId,
                            ["subscription"] = new JsonObject
                            {
                                ["id"] = "845CC784-7348-4A27-BCDE-C083579DD113",
                                ["changeTypes"] = new JsonArray("everything"),
                            },
                        },
                    },
                },
            },
        };
        await xbox.SendAsync(HttpMethod.Put, SessionUrl(sessionId), 107, body, ct);
    }

    public async Task SetActivityAsync(Guid sessionId, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["version"] = 1,
            ["type"] = "activity",
            ["sessionRef"] = new JsonObject
            {
                ["scid"] = Scid,
                ["templateName"] = Template,
                ["name"] = sessionId.ToString(),
            },
        };
        await xbox.SendAsync(HttpMethod.Post, "https://sessiondirectory.xboxlive.com/handles", 107, body, ct);
    }

    /// <summary>Activity handles of the account's friends, with session custom properties.</summary>
    public Task<JsonArray> QueryFriendHandlesAsync(string xuid, CancellationToken ct) =>
        QueryHandlesAsync(new JsonObject
        {
            ["people"] = new JsonObject { ["moniker"] = "people", ["monikerXuid"] = xuid },
        }, ct);

    public Task<JsonArray> QueryOwnHandlesAsync(string xuid, CancellationToken ct) =>
        QueryHandlesAsync(new JsonObject { ["xuids"] = new JsonArray(xuid) }, ct);

    async Task<JsonArray> QueryHandlesAsync(JsonObject owners, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["type"] = "activity",
            ["scid"] = Scid,
            ["owners"] = owners,
        };
        var res = await xbox.SendAsync(HttpMethod.Post,
            "https://sessiondirectory.xboxlive.com/handles/query?include=relatedInfo,customProperties", 107, body, ct);
        return res?["results"]?.AsArray() ?? [];
    }

    /// <summary>XUIDs of everyone in the session. Joining friends add themselves before connecting.</summary>
    public async Task<List<string>> GetMemberXuidsAsync(Guid sessionId, CancellationToken ct)
    {
        var res = await xbox.SendAsync(HttpMethod.Get, SessionUrl(sessionId), 107, ct: ct);
        return res?["members"]?.AsObject()
            .Select(m => m.Value?["constants"]?["system"]?["xuid"]?.GetValue<string>())
            .OfType<string>().ToList() ?? [];
    }

    public Task UpdateNoncesAsync(Guid sessionId, IReadOnlyDictionary<string, string> nonces, CancellationToken ct) =>
        xbox.SendAsync(HttpMethod.Put, SessionUrl(sessionId), 107, new JsonObject
        {
            ["properties"] = new JsonObject { ["custom"] = new JsonObject { ["nonces"] = Nonces(nonces) } },
        }, ct);

    static JsonObject Nonces(IReadOnlyDictionary<string, string> nonces) =>
        new(nonces.Select(n => KeyValuePair.Create(n.Key, (JsonNode?)n.Value)));

    public async Task LeaveAsync(Guid sessionId, CancellationToken ct)
    {
        try
        {
            await xbox.SendAsync(HttpMethod.Delete, SessionUrl(sessionId) + "/members/me", 107, ct: ct);
        }
        catch (XboxApiException)
        {
        }
    }

    static JsonObject Custom(string xuid, SessionInfo info) => new()
    {
        ["BroadcastSetting"] = 3,
        ["CrossPlayDisabled"] = false,
        ["Joinability"] = "joinable_by_friends",
        ["LanGame"] = false,
        ["MaxMemberCount"] = Math.Max(info.MaxPlayers, info.Players + 1),
        ["MemberCount"] = info.Players,
        ["OnlineCrossPlatformGame"] = true,
        // 7 = NetherNet over JSON-RPC signaling. Current clients skip type 3.
        ["SupportedConnections"] = new JsonArray(new JsonObject
        {
            ["ConnectionType"] = 7,
            ["HostIpAddress"] = "",
            ["HostPort"] = 0,
            ["NetherNetId"] = info.NetherNetId,
            ["PmsgId"] = info.PmsgId,
        }),
        ["nonces"] = Nonces(info.Nonces),
        ["TitleId"] = 0,
        ["TransportLayer"] = 2,
        ["WebRTCNetworkId"] = info.NetherNetId,
        ["levelId"] = "level",
        ["hostName"] = info.HostName,
        ["ownerId"] = xuid,
        ["rakNetGUID"] = "",
        ["worldName"] = info.WorldName,
        ["worldType"] = "Survival",
        ["protocol"] = info.Protocol,
        ["version"] = info.Version,
        ["isEditorWorld"] = false,
        ["isHardcore"] = false,
    };
}
