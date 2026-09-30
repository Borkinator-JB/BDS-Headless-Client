using Android.Security.Keystore;
using Bds.Core.Auth;
using Java.Security;
using Javax.Crypto;
using Javax.Crypto.Spec;

namespace Bds.Android;

/// <summary>AES-GCM with a non-exportable key in the Android Keystore.</summary>
public sealed class KeystoreProtector : ISecretProtector
{
    const string Provider = "AndroidKeyStore";
    const string Alias = "bds-token-key";
    const string Transformation = "AES/GCM/NoPadding";
    const int IvLength = 12;

    static IKey GetKey()
    {
        var store = KeyStore.GetInstance(Provider)!;
        store.Load(null);
        if (store.GetKey(Alias, null) is { } existing) return existing;

        var generator = KeyGenerator.GetInstance(KeyProperties.KeyAlgorithmAes, Provider)!;
        generator.Init(new KeyGenParameterSpec.Builder(Alias, KeyStorePurpose.Encrypt | KeyStorePurpose.Decrypt)
            .SetBlockModes(KeyProperties.BlockModeGcm)
            .SetEncryptionPaddings(KeyProperties.EncryptionPaddingNone)
            .SetKeySize(256)
            .Build());
        return generator.GenerateKey()!;
    }

    public byte[] Protect(byte[] data)
    {
        var cipher = Cipher.GetInstance(Transformation)!;
        cipher.Init(CipherMode.EncryptMode, GetKey());
        var iv = cipher.GetIV()!;
        var encrypted = cipher.DoFinal(data)!;
        return [.. iv, .. encrypted];
    }

    public byte[] Unprotect(byte[] data)
    {
        var cipher = Cipher.GetInstance(Transformation)!;
        cipher.Init(CipherMode.DecryptMode, GetKey(), new GCMParameterSpec(128, data, 0, IvLength));
        return cipher.DoFinal(data, IvLength, data.Length - IvLength)!;
    }
}
