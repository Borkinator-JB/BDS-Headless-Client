using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Threading;

namespace Bds.Tray;

public partial class App : Application
{
    public static bool OpenOnStart { get; set; } = true;

    TrayIcon? _tray;
    readonly CancellationTokenSource _cts = new();

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        var stop = new NativeMenuItem("Stop service");
        stop.Click += async (_, _) => await ServiceControl.StopAsync();
        var open = new NativeMenuItem("Open");
        open.Click += (_, _) => _ = StartAndOpenAsync();
        var exit = new NativeMenuItem("Exit");
        exit.Click += (_, _) => Exit();

        _tray = new TrayIcon
        {
            Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://bds-headless/Assets/icon.png"))),
            ToolTipText = "BDS Headless",
            Menu = [open, new NativeMenuItemSeparator(), stop, exit],
            IsVisible = true,
        };
        _tray.Clicked += (_, _) => _ = StartAndOpenAsync();
        TrayIcon.SetIcons(this, [_tray]);

        _ = RunAsync();
        base.OnFrameworkInitializationCompleted();
    }

    async Task RunAsync()
    {
        if (OpenOnStart) await StartAndOpenAsync();
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        do
        {
            var status = await ServiceControl.GetStatusAsync();
            Dispatcher.UIThread.Post(() => _tray!.ToolTipText = status);
        } while (await timer.WaitForNextTickAsync(_cts.Token).AsTask().ContinueWith(t => !t.IsCanceled && t.Result));
    }

    static async Task StartAndOpenAsync()
    {
        if (await ServiceControl.EnsureRunningAsync()) ServiceControl.OpenUi();
    }

    void Exit()
    {
        // Only closes the tray. The service keeps running.
        _cts.Cancel();
        if (_tray is not null) _tray.IsVisible = false;
        (ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
    }
}
