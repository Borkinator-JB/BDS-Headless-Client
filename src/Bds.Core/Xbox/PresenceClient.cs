using System.Text.Json.Nodes;

namespace Bds.Core.Xbox;

/// <summary>"Playing Minecraft" presence. Without it friends see the account as offline.</summary>
public sealed class PresenceClient(XboxHttp xbox)
{
    public Task SetActiveAsync(string xuid, CancellationToken ct) =>
        xbox.SendAsync(HttpMethod.Post,
            $"https://userpresence.xboxlive.com/users/xuid({xuid})/devices/current/titles/current", 3,
            new JsonObject { ["state"] = "active" }, ct);
}
