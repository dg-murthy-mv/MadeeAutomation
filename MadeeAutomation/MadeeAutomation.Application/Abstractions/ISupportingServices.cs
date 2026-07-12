using MadeeAutomation.Domain.Entities;

namespace MadeeAutomation.Application.Abstractions;

/// <summary>
/// Same role as ICurrentTenantService in Merkai CRM: resolves the current client's
/// currency, timezone, and integration config for whatever workflow is executing.
/// </summary>
public interface ICurrentClientService
{
    Task<ClientConfig?> GetClientAsync(Guid clientConfigId, CancellationToken cancellationToken = default);
    Task<T?> GetIntegrationSettingAsync<T>(Guid clientConfigId, string key, CancellationToken cancellationToken = default);
}

public interface IExecutionLogRepository
{
    Task<ExecutionLog> StartAsync(WorkflowDefinition workflow, string triggerPayloadJson, CancellationToken cancellationToken = default);
    Task CompleteAsync(Guid executionLogId, bool success, string? summary, string? error, CancellationToken cancellationToken = default);
    Task<List<ExecutionLog>> GetRecentAsync(Guid clientConfigId, int take = 50, CancellationToken cancellationToken = default);

    /// <summary>Paged, filterable execution log query for the Admin dashboard.</summary>
    Task<MadeeAutomation.Application.Common.PagedResult<ExecutionLog>> GetPagedAsync(
        Guid clientConfigId, int page, int pageSize, string? statusFilter = null, string? search = null,
        CancellationToken cancellationToken = default);
}

public interface IWorkflowRepository
{
    Task<List<WorkflowDefinition>> GetActiveWorkflowsAsync(Guid clientConfigId, CancellationToken cancellationToken = default);
    Task<WorkflowDefinition?> GetByIdAsync(Guid workflowDefinitionId, CancellationToken cancellationToken = default);
}

/// <summary>Thin wrapper over the Anthropic Messages API - see MadeeAutomation.AI project.</summary>
public interface IClaudeService
{
    Task<string> CompleteAsync(string systemPrompt, string userMessage, CancellationToken cancellationToken = default);

    /// <summary>Asks Claude to return strict JSON matching the caller's expected shape, for structured extraction
    /// (e.g. parsing a WhatsApp order message into { items: [...], customerName: "..." }).</summary>
    Task<string> CompleteJsonAsync(string systemPrompt, string userMessage, CancellationToken cancellationToken = default);
}

/// <summary>Connector back into Merkai CRM - used by CRM/Sales vertical pack handlers.</summary>
public interface IMerkaiConnector
{
    Task<string> CreateOrUpdateLeadAsync(Guid clientConfigId, object leadPayload, CancellationToken cancellationToken = default);
    Task<string> AddDealNoteAsync(Guid clientConfigId, string dealId, string note, CancellationToken cancellationToken = default);
}

/// <summary>Connector back into POSFirst - used by F&B/Retail vertical pack handlers.</summary>
public interface IPosFirstConnector
{
    Task<string> CreateOrderAsync(Guid clientConfigId, object orderPayload, CancellationToken cancellationToken = default);
    Task<List<string>> GetLowStockItemsAsync(Guid clientConfigId, CancellationToken cancellationToken = default);
}

/// <summary>Outbound messaging connector - WhatsApp Business API / LINE Messaging API.</summary>
public interface IMessagingConnector
{
    Task SendTextMessageAsync(string channel, string toAddress, string message, CancellationToken cancellationToken = default);
}
