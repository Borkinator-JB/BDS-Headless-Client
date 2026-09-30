using System.Buffers.Binary;
using System.Text;

namespace Bds.Core.Protocol;

public sealed class PacketWriter
{
    byte[] _buf;
    int _pos;

    public PacketWriter(int capacity = 256) => _buf = new byte[capacity];

    public int Length => _pos;
    public ReadOnlySpan<byte> Span => _buf.AsSpan(0, _pos);
    public byte[] ToArray() => _buf.AsSpan(0, _pos).ToArray();

    Span<byte> Take(int n)
    {
        if (_pos + n > _buf.Length) Array.Resize(ref _buf, Math.Max(_buf.Length * 2, _pos + n));
        var s = _buf.AsSpan(_pos, n);
        _pos += n;
        return s;
    }

    public PacketWriter Byte(byte v) { Take(1)[0] = v; return this; }
    public PacketWriter Bool(bool v) => Byte(v ? (byte)1 : (byte)0);
    public PacketWriter Bytes(ReadOnlySpan<byte> v) { v.CopyTo(Take(v.Length)); return this; }

    public PacketWriter UInt16LE(ushort v) { BinaryPrimitives.WriteUInt16LittleEndian(Take(2), v); return this; }
    public PacketWriter UInt16BE(ushort v) { BinaryPrimitives.WriteUInt16BigEndian(Take(2), v); return this; }
    public PacketWriter Int32LE(int v) { BinaryPrimitives.WriteInt32LittleEndian(Take(4), v); return this; }
    public PacketWriter Int32BE(int v) { BinaryPrimitives.WriteInt32BigEndian(Take(4), v); return this; }
    public PacketWriter Int64LE(long v) { BinaryPrimitives.WriteInt64LittleEndian(Take(8), v); return this; }
    public PacketWriter Int64BE(long v) { BinaryPrimitives.WriteInt64BigEndian(Take(8), v); return this; }
    public PacketWriter FloatLE(float v) { BinaryPrimitives.WriteSingleLittleEndian(Take(4), v); return this; }

    public PacketWriter UInt24LE(int v)
    {
        var s = Take(3);
        s[0] = (byte)v; s[1] = (byte)(v >> 8); s[2] = (byte)(v >> 16);
        return this;
    }

    public PacketWriter VarUInt(ulong v)
    {
        while (v >= 0x80) { Byte((byte)(v | 0x80)); v >>= 7; }
        return Byte((byte)v);
    }

    public PacketWriter VarInt(int v) => VarUInt((uint)((v << 1) ^ (v >> 31)));
    public PacketWriter VarLong(long v) => VarUInt((ulong)((v << 1) ^ (v >> 63)));

    public PacketWriter String(string v)
    {
        var bytes = Encoding.UTF8.GetBytes(v);
        VarUInt((ulong)bytes.Length);
        return Bytes(bytes);
    }

    public PacketWriter ByteArray(ReadOnlySpan<byte> v) { VarUInt((ulong)v.Length); return Bytes(v); }

    public PacketWriter Uuid(Guid g)
    {
        Span<byte> b = stackalloc byte[16];
        g.TryWriteBytes(b, bigEndian: true, out _);
        // Bedrock writes the two 64-bit halves little endian.
        b[..8].Reverse();
        b[8..].Reverse();
        return Bytes(b);
    }
}
