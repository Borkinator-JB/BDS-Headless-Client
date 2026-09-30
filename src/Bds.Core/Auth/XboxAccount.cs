using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace Bds.Core.Auth;

public enum AccountState
{
    SignedOut,
    WaitingForCode,
    SignedIn,
    Error,
}

/// <summary>Owns the Microsoft/Xbox session: sign in, token refresh, cached XSTS tokens.</summary>
public sealed class XboxAccount(HttpClient http, ITokenStore store, ILogger<XboxAccount> log)
{
    readonly MsaClient _msa = new(http);
    readonly XboxClient _xbox = new(http);
    readonly MinecraftServicesClient _mc = new(http);
    readonly SemaphoreSlim _lock = new(1, 1);
    readonly ConcurrentDictionary<string, XstsToken> _xsts = new();

    TokenState? _state;
    ECDsa? _deviceKey;
    MsaToken? _msaToken;
    (string Token, DateTimeOffset NotAfter)? _deviceToken;
    McToken? _mcToken;
    CancellationTokenSource? _signInCts;

    public AccountState State { get; private set; } = AccountState.SignedOut;
    public DeviceCodeInfo? PendingCode { get; private set; }
    public string? Error { get; private set; }
    public string? Gamertag => _state?.Gamertag;
    public string? Xuid => _state?.Xuid;
    public Guid DeviceId => _state?.DeviceId ?? Guid.Empty;
    public MinecraftServicesClient Minecraft => _mc;

    public event Action? Changed;

    public async Task<bool> TryResumeAsync(CancellationToken ct)
    {
        try
        {
            _state = await store.LoadAsync(ct);
        }
        catch (Exception e) when (e is CryptographicException or System.Text.Json.JsonException)
        {
            log.LogWarning("Stored credentials could not be read, sign in again");
            await store.DeleteAsync(ct);
        }
        if (_state is null) return false;

        _deviceKey = ECDsa.Create();
        _deviceKey.ImportPkcs8PrivateKey(_state.DeviceKey, out _);
        try
        {
            await GetXstsAsync(AuthConstants.XboxLiveRelyingParty, ct);
        }
        catch (AuthException e)
        {
            log.LogWarning("Resume failed: {Message}", e.Message);
            SetState(AccountState.Error, e.Message);
            return false;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            // Offline (e.g. at boot). Tokens refresh on the next call.
            log.LogWarning("Resume offline: {Message}", e.Message);
        }
        SetState(AccountState.SignedIn);
        return true;
    }

    public async Task<DeviceCodeInfo> BeginSignInAsync(CancellationToken ct)
    {
        _signInCts?.Cancel();
        _signInCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var code = await _msa.StartDeviceCodeAsync(ct);
        PendingCode = code;
        SetState(AccountState.WaitingForCode);
        _ = CompleteSignInAsync(code, _signInCts.Token);
        return code;
    }

    public void CancelSignIn()
    {
        _signInCts?.Cancel();
        PendingCode = null;
        SetState(_state is null ? AccountState.SignedOut : AccountState.SignedIn);
    }

    async Task CompleteSignInAsync(DeviceCodeInfo code, CancellationToken ct)
    {
        try
        {
            var token = await _msa.PollDeviceCodeAsync(code, ct);
            await _lock.WaitAsync(ct);
            try
            {
                _deviceKey?.Dispose();
                _deviceKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
                _state = new TokenState
                {
                    RefreshToken = token.RefreshToken,
                    DeviceKey = _deviceKey.ExportPkcs8PrivateKey(),
                    DeviceId = Guid.NewGuid(),
                };
                _msaToken = token;
                _deviceToken = null;
                _mcToken = null;
                _xsts.Clear();
            }
            finally
            {
                _lock.Release();
            }

            var xsts = await GetXstsAsync(AuthConstants.XboxLiveRelyingParty, ct);
            _state.Gamertag = xsts.Gamertag;
            _state.Xuid = xsts.Xuid;
            await store.SaveAsync(_state, ct);
            PendingCode = null;
            log.LogInformation("Signed in as {Gamertag}", xsts.Gamertag);
            SetState(AccountState.SignedIn);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception e)
        {
            log.LogError(e, "Sign in failed");
            PendingCode = null;
            SetState(AccountState.Error, e is AuthException ? e.Message : $"Sign in failed: {e.Message}");
        }
    }

    public async Task SignOutAsync(CancellationToken ct)
    {
        _signInCts?.Cancel();
        await _lock.WaitAsync(ct);
        try
        {
            await store.DeleteAsync(ct);
            _state = null;
            _msaToken = null;
            _deviceToken = null;
            _mcToken = null;
            _xsts.Clear();
            _deviceKey?.Dispose();
            _deviceKey = null;
        }
        finally
        {
            _lock.Release();
        }
        SetState(AccountState.SignedOut);
    }

    public async Task<XstsToken> GetXstsAsync(string relyingParty, CancellationToken ct)
    {
        if (_xsts.TryGetValue(relyingParty, out var cached) && cached.NotAfter > DateTimeOffset.UtcNow.AddMinutes(5))
            return cached;

        await _lock.WaitAsync(ct);
        try
        {
            if (_state is null || _deviceKey is null) throw new AuthException("Not signed in");

            if (_msaToken is null || _msaToken.ExpiresAt < DateTimeOffset.UtcNow.AddMinutes(5))
            {
                _msaToken = await _msa.RefreshAsync(_state.RefreshToken, ct);
                if (_msaToken.RefreshToken != _state.RefreshToken)
                {
                    _state.RefreshToken = _msaToken.RefreshToken;
                    await store.SaveAsync(_state, ct);
                }
            }

            if (_deviceToken is null || _deviceToken.Value.NotAfter < DateTimeOffset.UtcNow.AddMinutes(5))
                _deviceToken = await _xbox.GetDeviceTokenAsync(_deviceKey, _state.DeviceId, ct);

            var token = await _xbox.AuthorizeAsync(_msaToken.AccessToken, _deviceToken.Value.Token, relyingParty, _deviceKey, ct);
            _xsts[relyingParty] = token;
            return token;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<McToken> GetMcTokenAsync(string gameVersion, CancellationToken ct)
    {
        if (_mcToken is { } t && t.ValidUntil > DateTimeOffset.UtcNow.AddMinutes(5)) return t;
        var playFab = await GetXstsAsync(AuthConstants.PlayFabRelyingParty, ct);
        var ticket = await _mc.GetPlayFabTicketAsync(playFab, ct);
        _mcToken = await _mc.StartSessionAsync(ticket, gameVersion, DeviceId, ct);
        return _mcToken;
    }

    void SetState(AccountState state, string? error = null)
    {
        State = state;
        Error = error;
        Changed?.Invoke();
    }
}
