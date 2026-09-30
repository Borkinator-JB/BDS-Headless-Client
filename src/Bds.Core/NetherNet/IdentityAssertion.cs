using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Bds.Core.Util;

namespace Bds.Core.NetherNet;

/// <summary>
/// The <c>a=identity</c> SDP attribute NetherNet servers require (WebRTC identity, RFC 8827):
/// base64 of <c>{"assertion": "{\"fingerprints\":JWS,\"token\":multiplayerToken}", "idp": {"domain": issuer, "protocol": "default"}}</c>.
/// The fingerprints JWS has a detached payload and is signed with the key bound to the token.
/// </summary>
public static partial class IdentityAssertion
{
    public static string Add(string sdp, ECDsa key, string token, string issuer)
    {
        var fingerprints = new JsonArray();
        foreach (Match m in FingerprintRegex().Matches(sdp))
            fingerprints.Add(new JsonObject { ["algorithm"] = m.Groups[1].Value, ["digest"] = m.Groups[2].Value.Trim() });
        if (fingerprints.Count == 0) throw new InvalidOperationException("SDP has no fingerprint");

        var payload = Encoding.UTF8.GetBytes(new JsonObject { ["fingerprint"] = fingerprints }.ToJsonString());
        var header = Base64Url.Encode("{\"alg\":\"ES384\"}"u8.ToArray());
        var signature = key.SignData(Encoding.ASCII.GetBytes($"{header}.{Base64Url.Encode(payload)}"),
            HashAlgorithmName.SHA384, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        var assertion = JsonSerializer.Serialize(new { fingerprints = $"{header}..{Base64Url.Encode(signature)}", token });
        var envelope = JsonSerializer.Serialize(new { assertion, idp = new { domain = issuer, protocol = "default" } });
        var line = "a=identity:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(envelope));

        // Session level attribute, so it goes before the first media section.
        var media = sdp.IndexOf("m=", StringComparison.Ordinal);
        if (media < 0) throw new InvalidOperationException("SDP has no media section");
        return sdp.Insert(media, line + (sdp.Contains("\r\n") ? "\r\n" : "\n"));
    }

    [GeneratedRegex(@"^a=fingerprint:(\S+) ([0-9A-Fa-f:]+)\s*$", RegexOptions.Multiline)]
    private static partial Regex FingerprintRegex();
}
