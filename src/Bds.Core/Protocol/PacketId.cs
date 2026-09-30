namespace Bds.Core.Protocol;

public static class PacketId
{
    public const int Login = 1;
    public const int PlayStatus = 2;
    public const int ServerToClientHandshake = 3;
    public const int ClientToServerHandshake = 4;
    public const int Disconnect = 5;
    public const int ResourcePacksInfo = 6;
    public const int ResourcePackStack = 7;
    public const int ResourcePackClientResponse = 8;
    public const int StartGame = 11;
    public const int PlayerList = 63;
    public const int RequestChunkRadius = 69;
    public const int Transfer = 85;
    public const int SetLocalPlayerAsInitialized = 113;
    public const int NetworkStackLatency = 115;
    public const int NetworkSettings = 143;
    public const int RequestNetworkSettings = 193;
}

public enum PlayStatus
{
    LoginSuccess = 0,
    FailedClient = 1,
    FailedServer = 2,
    PlayerSpawn = 3,
    FailedInvalidTenant = 4,
    FailedVanillaEdu = 5,
    FailedEduVanilla = 6,
    FailedServerFull = 7,
    FailedEditorVanilla = 8,
    FailedVanillaEditor = 9,
}
