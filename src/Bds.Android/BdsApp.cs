using Android.App;
using Android.Runtime;
using Bds.Core;
using Bds.Core.Services;
using Microsoft.Extensions.DependencyInjection;

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
        services.AddLogging();
        services.AddBdsCore(FilesDir!.AbsolutePath, new KeystoreProtector());
        Services = services.BuildServiceProvider();
        _ = Bridge.InitializeAsync(CancellationToken.None);
    }
}
