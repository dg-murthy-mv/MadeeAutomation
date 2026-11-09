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
    public string? Message { get; set; }

    /// <summary>True only when the run finished and every required action succeeded.</summary>
    public bool Succeeded => Status == ExecutionStatus.Succeeded;

    public static WorkflowRunOutcome NotRun(string message) => new() { Status = ExecutionStatus.Skipped, Message = message };
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
/// </summary>
public class WorkflowEngine
{
    private readonly IWorkflowRepository _workflowRepository;
    private readonly IExecutionLogRepository _executionLogRepository;
    private readonly IReadOnlyDictionary<string, IHandler> _handlers;
    private readonly IReadOnlyDictionary<ActionType, IAction> _actions;
    private readonly ILogger<WorkflowEngine> _logger;

    private static readonly JsonSerializerOptions ActionsJsonOptions = new() { WriteIndented = false };

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

        if (!workflow.IsEnabled)
        {
            _logger.LogWarning("Workflow {WorkflowId} is disabled - skipping run", workflow.Id);
            return WorkflowRunOutcome.NotRun("Workflow is disabled.");
        }

        context.ClientConfigId = workflow.ClientConfigId;
        context.Client = workflow.ClientConfig;

        var stopwatch = Stopwatch.StartNew();
        var executionLog = await _executionLogRepository.StartAsync(workflow, context, cancellationToken);

        async Task<WorkflowRunOutcome> Finish(ExecutionStatus status, string? summary, string? error, List<ActionRecord>? records)
        {
            var actionsJson = records is { Count: > 0 } ? JsonSerializer.Serialize(records, ActionsJsonOptions) : null;
            // CancellationToken.None: always record the outcome, even if the caller is shutting down.
            await _executionLogRepository.CompleteAsync(executionLog.Id, status, summary, error, actionsJson, CancellationToken.None);
            _logger.LogInformation("Workflow {WorkflowId} ({HandlerKey}) finished {Status} in {Ms} ms: {Summary} {Error}",
                workflow.Id, workflow.HandlerKey, status, stopwatch.ElapsedMilliseconds, summary, error);
            return new WorkflowRunOutcome { ExecutionLogId = executionLog.Id, Status = status, Message = error ?? summary };
        }

        if (context.Client is { CanRunWorkflows: false })
        {
            return await Finish(ExecutionStatus.Skipped, $"Client is {context.Client.Status} - nothing was run.", null, null);
        }

        if (!_handlers.TryGetValue(workflow.HandlerKey, out var handler))
        {
            return await Finish(ExecutionStatus.Failed, null, $"No handler registered for key '{workflow.HandlerKey}'.", null);
        }

        var records = new List<ActionRecord>();
        try
        {
            var result = await handler.HandleAsync(context, cancellationToken);
            if (!result.Success)
            {
                return await Finish(ExecutionStatus.Failed, result.Summary, result.ErrorMessage ?? "Handler reported a failure.", null);
            }

            string? firstRequiredError = null;

            foreach (var actionRequest in result.Actions)
            {
                // Every action needs to know which client it acts for, so connectors use that
                // client's own credentials rather than a shared global config.
                actionRequest.Parameters["clientConfigId"] = workflow.ClientConfigId.ToString();

                var record = new ActionRecord
                {
                    Type = actionRequest.Type.ToString(),
                    Required = actionRequest.Required,
                    Parameters = actionRequest.Parameters
                        .Where(p => p.Key is not ("clientConfigId" or "replyToken"))
                        .ToDictionary(p => p.Key, p => p.Value)
                };

                if (!_actions.TryGetValue(actionRequest.Type, out var action))
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
                        _logger.LogError(ex, "Action {ActionType} threw for workflow {WorkflowId}", actionRequest.Type, workflow.Id);
                        record.Success = false;
                        record.Error = ex.Message;
                    }
                }

                if (!record.Success)
                {
                    _logger.LogWarning("Action {ActionType} failed for workflow {WorkflowId}: {Error}",
                        actionRequest.Type, workflow.Id, record.Error);
                    if (actionRequest.Required && firstRequiredError is null)
                        firstRequiredError = $"{actionRequest.Type} failed: {record.Error}";
                }

                records.Add(record);
            }

            return firstRequiredError is null
                ? await Finish(ExecutionStatus.Succeeded, result.Summary, null, records)
                : await Finish(ExecutionStatus.Failed, result.Summary, firstRequiredError, records);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception running workflow {WorkflowId}", workflow.Id);
            return await Finish(ExecutionStatus.Failed, null, ex.Message, records);
        }
    }
}
