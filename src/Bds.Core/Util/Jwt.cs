using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Bds.Core.Util;

public static class Jwt
{
    public static string SignEs384(JsonObject header, JsonObject payload, ECDsa key)
    {
        var h = Base64Url.Encode(Encoding.UTF8.GetBytes(header.ToJsonString()));
        var p = Base64Url.Encode(Encoding.UTF8.GetBytes(payload.ToJsonString()));
        var sig = key.SignData(Encoding.ASCII.GetBytes($"{h}.{p}"), HashAlgorithmName.SHA384, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return $"{h}.{p}.{Base64Url.Encode(sig)}";
    }

    public static (JsonObject Header, JsonObject Payload) DecodeUnverified(string jwt)
    {
        var parts = jwt.Split('.');
        if (parts.Length != 3) throw new FormatException("Invalid JWT");
        return (Parse(parts[0]), Parse(parts[1]));
    }

    public static bool VerifyEs384(string jwt, ECDsa key)
    {
        var parts = jwt.Split('.');
        if (parts.Length != 3) return false;
        return key.VerifyData(Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}"), Base64Url.Decode(parts[2]),
            HashAlgorithmName.SHA384, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }

    public static ECDsa ImportX5u(string x5u)
    {
        var key = ECDsa.Create();
        key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(x5u), out _);
        return key;
    }

    public static string ExportX5u(ECDsa key) => Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());

    static JsonObject Parse(string part) =>
        JsonNode.Parse(Base64Url.Decode(part))?.AsObject() ?? throw new FormatException("Invalid JWT part");
}
