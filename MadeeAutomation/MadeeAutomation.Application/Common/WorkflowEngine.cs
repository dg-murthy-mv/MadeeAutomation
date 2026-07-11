using MadeeAutomation.Application.Abstractions;
using MadeeAutomation.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace MadeeAutomation.Application.Common;

/// <summary>
/// The core orchestrator. Given a TriggerContext, it:
///  1. Looks up the WorkflowDefinition
///  2. Resolves the matching IHandler by HandlerKey (via Scrutor-registered keyed lookup)
///  3. Runs the handler, gets back a list of ActionRequests
///  4. Executes each action via the matching IAction
///  5. Records everything to ExecutionLog
///
/// This is the one class every vertical pack and every Trigger source (webhook, scheduler,
/// message-received) ultimately calls into. New verticals never need to touch this file -
/// they just add a new IHandler + IAction implementations.
/// </summary>
public class WorkflowEngine
{
    private readonly IWorkflowRepository _workflowRepository;
    private readonly IExecutionLogRepository _executionLogRepository;
    private readonly IEnumerable<IHandler> _handlers;
    private readonly IEnumerable<IAction> _actions;
    private readonly ILogger<WorkflowEngine> _logger;

    public WorkflowEngine(
        IWorkflowRepository workflowRepository,
        IExecutionLogRepository executionLogRepository,
        IEnumerable<IHandler> handlers,
        IEnumerable<IAction> actions,
        ILogger<WorkflowEngine> logger)
    {
        _workflowRepository = workflowRepository;
        _executionLogRepository = executionLogRepository;
        _handlers = handlers;
        _actions = actions;
        _logger = logger;
    }

    public async Task<bool> RunAsync(TriggerContext context, CancellationToken cancellationToken = default)
    {
        var workflow = await _workflowRepository.GetByIdAsync(context.WorkflowDefinitionId, cancellationToken);
        if (workflow is null || !workflow.IsEnabled)
        {
            _logger.LogWarning("Workflow {WorkflowId} not found or disabled - skipping run", context.WorkflowDefinitionId);
            return false;
        }

        var executionLog = await _executionLogRepository.StartAsync(workflow, context.PayloadJson, cancellationToken);

        var handler = _handlers.FirstOrDefault(h => h.HandlerKey == workflow.HandlerKey);
        if (handler is null)
        {
            var msg = $"No handler registered for key '{workflow.HandlerKey}'";
            _logger.LogError(msg);
            await _executionLogRepository.CompleteAsync(executionLog.Id, false, null, msg, cancellationToken);
            return false;
        }

        try
        {
            var result = await handler.HandleAsync(context, cancellationToken);

            if (!result.Success)
            {
                await _executionLogRepository.CompleteAsync(executionLog.Id, false, result.Summary, result.ErrorMessage, cancellationToken);
                return false;
            }

            foreach (var actionRequest in result.Actions)
            {
                var action = _actions.FirstOrDefault(a => a.Type == actionRequest.Type);
                if (action is null)
                {
                    _logger.LogWarning("No IAction registered for type {ActionType} - skipping", actionRequest.Type);
                    continue;
                }

                var actionResult = await action.ExecuteAsync(actionRequest, cancellationToken);
                if (!actionResult.Success)
                {
                    _logger.LogWarning(
                        "Action {ActionType} failed for workflow {WorkflowId}: {Error}",
                        actionRequest.Type, workflow.Id, actionResult.ErrorMessage);
                }
            }

            await _executionLogRepository.CompleteAsync(executionLog.Id, true, result.Summary, null, cancellationToken);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception running workflow {WorkflowId}", workflow.Id);
            await _executionLogRepository.CompleteAsync(executionLog.Id, false, null, ex.Message, cancellationToken);
            return false;
        }
    }
}
