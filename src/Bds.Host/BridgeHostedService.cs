using Bds.Core.Services;

namespace Bds.Host;

public sealed class BridgeHostedService(BridgeService bridge) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => bridge.RunAsync(stoppingToken);
}
