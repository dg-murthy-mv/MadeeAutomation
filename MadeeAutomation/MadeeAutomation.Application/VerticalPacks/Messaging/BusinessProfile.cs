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
}

/// <summary>Loads a client's menu (structured, or old free text), opening hours and open/closed status.</summary>
public class BusinessProfileLoader
{
    private readonly IBusinessProfileRepository _repository;
    private readonly TimeProvider _clock;

    public BusinessProfileLoader(IBusinessProfileRepository repository, TimeProvider clock)
    {
        _repository = repository;
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

        return new BusinessProfile
        {
            Menu = MenuCatalog.Build(items, client.MenuText),
            MenuIsStructured = items.Count > 0,
            Hours = BusinessClock.GetStatus(hours, closed, client.TimeZoneId, utcNow)
        };
    }
}
