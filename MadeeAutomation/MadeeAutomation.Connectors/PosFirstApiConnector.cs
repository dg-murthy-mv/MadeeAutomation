using System.Text;
using System.Text.Json;
using MadeeAutomation.Application.Abstractions;
using Microsoft.Extensions.Logging;

namespace MadeeAutomation.Connectors;

/// <summary>
/// Talks to a client's own POSFirst instance. Each client has their own BaseUrl/ApiKey,
/// looked up per-call from ClientConfig.IntegrationSettingsJson - not a single global config.
/// Falls back to mock mode (logs what it would have sent, returns fake data) when a client
/// hasn't configured their POSFirst credentials yet.
/// </summary>
public class PosFirstApiConnector : IPosFirstConnector
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IClientSettingsService _settings;
    private readonly ILogger<PosFirstApiConnector> _logger;

    public PosFirstApiConnector(IHttpClientFactory httpClientFactory, IClientSettingsService settings, ILogger<PosFirstApiConnector> logger)
    {
        _httpClientFactory = httpClientFactory;
        _settings = settings;
        _logger = logger;
    }

    private async Task<(string? baseUrl, string? apiKey)> GetSettingsAsync(Guid clientConfigId, CancellationToken cancellationToken)
    {
        var settings = await _settings.GetAsync(clientConfigId, cancellationToken);
        return (settings.PosFirstBaseUrl, settings.PosFirstApiKey);
    }

    private static bool IsConfigured(string? baseUrl) => !string.IsNullOrWhiteSpace(baseUrl);

    public async Task<bool> IsConfiguredAsync(Guid clientConfigId, CancellationToken cancellationToken = default) =>
        IsConfigured((await GetSettingsAsync(clientConfigId, cancellationToken)).baseUrl);

    public async Task<string> CreateOrderAsync(Guid clientConfigId, object orderPayload, CancellationToken cancellationToken = default, string? idempotencyKeyFor = null)
    {
        var (baseUrl, apiKey) = await GetSettingsAsync(clientConfigId, cancellationToken);

        if (!IsConfigured(baseUrl))
        {
            var mockOrderId = $"MOCK-{DateTime.UtcNow:yyyyMMddHHmmss}";
            _logger.LogWarning(
                "PosFirstApiConnector MOCK MODE - client {ClientConfigId} has no posFirstBaseUrl configured. " +
                "Would have created order: {OrderPayload}. Returning fake order ID {MockOrderId}",
                clientConfigId, JsonSerializer.Serialize(orderPayload), mockOrderId);
            return mockOrderId;
        }

        var httpClient = _httpClientFactory.CreateClient("PosFirstApi");
        httpClient.BaseAddress = new Uri(baseUrl!);
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            httpClient.DefaultRequestHeaders.Add("X-Api-Key", apiKey);
        }

        var json = JsonSerializer.Serialize(orderPayload);

        // The same Idempotency-Key on every try (and on a later "Send to POS again"): a POS that honours
        // it creates the order once. We still only retry when the order certainly did not arrive.
        var idempotencyKey = idempotencyKeyFor ?? Guid.NewGuid().ToString("N");
        using var response = await HttpRetry.SendAsync(ct =>
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/api/orders")
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            request.Headers.Add("Idempotency-Key", idempotencyKey);
            return httpClient.SendAsync(request, ct);
        }, "POSFirst order", _logger, cancellationToken, safeToRepeat: false);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException($"POSFirst answered {(int)response.StatusCode}: {(error.Length > 200 ? error[..200] : error)}", null, response.StatusCode);
        }
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        using var responseDoc = JsonDocument.Parse(body);
        return responseDoc.RootElement.TryGetProperty("orderId", out var idEl) ? idEl.GetString() ?? "" : "";
    }

    public async Task<List<string>> GetLowStockItemsAsync(Guid clientConfigId, CancellationToken cancellationToken = default)
    {
        var (baseUrl, apiKey) = await GetSettingsAsync(clientConfigId, cancellationToken);

        if (!IsConfigured(baseUrl))
        {
            // No POS connected: there is nothing to check. Never return sample items here - now that the
            // scheduler really runs jobs, made-up stock would reach the client's staff as a real alert.
            _logger.LogWarning("Low-stock check for client {ClientConfigId} skipped - no posFirstBaseUrl is configured.", clientConfigId);
            return new List<string>();
        }

        var httpClient = _httpClientFactory.CreateClient("PosFirstApi");
        httpClient.BaseAddress = new Uri(baseUrl!);
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            httpClient.DefaultRequestHeaders.Add("X-Api-Key", apiKey);
        }

        // Reading stock changes nothing, so a short blip (timeout, 503) is simply tried again.
        using var response = await HttpRetry.SendAsync(ct => httpClient.GetAsync("/api/inventory/low-stock", ct),
            "POSFirst low-stock check", _logger, cancellationToken, safeToRepeat: true);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"POSFirst low-stock check answered {(int)response.StatusCode} {response.ReasonPhrase}.");

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(body);

        var items = new List<string>();
        if (doc.RootElement.TryGetProperty("items", out var itemsEl) && itemsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in itemsEl.EnumerateArray())
            {
                var name = item.TryGetProperty("name", out var n) ? n.GetString() : null;
                if (!string.IsNullOrWhiteSpace(name)) items.Add(name);
            }
        }

        return items;
    }
}
