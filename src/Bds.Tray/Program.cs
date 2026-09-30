using Avalonia;

namespace Bds.Tray;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        using var single = new Mutex(true, "BdsHeadlessTray", out var first);
        var background = args.Contains("--background");
        if (!first)
        {
            // Tray already running: just show the UI.
            if (!background) ServiceControl.OpenUi();
            return 0;
        }
        App.OpenOnStart = !background;
        return AppBuilder.Configure<App>().UsePlatformDetect().StartWithClassicDesktopLifetime(args,
            lifetime => lifetime.ShutdownMode = Avalonia.Controls.ShutdownMode.OnExplicitShutdown);
    }
}
