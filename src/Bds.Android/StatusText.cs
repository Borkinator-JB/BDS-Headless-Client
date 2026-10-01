using Bds.Core.Auth;
using Bds.Core.Services;

namespace Bds.Android;

static class StatusText
{
    public static string Short(BridgeService bridge)
    {
        if (bridge.Account.State == AccountState.WaitingForCode) return "Waiting for sign in";
        if (bridge.Account.State != AccountState.SignedIn) return "Not signed in";
        if (bridge.ActiveServer is null) return "No server selected";
        return bridge.Bot.Pong is { } pong
            ? $"{bridge.ActiveServer.Name}: {bridge.Bot.State}, {pong.Players}/{pong.MaxPlayers} players"
            : $"{bridge.ActiveServer.Name}: {bridge.Bot.State}";
    }

    public static string Long(BridgeService bridge)
    {
        var lines = new List<string>
        {
            bridge.Account.State switch
            {
                AccountState.SignedIn => $"Account: {bridge.Account.Gamertag}",
                AccountState.WaitingForCode => "Account: waiting for sign in",
                AccountState.Error => $"Account error: {bridge.Account.Error}",
                _ => "Account: not signed in",
            },
            $"Server: {bridge.ActiveServer?.Name ?? "none"}",
            $"Server status: {bridge.Bot.State} {bridge.Bot.Status}",
            $"Friends can join: {bridge.Gateway.State} {bridge.Gateway.Status}",
        };
        if (bridge.Bot.Pong is { } pong) lines.Add($"{pong.Motd} · {pong.Version} · {pong.Players}/{pong.MaxPlayers}");
        return string.Join('\n', lines);
    }
}
