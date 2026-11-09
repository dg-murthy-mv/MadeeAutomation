using MadeeAutomation.Application.Abstractions;
using MadeeAutomation.Domain.Entities;
using MadeeAutomation.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace MadeeAutomation.Application.Inbound;

public class IngestResult
{
    public int Stored { get; set; }
    public int Duplicates { get; set; }
    public int Ignored { get; set; }
}

/// <summary>
/// Stores parsed webhook messages as InboundEvents (skipping ones already stored) and wakes
/// the worker. This is all a webhook request does, so it answers the platform in milliseconds.
/// </summary>
public class InboundEventIngestor
{
    private readonly IInboundEventRepository _repository;
    private readonly InboundSignal _signal;
    private readonly ILogger<InboundEventIngestor> _logger;

    public InboundEventIngestor(IInboundEventRepository repository, InboundSignal signal, ILogger<InboundEventIngestor> logger)
    {
        _repository = repository;
        _signal = signal;
        _logger = logger;
    }

    public async Task<IngestResult> IngestAsync(ClientConfig client, string channel, Guid? workflowDefinitionId,
        ParsedWebhook parsed, CancellationToken cancellationToken = default)
    {
        var result = new IngestResult { Ignored = parsed.IgnoredEvents };

        foreach (var message in parsed.Messages)
        {
            var inbound = new InboundEvent
            {
                ClientConfigId = client.Id,
                WorkflowDefinitionId = workflowDefinitionId,
                Channel = channel,
                ExternalEventId = message.ExternalEventId,
                CustomerId = message.CustomerId,
                MessageText = message.Text,
                ReplyToken = message.ReplyToken,
                Status = InboundEventStatus.Pending,
                ReceivedAtUtc = DateTime.UtcNow
            };

            if (await _repository.TryAddAsync(inbound, cancellationToken))
                result.Stored++;
            else
                result.Duplicates++;
        }

        if (result.Stored > 0) _signal.Notify();

        _logger.LogInformation("{Channel} webhook for {ClientCode}: {Stored} stored, {Duplicates} duplicate(s), {Ignored} ignored",
            channel, client.ClientCode, result.Stored, result.Duplicates, result.Ignored);

        return result;
    }
}
