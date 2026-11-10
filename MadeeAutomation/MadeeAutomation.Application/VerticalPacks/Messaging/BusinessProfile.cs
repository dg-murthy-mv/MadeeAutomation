using MadeeAutomation.Application.Abstractions;
using MadeeAutomation.Domain.Entities;
using MadeeAutomation.Domain.Regional;

namespace MadeeAutomation.Application.VerticalPacks.Messaging;

/// <summary>Everything about the business the AI and the order logic need for one message.</summary>
public class BusinessProfile
{
    public List<MenuLine> Menu { get; init; } = new();
    public bool MenuIsStructured { get; init; }
    public BusinessStatus Hours { get; init; } = new(false, false, "", "", null);

    /// <summary>Today's date in the client's time zone.</summary>
    public DateOnly LocalToday { get; init; }

    /// <summary>How many of each menu item are already ordered for today (key = menu item id).</summary>
    public IReadOnlyDictionary<Guid, int> OrderedToday { get; init; } = new Dictionary<Guid, int>();

    /// <summary>How many more of this item can be ordered for today. Null = no daily limit.</summary>
    public int? LeftToday(MenuLine item) => Left(item, OrderedToday);

    public static int? Left(MenuLine item, IReadOnlyDictionary<Guid, int> ordered) =>
        item is { DailyLimit: { } limit, Id: { } id }
            ? Math.Max(0, limit - ordered.GetValueOrDefault(id))
            : null;
}

/// <summary>Loads a client's menu (structured, or old free text), opening hours, open/closed status and today's order counts.</summary>
public class BusinessProfileLoader
{
    private readonly IBusinessProfileRepository _repository;
    private readonly IOrderRepository _orders;
    private readonly TimeProvider _clock;

    public BusinessProfileLoader(IBusinessProfileRepository repository, IOrderRepository orders, TimeProvider clock)
    {
        _repository = repository;
        _orders = orders;
        _clock = clock;
    }

    public async Task<BusinessProfile> LoadAsync(ClientConfig? client, CancellationToken cancellationToken = default)
    {
        if (client is null) return new BusinessProfile();

        var utcNow = _clock.GetUtcNow().UtcDateTime;
        var localToday = DateOnly.FromDateTime(LocalTime.ToLocal(utcNow, client.TimeZoneId));

        var items = await _repository.GetMenuAsync(client.Id, cancellationToken);
        var hours = await _repository.GetHoursAsync(client.Id, cancellationToken);
        // From yesterday: an overnight period that started yesterday may still be running.
        var closed = await _repository.GetClosedDatesAsync(client.Id, localToday.AddDays(-1), cancellationToken);

        // Only needed when at least one item has a daily limit.
        IReadOnlyDictionary<Guid, int> orderedToday = items.Any(i => i.DailyLimit.HasValue)
            ? await _orders.GetOrderedQuantitiesAsync(client.Id, localToday, cancellationToken)
            : new Dictionary<Guid, int>();

        return new BusinessProfile
        {
            Menu = MenuCatalog.Build(items, client.MenuText),
            MenuIsStructured = items.Count > 0,
            Hours = BusinessClock.GetStatus(hours, closed, client.TimeZoneId, utcNow),
            LocalToday = localToday,
            OrderedToday = orderedToday
        };
    }
}
