using MadeeAutomation.Application.Common;
using System.Globalization;
using MadeeAutomation.Application.Abstractions;
using MadeeAutomation.Domain.Entities;
using MadeeAutomation.Domain.Regional;
using Microsoft.Extensions.Logging;

namespace MadeeAutomation.Application.VerticalPacks.Messaging;

public class OrderNoticeResult
{
    /// <summary>"sent", "failed", "off" (the client switched these messages off) or "skipped" (nothing to tell / nobody to tell).</summary>
    public string Outcome { get; set; } = "skipped";
    public string? Detail { get; set; }      // what was sent, or why not
    public bool Sent => Outcome == "sent";
}

/// <summary>
/// Tells the customer when staff ACCEPT or CANCEL their order, on the channel they ordered on.
///
/// The message is written in the customer's own language: English customers get a fixed English
/// text (no AI call); for any other language the AI translates that same text. If the AI is down
/// or answers something odd, the English text is sent - the customer is never left without news.
/// Whatever happens is recorded on the order, so staff can see whether the customer was told.
/// </summary>
public class OrderCustomerNotifier
{
    private readonly IOrderRepository _orders;
    private readonly ICurrentClientService _clients;
    private readonly IMessagingConnector _messaging;
    private readonly IClaudeService _claude;
    private readonly ILogger<OrderCustomerNotifier> _logger;

    public OrderCustomerNotifier(IOrderRepository orders, ICurrentClientService clients, IMessagingConnector messaging,
        IClaudeService claude, ILogger<OrderCustomerNotifier> logger)
    {
        _orders = orders;
        _clients = clients;
        _messaging = messaging;
        _claude = claude;
        _logger = logger;
    }

    public async Task<OrderNoticeResult> NotifyAsync(Guid clientConfigId, Guid orderId, CancellationToken cancellationToken = default)
    {
        var order = await _orders.GetAsync(clientConfigId, orderId, cancellationToken);
        var client = await _clients.GetClientAsync(clientConfigId, cancellationToken);
        if (order is null || client is null) return new OrderNoticeResult { Outcome = "skipped", Detail = "Order not found." };

        if (order.Status is not (OrderStatus.Accepted or OrderStatus.Cancelled))
            return new OrderNoticeResult { Outcome = "skipped", Detail = $"Nothing is sent for {order.Status}." };
        if (!client.NotifyCustomerOnOrderStatus)
            return new OrderNoticeResult { Outcome = "off", Detail = "Customer messages are switched off for this business." };
        if (string.IsNullOrWhiteSpace(order.CustomerId))
            return await RecordAsync(order, false, "There is no customer id on this order.", cancellationToken);

        var english = EnglishText(order, client);
        var text = await InCustomerLanguageAsync(english, order, cancellationToken);

        var result = await _messaging.SendAsync(new OutboundMessage
        {
            ClientConfigId = clientConfigId,
            Channel = order.Channel,
            To = order.CustomerId,
            Text = text
        }, cancellationToken);

        if (!result.Success)
        {
            _logger.LogWarning("Could not tell customer {CustomerId} that order {Reference} is {Status}: {Error}",
                order.CustomerId, order.Reference, order.Status, result.Error);
            return await RecordAsync(order, false, $"Could not be sent ({result.Error})", cancellationToken);
        }

        // "mock" = the channel has no credentials: nothing really went out.
        return result.Mode == "mock"
            ? await RecordAsync(order, false, "Not sent - this channel has no access token (mock mode).", cancellationToken)
            : await RecordAsync(order, true, text, cancellationToken);
    }

    private async Task<OrderNoticeResult> RecordAsync(Order order, bool sent, string note, CancellationToken cancellationToken)
    {
        await _orders.SetCustomerNoticeAsync(order.ClientConfigId, order.Id, sent, note, CancellationToken.None);
        return new OrderNoticeResult { Outcome = sent ? "sent" : "failed", Detail = note };
    }

    /// <summary>The fixed English message. Built by our code, so the facts (reference, items, time, reason) are always right.</summary>
    public static string EnglishText(Order order, ClientConfig client)
    {
        var items = string.Join(", ", order.Items.Select(i => $"{i.Quantity} x {i.Name}"));

        if (order.Status == OrderStatus.Cancelled)
        {
            var reason = string.IsNullOrWhiteSpace(order.CancelReason) ? "" : $" Reason: {order.CancelReason.Trim().TrimEnd('.')}.";
            return $"Sorry, {client.ClientName} could not take your order {order.Reference} ({items}).{reason} Please message us if you would like to change it.";
        }

        var when = order.RequestedForUtc is { } forUtc
            ? " for " + LocalTime.ToLocal(forUtc, client.TimeZoneId).ToString("ddd d MMM, HH:mm", CultureInfo.InvariantCulture)
            : "";
        var total = order.Total is { } t ? $" Total: {Money.Format(t, order.Currency)}." : "";
        return $"Your order {order.Reference} ({items}){when} is confirmed by {client.ClientName}.{total} Thank you!";
    }

    private static bool IsEnglish(string? language) =>
        string.IsNullOrWhiteSpace(language) ||
        language.Trim().StartsWith("en", StringComparison.OrdinalIgnoreCase);

    private async Task<string> InCustomerLanguageAsync(string english, Order order, CancellationToken cancellationToken)
    {
        if (IsEnglish(order.Language)) return english;

        try
        {
            var system =
                "You translate one short chat message from a shop to its customer.\n" +
                $"Translate it into this language: {order.Language}. Keep it short, friendly and natural.\n" +
                $"Keep the order reference {order.Reference}, item names, numbers, prices and times EXACTLY as written. " +
                "Do not add or remove any information. Answer with the translated message only - no quotes, no notes.";
            string translated;
            using (AiUsageScope.Begin(order.ClientConfigId, "notice-translate"))
                translated = (await _claude.CompleteAsync(system, english, cancellationToken)).Trim();

            // Safety net: it must still carry the order reference and be a sensible length.
            if (translated.Length is > 0 and <= 900 && translated.Contains(order.Reference, StringComparison.OrdinalIgnoreCase))
                return translated;

            _logger.LogWarning("Translation of the order notice for {Reference} was not usable - sending English", order.Reference);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not translate the order notice for {Reference} - sending English", order.Reference);
        }

        return english;
    }
}
