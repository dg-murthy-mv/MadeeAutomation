using System.Text.Json;
using MadeeAutomation.Application.Abstractions;
using MadeeAutomation.Application.Common;
using MadeeAutomation.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace MadeeAutomation.Application.Inbound;

/// <summary>
/// Runs ONE stored InboundEvent: picks the client's workflow for the channel (or the fixed
/// workflow from a legacy per-workflow URL), builds the TriggerContext and calls the engine.
/// Called by the background worker, one DI scope per event.
/// </summary>
public class InboundEventProcessor
{
    private readonly IInboundEventRepository _events;
    private readonly IWorkflowRepository _workflows;
    private readonly ICurrentClientService _clients;
    private readonly WorkflowEngine _engine;
    private readonly ILogger<InboundEventProcessor> _logger;

    public InboundEventProcessor(IInboundEventRepository events, IWorkflowRepository workflows,
        ICurrentClientService clients, WorkflowEngine engine, ILogger<InboundEventProcessor> logger)
    {
        _events = events;
        _workflows = workflows;
        _clients = clients;
        _engine = engine;
        _logger = logger;
    }

    public async Task ProcessAsync(Guid inboundEventId, CancellationToken cancellationToken = default)
    {
        if (!await _events.TryClaimAsync(inboundEventId, cancellationToken)) return;

        try
        {
            var ev = await _events.GetByIdAsync(inboundEventId, cancellationToken);
            if (ev is null) return;

            var client = await _clients.GetClientAsync(ev.ClientConfigId, cancellationToken);
            if (client is null)
            {
                await _events.MarkAsync(ev.Id, InboundEventStatus.Failed, "Client no longer exists.", null, CancellationToken.None);
                return;
            }

            Guid? workflowId = ev.WorkflowDefinitionId;
            if (workflowId is null)
            {
                var candidates = await _workflows.GetEnabledMessageWorkflowsAsync(client.Id, ev.Channel, cancellationToken);
                if (candidates.Count == 0)
                {
                    await _events.MarkAsync(ev.Id, InboundEventStatus.Skipped,
                        $"No enabled message workflow for {Channels.DisplayName(ev.Channel)}.", null, CancellationToken.None);
                    _logger.LogWarning("{ClientCode}: {Channel} message skipped - no enabled message workflow for this channel",
                        client.ClientCode, ev.Channel);
                    return;
                }
                if (candidates.Count > 1)
                {
                    _logger.LogWarning("{ClientCode}: {Count} enabled message workflows match {Channel} - using '{Name}'",
                        client.ClientCode, candidates.Count, ev.Channel, candidates[0].Name);
                }
                workflowId = candidates[0].Id;
            }

            var context = new TriggerContext
            {
                ClientConfigId = client.Id,
                WorkflowDefinitionId = workflowId.Value,
                TriggerType = TriggerType.MessageReceived,
                InboundEventId = ev.Id,
                Channel = ev.Channel,
                CustomerId = ev.CustomerId,
                MessageText = ev.MessageText,
                ReplyToken = ev.ReplyToken,
                OccurredAtUtc = ev.ReceivedAtUtc,
                // Same { message, from } shape the older handlers read, plus the channel.
                PayloadJson = JsonSerializer.Serialize(new { message = ev.MessageText, from = ev.CustomerId, channel = ev.Channel })
            };

            var outcome = await _engine.RunAsync(context, cancellationToken);

            if (outcome.ExecutionLogId is null)
            {
                // Engine did not run (workflow disabled/deleted between routing and running).
                await _events.MarkAsync(ev.Id, InboundEventStatus.Skipped, outcome.Message, null, CancellationToken.None);
                return;
            }

            await _events.MarkAsync(ev.Id,
                outcome.Status == ExecutionStatus.Skipped ? InboundEventStatus.Skipped : InboundEventStatus.Done,
                outcome.Succeeded ? null : outcome.Message, outcome.ExecutionLogId, CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Inbound event {EventId} failed", inboundEventId);
            await _events.MarkAsync(inboundEventId, InboundEventStatus.Failed, ex.Message, null, CancellationToken.None);
        }
    }
}
