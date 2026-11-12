using System.Globalization;
using System.Text;
using MadeeAutomation.Application.Abstractions;
using MadeeAutomation.Domain.Entities;
using MadeeAutomation.Domain.Enums;
using MadeeAutomation.Domain.Regional;

namespace MadeeAutomation.Application.VerticalPacks.Messaging;

/// <summary>
/// Places an order the bot took:
///   1. sends it to the client's POS, when one is connected
///   2. ALWAYS saves it in our own Orders list (so a client with no POS still has every order,
///      and daily limits can be counted)
///   3. writes the "New order" message for the client's staff
/// </summary>
public class OrderDesk
{
    public const string TestReference = "TEST-ORDER";

    private readonly IPosFirstConnector _posFirst;
    private readonly IOrderRepository _orders;
    private readonly IMessagingConnector _messaging;

    public OrderDesk(IPosFirstConnector posFirst, IOrderRepository orders, IMessagingConnector messaging)
    {
        _posFirst = posFirst;
        _orders = orders;
        _messaging = messaging;
    }

    /// <summary>How many of each menu item are already ordered for that day.</summary>
    public Task<Dictionary<Guid, int>> OrderedOnAsync(Guid clientConfigId, DateOnly date, CancellationToken cancellationToken = default) =>
        _orders.GetOrderedQuantitiesAsync(clientConfigId, date, cancellationToken);

    /// <summary>
    /// Sends the order to the POS (if any) and saves it. In a test run nothing is sent or saved -
    /// the order just gets the reference "TEST-ORDER".
    ///
    /// If the POS can't be reached or refuses the order (after the quick retries), the order is NOT
    /// lost: it is saved here with our own reference (ORD-0001), the customer is still confirmed, and
    /// the staff message says to enter it in the POS by hand (or "Send to POS again" on the Orders page).
    /// </summary>
    public async Task PlaceAsync(Order order, ClientConfig? client, bool isTest, CancellationToken cancellationToken = default)
    {
        if (isTest)
        {
            order.Reference = TestReference;
            return;
        }

        if (await _posFirst.IsConfiguredAsync(order.ClientConfigId, cancellationToken))
        {
            var (posOrderId, error) = await SendToPosAsync(order, client, cancellationToken);
            if (posOrderId is not null)
            {
                order.PosOrderId = posOrderId;
                order.Reference = posOrderId;
            }
            order.PosError = error;
        }

        if (!string.IsNullOrWhiteSpace(order.CustomerId) && string.IsNullOrWhiteSpace(order.CustomerName))
            order.CustomerName = await _messaging.GetCustomerNameAsync(order.ClientConfigId, order.Channel, order.CustomerId, cancellationToken);

        await _orders.AddAsync(order, cancellationToken);   // no POS id -> gets our own "ORD-0001"
    }

    /// <summary>"Send to POS again" for a saved order whose first send failed. Returns the error, or null when it worked.</summary>
    public async Task<string?> ResendToPosAsync(Guid clientConfigId, Guid orderId, ClientConfig? client, CancellationToken cancellationToken = default)
    {
        var order = await _orders.GetAsync(clientConfigId, orderId, cancellationToken);
        if (order is null) return "Order not found.";
        if (order.PosOrderId is not null) return null;   // already there
        if (!await _posFirst.IsConfiguredAsync(clientConfigId, cancellationToken)) return "This client has no POS connected.";

        var (posOrderId, error) = await SendToPosAsync(order, client, cancellationToken);
        await _orders.SetPosResultAsync(clientConfigId, orderId, posOrderId, error, CancellationToken.None);
        return error;
    }

