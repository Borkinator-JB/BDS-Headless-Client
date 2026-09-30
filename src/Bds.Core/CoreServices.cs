using Bds.Core.Auth;
using Bds.Core.Services;
using Bds.Core.Storage;
using Bds.Core.Xbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Bds.Core;

public static class CoreServices
{
    public static IServiceCollection AddBdsCore(this IServiceCollection services, string dataDirectory, ISecretProtector protector)
    {
        Directory.CreateDirectory(dataDirectory);
        services.AddDbContextFactory<AppDbContext>(o => o.UseSqlite($"Data Source={Path.Combine(dataDirectory, "bds.db")}"));
        services.AddSingleton(new HttpClient { Timeout = TimeSpan.FromSeconds(30) });
        services.AddSingleton<ITokenStore>(new FileTokenStore(Path.Combine(dataDirectory, "secure", "account.bin"), protector));
        services.AddSingleton<SettingsStore>();
        services.AddSingleton<XboxAccount>();
        services.AddSingleton<XboxHttp>();
        services.AddSingleton<ServerBot>();
        services.AddSingleton<FriendGateway>();
        services.AddSingleton<FriendManager>();
        services.AddSingleton<BridgeService>();
        return services;
    }
}
