using MadeeAutomation.Application.Common;
using MadeeAutomation.Domain.Entities;
using MadeeAutomation.Domain.Enums;
using MadeeAutomation.Domain.Settings;

namespace MadeeAutomation.Application.Abstractions;

/// <summary>
/// Same role as ICurrentTenantService in Merkai CRM: resolves the client a workflow runs for.
/// </summary>
public interface ICurrentClientService
{
    Task<ClientConfig?> GetClientAsync(Guid clientConfigId, CancellationToken cancellationToken = default);
    Task<ClientConfig?> GetByCodeAsync(string clientCode, CancellationToken cancellationToken = default);
}

/// <summary>
/// The ONLY way to read or write a client's credentials. Decrypts/encrypts
/// ClientConfig.IntegrationSettingsJson so nothing else ever touches the raw column.
/// </summary>
public interface IClientSettingsService
{
    Task<ClientIntegrationSettings> GetAsync(Guid clientConfigId, CancellationToken cancellationToken = default);

    /// <summary>Decrypts a stored column value (plain legacy JSON is accepted).</summary>
    ClientIntegrationSettings Read(string? storedValue);

    /// <summary>Encrypts settings for storage. Throws if the encryption key is not configured.</summary>
    string Protect(ClientIntegrationSettings settings);
}

public interface IExecutionLogRepository
{
    Task<ExecutionLog> StartAsync(WorkflowDefinition workflow, TriggerContext context, CancellationToken cancellationToken = default);

    Task CompleteAsync(Guid executionLogId, ExecutionStatus status, string? summary, string? error, string? actionsJson,
        CancellationToken cancellationToken = default);

    Task<List<ExecutionLog>> GetRecentAsync(Guid clientConfigId, int take = 50, CancellationToken cancellationToken = default);

    Task<ExecutionLog?> GetByIdAsync(Guid executionLogId, CancellationToken cancellationToken = default);

    /// <summary>Paged, filterable execution log query for the Admin dashboard.</summary>
    Task<PagedResult<ExecutionLog>> GetPagedAsync(
        Guid clientConfigId, int page, int pageSize, string? statusFilter = null, string? search = null,
        CancellationToken cancellationToken = default);
}

public interface IWorkflowRepository
{
    Task<List<WorkflowDefinition>> GetActiveWorkflowsAsync(Guid clientConfigId, CancellationToken cancellationToken = default);

    /// <summary>Includes ClientConfig.</summary>
    Task<WorkflowDefinition?> GetByIdAsync(Guid workflowDefinitionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Enabled MessageReceived workflows that listen to this channel - exact channel matches first,
    /// then "any channel" (Channel = null) workflows.
    /// </summary>
    Task<List<WorkflowDefinition>> GetEnabledMessageWorkflowsAsync(Guid clientConfigId, string channel,
        CancellationToken cancellationToken = default);

    Task AddAsync(WorkflowDefinition workflow, CancellationToken cancellationToken = default);
    Task UpdateAsync(WorkflowDefinition workflow, CancellationToken cancellationToken = default);
    Task<bool> SetEnabledAsync(Guid workflowDefinitionId, bool isEnabled, CancellationToken cancellationToken = default);
}

/// <summary>Client list/create/update for the Admin dashboard (keeps DbContext out of controllers).</summary>
public interface IClientRepository
{
    Task<PagedResult<ClientConfig>> GetPagedAsync(string? search, int page, int pageSize, CancellationToken cancellationToken = default);

    /// <summary>Includes Workflows.</summary>
    Task<ClientConfig?> GetWithWorkflowsAsync(Guid clientConfigId, CancellationToken cancellationToken = default);

    Task<bool> CodeExistsAsync(string clientCode, Guid? exceptClientId = null, CancellationToken cancellationToken = default);
    Task AddAsync(ClientConfig client, CancellationToken cancellationToken = default);
    Task UpdateAsync(ClientConfig client, CancellationToken cancellationToken = default);
}

public interface IInboundEventRepository
{
    /// <summary>Stores the event. Returns false when the same (client, channel, external id) is already stored.</summary>
    Task<bool> TryAddAsync(InboundEvent inboundEvent, CancellationToken cancellationToken = default);

    Task<List<Guid>> GetPendingIdsAsync(int take, CancellationToken cancellationToken = default);

    /// <summary>Atomically moves Pending -> Processing. Returns false if another worker already took it.</summary>
    Task<bool> TryClaimAsync(Guid inboundEventId, CancellationToken cancellationToken = default);

