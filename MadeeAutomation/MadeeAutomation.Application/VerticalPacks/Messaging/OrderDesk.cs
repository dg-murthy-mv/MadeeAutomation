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
    /// </summary>
    public async Task PlaceAsync(Order order, object posPayload, bool isTest, CancellationToken cancellationToken = default)
    {
        if (isTest)
        {
            order.Reference = TestReference;
            return;
        }

        if (await _posFirst.IsConfiguredAsync(order.ClientConfigId, cancellationToken))
        {
            var posOrderId = await _posFirst.CreateOrderAsync(order.ClientConfigId, posPayload, cancellationToken);
            if (!string.IsNullOrWhiteSpace(posOrderId))
            {
                order.PosOrderId = posOrderId.Length > 100 ? posOrderId[..100] : posOrderId;
                order.Reference = order.PosOrderId;
            }
        }

        if (!string.IsNullOrWhiteSpace(order.CustomerId))
            order.CustomerName = await _messaging.GetCustomerNameAsync(order.ClientConfigId, order.Channel, order.CustomerId, cancellationToken);

        await _orders.AddAsync(order, cancellationToken);   // no POS id -> gets our own "ORD-0001"
    }

    /// <summary>The message the client's staff get (LINE and/or email). English - our side is English only.</summary>
    public static string StaffMessage(Order order, ClientConfig? client)
    {
        var currency = order.Currency;
        var sb = new StringBuilder();
        sb.AppendLine($"New order {order.Reference} - {client?.ClientName ?? "client"}");
        foreach (var item in order.Items)
        {
            var price = item.LineTotal is { } t ? " - " + Money.Format(t, currency) : item.MenuItemId is null ? " (not on the menu - check the price)" : "";
            sb.AppendLine($"{item.Quantity} x {item.Name}{price}");
        }
        sb.AppendLine(order.Total is { } total ? $"Total: {Money.Format(total, currency)}" : "Total: not calculated (an item has no price)");

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
            : "No POS is connected - please prepare it from this message or the Orders page.");
        return sb.ToString();
    }

    private static string Short(string text, int max) => text.Length <= max ? text : text[..max] + "...";
}
