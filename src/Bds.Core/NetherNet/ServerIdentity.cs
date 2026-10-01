using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Bds.Core.Util;

namespace Bds.Core.NetherNet;

/// <summary>
/// Self-issued <c>a=identity</c> for our SDP answers. Clients reject answers without it.
/// Same layout as bedrock-portal-nethernet's signServerIdentity.
/// </summary>
public sealed class ServerIdentity : IDisposable
{
    readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP384);
    readonly string _publicKey;

    public ServerIdentity() => _publicKey = Convert.ToBase64String(_key.ExportSubjectPublicKeyInfo());

    public string Sign(string sdp)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var token = Jwt.SignEs384(
            new JsonObject { ["alg"] = "ES384", ["x5u"] = _publicKey },
            new JsonObject { ["cpk"] = _publicKey, ["iat"] = now, ["exp"] = now + 60 },
            _key);

        // Detached JWS over the first fingerprint, as the client rebuilds it.
        var fingerprint = Attribute(sdp, "fingerprint") ?? throw new InvalidOperationException("SDP has no fingerprint");
        var space = fingerprint.IndexOf(' ');
        if (space < 0) throw new InvalidOperationException("Malformed SDP fingerprint");
        var payload = new JsonObject
        {
            ["fingerprint"] = new JsonArray(new JsonObject
            {
                ["algorithm"] = fingerprint[..space],
                ["digest"] = fingerprint[(space + 1)..],
            }),
        }.ToJsonString();
        var header = Base64Url.Encode("{\"alg\":\"ES384\"}"u8.ToArray());
        var signature = Jwt.SignatureEs384(_key, $"{header}.{Base64Url.Encode(Encoding.UTF8.GetBytes(payload))}");

        var assertion = new JsonObject { ["fingerprints"] = $"{header}..{signature}", ["token"] = token }.ToJsonString();
        var value = new JsonObject
        {
            ["assertion"] = assertion,
            ["idp"] = new JsonObject { ["domain"] = "self", ["protocol"] = "default" },
        }.ToJsonString();
        var line = "a=identity:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(value));

        // Session level, so before the first media section.
        var media = sdp.IndexOf("m=", StringComparison.Ordinal);
        if (media < 0) throw new InvalidOperationException("SDP has no media section");
        return sdp.Insert(media, line + (sdp.Contains("\r\n") ? "\r\n" : "\n"));
    }

    static string? Attribute(string sdp, string name) => sdp.Split('\n')
        .Select(l => l.TrimEnd('\r'))
        .FirstOrDefault(l => l.StartsWith($"a={name}:", StringComparison.Ordinal))?[(name.Length + 3)..].Trim();

    public void Dispose() => _key.Dispose();
}
