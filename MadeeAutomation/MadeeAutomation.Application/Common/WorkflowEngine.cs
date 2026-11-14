using System.Diagnostics;
using System.Text.Json;
using MadeeAutomation.Application.Abstractions;
using MadeeAutomation.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace MadeeAutomation.Application.Common;

/// <summary>What happened when the engine was asked to run a workflow.</summary>
public class WorkflowRunOutcome
{
    public Guid? ExecutionLogId { get; set; }
    public ExecutionStatus Status { get; set; }
    public string? Summary { get; set; }
    public string? Error { get; set; }

    /// <summary>Every action the run asked for (in a test run: what WOULD have been sent).</summary>
    public List<ActionRecord> Actions { get; set; } = new();

    /// <summary>Error if there was one, otherwise the summary.</summary>
    public string? Message => Error ?? Summary;

    /// <summary>True only when the run finished and every required action succeeded.</summary>
    public bool Succeeded => Status == ExecutionStatus.Succeeded;

    public static WorkflowRunOutcome NotRun(string message) => new() { Status = ExecutionStatus.Skipped, Error = message };
}

/// <summary>One line of ExecutionLog.ActionsJson - what an action was asked to do and how it went.</summary>
public class ActionRecord
{
    public string Type { get; set; } = string.Empty;
    public bool Required { get; set; }
    public bool Success { get; set; }
    public string? Result { get; set; }
    public string? Error { get; set; }
    public Dictionary<string, string> Parameters { get; set; } = new();
}

/// <summary>
/// The core orchestrator. Given a TriggerContext, it:
///  1. Looks up the WorkflowDefinition (and its client)
///  2. Skips the run if the client is Paused/Cancelled (logged as Skipped)
///  3. Resolves the matching IHandler by HandlerKey and runs it
///  4. Executes each returned action, recording every result in ExecutionLog.ActionsJson
///  5. Marks the run Failed when the handler fails OR any REQUIRED action fails
///  6. For customer messages, never leaves the customer hanging:
///       - handler failed (AI down, unreadable answer, POSFirst error) -> fallback reply + staff alert
///       - the reply itself could not be delivered                     -> staff alert
///  7. In a test run (TriggerContext.IsTest) nothing is sent - actions are recorded as "not sent".
/// </summary>
public class WorkflowEngine
{
    private readonly IWorkflowRepository _workflowRepository;
    private readonly IExecutionLogRepository _executionLogRepository;
    private readonly IReadOnlyDictionary<string, IHandler> _handlers;
    private readonly IReadOnlyDictionary<ActionType, IAction> _actions;
    private readonly ILogger<WorkflowEngine> _logger;

    private static readonly JsonSerializerOptions ActionsJsonOptions = new()
    {
        WriteIndented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public const string TestCustomerId = "admin-test";

    public WorkflowEngine(
        IWorkflowRepository workflowRepository,
        IExecutionLogRepository executionLogRepository,
        IEnumerable<IHandler> handlers,
        IEnumerable<IAction> actions,
        ILogger<WorkflowEngine> logger)
    {
        _workflowRepository = workflowRepository;
        _executionLogRepository = executionLogRepository;
        _logger = logger;

        var handlerMap = new Dictionary<string, IHandler>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in handlers)
        {
            if (!handlerMap.TryAdd(h.HandlerKey, h))
                _logger.LogError("Two handlers use the same HandlerKey '{Key}' - only the first one is used.", h.HandlerKey);
        }
        _handlers = handlerMap;

        var actionMap = new Dictionary<ActionType, IAction>();
        foreach (var a in actions) actionMap.TryAdd(a.Type, a);
        _actions = actionMap;
    }

