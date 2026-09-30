using Microsoft.EntityFrameworkCore;

namespace Bds.Core.Storage;

public static class SettingKeys
{
    public const string AutoAcceptFriends = "AutoAcceptFriends";
    public const string StartAtBoot = "StartAtBoot";
    public const string LanAccess = "LanAccess";
    public const string LanPasswordHash = "LanPasswordHash";
    public const string BotEnabled = "BotEnabled";
}

public sealed class SettingsStore(IDbContextFactory<AppDbContext> db)
{
    public async Task<string?> GetAsync(string key, CancellationToken ct = default)
    {
        await using var ctx = await db.CreateDbContextAsync(ct);
        return (await ctx.Settings.FindAsync([key], ct))?.Value;
    }

    public async Task<bool> GetBoolAsync(string key, bool fallback = false, CancellationToken ct = default) =>
        bool.TryParse(await GetAsync(key, ct), out var v) ? v : fallback;

    public async Task SetAsync(string key, string? value, CancellationToken ct = default)
    {
        await using var ctx = await db.CreateDbContextAsync(ct);
        var entry = await ctx.Settings.FindAsync([key], ct);
        if (value is null)
        {
            if (entry is not null) ctx.Settings.Remove(entry);
        }
        else if (entry is null) ctx.Settings.Add(new SettingEntry { Key = key, Value = value });
        else entry.Value = value;
        await ctx.SaveChangesAsync(ct);
    }

    public Task SetBoolAsync(string key, bool value, CancellationToken ct = default) => SetAsync(key, value.ToString(), ct);

    public async Task LogAsync(string kind, string message, CancellationToken ct = default)
    {
        await using var ctx = await db.CreateDbContextAsync(ct);
        ctx.Activity.Add(new ActivityEntry { Kind = kind, Message = message });
        await ctx.SaveChangesAsync(ct);
        var cutoff = DateTimeOffset.UtcNow.AddDays(-14).ToUnixTimeMilliseconds();
        await ctx.Database.ExecuteSqlAsync($"DELETE FROM Activity WHERE Time < {cutoff}", ct);
    }
}
