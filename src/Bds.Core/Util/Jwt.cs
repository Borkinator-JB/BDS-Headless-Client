using System.Text.Json.Nodes;

namespace Bds.Core.Util;

public static class Jwt
{
    public static (JsonObject Header, JsonObject Payload) DecodeUnverified(string jwt)
    {
        var parts = jwt.Split('.');
        if (parts.Length != 3) throw new FormatException("Invalid JWT");
        return (Parse(parts[0]), Parse(parts[1]));
    }

    static JsonObject Parse(string part) =>
        JsonNode.Parse(Base64Url.Decode(part))?.AsObject() ?? throw new FormatException("Invalid JWT part");
}