    private async Task<(string? PosOrderId, string? Error)> SendToPosAsync(Order order, ClientConfig? client, CancellationToken cancellationToken)
    {
        try
        {
            var posOrderId = await _posFirst.CreateOrderAsync(order.ClientConfigId, PosPayload(order, client), cancellationToken, order.Id.ToString("N"));
            return string.IsNullOrWhiteSpace(posOrderId)
                ? (null, "The POS accepted the order but sent back no order number.")
                : (posOrderId.Length > 100 ? posOrderId[..100] : posOrderId, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            var reason = ex is TaskCanceledException ? "the POS did not answer in time" : ex.Message;
            return (null, reason.Length > 300 ? reason[..300] : reason);
        }
    }

    /// <summary>What we send to the POS - built from the saved order, so a resend sends exactly the same.</summary>
    public static object PosPayload(Order order, ClientConfig? client)
    {
        var timeZoneId = client?.TimeZoneId;
        DateTimeOffset? deliveryLocal = order.RequestedForUtc is { } utc ? LocalTime.ToLocalOffset(utc, timeZoneId) : null;
        var tax = order.Money() is { HasTax: true } m ? m : null;
        return new
        {
            externalOrderId = order.Id.ToString("N"),   // our id: the same on every try
            source = order.Channel,
            customerId = order.CustomerId,
            rawMessage = order.CustomerMessage,
            currency = order.Currency,
            items = order.Items.Select(i => new { name = i.Name, quantity = i.Quantity, unitPrice = i.UnitPrice, lineTotal = i.LineTotal, onMenu = OnMenu(i) }).ToList(),
            orderTotal = order.Total,
            subtotal = tax?.Subtotal,
            taxName = tax?.TaxName,
            taxRatePercent = tax?.RatePercent,
            taxAmount = tax?.TaxAmount,
            pricesIncludeTax = tax?.PricesIncludeTax,
            requestedDeliveryLocal = deliveryLocal?.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture),
            requestedDeliveryUtc = order.RequestedForUtc?.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            requestedDeliveryText = order.RequestedForText,
            timeZone = timeZoneId,
            customerNote = order.CustomerNote
        };
    }

    /// <summary>
    /// The line matched the menu: either a Menu-page item (MenuItemId) or a priced line from the business
    /// profile's price list (no MenuItemId, but a price). Only lines nobody could price are "not on the menu".
    /// </summary>
    public static bool OnMenu(OrderItem item) => item.MenuItemId is not null || item.UnitPrice is not null;

    /// <summary>The message the client's staff get (LINE and/or email). English - our side is English only.</summary>
    public static string StaffMessage(Order order, ClientConfig? client)
    {
        var currency = order.Currency;
        var sb = new StringBuilder();
        sb.AppendLine($"New order {order.Reference} - {client?.ClientName ?? "client"}");
        foreach (var item in order.Items)
        {
            var price = item.LineTotal is { } t ? " - " + Money.Format(t, currency) : !OnMenu(item) ? " (not on the menu - check the price)" : "";
            sb.AppendLine($"{item.Quantity} x {item.Name}{price}");
        }
        if (order.Money() is { } money)
            foreach (var line in Tax.Lines(money, currency)) sb.AppendLine(line);
        else
            sb.AppendLine("Total: not calculated (an item has no price)");

        if (order.RequestedForUtc is { } forUtc)
        {
            var local = LocalTime.ToLocal(forUtc, client?.TimeZoneId);
            var said = string.IsNullOrWhiteSpace(order.RequestedForText) ? "" : $" (customer said \"{order.RequestedForText}\")";
            sb.AppendLine($"For: {local.ToString("ddd d MMM yyyy, HH:mm", CultureInfo.InvariantCulture)}{said}");
        }
        else
        {
            sb.AppendLine(string.IsNullOrWhiteSpace(order.RequestedForText) ? "For: no time given" : $"For: \"{order.RequestedForText}\"");
        }

        var who = string.IsNullOrWhiteSpace(order.CustomerName) ? order.CustomerId : $"{order.CustomerName}";
        sb.AppendLine($"Customer: {who} ({Channels.DisplayName(order.Channel)})");
        if (!string.IsNullOrWhiteSpace(order.CustomerNote)) sb.AppendLine($"Note: {order.CustomerNote}");
        if (!string.IsNullOrWhiteSpace(order.CustomerMessage)) sb.AppendLine($"Customer wrote: \"{Short(order.CustomerMessage, 300)}\"");

        sb.Append(order.PosOrderId is not null
            ? "It is also in your POS."
            : order.PosError is not null
                ? $"It could NOT be sent to your POS ({order.PosError}) - please enter it in the POS by hand, or use \"Send to POS again\" on the Orders page."
                : "No POS is connected - please prepare it from this message or the Orders page.");
        return sb.ToString();
    }

    private static string Short(string text, int max) => text.Length <= max ? text : text[..max] + "...";
}
