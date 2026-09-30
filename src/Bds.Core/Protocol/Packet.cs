namespace Bds.Core.Protocol;

public readonly record struct Packet(int Id, ReadOnlyMemory<byte> Body)
{
    public PacketReader Reader() => new(Body);

    public static byte[] Encode(int id, Action<PacketWriter>? body = null)
    {
        var w = new PacketWriter();
        w.VarUInt((uint)id & 0x3FF);
        body?.Invoke(w);
        return w.ToArray();
    }

    public static Packet Decode(ReadOnlyMemory<byte> raw)
    {
        var r = new PacketReader(raw);
        var header = (int)r.VarUInt();
        return new Packet(header & 0x3FF, raw[r.Position..]);
    }
}
