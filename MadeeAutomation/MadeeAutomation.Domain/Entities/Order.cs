namespace MadeeAutomation.Domain.Entities;

public enum OrderStatus
{
    New = 1,        // the bot took it - waiting for staff
    Accepted = 2,   // staff said yes
    Completed = 3,  // handed over / delivered
    Cancelled = 4   // not going ahead (does not count toward daily limits)
}

/// <summary>
/// An order the bot took from a customer message. Saved for EVERY client - with or without a POS -
/// so staff always have a list of what came in, and daily limits can be counted.
/// When the client has a POS connected, the same order is also sent there (PosOrderId).
/// </summary>
public class Order
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ClientConfigId { get; set; }

    /// <summary>1, 2, 3 ... per client. Set when the order is saved.</summary>
    public int OrderNumber { get; set; }

    /// <summary>What the customer was told: the POS order id, or our own "ORD-0001" when there is no POS.</summary>
    public string Reference { get; set; } = string.Empty;

    public string? PosOrderId { get; set; }

    /// <summary>
    /// The client has a POS but this order could not be sent to it (POS down, refused it). The order
    /// is still saved here and confirmed to the customer; staff are told to enter it by hand, or use
    /// "Send to POS again" on the Orders page. Null = sent fine, or there is no POS.
    /// </summary>
    public string? PosError { get; set; }

    public string Channel { get; set; } = string.Empty;
    public string? CustomerId { get; set; }
    public string? CustomerName { get; set; }          // e.g. the LINE display name, when we could read it
    public string? CustomerMessage { get; set; }       // the customer's own words

    public string? Currency { get; set; }
    public decimal? Total { get; set; }                // what the customer pays; null when an item had no price

    // Tax on this order, as it was when the order was taken (null / 0 = the client has no tax set).
    public decimal? Subtotal { get; set; }             // the total before tax
    public decimal? TaxAmount { get; set; }
    public string? TaxName { get; set; }               // "VAT", "GST"
    public decimal? TaxRatePercent { get; set; }
    public bool PricesIncludeTax { get; set; } = true;

    public DateTime? RequestedForUtc { get; set; }     // delivery / pick-up time, when the customer gave one
    public string? RequestedForText { get; set; }      // "tomorrow 9am"

    /// <summary>
    /// The local day (client's time zone) this order is FOR: the delivery date when given, otherwise the
    /// day it was placed. Daily limits are counted on this date.
    /// </summary>
    public DateOnly OrderDate { get; set; }

    public string? CustomerNote { get; set; }

    public OrderStatus Status { get; set; } = OrderStatus.New;
    public string? StatusChangedBy { get; set; }
    public DateTime? StatusChangedAtUtc { get; set; }

    /// <summary>The language the customer wrote in, as the AI named it ("en", "th", "Thai"...). Used to message them later.</summary>
    public string? Language { get; set; }

    /// <summary>Why staff cancelled it - included in the message to the customer.</summary>
    public string? CancelReason { get; set; }

    /// <summary>
    /// Whether the customer was told about the last Accept / Cancel: true = sent, false = could not be sent,
    /// null = not tried (the client has these messages switched off, or nothing has happened yet).
    /// </summary>
    public bool? CustomerNoticeSent { get; set; }

    /// <summary>What was sent, or why it could not be sent.</summary>
    public string? CustomerNoticeNote { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public List<OrderItem> Items { get; set; } = new();

    public static string OwnReference(int orderNumber) => $"ORD-{orderNumber:0000}";

    /// <summary>The order's money lines (subtotal, tax, total). Null when there is no total.</summary>
    public MadeeAutomation.Domain.Regional.TaxBreakdown? Money() => Total is not { } total ? null
        : TaxName is not null && TaxRatePercent is > 0 && TaxAmount is { } tax
            ? new MadeeAutomation.Domain.Regional.TaxBreakdown(Subtotal ?? total - tax, tax, total, TaxName, TaxRatePercent, PricesIncludeTax)
            : new MadeeAutomation.Domain.Regional.TaxBreakdown(total, 0m, total, null, null, PricesIncludeTax);
}

public class OrderItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrderId { get; set; }

    /// <summary>The menu item it matched (null when the item is not on the menu). Not a foreign key: deleting a menu item keeps past orders.</summary>
    public Guid? MenuItemId { get; set; }

    public string Name { get; set; } = string.Empty;
    public int Quantity { get; set; }
    public decimal? UnitPrice { get; set; }
    public decimal? LineTotal { get; set; }
}
