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

    public async Task<string> CreateOrderAsync(Guid clientConfigId, object orderPayload, CancellationToken cancellationToken = default)
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
        var response = await httpClient.PostAsync("/api/orders",
            new StringContent(json, Encoding.UTF8, "application/json"), cancellationToken);

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        using var responseDoc = JsonDocument.Parse(body);
        return responseDoc.RootElement.TryGetProperty("orderId", out var idEl) ? idEl.GetString() ?? "" : "";
    }

    public async Task<List<string>> GetLowStockItemsAsync(Guid clientConfigId, CancellationToken cancellationToken = default)
    {
        var (baseUrl, apiKey) = await GetSettingsAsync(clientConfigId, cancellationToken);

        if (!IsConfigured(baseUrl))
        {
            var mockItems = new List<string> { "Sourdough flour (5kg bags)", "Butter (1kg blocks)", "Paper bakery bags" };
            _logger.LogWarning(
                "PosFirstApiConnector MOCK MODE - client {ClientConfigId} has no posFirstBaseUrl configured. " +
                "Returning fake low-stock items: {Items}", clientConfigId, string.Join(", ", mockItems));
            return mockItems;
        }

        var httpClient = _httpClientFactory.CreateClient("PosFirstApi");
        httpClient.BaseAddress = new Uri(baseUrl!);
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            httpClient.DefaultRequestHeaders.Add("X-Api-Key", apiKey);
        }

        var response = await httpClient.GetAsync("/api/inventory/low-stock", cancellationToken);
        response.EnsureSuccessStatusCode();

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
