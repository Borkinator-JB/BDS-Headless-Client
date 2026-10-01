using System.Security.Cryptography;
using Bds.Core.Auth;

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

    [Theory]
    [InlineData("26.51", "26.51.0")]
    [InlineData("1.21.100", "1.21.100")]
    [InlineData("v1.21", "1.21.0")]
    [InlineData("", "1.21.0")]
    public void Version_Is_Normalized(string input, string expected) =>
        Assert.Equal(expected, MinecraftServicesClient.NormalizeVersion(input));
}
