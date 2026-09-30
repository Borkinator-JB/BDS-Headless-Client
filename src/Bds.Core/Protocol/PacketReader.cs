using System.Buffers.Binary;
using System.Text;

namespace Bds.Core.Protocol;

public sealed class PacketReader(ReadOnlyMemory<byte> data)
{
    int _pos;

    public int Position { get => _pos; set => _pos = value; }
    public int Remaining => data.Length - _pos;
    public ReadOnlySpan<byte> Rest => data.Span[_pos..];

    ReadOnlySpan<byte> Take(int n)
    {
        if (n < 0 || _pos + n > data.Length) throw new EndOfStreamException();
        var s = data.Span.Slice(_pos, n);
        _pos += n;
        return s;
    }

    public void Skip(int n) => Take(n);
    public byte Byte() => Take(1)[0];
    public bool Bool() => Byte() != 0;
    public byte[] Bytes(int n) => Take(n).ToArray();
    public ReadOnlyMemory<byte> Slice(int n) { var m = data.Slice(_pos, n); Take(n); return m; }

    public ushort UInt16LE() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));
    public ushort UInt16BE() => BinaryPrimitives.ReadUInt16BigEndian(Take(2));
    public int Int32LE() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));
    public uint UInt32LE() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));
    public int Int32BE() => BinaryPrimitives.ReadInt32BigEndian(Take(4));
    public long Int64LE() => BinaryPrimitives.ReadInt64LittleEndian(Take(8));
    public long Int64BE() => BinaryPrimitives.ReadInt64BigEndian(Take(8));
    public float FloatLE() => BinaryPrimitives.ReadSingleLittleEndian(Take(4));

    public int UInt24LE()
    {
        var s = Take(3);
        return s[0] | s[1] << 8 | s[2] << 16;
    }

    public ulong VarUInt()
    {
        ulong result = 0;
        for (var shift = 0; shift < 70; shift += 7)
        {
            var b = Byte();
            result |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return result;
        }
        throw new FormatException("VarUInt too long");
    }

    public int VarInt()
    {
        var v = (uint)VarUInt();
        return (int)(v >> 1) ^ -(int)(v & 1);
    }

    public long VarLong()
    {
        var v = VarUInt();
        return (long)(v >> 1) ^ -(long)(v & 1);
    }

    public string String() => Encoding.UTF8.GetString(Take(checked((int)VarUInt())));
    public byte[] ByteArray() => Bytes(checked((int)VarUInt()));

    public Guid Uuid()
    {
        Span<byte> b = stackalloc byte[16];
        Take(16).CopyTo(b);
        b[..8].Reverse();
        b[8..].Reverse();
        return new Guid(b, bigEndian: true);
    }
}
