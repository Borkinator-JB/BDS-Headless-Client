using System.Threading.Channels;

namespace Bds.Core.Protocol;

/// <summary>Carries game batches between the bot and a server (RakNet or NetherNet).</summary>
public interface IGameTransport : IAsyncDisposable
{
    ChannelReader<byte[]> Incoming { get; }
    string? DisconnectReason { get; }

    /// <summary>NetherNet runs over DTLS, so Minecraft level encryption stays off.</summary>
    bool Encrypted { get; }

    void Send(byte[] gamePayload);
}
