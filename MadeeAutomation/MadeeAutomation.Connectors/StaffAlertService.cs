using MadeeAutomation.Application.Abstractions;
using MadeeAutomation.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace MadeeAutomation.Connectors;

/// <summary>
/// Sends a staff alert to every place that is set up for it:
///   1. the client's StaffAlertLineUserId - a LINE push through the client's own LINE account
///   2. the client's StaffAlertEmail      - needs Email:* settings
///   3. MadeeAutomation's internal WhatsApp (Connectors:WhatsApp:InternalNotifyTo)
/// Succeeds if at least one got through. If nothing is set up, the alert is only logged
/// (and counted as success, so a missing alert channel never fails a customer's run).
/// </summary>
public class StaffAlertService : IStaffAlertService
{
    private readonly ICurrentClientService _clients;
    private readonly IMessagingConnector _messaging;
    private readonly IEmailSender _email;
    private readonly ILogger<StaffAlertService> _logger;

    public StaffAlertService(ICurrentClientService clients, IMessagingConnector messaging, IEmailSender email, ILogger<StaffAlertService> logger)
    {
        _clients = clients;
        _messaging = messaging;
        _email = email;
        _logger = logger;
    }

    public async Task<MessageSendResult> SendAsync(Guid? clientConfigId, string message, CancellationToken cancellationToken = default)
    {
        var delivered = new List<string>();
        var failed = new List<string>();

        if (clientConfigId is { } id && await _clients.GetClientAsync(id, cancellationToken) is { } client)
        {
            if (!string.IsNullOrWhiteSpace(client.StaffAlertLineUserId))
            {
                var r = await _messaging.SendAsync(new OutboundMessage
                {
                    ClientConfigId = client.Id,
                    Channel = Channels.Line,
                    To = client.StaffAlertLineUserId.Trim(),
                    Text = "⚠️ " + message
                }, cancellationToken);
                (r.Success ? delivered : failed).Add(r.Success ? $"LINE {r.Mode}" : $"LINE: {r.Error}");
            }

            if (!string.IsNullOrWhiteSpace(client.StaffAlertEmail))
            {
                if (_email.IsConfigured)
                {
                    var r = await _email.SendAsync(client.StaffAlertEmail.Trim(),
                        $"[MadeeAutomation] {client.ClientName} needs attention", message, cancellationToken);
                    (r.Success ? delivered : failed).Add(r.Success ? $"email {r.Mode}" : $"email: {r.Error}");
                }
                else
                {
                    failed.Add("email: the email settings (Resend:ApiKey, Email:FromAddress) are missing in WebApi");
                    _logger.LogWarning("Staff alert email for {ClientCode} skipped - Email:* is not configured in WebApi", client.ClientCode);
                }
            }
        }

        var internalResult = await _messaging.SendInternalNotificationAsync(message, cancellationToken);
        if (internalResult.Mode != "mock")
            (internalResult.Success ? delivered : failed).Add(internalResult.Success ? "internal WhatsApp" : $"internal WhatsApp: {internalResult.Error}");

        if (delivered.Count > 0) return MessageSendResult.Ok(string.Join(", ", delivered) + (failed.Count > 0 ? " - NOT sent: " + string.Join("; ", failed) : ""));
        if (failed.Count > 0) return MessageSendResult.Fail("alert", string.Join("; ", failed));

        _logger.LogWarning("Staff alert (no alert channel set up - set a staff LINE user id or email on the client): {Message}", message);
        return MessageSendResult.Ok("log only - no staff alert channel set up");
    }

    public async Task<MessageSendResult> SendNewOrderAsync(Guid clientConfigId, string subject, string message, CancellationToken cancellationToken = default)
    {
        var client = await _clients.GetClientAsync(clientConfigId, cancellationToken);
        if (client is null) return MessageSendResult.Fail("order message", "Client not found.");

        var delivered = new List<string>();
        var failed = new List<string>();

        if (!string.IsNullOrWhiteSpace(client.StaffAlertLineUserId))
        {
            var r = await _messaging.SendAsync(new OutboundMessage
            {
                ClientConfigId = client.Id,
                Channel = Channels.Line,
                To = client.StaffAlertLineUserId.Trim(),
                Text = "🛒 " + message
            }, cancellationToken);
            (r.Success ? delivered : failed).Add(r.Success ? $"LINE {r.Mode}" : $"LINE: {r.Error}");
        }

        if (!string.IsNullOrWhiteSpace(client.StaffAlertEmail))
        {
            if (_email.IsConfigured)
            {
                var r = await _email.SendAsync(client.StaffAlertEmail.Trim(), subject, message, cancellationToken);
                (r.Success ? delivered : failed).Add(r.Success ? $"email {r.Mode}" : $"email: {r.Error}");
            }
            else
            {
                failed.Add("email: the email settings (Resend:ApiKey, Email:FromAddress) are missing in WebApi");
                _logger.LogWarning("New-order email for {ClientCode} skipped - Email:* is not configured in WebApi", client.ClientCode);
            }
        }

        if (delivered.Count > 0) return MessageSendResult.Ok(string.Join(", ", delivered) + (failed.Count > 0 ? " - NOT sent: " + string.Join("; ", failed) : ""));
        if (failed.Count > 0) return MessageSendResult.Fail("order message", string.Join("; ", failed));

        _logger.LogWarning("New order for {ClientCode} was not sent to staff - no staff LINE user id or email is set. It is on the Orders page only.", client.ClientCode);
        return MessageSendResult.Ok("Orders page only - no staff LINE user id or email set");
    }
}
