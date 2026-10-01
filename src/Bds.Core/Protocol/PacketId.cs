namespace Bds.Core.Protocol;

public static class PacketId
{
    public const int Login = 1;
    public const int PlayStatus = 2;
    public const int Disconnect = 5;
    public const int Transfer = 85;
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
