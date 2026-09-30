using System.Net;
using Bds.Core.Protocol;

namespace Bds.Core.RakNet;

static class RakId
{
    public const byte ConnectedPing = 0x00;
    public const byte UnconnectedPing = 0x01;
    public const byte ConnectedPong = 0x03;
    public const byte OpenConnectionRequest1 = 0x05;
    public const byte OpenConnectionReply1 = 0x06;
    public const byte OpenConnectionRequest2 = 0x07;
    public const byte OpenConnectionReply2 = 0x08;
    public const byte ConnectionRequest = 0x09;
    public const byte ConnectionRequestAccepted = 0x10;
    public const byte NewIncomingConnection = 0x13;
    public const byte DisconnectionNotification = 0x15;
    public const byte IncompatibleProtocol = 0x19;
    public const byte UnconnectedPong = 0x1C;
    public const byte Game = 0xFE;
}

static class RakBinary
{
    public const byte ProtocolVersion = 11;

    public static readonly byte[] Magic =
    [
        0x00, 0xff, 0xff, 0x00, 0xfe, 0xfe, 0xfe, 0xfe, 0xfd, 0xfd, 0xfd, 0xfd, 0x12, 0x34, 0x56, 0x78,
    ];

    public static void WriteAddress(PacketWriter w, IPEndPoint ep)
    {
        var bytes = ep.Address.GetAddressBytes();
        if (ep.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            w.Byte(4);
            foreach (var b in bytes) w.Byte((byte)~b);
            w.UInt16BE((ushort)ep.Port);
        }
        else
        {
            w.Byte(6).UInt16LE(23).UInt16BE((ushort)ep.Port).Int32BE(0).Bytes(bytes).Int32BE((int)ep.Address.ScopeId);
        }
    }

    public static IPEndPoint ReadAddress(PacketReader r)
    {
        var version = r.Byte();
        if (version == 4)
        {
            var b = r.Bytes(4);
            for (var i = 0; i < 4; i++) b[i] = (byte)~b[i];
            return new IPEndPoint(new IPAddress(b), r.UInt16BE());
        }
        r.UInt16LE();
        var port = r.UInt16BE();
        r.Int32BE();
        var addr = r.Bytes(16);
        r.Int32BE();
        return new IPEndPoint(new IPAddress(addr), port);
    }
}

public enum Reliability : byte
{
    Unreliable = 0,
    UnreliableSequenced = 1,
    Reliable = 2,
    ReliableOrdered = 3,
    ReliableSequenced = 4,
}

static class ReliabilityExt
{
    public static bool IsReliable(this Reliability r) => r is Reliability.Reliable or Reliability.ReliableOrdered or Reliability.ReliableSequenced;
    public static bool IsSequenced(this Reliability r) => r is Reliability.UnreliableSequenced or Reliability.ReliableSequenced;
    public static bool IsOrdered(this Reliability r) => r is Reliability.ReliableOrdered || r.IsSequenced();
}
