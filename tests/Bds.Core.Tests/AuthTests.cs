using System.Security.Cryptography;
using Bds.Core.Auth;
using Bds.Core.Protocol;
using Bds.Core.Util;

namespace Bds.Core.Tests;

public class AuthTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void AesGcm_RoundTrips_And_Detects_Tamper()
    {
        var p = new AesGcmProtector(RandomNumberGenerator.GetBytes(32));
        var secret = "refresh-token"u8.ToArray();
        var sealedData = p.Protect(secret);
        Assert.Equal(secret, p.Unprotect(sealedData));

        sealedData[^1] ^= 1;
        Assert.ThrowsAny<CryptographicException>(() => p.Unprotect(sealedData));
    }

    [Fact]
    public async Task FileStore_Saves_Encrypted()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(dir.FullName, "a.bin");
            var store = new FileTokenStore(path, new AesGcmProtector(RandomNumberGenerator.GetBytes(32)));
            await store.SaveAsync(new TokenState { RefreshToken = "secret-refresh", DeviceKey = [1, 2], DeviceId = Guid.NewGuid() }, Ct);

            Assert.DoesNotContain("secret-refresh", System.Text.Encoding.UTF8.GetString(await File.ReadAllBytesAsync(path, Ct)));
            Assert.Equal("secret-refresh", (await store.LoadAsync(Ct))!.RefreshToken);

            await store.DeleteAsync(Ct);
            Assert.Null(await store.LoadAsync(Ct));
        }
        finally
        {
            dir.Delete(true);
        }
    }

    [Fact]
    public void Signature_Header_Has_Expected_Layout()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var header = Convert.FromBase64String(XboxClient.Sign(key, "POST", "/authorize", "", "{}"u8.ToArray()));
        Assert.Equal(12 + 64, header.Length);
        Assert.Equal(new byte[] { 0, 0, 0, 1 }, header[..4]);
    }

    [Fact]
    public void Identity_Chain_Is_Signed_And_Readable()
    {
        using var mojang = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        var mojangToken = Jwt.SignEs384(
            new() { ["alg"] = "ES384", ["x5u"] = Jwt.ExportX5u(mojang) },
            new() { ["extraData"] = new System.Text.Json.Nodes.JsonObject { ["displayName"] = "Bot", ["XUID"] = "42" } },
            mojang);

        var legacy = LoginBuilder.Identity([mojangToken], key, null, 700);
        var modern = LoginBuilder.Identity([mojangToken], key, "tok", LoginBuilder.ProtocolTokenLogin);

        Assert.Equal(("Bot", "42"), LoginBuilder.ReadIdentity(legacy));
        Assert.Equal(("Bot", "42"), LoginBuilder.ReadIdentity(modern));
    }

    [Fact]
    public void Ecdh_Shared_Secret_Matches()
    {
        using var a = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP384);
        using var b = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP384);
        var x5uB = Convert.ToBase64String(b.ExportSubjectPublicKeyInfo());
        var x5uA = Convert.ToBase64String(a.ExportSubjectPublicKeyInfo());
        Assert.Equal(LoginBuilder.SharedSecret(a, x5uB), LoginBuilder.SharedSecret(b, x5uA));
    }
}
