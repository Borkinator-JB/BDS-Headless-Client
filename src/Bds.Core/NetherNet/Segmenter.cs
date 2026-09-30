namespace Bds.Core.NetherNet;

/// <summary>NetherNet reliable channel framing: [segments remaining][data].</summary>
public sealed class Segmenter
{
    public const int MaxMessageSize = 10_000;

    readonly MemoryStream _buffer = new();
    int _expected = -1;

    public static List<byte[]> Split(ReadOnlySpan<byte> packet)
    {
        const int chunk = MaxMessageSize - 1;
        var count = Math.Max(1, (packet.Length + chunk - 1) / chunk);
        if (count > 256) throw new InvalidOperationException("Packet too large for NetherNet");
        var result = new List<byte[]>(count);
        for (var i = 0; i < count; i++)
        {
            var part = packet.Slice(i * chunk, Math.Min(chunk, packet.Length - i * chunk));
            var msg = new byte[part.Length + 1];
            msg[0] = (byte)(count - i - 1);
            part.CopyTo(msg.AsSpan(1));
            result.Add(msg);
        }
        return result;
    }

    /// <summary>Returns the full packet once the last segment arrives.</summary>
    public byte[]? Push(ReadOnlySpan<byte> message)
    {
        if (message.Length < 1) throw new InvalidDataException("Empty NetherNet message");
        var remaining = message[0];
        if (_expected > 0 && remaining != _expected - 1)
        {
            _buffer.SetLength(0);
            throw new InvalidDataException("Out of order NetherNet segment");
        }
        _buffer.Write(message[1..]);
        _expected = remaining;
        if (remaining > 0) return null;
        var packet = _buffer.ToArray();
        _buffer.SetLength(0);
        _expected = -1;
        return packet;
    }
}
