using System.Buffers.Binary;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Bds.Core.Util;

namespace Bds.Core.Auth;

public sealed record XstsToken(string Token, string UserHash, string? Xuid, string? Gamertag, DateTimeOffset NotAfter)
{
    public string Header => $"XBL3.0 x={UserHash};{Token}";
}

/// <summary>Xbox device token + SISU authorization with proof of possession signing.</summary>
public sealed class XboxClient(HttpClient http)
{
    const string DeviceAuthUrl = "https://device.auth.xboxlive.com/device/authenticate";
    const string SisuUrl = "https://sisu.xboxlive.com/authorize";

    public async Task<(string Token, DateTimeOffset NotAfter)> GetDeviceTokenAsync(ECDsa key, Guid deviceId, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["Properties"] = new JsonObject
            {
                ["AuthMethod"] = "ProofOfPossession",
                ["Id"] = "{" + deviceId + "}",
                ["DeviceType"] = "Android",
                ["SerialNumber"] = "{" + deviceId + "}",
                ["Version"] = "10",
                ["ProofKey"] = Jwk(key),
            },
            ["RelyingParty"] = "http://auth.xboxlive.com",
            ["TokenType"] = "JWT",
        };
        var res = await SendSignedAsync(DeviceAuthUrl, body, key, ct);
        return (res["Token"]!.GetValue<string>(), DateTimeOffset.Parse(res["NotAfter"]!.GetValue<string>()));
    }

    public async Task<XstsToken> AuthorizeAsync(string msaAccessToken, string deviceToken, string relyingParty, ECDsa key, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["AccessToken"] = "t=" + msaAccessToken,
            ["AppId"] = AuthConstants.ClientId,
            ["DeviceToken"] = deviceToken,
            ["Sandbox"] = "RETAIL",
            ["UseModernGamertag"] = true,
            ["SiteName"] = "user.auth.xboxlive.com",
            ["RelyingParty"] = relyingParty,
            ["ProofKey"] = Jwk(key),
        };
        var res = await SendSignedAsync(SisuUrl, body, key, ct);
        var auth = res["AuthorizationToken"] ?? throw new AuthException("No authorization token");
        var xui = auth["DisplayClaims"]?["xui"]?[0];
        return new XstsToken(
            auth["Token"]!.GetValue<string>(),
            xui?["uhs"]?.GetValue<string>() ?? throw new AuthException("No user hash"),
            xui["xid"]?.GetValue<string>(),
            xui["gtg"]?.GetValue<string>(),
            DateTimeOffset.Parse(auth["NotAfter"]!.GetValue<string>()));
    }

    async Task<JsonNode> SendSignedAsync(string url, JsonObject body, ECDsa key, CancellationToken ct)
    {
        var json = body.ToJsonString();
        var uri = new Uri(url);
        using var req = new HttpRequestMessage(HttpMethod.Post, uri);
        req.Content = new StringContent(json, Encoding.UTF8);
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        req.Headers.Add("x-xbl-contract-version", "1");
        req.Headers.Add("Signature", Sign(key, "POST", uri.PathAndQuery, "", Encoding.UTF8.GetBytes(json)));
        using var res = await http.SendAsync(req, ct);
        if (!res.IsSuccessStatusCode)
        {
            var xerr = res.Headers.TryGetValues("x-err", out var v) ? v.FirstOrDefault() : null;
            throw new AuthException(XErrMessage(xerr) ?? $"Xbox auth failed ({(int)res.StatusCode})");
        }
        return JsonNode.Parse(await res.Content.ReadAsStringAsync(ct)) ?? throw new AuthException("Empty Xbox response");
    }

    static string? XErrMessage(string? xerr) => xerr switch
    {
        "2148916233" => "This Microsoft account has no Xbox profile. Sign in once on xbox.com first.",
        "2148916235" => "Xbox Live is not available in this account's country.",
        "2148916236" or "2148916237" => "This account needs adult verification.",
        "2148916238" => "Child accounts must be added to a family first.",
        null => null,
        _ => $"Xbox auth failed (XErr {xerr})",
    };

    internal static string Sign(ECDsa key, string method, string pathAndQuery, string authorization, byte[] body)
    {
        var timestamp = DateTime.UtcNow.ToFileTimeUtc();
        using var ms = new MemoryStream();
        Span<byte> tmp = stackalloc byte[8];
        BinaryPrimitives.WriteInt32BigEndian(tmp, 1);
        ms.Write(tmp[..4]);
        ms.WriteByte(0);
        BinaryPrimitives.WriteInt64BigEndian(tmp, timestamp);
        ms.Write(tmp);
        ms.WriteByte(0);
        foreach (var part in new[] { Encoding.ASCII.GetBytes(method), Encoding.ASCII.GetBytes(pathAndQuery), Encoding.ASCII.GetBytes(authorization), body })
        {
            ms.Write(part);
            ms.WriteByte(0);
        }
        var sig = key.SignData(ms.ToArray(), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

        var header = new byte[12 + sig.Length];
        BinaryPrimitives.WriteInt32BigEndian(header, 1);
        BinaryPrimitives.WriteInt64BigEndian(header.AsSpan(4), timestamp);
        sig.CopyTo(header, 12);
        return Convert.ToBase64String(header);
    }

    static JsonObject Jwk(ECDsa key)
    {
        var p = key.ExportParameters(false);
        return new JsonObject
        {
            ["crv"] = "P-256",
            ["alg"] = "ES256",
            ["use"] = "sig",
            ["kty"] = "EC",
            ["x"] = Base64Url.Encode(p.Q.X),
            ["y"] = Base64Url.Encode(p.Q.Y),
        };
    }
}
