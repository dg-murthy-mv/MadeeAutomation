using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace MadeeAutomation.Connectors;

public record LineCheckResult(bool Ok, string Message);

/// <summary>
/// Asks LINE "who am I?" (GET /v2/bot/info) with a channel access token. Proves the token is valid
/// and shows which LINE Official Account it belongs to - without sending any message.
/// (The channel secret can't be checked this way; LINE's own "Verify" button checks it.)
/// </summary>
public class LineCredentialChecker
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;

    public LineCredentialChecker(IHttpClientFactory httpClientFactory, IConfiguration configuration)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
    }

    public async Task<LineCheckResult> CheckAccessTokenAsync(string? accessToken, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(accessToken))
            return new LineCheckResult(false, "No LINE channel access token is saved for this client.");

        var baseUrl = _configuration["Connectors:Line:ApiBaseUrl"] is { Length: > 0 } b ? b : "https://api.line.me/";
        if (!baseUrl.EndsWith('/')) baseUrl += "/";

        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri(baseUrl), "v2/bot/info"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken.Trim());

        try
        {
            using var response = await _httpClientFactory.CreateClient("LineApi").SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
                return new LineCheckResult(false, (int)response.StatusCode == 401
                    ? "LINE rejected the channel access token (401). Copy it again from LINE Developers Console -> Messaging API tab, or click Reissue."
                    : $"LINE answered {(int)response.StatusCode}: {body}");

            using var doc = JsonDocument.Parse(body);
            var name = doc.RootElement.TryGetProperty("displayName", out var n) ? n.GetString() : "?";
            var basicId = doc.RootElement.TryGetProperty("basicId", out var id) ? id.GetString() : "";
            return new LineCheckResult(true, $"LINE access token works - connected to \"{name}\" {basicId}".TrimEnd());
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return new LineCheckResult(false, "Could not reach LINE: " + ex.Message);
        }
    }
}
