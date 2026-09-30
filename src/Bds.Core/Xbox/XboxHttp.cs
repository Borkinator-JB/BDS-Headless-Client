using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Bds.Core.Auth;

namespace Bds.Core.Xbox;

public sealed class XboxApiException(string message, int status) : Exception(message)
{
    public int Status { get; } = status;
}

/// <summary>Authenticated calls to *.xboxlive.com.</summary>
public sealed class XboxHttp(HttpClient http, XboxAccount account)
{
    public async Task<JsonNode?> SendAsync(HttpMethod method, string url, int contractVersion, JsonNode? body = null, CancellationToken ct = default)
    {
        var xsts = await account.GetXstsAsync(AuthConstants.XboxLiveRelyingParty, ct);
        using var req = new HttpRequestMessage(method, url);
        req.Headers.TryAddWithoutValidation("Authorization", xsts.Header);
        req.Headers.Add("x-xbl-contract-version", contractVersion.ToString());
        req.Headers.AcceptLanguage.Add(new StringWithQualityHeaderValue("en-US"));
        if (body is not null)
        {
            req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8);
            req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }
        using var res = await http.SendAsync(req, ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
            throw new XboxApiException($"{method} {new Uri(url).Host} failed ({(int)res.StatusCode})", (int)res.StatusCode);
        return string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
    }
}
