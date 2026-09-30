using System.Security.Cryptography;

namespace Bds.Core.Auth;

/// <summary>AES-256-GCM with a key-encryption key from a file (Linux: systemd credential).</summary>
public sealed class AesGcmProtector : ISecretProtector
{
    readonly byte[] _key;

    public AesGcmProtector(byte[] key)
    {
        if (key.Length < 32) throw new ArgumentException("Key must be at least 32 bytes", nameof(key));
        _key = SHA256.HashData(key);
    }

    public static AesGcmProtector FromFile(string path) => new(File.ReadAllBytes(path));

    public byte[] Protect(byte[] data)
    {
        var output = new byte[12 + 16 + data.Length];
        var nonce = output.AsSpan(0, 12);
        RandomNumberGenerator.Fill(nonce);
        using var gcm = new AesGcm(_key, 16);
        gcm.Encrypt(nonce, data, output.AsSpan(28), output.AsSpan(12, 16));
        return output;
    }

    public byte[] Unprotect(byte[] data)
    {
        if (data.Length < 28) throw new CryptographicException("Invalid secret file");
        var plain = new byte[data.Length - 28];
        using var gcm = new AesGcm(_key, 16);
        gcm.Decrypt(data.AsSpan(0, 12), data.AsSpan(28), data.AsSpan(12, 16), plain);
        return plain;
    }
}
