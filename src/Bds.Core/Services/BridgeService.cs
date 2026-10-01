using Bds.Core.Auth;
using Bds.Core.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Bds.Core.Services;

/// <summary>Runs the bot, gateway and auto accept for the active server while signed in.</summary>
public sealed class BridgeService(
    XboxAccount account,
    ServerBot bot,
    FriendGateway gateway,
    FriendManager friends,
    FriendRoutes routes,
    IDbContextFactory<AppDbContext> db,
    SettingsStore settings,
    ILogger<BridgeService> log)
{
    static readonly TimeSpan AutoAcceptInterval = TimeSpan.FromMinutes(2);

    readonly SemaphoreSlim _wake = new(0);
    readonly SemaphoreSlim _initLock = new(1, 1);
    bool _initialized;
    CancellationTokenSource? _current;

    public XboxAccount Account => account;
    public ServerBot Bot => bot;
    public FriendGateway Gateway => gateway;
    public FriendManager Friends => friends;
    public FriendRoutes Routes => routes;
    public ServerEntry? ActiveServer { get; private set; }
    public bool IsRunning { get; private set; }

    public event Action? Changed;

    /// <summary>Migrates the database and restores the saved account. Safe to call more than once.</summary>
    public async Task InitializeAsync(CancellationToken ct)
    {
        await _initLock.WaitAsync(ct);
        try
        {
            if (_initialized) return;
            await using (var ctx = await db.CreateDbContextAsync(ct))
                await ctx.Database.MigrateAsync(ct);
            await routes.ReloadAsync(ct);
            await account.TryResumeAsync(ct);
            _initialized = true;
        }
        finally
        {
            _initLock.Release();
        }
    }

    public async Task RunAsync(CancellationToken ct)
    {
        await InitializeAsync(ct);
        void OnSocial() => _ = SafeAutoAcceptAsync(ct);
        void OnJoined(JoinedFriend f) => _ = settings.LogAsync("join", $"{f.Name} joined {f.Server}", CancellationToken.None);
        account.Changed += Reload;
        bot.Changed += OnChanged;
        gateway.Changed += OnChanged;
        gateway.SocialChanged += OnSocial;
        gateway.FriendJoined += OnJoined;
        IsRunning = true;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                ActiveServer = await GetActiveServerAsync(ct);
                OnChanged();
                if (account.State != AccountState.SignedIn || ActiveServer is null)
                {
                    await _wake.WaitAsync(ct);
                    continue;
                }

                _current = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var target = ServerTarget.From(ActiveServer);
                log.LogInformation("Starting bridge for {Name}", ActiveServer.Name);
                await Task.WhenAll(
                    bot.RunAsync(target, _current.Token),
                    gateway.RunAsync(_current.Token),
                    AutoAcceptLoopAsync(_current.Token),
                    WaitForReloadAsync(_current));
                _current.Dispose();
                _current = null;
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            account.Changed -= Reload;
            bot.Changed -= OnChanged;
            gateway.Changed -= OnChanged;
            gateway.SocialChanged -= OnSocial;
            gateway.FriendJoined -= OnJoined;
            IsRunning = false;
            OnChanged();
        }
    }

    async Task WaitForReloadAsync(CancellationTokenSource current)
    {
        try
        {
            await _wake.WaitAsync(current.Token);
            current.Cancel();
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>Picks up a new active server or account state.</summary>
    public void Reload()
    {
        if (_wake.CurrentCount == 0) _wake.Release();
        OnChanged();
    }

    public async Task SetActiveServerAsync(int? id, CancellationToken ct)
    {
        await using var ctx = await db.CreateDbContextAsync(ct);
        foreach (var s in await ctx.Servers.ToListAsync(ct)) s.IsActive = s.Id == id;
        await ctx.SaveChangesAsync(ct);
        Reload();
    }

    async Task<ServerEntry?> GetActiveServerAsync(CancellationToken ct)
    {
        await using var ctx = await db.CreateDbContextAsync(ct);
        return await ctx.Servers.AsNoTracking().FirstOrDefaultAsync(s => s.IsActive, ct);
    }

    async Task AutoAcceptLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await SafeAutoAcceptAsync(ct);
                await Task.Delay(AutoAcceptInterval, ct);
            }
        }
        catch (OperationCanceledException) { }
    }

    async Task SafeAutoAcceptAsync(CancellationToken ct)
    {
        try
        {
            await friends.AutoAcceptAsync(ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log.LogDebug("Auto accept failed: {Message}", e.Message);
        }
    }

    void OnChanged() => Changed?.Invoke();
}
