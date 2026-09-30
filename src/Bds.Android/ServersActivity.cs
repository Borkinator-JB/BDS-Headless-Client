using Android.App;
using Android.OS;
using Android.Widget;
using AndroidX.AppCompat.App;
using Bds.Core.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Bds.Android;

[Activity(Label = "@string/servers", ParentActivity = typeof(MainActivity))]
public sealed class ServersActivity : AppCompatActivity
{
    ArrayAdapter<string> _adapter = null!;
    List<ServerEntry> _servers = [];

    static IDbContextFactory<AppDbContext> Db => BdsApp.Services.GetRequiredService<IDbContextFactory<AppDbContext>>();

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        SetContentView(Resource.Layout.activity_servers);
        SupportActionBar?.SetDisplayHomeAsUpEnabled(true);

        _adapter = new ArrayAdapter<string>(this, global::Android.Resource.Layout.SimpleListItem1);
        var list = FindViewById<ListView>(Resource.Id.list)!;
        list.Adapter = _adapter;
        list.ItemClick += async (_, e) => await Activate(_servers[e.Position]);
        list.ItemLongClick += async (_, e) => await Delete(_servers[e.Position]);
        FindViewById<Button>(Resource.Id.add)!.Click += async (_, _) => await Add();
    }

    protected override async void OnResume()
    {
        base.OnResume();
        await BdsApp.Bridge.InitializeAsync(CancellationToken.None);
        await Load();
    }

    async Task Load()
    {
        await using var ctx = await Db.CreateDbContextAsync();
        _servers = await ctx.Servers.AsNoTracking().OrderBy(s => s.Name).ToListAsync();
        _adapter.Clear();
        _adapter.AddAll(_servers.Select(s => $"{(s.IsActive ? "● " : "")}{s.Name}  {s.Host}:{s.Port}").ToList());
    }

    async Task Add()
    {
        string Text(int id) => FindViewById<EditText>(id)!.Text?.Trim() ?? "";
        var name = Text(Resource.Id.name);
        var host = Text(Resource.Id.host);
        if (name.Length == 0 || host.Length == 0 || !int.TryParse(Text(Resource.Id.port), out var port)) return;

        await using (var ctx = await Db.CreateDbContextAsync())
        {
            ctx.Servers.Add(new ServerEntry
            {
                Name = name,
                Host = host,
                Port = port,
                PublicHost = Text(Resource.Id.public_host) is { Length: > 0 } ph ? ph : null,
                PublicPort = int.TryParse(Text(Resource.Id.public_port), out var pp) ? pp : null,
            });
            await ctx.SaveChangesAsync();
        }
        FindViewById<EditText>(Resource.Id.name)!.Text = "";
        FindViewById<EditText>(Resource.Id.host)!.Text = "";
        await Load();
    }

    async Task Activate(ServerEntry s)
    {
        await BdsApp.Bridge.SetActiveServerAsync(s.IsActive ? null : s.Id, CancellationToken.None);
        if (!s.IsActive && !GatewayService.Running) GatewayService.Start(this);
        await Load();
    }

    async Task Delete(ServerEntry s)
    {
        await using (var ctx = await Db.CreateDbContextAsync())
        {
            await ctx.Servers.Where(x => x.Id == s.Id).ExecuteDeleteAsync();
        }
        if (s.IsActive) BdsApp.Bridge.Reload();
        await Load();
    }
}
