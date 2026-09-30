using Bds.Core.Auth;
using Bds.Core.Services;

namespace Bds.Android;

static class StatusText
{
    public static string Short(BridgeService bridge)
    {
        if (bridge.Account.State != AccountState.SignedIn) return "Not signed in";
        if (bridge.ActiveServer is null) return "No server selected";
        return $"{bridge.ActiveServer.Name}: {bridge.Bot.State}, {bridge.Bot.Players.Count} players";
    }

    public static string Long(BridgeService bridge)
    {
        var lines = new List<string>
        {
            bridge.Account.State == AccountState.SignedIn ? $"Account: {bridge.Account.Gamertag}" : "Account: not signed in",
            $"Server: {bridge.ActiveServer?.Name ?? "none"}",
            $"Bot: {bridge.Bot.State} {bridge.Bot.Status}",
            $"Friends can join: {bridge.Gateway.State} {bridge.Gateway.Status}",
        };
        if (bridge.Bot.Pong is { } pong) lines.Add($"{pong.Motd} · {pong.Version} · {pong.Players}/{pong.MaxPlayers}");
        return string.Join('\n', lines);
    }
}
