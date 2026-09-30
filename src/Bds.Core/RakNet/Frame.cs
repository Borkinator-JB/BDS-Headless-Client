using Bds.Core.Protocol;

namespace Bds.Core.RakNet;

sealed class Frame
{
    public Reliability Reliability;
    public int ReliableIndex;
    public int SequenceIndex;
    public int OrderIndex;
    public byte OrderChannel;
    public bool Split;
    public int SplitCount;
    public ushort SplitId;
    public int SplitIndex;
    public byte[] Body = [];

    public int Size => 3 + Body.Length
        + (Reliability.IsReliable() ? 3 : 0)
        + (Reliability.IsSequenced() ? 3 : 0)
        + (Reliability.IsOrdered() ? 4 : 0)
        + (Split ? 10 : 0);

    public void Write(PacketWriter w)
    {
        w.Byte((byte)((byte)Reliability << 5 | (Split ? 0x10 : 0)));
        w.UInt16BE((ushort)(Body.Length * 8));
        if (Reliability.IsReliable()) w.UInt24LE(ReliableIndex);
        if (Reliability.IsSequenced()) w.UInt24LE(SequenceIndex);
        if (Reliability.IsOrdered()) w.UInt24LE(OrderIndex).Byte(OrderChannel);
        if (Split) w.Int32BE(SplitCount).UInt16BE(SplitId).Int32BE(SplitIndex);
        w.Bytes(Body);
    }

    public static Frame Read(PacketReader r)
    {
        var flags = r.Byte();
        var f = new Frame
        {
            Reliability = (Reliability)(flags >> 5),
            Split = (flags & 0x10) != 0,
        };
        var length = (r.UInt16BE() + 7) / 8;
        if (f.Reliability.IsReliable()) f.ReliableIndex = r.UInt24LE();
        if (f.Reliability.IsSequenced()) f.SequenceIndex = r.UInt24LE();
        if (f.Reliability.IsOrdered())
        {
            f.OrderIndex = r.UInt24LE();
            f.OrderChannel = r.Byte();
        }
        if (f.Split)
        {
            f.SplitCount = r.Int32BE();
            f.SplitId = r.UInt16BE();
            f.SplitIndex = r.Int32BE();
        }
        f.Body = r.Bytes(length);
        return f;
    }
}

static class AckCodec
{
    public const byte AckFlag = 0xC0;
    public const byte NakFlag = 0xA0;

    public static byte[] Encode(byte id, IReadOnlyList<int> sequences)
    {
        var sorted = sequences.Distinct().Order().ToList();
        var records = new List<(int Start, int End)>();
        foreach (var s in sorted)
        {
            if (records.Count > 0 && records[^1].End + 1 == s) records[^1] = (records[^1].Start, s);
            else records.Add((s, s));
        }
        var w = new PacketWriter();
        w.Byte(id).UInt16BE((ushort)records.Count);
        foreach (var (start, end) in records)
        {
            if (start == end) w.Byte(1).UInt24LE(start);
            else w.Byte(0).UInt24LE(start).UInt24LE(end);
        }
        return w.ToArray();
    }

    public static List<int> Decode(PacketReader r)
    {
        var result = new List<int>();
        var count = r.UInt16BE();
        for (var i = 0; i < count; i++)
        {
            var single = r.Bool();
            var start = r.UInt24LE();
            var end = single ? start : r.UInt24LE();
            if (end - start > 4096) throw new InvalidDataException("ACK range too large");
            for (var s = start; s <= end; s++) result.Add(s);
        }
        return result;
    }
}
