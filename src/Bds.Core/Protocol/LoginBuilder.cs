using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Bds.Core.Util;

namespace Bds.Core.Protocol;

/// <summary>Builds the identity and client data parts of the Login packet.</summary>
public static class LoginBuilder
{
    // 1.21.90 moved the chain into "Certificate" and added a signed token.
    public const int ProtocolTokenLogin = 818;

    public static string Identity(IReadOnlyList<string> mojangChain, ECDsa key, string? multiplayerToken, int protocol)
    {
        var mojangKey = Jwt.DecodeUnverified(mojangChain[0]).Header["x5u"]!.GetValue<string>();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var selfSigned = Jwt.SignEs384(
            new JsonObject { ["alg"] = "ES384", ["x5u"] = Jwt.ExportX5u(key) },
            new JsonObject
            {
                ["certificateAuthority"] = true,
                ["exp"] = now + 6 * 3600,
                ["identityPublicKey"] = mojangKey,
                ["nbf"] = now - 6 * 3600,
            },
            key);

        var chain = new JsonObject { ["chain"] = new JsonArray([selfSigned, .. mojangChain.Select(c => (JsonNode)c)]) };
        if (protocol < ProtocolTokenLogin) return chain.ToJsonString();
        return new JsonObject
        {
            ["AuthenticationType"] = 0,
            ["Certificate"] = chain.ToJsonString(),
            ["Token"] = multiplayerToken ?? "",
        }.ToJsonString();
    }

    public static string ClientData(ECDsa key, string gamertag, string gameVersion, string serverAddress, Guid deviceId)
    {
        var skin = new byte[64 * 64 * 4];
        for (var i = 0; i < skin.Length; i += 4)
        {
            skin[i] = 0x60; skin[i + 1] = 0x60; skin[i + 2] = 0x60; skin[i + 3] = 0xFF;
        }
        var claims = new JsonObject
        {
            ["AnimatedImageData"] = new JsonArray(),
            ["ArmSize"] = "wide",
            ["CapeData"] = "",
            ["CapeId"] = "",
            ["CapeImageHeight"] = 0,
            ["CapeImageWidth"] = 0,
            ["CapeOnClassicSkin"] = false,
            ["ClientRandomId"] = Random.Shared.NextInt64(),
            ["CompatibleWithClientSideChunkGen"] = false,
            ["CurrentInputMode"] = 1,
            ["DefaultInputMode"] = 1,
            ["DeviceId"] = deviceId.ToString(),
            ["DeviceModel"] = "BDS Headless Client",
            ["DeviceOS"] = 1,
            ["GameVersion"] = gameVersion,
            ["GuiScale"] = 0,
            ["IsEditorMode"] = false,
            ["LanguageCode"] = "en_US",
            ["MaxViewDistance"] = 4,
            ["MemoryTier"] = 0,
            ["OverrideSkin"] = false,
            ["PersonaPieces"] = new JsonArray(),
            ["PersonaSkin"] = false,
            ["PieceTintColors"] = new JsonArray(),
            ["PlatformOfflineId"] = "",
            ["PlatformOnlineId"] = "",
            ["PlatformType"] = 0,
            ["PlayFabId"] = "",
            ["PremiumSkin"] = false,
            ["SelfSignedId"] = Guid.NewGuid().ToString(),
            ["ServerAddress"] = serverAddress,
            ["SkinAnimationData"] = "",
            ["SkinColor"] = "#0",
            ["SkinData"] = Convert.ToBase64String(skin),
            ["SkinGeometryData"] = "",
            ["SkinGeometryDataEngineVersion"] = Convert.ToBase64String("0.0.0"u8),
            ["SkinId"] = "Standard_Custom",
            ["SkinImageHeight"] = 64,
            ["SkinImageWidth"] = 64,
            ["SkinResourcePatch"] = Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"geometry\":{\"default\":\"geometry.humanoid.custom\"}}")),
            ["ThirdPartyName"] = gamertag,
            ["ThirdPartyNameOnly"] = false,
            ["TrustedSkin"] = false,
            ["UIProfile"] = 0,
        };
        return Jwt.SignEs384(new JsonObject { ["alg"] = "ES384", ["x5u"] = Jwt.ExportX5u(key) }, claims, key);
    }

    /// <summary>Reads display name and XUID from a received login. Used only for logging.</summary>
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

    public static byte[] SharedSecret(ECDiffieHellman local, string serverX5u)
    {
        using var remote = ECDiffieHellman.Create();
        remote.ImportSubjectPublicKeyInfo(Convert.FromBase64String(serverX5u), out _);
        return local.DeriveRawSecretAgreement(remote.PublicKey);
    }
}
