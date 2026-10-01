using System.Text.Json.Nodes;
using Bds.Core.Util;

namespace Bds.Core.Protocol;

/// <summary>Reads the identity part of a received Login packet.</summary>
public static class LoginBuilder
{
    /// <summary>Reads display name and XUID from a received login. Not verified.</summary>
    public static (string? Name, string? Xuid) ReadIdentity(string identity)
    {
        try
        {
            var root = JsonNode.Parse(identity);
            var chainJson = root?["Certificate"]?.GetValue<string>() is { } cert ? JsonNode.Parse(cert) : root;
            foreach (var token in chainJson?["chain"]?.AsArray() ?? [])
            {
                var extra = Jwt.DecodeUnverified(token!.GetValue<string>()).Payload["extraData"];
                if (extra is not null)
                    return (extra["displayName"]?.GetValue<string>(), extra["XUID"]?.GetValue<string>());
            }
            if (root?["Token"]?.GetValue<string>() is { Length: > 0 } tok)
            {
                var p = Jwt.DecodeUnverified(tok).Payload;
                return (p["xname"]?.GetValue<string>(), p["xid"]?.GetValue<string>());
            }
        }
        catch (Exception e) when (e is FormatException or System.Text.Json.JsonException or InvalidOperationException)
        {
        }
        return (null, null);
    }
}
