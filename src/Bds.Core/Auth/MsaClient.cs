using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Bds.Core.Auth;

public sealed record DeviceCodeInfo(string UserCode, string VerificationUri, string DeviceCode, TimeSpan Interval, DateTimeOffset ExpiresAt);

public sealed record MsaToken(string AccessToken, string RefreshToken, DateTimeOffset ExpiresAt);

public sealed class AuthException(string message) : Exception(message);

public sealed class MsaClient(HttpClient http)
{
    const string DeviceCodeUrl = "https://login.live.com/oauth20_connect.srf";
    const string TokenUrl = "https://login.live.com/oauth20_token.srf";

    public async Task<DeviceCodeInfo> StartDeviceCodeAsync(CancellationToken ct)
    {
        using var res = await http.PostAsync(DeviceCodeUrl, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = AuthConstants.ClientId,
            ["scope"] = AuthConstants.Scope,
            ["response_type"] = "device_code",
        }), ct);
        res.EnsureSuccessStatusCode();
        var body = await res.Content.ReadFromJsonAsync<DeviceCodeResponse>(ct) ?? throw new AuthException("Empty device code response");
        return new DeviceCodeInfo(body.UserCode, body.VerificationUri, body.DeviceCode,
            TimeSpan.FromSeconds(Math.Max(body.Interval, 1)), DateTimeOffset.UtcNow.AddSeconds(body.ExpiresIn));
    }

    public async Task<MsaToken> PollDeviceCodeAsync(DeviceCodeInfo info, CancellationToken ct)
    {
        var interval = info.Interval;
        while (DateTimeOffset.UtcNow < info.ExpiresAt)
        {
            await Task.Delay(interval, ct);
            using var res = await http.PostAsync(TokenUrl, new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = AuthConstants.ClientId,
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
                ["device_code"] = info.DeviceCode,
            }), ct);
            var body = await res.Content.ReadFromJsonAsync<TokenResponse>(ct) ?? throw new AuthException("Empty token response");
            if (res.IsSuccessStatusCode) return body.ToToken();
            switch (body.Error)
            {
                case "authorization_pending":
                    continue;
                case "slow_down":
                    interval += TimeSpan.FromSeconds(5);
                    continue;
                default:
                    throw new AuthException($"Sign in failed: {body.Error}");
            }
        }
        throw new AuthException("Sign in code expired");
    }

    public async Task<MsaToken> RefreshAsync(string refreshToken, CancellationToken ct)
    {
        using var res = await http.PostAsync(TokenUrl, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = AuthConstants.ClientId,
            ["scope"] = AuthConstants.Scope,
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
        }), ct);
        var body = await res.Content.ReadFromJsonAsync<TokenResponse>(ct) ?? throw new AuthException("Empty token response");
        if (!res.IsSuccessStatusCode) throw new AuthException($"Refresh failed: {body.Error}");
        return body.ToToken();
    }

    sealed class DeviceCodeResponse
    {
        [JsonPropertyName("user_code")] public string UserCode { get; set; } = "";
        [JsonPropertyName("device_code")] public string DeviceCode { get; set; } = "";
        [JsonPropertyName("verification_uri")] public string VerificationUri { get; set; } = "";
        [JsonPropertyName("interval")] public int Interval { get; set; }
        [JsonPropertyName("expires_in")] public int ExpiresIn { get; set; }
    }

    sealed class TokenResponse
    {
        [JsonPropertyName("access_token")] public string? AccessToken { get; set; }
        [JsonPropertyName("refresh_token")] public string? RefreshToken { get; set; }
        [JsonPropertyName("expires_in")] public int ExpiresIn { get; set; }
        [JsonPropertyName("error")] public string? Error { get; set; }

        public MsaToken ToToken() => new(
            AccessToken ?? throw new AuthException("No access token"),
            RefreshToken ?? throw new AuthException("No refresh token"),
            DateTimeOffset.UtcNow.AddSeconds(ExpiresIn));
    }
}
