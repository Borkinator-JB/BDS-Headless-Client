using System.Net;
using System.Security.Claims;
using Bds.Core;
using Bds.Core.Services;
using Bds.Core.Storage;
using Bds.Host;
using Bds.Host.Components;
using Bds.Host.Platform;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

var dataDir = HostPaths.DataDirectory();
var asService = Microsoft.Extensions.Hosting.WindowsServices.WindowsServiceHelpers.IsWindowsService()
    || Microsoft.Extensions.Hosting.Systemd.SystemdHelpers.IsSystemdService();
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = asService ? AppContext.BaseDirectory : null,
});

builder.Host.UseWindowsService(o => o.ServiceName = HostPaths.ServiceName);
builder.Host.UseSystemd();

builder.Services.AddBdsCore(dataDir, HostPaths.CreateProtector(dataDir));
builder.Services.AddSingleton(sp => new Autostart(dataDir, sp.GetRequiredService<ILogger<Autostart>>()));
builder.Services.AddHostedService<BridgeHostedService>();
builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataDir, "keys")));
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.LoginPath = "/login";
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Strict;
        o.ExpireTimeSpan = TimeSpan.FromDays(7);
    });
builder.Services.AddRazorComponents().AddInteractiveServerComponents();

var lanAccess = await ReadLanAccessAsync(dataDir);
builder.WebHost.ConfigureKestrel(k =>
{
    if (lanAccess) k.ListenAnyIP(HostPaths.Port);
    else k.Listen(IPAddress.Loopback, HostPaths.Port);
});

var app = builder.Build();

app.UseAuthentication();
app.Use(async (ctx, next) =>
{
    // Loopback needs no login. LAN clients need the password cookie.
    if (!LanAuth.IsLocal(ctx) && ctx.User.Identity?.IsAuthenticated != true
        && !ctx.Request.Path.StartsWithSegments("/login") && !ctx.Request.Path.StartsWithSegments("/app.css"))
    {
        ctx.Response.Redirect("/login");
        return;
    }
    await next();
});
app.UseAntiforgery();
app.MapStaticAssets();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

app.MapPost("/login", async (HttpContext ctx, SettingsStore settings) =>
{
    var form = await ctx.Request.ReadFormAsync();
    var hash = await settings.GetAsync(SettingKeys.LanPasswordHash);
    if (!LanAuth.Verify(form["password"].ToString(), hash))
    {
        await Task.Delay(1000);
        return Results.Redirect("/login?failed=1");
    }
    var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, "admin")], CookieAuthenticationDefaults.AuthenticationScheme);
    await ctx.SignInAsync(new ClaimsPrincipal(identity));
    return Results.Redirect("/");
}).DisableAntiforgery();

app.MapGet("/login", (HttpContext ctx) => Results.Content(LoginPage.Html(ctx.Request.Query.ContainsKey("failed")), "text/html"));

// Custom header forces a CORS preflight, so other websites can't call these.
var api = app.MapGroup("/api").AddEndpointFilter(async (ctx, next) =>
    LanAuth.IsLocal(ctx.HttpContext) && ctx.HttpContext.Request.Headers.ContainsKey("X-Bds-Local")
        ? await next(ctx)
        : Results.StatusCode(403));

api.MapGet("/status", (BridgeService bridge) => new
{
    account = bridge.Account.State.ToString(),
    gamertag = bridge.Account.Gamertag,
    server = bridge.ActiveServer?.Name,
    bot = bridge.Bot.State.ToString(),
    gateway = bridge.Gateway.State.ToString(),
    players = bridge.Bot.Players.Count,
});

api.MapPost("/shutdown", (IHostApplicationLifetime life) =>
{
    life.StopApplication();
    return Results.Ok();
});

await app.RunAsync();

static async Task<bool> ReadLanAccessAsync(string dataDir)
{
    try
    {
        await using var ctx = new AppDbContext(AppDbContext.SqliteOptions(Path.Combine(dataDir, "bds.db")));
        if (!File.Exists(Path.Combine(dataDir, "bds.db"))) return false;
        await ctx.Database.MigrateAsync();
        var setting = await ctx.Settings.FindAsync(SettingKeys.LanAccess);
        var hash = await ctx.Settings.FindAsync(SettingKeys.LanPasswordHash);
        return bool.TryParse(setting?.Value, out var v) && v && hash is not null;
    }
    catch (Exception e) when (e is Microsoft.Data.Sqlite.SqliteException or InvalidOperationException)
    {
        return false;
    }
}
