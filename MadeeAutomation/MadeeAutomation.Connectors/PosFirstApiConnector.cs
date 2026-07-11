using System.Text;
using System.Text.Json;
using MadeeAutomation.Application.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace MadeeAutomation.Connectors;

public class PosFirstApiConnector : IPosFirstConnector
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<PosFirstApiConnector> _logger;

    public PosFirstApiConnector(IHttpClientFactory httpClientFactory, IConfiguration configuration, ILogger<PosFirstApiConnector> logger)
    {
        _httpClient = httpClientFactory.CreateClient("PosFirstApi");
        var baseUrl = configuration["Connectors:PosFirst:BaseUrl"];
        if (!string.IsNullOrWhiteSpace(baseUrl))
        {
            _httpClient.BaseAddress = new Uri(baseUrl);
        }
        var apiKey = configuration["Connectors:PosFirst:ApiKey"];
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            _httpClient.DefaultRequestHeaders.Add("X-Api-Key", apiKey);
        }
        _logger = logger;
    }

    public async Task<string> CreateOrderAsync(Guid clientConfigId, object orderPayload, CancellationToken cancellationToken = default)
    {
        // Mock mode: no real POSFirst instance configured yet - skip the HTTP call,
        // log what would have been sent, and return a fake order ID so the rest of
        // the workflow (confirmation message) can still run end-to-end.
        if (_httpClient.BaseAddress is null || _httpClient.BaseAddress.Host.Contains("example.com"))
        {
            var mockOrderId = $"MOCK-{DateTime.UtcNow:yyyyMMddHHmmss}";
            _logger.LogWarning(
                "PosFirstApiConnector running in MOCK MODE (no real BaseUrl configured). " +
                "Would have created order: {OrderPayload}. Returning fake order ID {MockOrderId}",
                System.Text.Json.JsonSerializer.Serialize(orderPayload), mockOrderId);

            return mockOrderId;
        }

        var json = JsonSerializer.Serialize(orderPayload);
        var response = await _httpClient.PostAsync("/api/orders",
            new StringContent(json, Encoding.UTF8, "application/json"), cancellationToken);

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.TryGetProperty("orderId", out var idEl) ? idEl.GetString() ?? "" : "";
    }

    public async Task<List<string>> GetLowStockItemsAsync(Guid clientConfigId, CancellationToken cancellationToken = default)
    {
        // Mock mode: same placeholder-BaseUrl check as CreateOrderAsync.
        if (_httpClient.BaseAddress is null || _httpClient.BaseAddress.Host.Contains("example.com"))
        {
            var mockItems = new List<string> { "Sourdough flour (5kg bags)", "Butter (1kg blocks)", "Paper bakery bags" };
            _logger.LogWarning(
                "PosFirstApiConnector running in MOCK MODE (no real BaseUrl configured). " +
                "Returning fake low-stock items: {Items}", string.Join(", ", mockItems));

            return mockItems;
        }

        var response = await _httpClient.GetAsync("/api/inventory/low-stock", cancellationToken);
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
