namespace MadeeAutomation.Domain.Entities;

/// <summary>
/// One opening period on a weekday, in the client's own time zone, e.g. Monday 07:00-14:00.
/// A day can have two periods (lunch break). No period on a day = closed that day.
/// CloseTime earlier than OpenTime means it runs past midnight (Saturday 18:00-02:00).
/// </summary>
public class OpeningHours
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ClientConfigId { get; set; }
    public DayOfWeek DayOfWeek { get; set; }
    public TimeOnly OpenTime { get; set; }
    public TimeOnly CloseTime { get; set; }
}

/// <summary>A whole day the business is closed (holiday, Songkran, Diwali, Eid...).</summary>
public class ClosedDate
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ClientConfigId { get; set; }
    public DateOnly Date { get; set; }
    public string? Reason { get; set; }
}
