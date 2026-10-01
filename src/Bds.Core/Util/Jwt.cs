using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Bds.Core.Util;

public static class Jwt
{
    public static string SignEs384(JsonObject header, JsonObject payload, ECDsa key)
    {
        var h = Base64Url.Encode(Encoding.UTF8.GetBytes(header.ToJsonString()));
        var p = Base64Url.Encode(Encoding.UTF8.GetBytes(payload.ToJsonString()));
        return $"{h}.{p}.{SignatureEs384(key, $"{h}.{p}")}";
    }

    public static string SignatureEs384(ECDsa key, string signingInput) => Base64Url.Encode(
        key.SignData(Encoding.ASCII.GetBytes(signingInput), HashAlgorithmName.SHA384, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));

    public static (JsonObject Header, JsonObject Payload) DecodeUnverified(string jwt)
    {
        var parts = jwt.Split('.');
        if (parts.Length != 3) throw new FormatException("Invalid JWT");
        return (Parse(parts[0]), Parse(parts[1]));
    }

    static JsonObject Parse(string part) =>
        JsonNode.Parse(Base64Url.Decode(part))?.AsObject() ?? throw new FormatException("Invalid JWT part");
}
