using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Bds.Core.Util;

namespace Bds.Core.Auth;

public sealed record McToken(string AuthorizationHeader, DateTimeOffset ValidUntil);

/// <summary>Minecraft login chain, PlayFab and franchise services tokens.</summary>
public sealed class MinecraftServicesClient(HttpClient http)
{
    const string ChainUrl = "https://multiplayer.minecraft.net/authentication";
    const string PlayFabUrl = "https://20ca2.playfabapi.com/Client/LoginWithXbox";
    const string SessionStartUrl = "https://authorization.franchise.minecraft-services.net/api/v1.0/session/start";
    const string MultiplayerStartUrl = "https://authorization.franchise.minecraft-services.net/api/v1.0/multiplayer/session/start";

    public async Task<List<string>> GetChainAsync(XstsToken xsts, ECDsa identityKey, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, ChainUrl)
        {
            Content = JsonContent.Create(new JsonObject { ["identityPublicKey"] = Jwt.ExportX5u(identityKey) }),
        };
        req.Headers.TryAddWithoutValidation("Authorization", xsts.Header);
        req.Headers.Add("Client-Version", "1.21.0");
        using var res = await http.SendAsync(req, ct);
        if (!res.IsSuccessStatusCode) throw new AuthException($"Minecraft authentication failed ({(int)res.StatusCode}): {await ErrorTextAsync(res, ct)}");
        var body = JsonNode.Parse(await res.Content.ReadAsStringAsync(ct));
        return body?["chain"]?.AsArray().Select(n => n!.GetValue<string>()).ToList()
            ?? throw new AuthException("No chain in Minecraft authentication response");
    }

    public async Task<string> GetPlayFabTicketAsync(XstsToken playFabXsts, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["CreateAccount"] = true,
            ["TitleId"] = AuthConstants.PlayFabTitleId,
            ["XboxToken"] = playFabXsts.Header,
        };
        using var res = await http.PostAsJsonAsync(PlayFabUrl, body, ct);
        if (!res.IsSuccessStatusCode) throw new AuthException($"PlayFab login failed ({(int)res.StatusCode}): {await ErrorTextAsync(res, ct)}");
        var json = JsonNode.Parse(await res.Content.ReadAsStringAsync(ct));
        return json?["data"]?["SessionTicket"]?.GetValue<string>() ?? throw new AuthException("No PlayFab session ticket");
    }

    // Tried in order. Some versions/platform combos are rejected by the service.
    static readonly (string Platform, string Store)[] DeviceProfiles =
    [
        ("Windows10", "uwp.store"),
        ("Android", "android.googleplay"),
    ];

    public async Task<McToken> StartSessionAsync(string playFabTicket, string gameVersion, Guid deviceId, CancellationToken ct)
    {
        gameVersion = NormalizeVersion(gameVersion);
        AuthException? last = null;
        foreach (var (platform, store) in DeviceProfiles)
        {
            try
            {
                return await StartSessionAsync(playFabTicket, gameVersion, deviceId, platform, store, ct);
            }
            catch (AuthException e)
            {
                last = e;
            }
        }
        throw last!;
    }

    async Task<McToken> StartSessionAsync(string playFabTicket, string gameVersion, Guid deviceId, string platform, string store, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["device"] = new JsonObject
            {
                ["applicationType"] = "MinecraftPE",
                ["capabilities"] = new JsonArray(),
                ["gameVersion"] = gameVersion,
                ["id"] = deviceId.ToString(),
                ["memory"] = "8589934592",
                ["platform"] = platform,
                ["playFabTitleId"] = AuthConstants.PlayFabTitleId,
                ["storePlatform"] = store,
                ["treatmentOverrides"] = null,
                ["type"] = platform,
            },
            ["user"] = new JsonObject
            {
                ["language"] = "en",
                ["languageCode"] = "en-US",
                ["regionCode"] = "US",
                ["token"] = playFabTicket,
                ["tokenType"] = "PlayFab",
            },
        };
        using var res = await http.PostAsJsonAsync(SessionStartUrl, body, ct);
        if (!res.IsSuccessStatusCode)
            throw new AuthException($"Minecraft services session failed ({(int)res.StatusCode}, {platform}, version {gameVersion}): {await ErrorTextAsync(res, ct)}");
        var json = JsonNode.Parse(await res.Content.ReadAsStringAsync(ct))?["result"];
        return new McToken(
            json?["authorizationHeader"]?.GetValue<string>() ?? throw new AuthException("No MCToken"),
            json["validUntil"] is { } v ? DateTimeOffset.Parse(v.GetValue<string>()) : DateTimeOffset.UtcNow.AddHours(1));
    }

    /// <summary>The service only accepts "major.minor.patch". Servers may report "26.51" or "v1.21.x".</summary>
    internal static string NormalizeVersion(string version)
    {
        var match = System.Text.RegularExpressions.Regex.Match(version, @"\d+(\.\d+){0,3}");
        var parts = (match.Success ? match.Value : "1.21.0").Split('.').ToList();
        while (parts.Count < 3) parts.Add("0");
        return string.Join('.', parts);
    }

    static async Task<string> ErrorTextAsync(HttpResponseMessage res, CancellationToken ct)
    {
        var text = (await res.Content.ReadAsStringAsync(ct)).Trim();
        if (text.Length == 0) return "no details";
        return text.Length > 300 ? text[..300] : text;
    }

    /// <summary>
    /// Newer login token (1.21.90+), bound to <paramref name="identityKey"/>. RakNet servers still accept
    /// the legacy chain without it; NetherNet servers need it for the WebRTC identity.
    /// </summary>
    public async Task<string> GetMultiplayerTokenAsync(McToken mcToken, ECDsa identityKey, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, MultiplayerStartUrl)
        {
            Content = JsonContent.Create(new JsonObject { ["publicKey"] = Jwt.ExportX5u(identityKey) }),
        };
        req.Headers.TryAddWithoutValidation("Authorization", mcToken.AuthorizationHeader);
        using var res = await http.SendAsync(req, ct);
        if (!res.IsSuccessStatusCode) throw new AuthException($"Multiplayer token failed ({(int)res.StatusCode}): {await ErrorTextAsync(res, ct)}");
        var json = JsonNode.Parse(await res.Content.ReadAsStringAsync(ct));
        return json?["result"]?["signedToken"]?.GetValue<string>() ?? throw new AuthException("No multiplayer token");
    }
}
