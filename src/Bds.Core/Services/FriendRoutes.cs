using Bds.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace Bds.Core.Services;

/// <summary>Per-friend server assignments, cached so joins never wait on the database.</summary>
public sealed class FriendRoutes(IDbContextFactory<AppDbContext> db)
{
    sealed record Snapshot(IReadOnlyDictionary<string, int> Routes, IReadOnlyDictionary<int, ServerTarget> Servers);

    volatile Snapshot _snapshot = new(new Dictionary<string, int>(), new Dictionary<int, ServerTarget>());

    public int? ServerIdFor(string xuid) => _snapshot.Routes.TryGetValue(xuid, out var id) ? id : null;

    public ServerTarget? Resolve(string? xuid, ServerTarget? fallback)
    {
        var s = _snapshot;
        return Resolve(xuid, s.Routes, s.Servers, fallback);
    }

    // The xuid comes from the client's unverified login data. Routing is a convenience, not access control.
    public static ServerTarget? Resolve(string? xuid, IReadOnlyDictionary<string, int> routes,
        IReadOnlyDictionary<int, ServerTarget> servers, ServerTarget? fallback) =>
        xuid is not null && routes.TryGetValue(xuid, out var id) && servers.TryGetValue(id, out var target) ? target : fallback;

    /// <summary>Call after servers or routes change.</summary>
    public async Task ReloadAsync(CancellationToken ct)
    {
        await using var ctx = await db.CreateDbContextAsync(ct);
        var routes = await ctx.FriendRoutes.AsNoTracking().ToDictionaryAsync(r => r.Xuid, r => r.ServerId, ct);
        var servers = await ctx.Servers.AsNoTracking().ToDictionaryAsync(s => s.Id, ct);
        _snapshot = new Snapshot(routes, servers.ToDictionary(s => s.Key, s => ServerTarget.From(s.Value)));
    }

    /// <summary>Null sends the friend to the active server.</summary>
    public async Task SetAsync(string xuid, int? serverId, CancellationToken ct)
    {
        await using (var ctx = await db.CreateDbContextAsync(ct))
        {
            await ctx.FriendRoutes.Where(r => r.Xuid == xuid).ExecuteDeleteAsync(ct);
            if (serverId is { } id)
            {
                ctx.FriendRoutes.Add(new FriendRoute { Xuid = xuid, ServerId = id });
                await ctx.SaveChangesAsync(ct);
            }
        }
        await ReloadAsync(ct);
    }
}
