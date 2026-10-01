using System.IO.Compression;

namespace Bds.Core.Protocol;

public enum CompressionAlgorithm : ushort
{
    Zlib = 0,
    Snappy = 1,
    None = 0xFFFF,
}

/// <summary>Frames game packets into batches: length prefix and compression.</summary>
public sealed class BatchCodec
{
    const byte NoCompressionId = 0xFF;

    public bool CompressionEnabled { get; private set; }
    public CompressionAlgorithm Algorithm { get; private set; }
    public int Threshold { get; private set; } = 1;

    public void EnableCompression(CompressionAlgorithm algorithm, int threshold)
    {
        if (algorithm == CompressionAlgorithm.Snappy)
            throw new NotSupportedException("Snappy compression is not supported");
        Algorithm = algorithm;
        Threshold = Math.Max(threshold, 0);
        CompressionEnabled = true;
    }

    public byte[] Encode(IEnumerable<byte[]> packets)
    {
        var batch = new PacketWriter();
        foreach (var p in packets) batch.ByteArray(p);
        var data = batch.ToArray();

        if (CompressionEnabled)
        {
            var compress = Algorithm == CompressionAlgorithm.Zlib && data.Length >= Threshold;
            var body = compress ? Deflate(data) : data;
            var framed = new byte[body.Length + 1];
            framed[0] = compress ? (byte)CompressionAlgorithm.Zlib : NoCompressionId;
            body.CopyTo(framed, 1);
            data = framed;
        }
        return data;
    }

    public List<Packet> Decode(ReadOnlySpan<byte> input)
    {
        var data = input.ToArray();
        if (CompressionEnabled)
        {
            if (data.Length == 0) throw new InvalidDataException("Empty batch");
            data = data[0] switch
            {
                NoCompressionId => data[1..],
                (byte)CompressionAlgorithm.Zlib => Inflate(data.AsSpan(1)),
                _ => throw new NotSupportedException($"Compression {data[0]} is not supported"),
            };
        }

        var packets = new List<Packet>();
        var r = new PacketReader(data);
        while (r.Remaining > 0)
        {
            var len = checked((int)r.VarUInt());
            packets.Add(Packet.Decode(r.Slice(len)));
        }
        return packets;
    }

    static byte[] Deflate(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var d = new DeflateStream(ms, CompressionLevel.Fastest, leaveOpen: true)) d.Write(data);
        return ms.ToArray();
    }

    static byte[] Inflate(ReadOnlySpan<byte> data)
    {
        using var input = new MemoryStream(data.ToArray());
        using var d = new DeflateStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        var buf = new byte[8192];
        int n;
        while ((n = d.Read(buf)) > 0)
        {
            output.Write(buf, 0, n);
            if (output.Length > 64 * 1024 * 1024) throw new InvalidDataException("Batch too large");
        }
        return output.ToArray();
    }
}