    public async Task<WorkflowRunOutcome> RunAsync(TriggerContext context, CancellationToken cancellationToken = default)
    {
        var workflow = await _workflowRepository.GetByIdAsync(context.WorkflowDefinitionId, cancellationToken);
        if (workflow is null)
        {
            _logger.LogWarning("Workflow {WorkflowId} not found - skipping run", context.WorkflowDefinitionId);
            return WorkflowRunOutcome.NotRun("Workflow not found.");
        }

        if (!workflow.IsEnabled && !context.IsTest)
        {
            _logger.LogWarning("Workflow {WorkflowId} is disabled - skipping run", workflow.Id);
            return WorkflowRunOutcome.NotRun("Workflow is disabled.");
        }

        context.ClientConfigId = workflow.ClientConfigId;
        context.Client = workflow.ClientConfig;

        // Every AI call made during this run is recorded against this client (Admin -> Reports -> AI cost).
        using var aiUsage = AiUsageScope.Begin(workflow.ClientConfigId, workflow.HandlerKey, context.IsTest);

        var stopwatch = Stopwatch.StartNew();
        var executionLog = await _executionLogRepository.StartAsync(workflow, context, cancellationToken);
        var records = new List<ActionRecord>();

        async Task<WorkflowRunOutcome> Finish(ExecutionStatus status, string? summary, string? error)
        {
            if (context.IsTest && summary is not null) summary = "[Test] " + summary;
            if (context.IsTest && summary is null && error is not null) summary = "[Test]";

            var actionsJson = records.Count > 0 ? JsonSerializer.Serialize(records, ActionsJsonOptions) : null;
            // CancellationToken.None: always record the outcome, even if the caller is shutting down.
            await _executionLogRepository.CompleteAsync(executionLog.Id, status, summary, error, actionsJson, CancellationToken.None);
            _logger.LogInformation("Workflow {WorkflowId} ({HandlerKey}) finished {Status} in {Ms} ms: {Summary} {Error}",
                workflow.Id, workflow.HandlerKey, status, stopwatch.ElapsedMilliseconds, summary, error);
            return new WorkflowRunOutcome
            {
                ExecutionLogId = executionLog.Id, Status = status, Summary = summary, Error = error, Actions = records
            };
        }

        if (context.Client is { CanRunWorkflows: false } && !context.IsTest)
        {
            return await Finish(ExecutionStatus.Skipped, $"Client is {context.Client.Status} - nothing was run.", null);
        }

        if (!_handlers.TryGetValue(workflow.HandlerKey, out var handler))
        {
            return await Finish(ExecutionStatus.Failed, null, $"No handler registered for key '{workflow.HandlerKey}'.");
        }

        HandlerResult? result = null;
        string? handlerError = null;
        try
        {
            result = await handler.HandleAsync(context, cancellationToken);
            if (!result.Success) handlerError = result.ErrorMessage ?? "Handler reported a failure.";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Handler {HandlerKey} threw for workflow {WorkflowId}", workflow.HandlerKey, workflow.Id);
            handlerError = ex.Message;
        }

        try
        {
            if (handlerError is not null)
            {
                // ---- The AI step (or POSFirst) failed: don't leave the customer without an answer. ----
                if (IsCustomerMessage(context))
                {
                    var fallback = new List<ActionRequest>
                    {
                        Reply(context, context.Client?.EffectiveFallbackReply ?? ClientConfigFallback()),
                        StaffAlert(context, $"Automatic reply failed ({Short(handlerError)}). The customer got the fallback message - please follow up.")
                    };
                    await RunActionsAsync(fallback, context, workflow.Id, records, cancellationToken);
                    var fallbackDelivered = records.Any(r => r.Type == nameof(ActionType.SendMessage) && r.Success);
                    return await Finish(ExecutionStatus.Failed,
                        fallbackDelivered ? "Fallback reply sent; staff alerted." : "Fallback reply could not be sent; staff alerted.",
                        handlerError);
                }

                return await Finish(ExecutionStatus.Failed, result?.Summary, handlerError);
            }

            var firstRequiredError = await RunActionsAsync(result!.Actions, context, workflow.Id, records, cancellationToken);

            // ---- The reply could not be delivered: tell a person. ----
            if (firstRequiredError is not null && IsCustomerMessage(context) &&
                records.Any(r => r.Type == nameof(ActionType.SendMessage) && !r.Success) &&
                !records.Any(r => r.Type == nameof(ActionType.NotifyInternal) &&
                                  r.Parameters.GetValueOrDefault("kind") != NotifyInternalAction.OrderKind))
            {
                await RunActionsAsync(new List<ActionRequest>
                {
                    StaffAlert(context, $"Reply could not be delivered ({Short(firstRequiredError)}). Please contact the customer directly.")
                }, context, workflow.Id, records, cancellationToken);
            }

            return firstRequiredError is null
                ? await Finish(ExecutionStatus.Succeeded, result.Summary, null)
                : await Finish(ExecutionStatus.Failed, result.Summary, firstRequiredError);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Unhandled exception running workflow {WorkflowId}", workflow.Id);
            return await Finish(ExecutionStatus.Failed, null, ex.Message);
        }
    }