    Task<InboundEvent?> GetByIdAsync(Guid inboundEventId, CancellationToken cancellationToken = default);

    Task MarkAsync(Guid inboundEventId, InboundEventStatus status, string? error, Guid? executionLogId,
        CancellationToken cancellationToken = default);

    /// <summary>Events left in Processing by a crash/restart are marked Failed (not re-run, to avoid duplicate orders).</summary>
    Task<int> FailStuckAsync(TimeSpan olderThan, CancellationToken cancellationToken = default);

    /// <summary>Newest first, for the Admin "Inbound events" page.</summary>
    Task<PagedResult<InboundEvent>> GetPagedAsync(Guid clientConfigId, int page, int pageSize, InboundEventStatus? status = null,
        CancellationToken cancellationToken = default);
}

/// <summary>Admin.Web sign-in accounts and their password-reset links.</summary>
public interface IAdminUserRepository
{
    Task<int> CountAsync(CancellationToken cancellationToken = default);
    Task<AdminUser?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<AdminUser?> GetByEmailAsync(string email, CancellationToken cancellationToken = default);
    Task<List<AdminUser>> ListAsync(CancellationToken cancellationToken = default);
    Task AddAsync(AdminUser user, CancellationToken cancellationToken = default);
    Task UpdateAsync(AdminUser user, CancellationToken cancellationToken = default);

    Task AddResetTokenAsync(PasswordResetToken token, CancellationToken cancellationToken = default);
    Task<PasswordResetToken?> GetResetTokenAsync(string tokenHash, CancellationToken cancellationToken = default);

    /// <summary>Marks every unused link of this user as used (after a successful reset or a new link).</summary>
    Task UseAllResetTokensAsync(Guid adminUserId, CancellationToken cancellationToken = default);
}

public class EmailSendResult
{
    public bool Success { get; set; }
    public string Mode { get; set; } = string.Empty;   // "smtp", "pickup" (written to a folder), "not-configured"
    public string? Error { get; set; }
}

/// <summary>Plain-text email (password resets, staff alerts). SMTP or a pickup folder - see Email:* settings.</summary>
public interface IEmailSender
{
    bool IsConfigured { get; }
    Task<EmailSendResult> SendAsync(string to, string subject, string textBody, CancellationToken cancellationToken = default);
}

/// <summary>
/// Tells people when something needs a human: the client's staff LINE user (pushed through the
/// client's own LINE account), the client's staff alert email, and MadeeAutomation's own internal
/// WhatsApp number. Whatever is configured gets the alert.
/// </summary>
public interface IStaffAlertService
{
    Task<MessageSendResult> SendAsync(Guid? clientConfigId, string message, CancellationToken cancellationToken = default);
}

/// <summary>Thin wrapper over the Anthropic Messages API - see MadeeAutomation.AI project.</summary>
public interface IClaudeService
{
    Task<string> CompleteAsync(string systemPrompt, string userMessage, CancellationToken cancellationToken = default);

    /// <summary>Asks Claude to return strict JSON. Returns the raw JSON text (fences stripped).</summary>
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

/// <summary>One outbound customer message.</summary>
public class OutboundMessage
{
    public Guid ClientConfigId { get; set; }
    public string Channel { get; set; } = string.Empty;
    public string To { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;

    /// <summary>LINE only: reply with this token first (free); falls back to a push message if it fails.</summary>
    public string? ReplyToken { get; set; }
}

public class MessageSendResult
{
    public bool Success { get; set; }

    /// <summary>"reply", "push", "send" or "mock" (no credentials - logged only).</summary>
    public string Mode { get; set; } = string.Empty;
    public string? Error { get; set; }

    public static MessageSendResult Ok(string mode) => new() { Success = true, Mode = mode };
    public static MessageSendResult Fail(string mode, string error) => new() { Success = false, Mode = mode, Error = error };
}

/// <summary>Outbound messaging connector - WhatsApp Business API / LINE Messaging API / Messenger.</summary>
public interface IMessagingConnector
{
    /// <summary>Sends a message to a client's own customer, using that client's own credentials.</summary>
    Task<MessageSendResult> SendAsync(OutboundMessage message, CancellationToken cancellationToken = default);

    /// <summary>Sends an internal notification to MadeeAutomation's own ops channel - not client-specific.</summary>
    Task<MessageSendResult> SendInternalNotificationAsync(string message, CancellationToken cancellationToken = default);
}
