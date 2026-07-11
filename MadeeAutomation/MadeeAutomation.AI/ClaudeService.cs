using System.Text;
using System.Text.Json;
using MadeeAutomation.Application.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace MadeeAutomation.AI;

/// <summary>
/// Thin wrapper around the Anthropic Messages API (api.anthropic.com/v1/messages).
/// This is the single place every vertical pack handler goes through to call Claude -
/// keeping prompt/response plumbing, retries, and API-key handling in one spot.
/// </summary>
public class ClaudeService : IClaudeService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<ClaudeService> _logger;
    private readonly string _model;

    public ClaudeService(IHttpClientFactory httpClientFactory, IConfiguration configuration, ILogger<ClaudeService> logger)
    {
        _httpClient = httpClientFactory.CreateClient("ClaudeApi");
        _httpClient.BaseAddress = new Uri("https://api.anthropic.com/");

        var apiKey = configuration["Anthropic:ApiKey"]
            ?? throw new InvalidOperationException("Anthropic:ApiKey is not configured. Set it in appsettings/user-secrets/environment.");

        _httpClient.DefaultRequestHeaders.Add("x-api-key", apiKey);
        _httpClient.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");

        _model = configuration["Anthropic:Model"] ?? "claude-sonnet-4-6";
        _logger = logger;
    }

    public async Task<string> CompleteAsync(string systemPrompt, string userMessage, CancellationToken cancellationToken = default)
    {
        var requestBody = new
        {
            model = _model,
            max_tokens = 1024,
            system = systemPrompt,
            messages = new[]
            {
                new { role = "user", content = userMessage }
            }
        };

        var response = await _httpClient.PostAsync("v1/messages",
            new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json"),
            cancellationToken);

        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Claude API call failed: {StatusCode} {Body}", response.StatusCode, responseBody);
            throw new InvalidOperationException($"Claude API call failed with status {response.StatusCode}: {responseBody}");
        }

        using var doc = JsonDocument.Parse(responseBody);
        var contentArray = doc.RootElement.GetProperty("content");

        var textBuilder = new StringBuilder();
        foreach (var block in contentArray.EnumerateArray())
        {
            if (block.TryGetProperty("type", out var typeEl) && typeEl.GetString() == "text")
            {
                textBuilder.Append(block.GetProperty("text").GetString());
            }
        }

        return textBuilder.ToString();
    }

    public async Task<string> CompleteJsonAsync(string systemPrompt, string userMessage, CancellationToken cancellationToken = default)
    {
        // Reinforce strict-JSON behavior at the wrapper level so every handler gets
        // consistent, parseable output without repeating this instruction everywhere.
        var strictSystemPrompt = systemPrompt +
            "\n\nIMPORTANT: Reply with ONLY valid JSON. No markdown code fences, no prose before or after.";

        var raw = await CompleteAsync(strictSystemPrompt, userMessage, cancellationToken);

        // Defensive cleanup in case the model wraps output in ```json fences anyway.
        var trimmed = raw.Trim();
        if (trimmed.StartsWith("```"))
        {
            var firstNewline = trimmed.IndexOf('\n');
            var lastFence = trimmed.LastIndexOf("```", StringComparison.Ordinal);
            if (firstNewline > -1 && lastFence > firstNewline)
            {
                trimmed = trimmed.Substring(firstNewline + 1, lastFence - firstNewline - 1).Trim();
            }
        }

        return trimmed;
    }
}
