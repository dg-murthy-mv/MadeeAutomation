using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace MadeeAutomation.Admin.Web.Services;

public class WorkflowTestAction
{
    public string Type { get; set; } = string.Empty;
    public Dictionary<string, string> Parameters { get; set; } = new();
}

public class WorkflowTestResult
{
    public bool CallFailed { get; set; }
    public string? CallError { get; set; }

    public string? Status { get; set; }
    public string? Summary { get; set; }
    public string? Error { get; set; }
    public Guid? ExecutionLogId { get; set; }
    public List<WorkflowTestAction> Actions { get; set; } = new();
}

/// <summary>Admin.Web -> WebApi calls (same IApiService pattern as Merkai). Sends X-Internal-Key.</summary>
public interface IApiService
{
    Task<WorkflowTestResult> TestWorkflowAsync(Guid workflowId, string message, string? channel, CancellationToken cancellationToken = default);
}

public class ApiService : IApiService
{
    public const string ClientName = "WebApi";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ApiService> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public ApiService(IHttpClientFactory httpClientFactory, IConfiguration configuration, ILogger<ApiService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<WorkflowTestResult> TestWorkflowAsync(Guid workflowId, string message, string? channel, CancellationToken cancellationToken = default)
    {
        var key = _configuration["InternalApi:Key"];
        if (string.IsNullOrWhiteSpace(key))
            return new WorkflowTestResult { CallFailed = true, CallError = "InternalApi:Key is not set in Admin.Web. Set the same key in Admin.Web and WebApi user-secrets (see SETUP.md)." };

        var client = _httpClientFactory.CreateClient(ClientName);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"api/internal/workflows/{workflowId}/test")
        {
            Content = JsonContent.Create(new { message, channel })
        };
        request.Headers.Add("X-Internal-Key", key.Trim());

        try
        {
            using var response = await client.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.ServiceUnavailable)
                return new WorkflowTestResult { CallFailed = true, CallError = $"WebApi refused the call ({(int)response.StatusCode}). InternalApi:Key must be set to the same value in WebApi and Admin.Web. {body}" };

            if (!response.IsSuccessStatusCode)
                return new WorkflowTestResult { CallFailed = true, CallError = $"WebApi answered {(int)response.StatusCode}: {body}" };

            return JsonSerializer.Deserialize<WorkflowTestResult>(body, JsonOptions) ?? new WorkflowTestResult { CallFailed = true, CallError = "Empty answer from WebApi." };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogError(ex, "Workflow test call to WebApi failed");
            return new WorkflowTestResult { CallFailed = true, CallError = $"Could not reach WebApi at {client.BaseAddress} - is it running? ({ex.Message})" };
        }
    }
}
