namespace Bds.Core.RakNet;

/// <summary>Parsed "MCPE;motd;protocol;version;players;max;guid;level;mode;..." pong string.</summary>
public sealed record ServerPong(string Motd, int Protocol, string Version, int Players, int MaxPlayers, string LevelName, string GameMode)
{
    public static ServerPong Parse(string raw)
    {
        var p = raw.Split(';');
        if (p.Length < 6) throw new FormatException("Invalid pong");
        return new ServerPong(
            p[1],
            int.TryParse(p[2], out var proto) ? proto : 0,
            p[3],
            int.TryParse(p[4], out var players) ? players : 0,
            int.TryParse(p[5], out var max) ? max : 0,
            p.Length > 7 ? p[7] : "",
            p.Length > 8 ? p[8] : "");
    }
}
