using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Bds.Core.Protocol;

/// <summary>AES-256-CTR with the Bedrock SHA-256 checksum. One instance per direction.</summary>
public sealed class BedrockCipher : IDisposable
{
    readonly Aes _aes;
    readonly byte[] _key;
    readonly byte[] _counterBlock = new byte[16];
    readonly byte[] _keystream = new byte[16];
    int _keystreamPos = 16;
    long _packetCounter;

    public BedrockCipher(byte[] key)
    {
        _key = key;
        _aes = Aes.Create();
        _aes.Key = key;
        key.AsSpan(0, 12).CopyTo(_counterBlock);
        _counterBlock[15] = 2;
    }

    public static byte[] DeriveKey(ReadOnlySpan<byte> salt, ReadOnlySpan<byte> sharedSecret)
    {
        var buf = new byte[salt.Length + sharedSecret.Length];
        salt.CopyTo(buf);
        sharedSecret.CopyTo(buf.AsSpan(salt.Length));
        return SHA256.HashData(buf);
    }

    public byte[] Encrypt(ReadOnlySpan<byte> plain)
    {
        var output = new byte[plain.Length + 8];
        plain.CopyTo(output);
        Checksum(plain, _packetCounter++).CopyTo(output.AsSpan(plain.Length));
        Xor(output);
        return output;
    }

    public byte[] Decrypt(ReadOnlySpan<byte> data)
    {
        if (data.Length < 8) throw new InvalidDataException("Encrypted packet too short");
        var buf = data.ToArray();
        Xor(buf);
        var plain = buf.AsSpan(0, buf.Length - 8);
        if (!Checksum(plain, _packetCounter++).AsSpan().SequenceEqual(buf.AsSpan(buf.Length - 8)))
            throw new InvalidDataException("Invalid packet checksum");
        return plain.ToArray();
    }

    byte[] Checksum(ReadOnlySpan<byte> plain, long counter)
    {
        var buf = new byte[8 + plain.Length + _key.Length];
        BinaryPrimitives.WriteInt64LittleEndian(buf, counter);
        plain.CopyTo(buf.AsSpan(8));
        _key.CopyTo(buf.AsSpan(8 + plain.Length));
        return SHA256.HashData(buf)[..8];
    }

    void Xor(Span<byte> data)
    {
        for (var i = 0; i < data.Length; i++)
        {
            if (_keystreamPos == 16)
            {
                _aes.EncryptEcb(_counterBlock, _keystream, PaddingMode.None);
                IncrementCounter();
                _keystreamPos = 0;
            }
            data[i] ^= _keystream[_keystreamPos++];
        }
    }

    void IncrementCounter()
    {
        for (var i = 15; i >= 12; i--)
            if (++_counterBlock[i] != 0) break;
    }

    public void Dispose() => _aes.Dispose();
}
