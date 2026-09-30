using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Net;
using Android.Net.Wifi;
using Android.OS;
using Android.Runtime;
using AndroidX.Core.App;
using Bds.Core.Services;

namespace Bds.Android;

/// <summary>Foreground service that keeps the bridge running when the app is closed or swiped away.</summary>
[Register("io.github.bdsheadless.GatewayService")]
public sealed class GatewayService : Service
{
    public const string ActionStop = "io.github.bdsheadless.STOP";
    const string ChannelId = "gateway";
    const int NotificationId = 1;

    static readonly TimeSpan NotifyThrottle = TimeSpan.FromSeconds(2);

    CancellationTokenSource? _cts;
    PowerManager.WakeLock? _wakeLock;
    WifiManager.WifiLock? _wifiLock;
    NetworkCallback? _network;
    DateTime _lastNotify;

    public static bool Running { get; private set; }

    public static void Start(Context context)
    {
        var intent = new Intent(context, typeof(GatewayService));
        if (Build.VERSION.SdkInt >= BuildVersionCodes.O) context.StartForegroundService(intent);
        else context.StartService(intent);
    }

    public static void Stop(Context context) =>
        context.StartService(new Intent(context, typeof(GatewayService)).SetAction(ActionStop));

    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, [GeneratedEnum] StartCommandFlags flags, int startId)
    {
        if (intent?.Action == ActionStop)
        {
            StopSelf();
            return StartCommandResult.NotSticky;
        }

        CreateChannel();
        if (OperatingSystem.IsAndroidVersionAtLeast(34))
            StartForeground(NotificationId, BuildNotification(), ForegroundService.TypeSpecialUse);
        else
            StartForeground(NotificationId, BuildNotification());

        if (_cts is null)
        {
            Running = true;
            AcquireLocks();
            _cts = new CancellationTokenSource();
            var bridge = BdsApp.Bridge;
            bridge.Changed += UpdateNotification;
            _ = Task.Run(() => bridge.RunAsync(_cts.Token));

            _network = new NetworkCallback(bridge);
            ((ConnectivityManager)GetSystemService(ConnectivityService)!).RegisterDefaultNetworkCallback(_network);
        }
        return StartCommandResult.Sticky;
    }

    void AcquireLocks()
    {
        var power = (PowerManager)GetSystemService(PowerService)!;
        _wakeLock = power.NewWakeLock(WakeLockFlags.Partial, "bdsheadless:gateway");
        _wakeLock!.Acquire();

        var wifi = (WifiManager)ApplicationContext!.GetSystemService(WifiService)!;
        var mode = OperatingSystem.IsAndroidVersionAtLeast(29) ? WifiMode.FullLowLatency : WifiMode.FullHighPerf;
        _wifiLock = wifi.CreateWifiLock(mode, "bdsheadless:gateway");
        _wifiLock!.Acquire();
    }

    void UpdateNotification()
    {
        if (DateTime.UtcNow - _lastNotify < NotifyThrottle) return;
        _lastNotify = DateTime.UtcNow;
        NotificationManagerCompat.From(this)!.Notify(NotificationId, BuildNotification());
    }

    Notification BuildNotification()
    {
        var bridge = BdsApp.Bridge;
        var text = StatusText.Short(bridge);

        var open = PendingIntent.GetActivity(this, 0, new Intent(this, typeof(MainActivity)), PendingIntentFlags.Immutable);
        var stop = PendingIntent.GetService(this, 1, new Intent(this, typeof(GatewayService)).SetAction(ActionStop), PendingIntentFlags.Immutable);

        return new NotificationCompat.Builder(this, ChannelId)
            .SetContentTitle(GetString(Resource.String.app_name))!
            .SetContentText(text)!
            .SetSmallIcon(Resource.Drawable.ic_notification)!
            .SetOngoing(true)!
            .SetOnlyAlertOnce(true)!
            .SetContentIntent(open)!
            .AddAction(0, GetString(Resource.String.stop), stop)!
            .SetForegroundServiceBehavior(NotificationCompat.ForegroundServiceImmediate)!
            .Build()!;
    }

    void CreateChannel()
    {
        if (Build.VERSION.SdkInt < BuildVersionCodes.O) return;
        var channel = new NotificationChannel(ChannelId, GetString(Resource.String.channel_name), NotificationImportance.Low);
        ((NotificationManager)GetSystemService(NotificationService)!).CreateNotificationChannel(channel);
    }

    public override void OnDestroy()
    {
        Running = false;
        BdsApp.Bridge.Changed -= UpdateNotification;
        _cts?.Cancel();
        _cts = null;
        if (_network is not null)
            ((ConnectivityManager)GetSystemService(ConnectivityService)!).UnregisterNetworkCallback(_network);
        if (_wakeLock?.IsHeld == true) _wakeLock.Release();
        if (_wifiLock?.IsHeld == true) _wifiLock.Release();
        base.OnDestroy();
    }

    sealed class NetworkCallback(BridgeService bridge) : ConnectivityManager.NetworkCallback
    {
        bool _first = true;

        public override void OnAvailable(Network network)
        {
            // Registration reports the current network once; only react to real changes.
            if (_first)
            {
                _first = false;
                return;
            }
            bridge.Reload();
        }
    }
}
