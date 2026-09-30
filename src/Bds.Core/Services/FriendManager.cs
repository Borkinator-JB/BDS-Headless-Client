using Bds.Core.Storage;
using Bds.Core.Xbox;
using Microsoft.Extensions.Logging;

namespace Bds.Core.Services;

public sealed class FriendManager(XboxHttp xbox, SettingsStore settings, ILogger<FriendManager> log)
{
    readonly SocialClient _social = new(xbox);
    readonly SemaphoreSlim _autoAcceptLock = new(1, 1);

    public int Limit => SocialClient.FriendLimit;

    public Task<List<XboxPerson>> ListAsync(CancellationToken ct) => _social.GetFriendsAsync(ct);
    public Task<List<XboxPerson>> IncomingAsync(CancellationToken ct) => _social.GetIncomingAsync(ct);

    public async Task<string> AddByGamertagAsync(string gamertag, CancellationToken ct)
    {
        var xuid = await _social.ResolveGamertagAsync(gamertag.Trim(), ct);
        await _social.AddAsync(xuid, ct);
        await settings.LogAsync("friend", $"Added {gamertag}", ct);
        return xuid;
    }

    public async Task AcceptAsync(XboxPerson person, CancellationToken ct)
    {
        await _social.AddAsync(person.Xuid, ct);
        await settings.LogAsync("friend", $"Accepted {person.Gamertag}", ct);
    }

    public async Task RemoveAsync(XboxPerson person, CancellationToken ct)
    {
        await _social.RemoveAsync(person.Xuid, ct);
        await settings.LogAsync("friend", $"Removed {person.Gamertag}", ct);
    }

    public async Task AutoAcceptAsync(CancellationToken ct)
    {
        if (!await settings.GetBoolAsync(SettingKeys.AutoAcceptFriends, ct: ct)) return;
        if (!await _autoAcceptLock.WaitAsync(0, ct)) return;
        try
        {
            foreach (var p in await IncomingAsync(ct))
            {
                try
                {
                    await AcceptAsync(p, ct);
                }
                catch (XboxApiException e)
                {
                    log.LogWarning("Auto accept {Gamertag} failed: {Message}", p.Gamertag, e.Message);
                }
            }
        }
        finally
        {
            _autoAcceptLock.Release();
        }
    }
}
