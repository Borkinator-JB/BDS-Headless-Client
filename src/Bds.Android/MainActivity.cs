using Android;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Provider;
using Android.Widget;
using AndroidX.AppCompat.App;
using AndroidX.Core.App;
using AndroidX.Core.Content;
using Bds.Core.Services;

namespace Bds.Android;

[Activity(Label = "@string/app_name", MainLauncher = true, LaunchMode = LaunchMode.SingleTop)]
public sealed class MainActivity : AppCompatActivity
{
    TextView _status = null!;
    Button _toggle = null!;
    TextView _playersTitle = null!;
    ArrayAdapter<string> _players = null!;
    ArrayAdapter<string> _joins = null!;

    static BridgeService Bridge => BdsApp.Bridge;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        SetContentView(Resource.Layout.activity_main);

        _status = FindViewById<TextView>(Resource.Id.status)!;
        _toggle = FindViewById<Button>(Resource.Id.toggle)!;
        _playersTitle = FindViewById<TextView>(Resource.Id.players_title)!;
        _players = new ArrayAdapter<string>(this, global::Android.Resource.Layout.SimpleListItem1);
        _joins = new ArrayAdapter<string>(this, global::Android.Resource.Layout.SimpleListItem1);
        FindViewById<ListView>(Resource.Id.players)!.Adapter = _players;
        FindViewById<ListView>(Resource.Id.joins)!.Adapter = _joins;

        _toggle.Click += (_, _) => Toggle();
        FindViewById<Button>(Resource.Id.servers)!.Click += (_, _) => StartActivity(typeof(ServersActivity));
        FindViewById<Button>(Resource.Id.friends)!.Click += (_, _) => StartActivity(typeof(FriendsActivity));
        FindViewById<Button>(Resource.Id.account)!.Click += (_, _) => StartActivity(typeof(AccountActivity));

        RequestNotificationPermission();
    }

    protected override void OnResume()
    {
        base.OnResume();
        Bridge.Changed += OnChanged;
        Bridge.Bot.Changed += OnChanged;
        Refresh();
    }

    protected override void OnPause()
    {
        Bridge.Changed -= OnChanged;
        Bridge.Bot.Changed -= OnChanged;
        base.OnPause();
    }

    void OnChanged() => RunOnUiThread(Refresh);

    void Refresh()
    {
        _status.Text = StatusText.Long(Bridge);
        _toggle.Text = GetString(GatewayService.Running ? Resource.String.stop : Resource.String.start);

        var players = Bridge.Bot.Players.Select(p => p.Name).ToList();
        _playersTitle.Text = $"{GetString(Resource.String.players)} ({players.Count})";
        _players.Clear();
        _players.AddAll(players);
        _joins.Clear();
        _joins.AddAll(Bridge.Gateway.RecentJoins.Select(j => $"{j.Name}  {j.Time.ToLocalTime():t}").ToList());
    }

    void Toggle()
    {
        if (GatewayService.Running)
        {
            GatewayService.Stop(this);
        }
        else
        {
            RequestBatteryExemption();
            GatewayService.Start(this);
        }
        _toggle.PostDelayed(Refresh, 300);
    }

    void RequestNotificationPermission()
    {
        if (Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu
            && ContextCompat.CheckSelfPermission(this, Manifest.Permission.PostNotifications) != Permission.Granted)
            ActivityCompat.RequestPermissions(this, [Manifest.Permission.PostNotifications], 1);
    }

    void RequestBatteryExemption()
    {
        var power = (PowerManager)GetSystemService(PowerService)!;
        if (power.IsIgnoringBatteryOptimizations(PackageName)) return;
        var intent = new Intent(Settings.ActionRequestIgnoreBatteryOptimizations)
            .SetData(global::Android.Net.Uri.Parse($"package:{PackageName}"));
        try
        {
            StartActivity(intent);
        }
        catch (ActivityNotFoundException)
        {
        }
    }
}
