using System.Text.Json.Nodes;

namespace Bds.Core.Xbox;

public sealed record XboxPerson(string Xuid, string Gamertag, bool IsFollowing, bool IsFollower, bool Online, string? Presence);

/// <summary>Friends via peoplehub (read) and social (write).</summary>
public sealed class SocialClient(XboxHttp xbox)
{
    public const int FriendLimit = 2000;

    public async Task<List<XboxPerson>> GetFriendsAsync(CancellationToken ct) =>
        Parse(await xbox.SendAsync(HttpMethod.Get,
            "https://peoplehub.xboxlive.com/users/me/people/social/decoration/presencedetail", 5, ct: ct));

    public async Task<List<XboxPerson>> GetFollowersAsync(CancellationToken ct) =>
        Parse(await xbox.SendAsync(HttpMethod.Get,
            "https://peoplehub.xboxlive.com/users/me/people/followers/decoration/presencedetail", 5, ct: ct));

    /// <summary>People following us that we don't follow back: pending friend requests.</summary>
    public async Task<List<XboxPerson>> GetIncomingAsync(CancellationToken ct) =>
        (await GetFollowersAsync(ct)).Where(p => !p.IsFollowing).ToList();

    public async Task<string> ResolveGamertagAsync(string gamertag, CancellationToken ct)
    {
        var res = await xbox.SendAsync(HttpMethod.Get,
            $"https://profile.xboxlive.com/users/gt({Uri.EscapeDataString(gamertag)})/profile/settings?settings=Gamertag", 2, ct: ct);
        return res?["profileUsers"]?[0]?["id"]?.GetValue<string>()
            ?? throw new XboxApiException($"Gamertag {gamertag} not found", 404);
    }

    public Task AddAsync(string xuid, CancellationToken ct) =>
        xbox.SendAsync(HttpMethod.Put, $"https://social.xboxlive.com/users/me/people/xuid({xuid})", 2, ct: ct);

    public Task RemoveAsync(string xuid, CancellationToken ct) =>
        xbox.SendAsync(HttpMethod.Delete, $"https://social.xboxlive.com/users/me/people/xuid({xuid})", 2, ct: ct);

    static List<XboxPerson> Parse(JsonNode? res) =>
        res?["people"]?.AsArray().Select(p => new XboxPerson(
            p!["xuid"]!.GetValue<string>(),
            p["gamertag"]?.GetValue<string>() ?? p["displayName"]?.GetValue<string>() ?? "",
            p["isFollowedByCaller"]?.GetValue<bool>() ?? false,
            p["isFollowingCaller"]?.GetValue<bool>() ?? false,
            string.Equals(p["presenceState"]?.GetValue<string>(), "Online", StringComparison.OrdinalIgnoreCase),
            p["presenceText"]?.GetValue<string>())).ToList() ?? [];
}
