using Android.App;
using Android.Runtime;
using Bds.Core;
using Bds.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Bds.Android;

[Application]
public sealed class BdsApp(IntPtr handle, JniHandleOwnership ownership) : Application(handle, ownership)
{
    public static IServiceProvider Services { get; private set; } = null!;
    public static BridgeService Bridge => Services.GetRequiredService<BridgeService>();

    public override void OnCreate()
    {
        base.OnCreate();
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddProvider(new AppLogProvider()));
        services.AddBdsCore(FilesDir!.AbsolutePath, new KeystoreProtector());
        Services = services.BuildServiceProvider();
        AndroidEnvironment.UnhandledExceptionRaiser += (_, e) => AppLog.Add($"Crash: {e.Exception}");
        TaskScheduler.UnobservedTaskException += (_, e) => AppLog.Add($"Unobserved: {e.Exception}");
        _ = InitializeAsync();
    }

    static async Task InitializeAsync()
    {
        try
        {
            await Bridge.InitializeAsync(CancellationToken.None);
        }
        catch (Exception e)
        {
            AppLog.Add($"Init failed: {e}");
        }
    }
}