    /// <summary>Runs actions in order, records each one, returns the first REQUIRED failure (or null).</summary>
    private async Task<string?> RunActionsAsync(List<ActionRequest> requests, TriggerContext context, Guid workflowId,
        List<ActionRecord> records, CancellationToken cancellationToken)
    {
        string? firstRequiredError = null;

        foreach (var actionRequest in requests)
        {
            // Every action needs to know which client it acts for, so connectors use that
            // client's own credentials rather than a shared global config.
            actionRequest.Parameters["clientConfigId"] = context.ClientConfigId.ToString();

            var record = new ActionRecord
            {
                Type = actionRequest.Type.ToString(),
                Required = actionRequest.Required,
                Parameters = actionRequest.Parameters
                    .Where(p => p.Key is not ("clientConfigId" or "replyToken"))
                    .ToDictionary(p => p.Key, p => p.Value)
            };

            if (context.IsTest)
            {
                record.Success = true;
                record.Result = "Not sent (test run)";
            }
            else if (!_actions.TryGetValue(actionRequest.Type, out var action))
            {
                record.Success = false;
                record.Error = $"No action is available for {actionRequest.Type}.";
            }
            else
            {
                try
                {
                    var actionResult = await action.ExecuteAsync(actionRequest, cancellationToken);
                    record.Success = actionResult.Success;
                    record.Result = actionResult.ResultData;
                    record.Error = actionResult.ErrorMessage;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "Action {ActionType} threw for workflow {WorkflowId}", actionRequest.Type, workflowId);
                    record.Success = false;
                    record.Error = ex.Message;
                }
            }

            if (!record.Success)
            {
                _logger.LogWarning("Action {ActionType} failed for workflow {WorkflowId}: {Error}", actionRequest.Type, workflowId, record.Error);
                if (actionRequest.Required && firstRequiredError is null)
                    firstRequiredError = $"{actionRequest.Type} failed: {record.Error}";
            }

            records.Add(record);
        }

        return firstRequiredError;
    }

    private static bool IsCustomerMessage(TriggerContext context) =>
        Channels.Normalize(context.Channel) is not null &&
        (!string.IsNullOrWhiteSpace(context.CustomerId) || !string.IsNullOrWhiteSpace(context.ReplyToken));

    private static ActionRequest Reply(TriggerContext context, string text) => ActionRequest.For(ActionType.SendMessage,
        new Dictionary<string, string>
        {
            ["channel"] = Channels.Normalize(context.Channel)!,
            ["to"] = context.CustomerId ?? "",
            ["message"] = text,
            ["replyToken"] = context.ReplyToken ?? ""
        });

    private static ActionRequest StaffAlert(TriggerContext context, string what) => ActionRequest.For(ActionType.NotifyInternal,
        new Dictionary<string, string>
        {
            ["message"] = $"[{context.Client?.ClientName ?? "client"}] {Channels.DisplayName(context.Channel)} customer {context.CustomerId}: " +
                          $"{what} Customer wrote: \"{Short(context.MessageText, 300)}\""
        }, required: false);

    private static string ClientConfigFallback() => Domain.Entities.ClientConfig.DefaultFallbackEnglish;

    private static string Short(string? text, int max = 200) =>
        string.IsNullOrEmpty(text) ? "" : text.Length <= max ? text : text[..max] + "...";
}
