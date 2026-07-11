using System.Text;
using System.Text.Json;
using MadeeAutomation.Application.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace MadeeAutomation.Connectors;

/// <summary>
/// Talks to Merkai CRM's existing API (IApiService pattern on the Merkai side).
/// This project intentionally has NO project reference to Merkai's codebase -
/// integration is over HTTP only, keeping MadeeAutomation a fully independent solution.
/// </summary>
public class MerkaiApiConnector : IMerkaiConnector
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<MerkaiApiConnector> _logger;

    public MerkaiApiConnector(IHttpClientFactory httpClientFactory, IConfiguration configuration, ILogger<MerkaiApiConnector> logger)
    {
        _httpClient = httpClientFactory.CreateClient("MerkaiApi");
        var baseUrl = configuration["Connectors:Merkai:BaseUrl"];
        if (!string.IsNullOrWhiteSpace(baseUrl))
        {
            _httpClient.BaseAddress = new Uri(baseUrl);
        }
        var apiKey = configuration["Connectors:Merkai:ApiKey"];
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            _httpClient.DefaultRequestHeaders.Add("X-Api-Key", apiKey);
        }
        _logger = logger;
    }

    public async Task<string> CreateOrUpdateLeadAsync(Guid clientConfigId, object leadPayload, CancellationToken cancellationToken = default)
    {
        var json = JsonSerializer.Serialize(leadPayload);
        var response = await _httpClient.PostAsync("/api/leads/upsert",
            new StringContent(json, Encoding.UTF8, "application/json"), cancellationToken);

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
    }

    public async Task<string> AddDealNoteAsync(Guid clientConfigId, string dealId, string note, CancellationToken cancellationToken = default)
    {
        var json = JsonSerializer.Serialize(new { dealId, note });
        var response = await _httpClient.PostAsync("/api/deals/notes",
            new StringContent(json, Encoding.UTF8, "application/json"), cancellationToken);

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }
}
