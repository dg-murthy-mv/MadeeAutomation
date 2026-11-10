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

    public string Channel { get; set; } = string.Empty;
    public string? CustomerId { get; set; }
    public string? CustomerName { get; set; }          // e.g. the LINE display name, when we could read it
    public string? CustomerMessage { get; set; }       // the customer's own words

    public string? Currency { get; set; }
    public decimal? Total { get; set; }                // null when an item had no price

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

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public List<OrderItem> Items { get; set; } = new();

    public static string OwnReference(int orderNumber) => $"ORD-{orderNumber:0000}";
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
