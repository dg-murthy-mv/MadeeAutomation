using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace MadeeAutomation.Connectors;

/// <summary>
/// Asks Meta "who am I?" with a client's saved WhatsApp / Messenger credentials - nothing is sent to anyone.
///   WhatsApp:  GET /{phone-number-id}?fields=display_phone_number,verified_name
///   Messenger: GET /me?fields=name  (with the Page access token, "me" is the Page)
/// (The App Secret can't be checked this way: a wrong one shows as rejected webhook calls in WebApi's log.)
/// </summary>
public class MetaCredentialChecker
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;

    public MetaCredentialChecker(IHttpClientFactory httpClientFactory, IConfiguration configuration)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
    }

    private Uri Url(string pathAndQuery)
    {
        var baseUrl = _configuration["Connectors:Meta:GraphBaseUrl"] is { Length: > 0 } b ? b.Trim() : "https://graph.facebook.com/";
        if (!baseUrl.EndsWith('/')) baseUrl += "/";
        var version = _configuration["Connectors:Meta:GraphApiVersion"] is { Length: > 0 } v ? v.Trim('/') : "v27.0";
        return new Uri(new Uri(baseUrl), $"{version}/{pathAndQuery}");
    }

    public async Task<LineCheckResult> CheckWhatsAppAsync(string? phoneNumberId, string? accessToken, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(phoneNumberId) || string.IsNullOrWhiteSpace(accessToken))
            return new LineCheckResult(false, "Save the WhatsApp phone number ID and access token first.");

        var (ok, json, error) = await GetAsync(Url($"{Uri.EscapeDataString(phoneNumberId.Trim())}?fields=display_phone_number,verified_name"),
            accessToken, "WhatsApp", cancellationToken);
        if (!ok) return new LineCheckResult(false, error!);

        var number = Str(json!.Value, "display_phone_number") ?? "?";
        var name = Str(json!.Value, "verified_name");
        return new LineCheckResult(true, $"WhatsApp works - connected to {number}" + (name is null ? "" : $" (\"{name}\")") + ".");
    }

    public async Task<LineCheckResult> CheckMessengerAsync(string? pageAccessToken, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(pageAccessToken))
            return new LineCheckResult(false, "Save the Messenger page access token first.");

        var (ok, json, error) = await GetAsync(Url("me?fields=name"), pageAccessToken, "Messenger", cancellationToken);
        // Reading the Page's name needs pages_read_engagement; sending and receiving messages does not.
        // Meta only says that about a token it accepted, so the token itself is fine.
        if (!ok && error!.Contains("pages_read_engagement"))
            return new LineCheckResult(true, "Messenger token accepted by Meta - messages will work. To show the Page name here, " +
                "add the pages_read_engagement permission (Meta app -> Messenger -> Permissions and features), then generate the Page token again.");
        if (!ok) return new LineCheckResult(false, error!);
        return new LineCheckResult(true, $"Messenger works - connected to the Page \"{Str(json!.Value, "name") ?? "?"}\".");
    }

    private async Task<(bool ok, JsonElement? json, string? error)> GetAsync(Uri url, string accessToken, string label, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken.Trim());
        try
        {
            using var response = await _httpClientFactory.CreateClient(label == "Messenger" ? "MessengerApi" : "WhatsAppApi").SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
            if (response.IsSuccessStatusCode) return (true, doc.RootElement.Clone(), null);

            // Meta errors: { "error": { "message": "...", "code": 190 } } - 190 = expired / invalid token
            var err = doc.RootElement.TryGetProperty("error", out var e) ? e : default;
            var message = err.ValueKind == JsonValueKind.Object ? Str(err, "message") : null;
            var code = err.ValueKind == JsonValueKind.Object && err.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetInt32() : 0;
            return (false, null, code == 190
                ? $"Meta rejected the {label} token (expired or wrong). Create a permanent System User token in Meta Business Settings and save it again."
                : $"Meta answered {(int)response.StatusCode}: {message ?? body}");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return (false, null, "Could not reach Meta: " + ex.Message);
        }
    }

    private static string? Str(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
