using System.Net;
using System.Text;
using System.Text.Json;
using MadeeAutomation.Application.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace MadeeAutomation.AI;

/// <summary>
/// Thin wrapper around the Anthropic Messages API (POST {BaseUrl}v1/messages).
/// Every vertical pack handler goes through here, so timeouts, retries and API-key handling live in one spot.
///
/// Config (appsettings / user-secrets):
///   Anthropic:ApiKey          required
///   Anthropic:Model           default claude-sonnet-4-6
///   Anthropic:BaseUrl         default https://api.anthropic.com/  (the E2E tests point this at a fake server)
///   Anthropic:MaxTokens       default 1024
///   Anthropic:TimeoutSeconds  default 60 (per attempt)
///   Anthropic:MaxRetries      default 2   (on 429 / 5xx / 529 overloaded / network errors)
/// </summary>
public class ClaudeService : IClaudeService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ClaudeService> _logger;
    private readonly string? _apiKey;
    private readonly string _model;
    private readonly Uri _baseUri;
    private readonly int _maxTokens;
    private readonly TimeSpan _timeout;
    private readonly int _maxRetries;

    public ClaudeService(IHttpClientFactory httpClientFactory, IConfiguration configuration, ILogger<ClaudeService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;

        var key = configuration["Anthropic:ApiKey"];
        _apiKey = string.IsNullOrWhiteSpace(key) || key.StartsWith("SET_", StringComparison.OrdinalIgnoreCase) ? null : key.Trim();

        _model = configuration["Anthropic:Model"] is { Length: > 0 } m ? m : "claude-sonnet-4-6";

        var baseUrl = configuration["Anthropic:BaseUrl"] is { Length: > 0 } b ? b : "https://api.anthropic.com/";
        _baseUri = new Uri(baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/");

        _maxTokens = int.TryParse(configuration["Anthropic:MaxTokens"], out var mt) && mt > 0 ? mt : 1024;
        _timeout = TimeSpan.FromSeconds(int.TryParse(configuration["Anthropic:TimeoutSeconds"], out var ts) && ts > 0 ? ts : 60);
        _maxRetries = int.TryParse(configuration["Anthropic:MaxRetries"], out var mr) && mr >= 0 ? mr : 2;
    }

    public async Task<string> CompleteAsync(string systemPrompt, string userMessage, CancellationToken cancellationToken = default)
    {
        if (_apiKey is null)
            throw new InvalidOperationException(
                "Anthropic:ApiKey is not configured. Set it with: dotnet user-secrets set \"Anthropic:ApiKey\" \"sk-ant-...\" (in MadeeAutomation.WebApi).");

        var requestJson = JsonSerializer.Serialize(new
        {
            model = _model,
            max_tokens = _maxTokens,
            system = systemPrompt,
            messages = new[] { new { role = "user", content = userMessage } }
        });

        for (var attempt = 0; ; attempt++)
        {
            using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            attemptCts.CancelAfter(_timeout);

            HttpResponseMessage? response = null;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_baseUri, "v1/messages"))
                {
                    Content = new StringContent(requestJson, Encoding.UTF8, "application/json")
                };
                request.Headers.Add("x-api-key", _apiKey);
                request.Headers.Add("anthropic-version", "2023-06-01");

                var client = _httpClientFactory.CreateClient("ClaudeApi");
                response = await client.SendAsync(request, attemptCts.Token);
                var body = await response.Content.ReadAsStringAsync(attemptCts.Token);

                if (response.IsSuccessStatusCode) return ExtractText(body);

                var status = (int)response.StatusCode;
                var retryable = status is 429 or 500 or 502 or 503 or 504 or 529;
                if (!retryable || attempt >= _maxRetries)
                {
                    _logger.LogError("Claude API call failed: {StatusCode} {Body}", status, body);
                    throw new InvalidOperationException($"Claude API call failed with status {status}: {Truncate(body, 500)}");
                }

                var delay = RetryDelay(response, attempt);
                _logger.LogWarning("Claude API returned {StatusCode} - retry {Attempt}/{Max} in {Delay}s", status, attempt + 1, _maxRetries, delay.TotalSeconds);
                await Task.Delay(delay, cancellationToken);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested
                                       && ex is HttpRequestException or TaskCanceledException
                                       && attempt < _maxRetries)
            {
                // Network error or per-attempt timeout (not the caller cancelling) - try again.
                _logger.LogWarning("Claude API attempt {Attempt} failed ({Error}) - retrying", attempt + 1, ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), cancellationToken);
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"Claude API did not answer within {_timeout.TotalSeconds:0} seconds.");
            }
            finally
            {
                response?.Dispose();
            }
        }
    }

    public async Task<string> CompleteJsonAsync(string systemPrompt, string userMessage, CancellationToken cancellationToken = default)
    {
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
                trimmed = trimmed.Substring(firstNewline + 1, lastFence - firstNewline - 1).Trim();
        }

        return trimmed;
    }

    private static string ExtractText(string responseBody)
    {
        using var doc = JsonDocument.Parse(responseBody);
        var textBuilder = new StringBuilder();
        if (doc.RootElement.TryGetProperty("content", out var contentArray) && contentArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var block in contentArray.EnumerateArray())
            {
                if (block.TryGetProperty("type", out var typeEl) && typeEl.GetString() == "text")
                    textBuilder.Append(block.GetProperty("text").GetString());
            }
        }
        return textBuilder.ToString();
    }

    private static TimeSpan RetryDelay(HttpResponseMessage response, int attempt)
    {
        if (response.Headers.RetryAfter?.Delta is { } delta)
            return delta > TimeSpan.FromSeconds(10) ? TimeSpan.FromSeconds(10) : delta;
        return TimeSpan.FromSeconds(Math.Pow(2, attempt)); // 1s, 2s, 4s...
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "...";
}
