namespace Bds.Core.Auth;

/// <summary>The only auth data that is persisted. Everything else lives in memory.</summary>
public sealed class TokenState
{
    public required string RefreshToken { get; set; }
    public required byte[] DeviceKey { get; set; }
    public required Guid DeviceId { get; set; }
    public string? Gamertag { get; set; }
    public string? Xuid { get; set; }
}

public interface ITokenStore
{
    Task<TokenState?> LoadAsync(CancellationToken ct = default);
    Task SaveAsync(TokenState state, CancellationToken ct = default);
    Task DeleteAsync(CancellationToken ct = default);
}

public interface ISecretProtector
{
    byte[] Protect(byte[] data);
    byte[] Unprotect(byte[] data);
}
