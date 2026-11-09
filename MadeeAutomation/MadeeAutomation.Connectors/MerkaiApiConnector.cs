using System.Text;
using System.Text.Json;
using MadeeAutomation.Application.Abstractions;
using Microsoft.Extensions.Logging;

namespace MadeeAutomation.Connectors;

/// <summary>
/// Talks to a client's own Merkai CRM instance, using per-client BaseUrl/ApiKey from
/// ClientConfig.IntegrationSettingsJson - same per-client pattern as PosFirstApiConnector.
/// This project intentionally has NO project reference to Merkai's codebase - integration
/// is over HTTP only, keeping MadeeAutomation a fully independent solution.
/// </summary>
public class MerkaiApiConnector : IMerkaiConnector
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IClientSettingsService _settings;
    private readonly ILogger<MerkaiApiConnector> _logger;

    public MerkaiApiConnector(IHttpClientFactory httpClientFactory, IClientSettingsService settings, ILogger<MerkaiApiConnector> logger)
    {
        _httpClientFactory = httpClientFactory;
        _settings = settings;
        _logger = logger;
    }

    private async Task<(string? baseUrl, string? apiKey)> GetSettingsAsync(Guid clientConfigId, CancellationToken cancellationToken)
    {
        var settings = await _settings.GetAsync(clientConfigId, cancellationToken);
        return (settings.MerkaiBaseUrl, settings.MerkaiApiKey);
    }

    public async Task<string> CreateOrUpdateLeadAsync(Guid clientConfigId, object leadPayload, CancellationToken cancellationToken = default)
    {
        var (baseUrl, apiKey) = await GetSettingsAsync(clientConfigId, cancellationToken);

        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            var mockLeadId = $"MOCK-LEAD-{DateTime.UtcNow:yyyyMMddHHmmss}";
            _logger.LogWarning(
                "MerkaiApiConnector MOCK MODE - client {ClientConfigId} has no merkaiBaseUrl configured. " +
                "Would have created/updated lead: {LeadPayload}. Returning fake lead ID {MockLeadId}",
                clientConfigId, JsonSerializer.Serialize(leadPayload), mockLeadId);
            return mockLeadId;
        }

        var httpClient = _httpClientFactory.CreateClient("MerkaiApi");
        httpClient.BaseAddress = new Uri(baseUrl);
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            httpClient.DefaultRequestHeaders.Add("X-Api-Key", apiKey);
        }

        var json = JsonSerializer.Serialize(leadPayload);
        var response = await httpClient.PostAsync("/api/leads/upsert",
            new StringContent(json, Encoding.UTF8, "application/json"), cancellationToken);

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
    }

    public async Task<string> AddDealNoteAsync(Guid clientConfigId, string dealId, string note, CancellationToken cancellationToken = default)
    {
        var (baseUrl, apiKey) = await GetSettingsAsync(clientConfigId, cancellationToken);

        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            _logger.LogWarning(
                "MerkaiApiConnector MOCK MODE - client {ClientConfigId} has no merkaiBaseUrl configured. " +
                "Would have added note to deal {DealId}: \"{Note}\"", clientConfigId, dealId, note);
            return "MOCK-NOTE-OK";
        }

        var httpClient = _httpClientFactory.CreateClient("MerkaiApi");
        httpClient.BaseAddress = new Uri(baseUrl);
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            httpClient.DefaultRequestHeaders.Add("X-Api-Key", apiKey);
        }

        var json = JsonSerializer.Serialize(new { dealId, note });
        var response = await httpClient.PostAsync("/api/deals/notes",
            new StringContent(json, Encoding.UTF8, "application/json"), cancellationToken);

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }
}
