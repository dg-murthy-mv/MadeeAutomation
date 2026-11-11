using System.Globalization;
using System.Text.Json;

namespace MadeeAutomation.Domain.Regional;

/// <summary>
/// When a scheduled workflow runs, always in the CLIENT's own time zone (7am Bangkok and 7am Dubai
/// are different moments). Stored in WorkflowDefinition.TriggerConfigJson in one of two shapes:
///
///   Simple (what the Admin form writes):  { "days": [1,2,3,4,5], "times": ["07:00", "15:30"] }
///       days: 0 = Sunday ... 6 = Saturday; empty or missing = every day.
///   Advanced (5-field cron):              { "cron": "0 7 * * 1-5" }
///       minute hour day-of-month month day-of-week, with * , - and / (e.g. "*/15 8-18 * * *").
///
/// Nothing here depends on the database or the clock: the scheduler passes "now" in, so the rules
/// can be checked exactly.
/// </summary>
public sealed class WorkflowSchedule
{
    private static readonly string[] DayNames = { "Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat" };

    private readonly bool[] _minutes = new bool[60];
    private readonly bool[] _hours = new bool[24];
    private readonly bool[] _daysOfMonth = new bool[32];   // 1..31
    private readonly bool[] _months = new bool[13];        // 1..12
    private readonly bool[] _daysOfWeek = new bool[7];     // 0 = Sunday
    private readonly bool _anyDayOfMonth;
    private readonly bool _anyDayOfWeek;

    /// <summary>Simple form only: the exact times (each one is its own hour + minute).</summary>
    private readonly List<TimeOnly>? _times;

    public string? Cron { get; }
    public IReadOnlyList<int> Days { get; } = Array.Empty<int>();
    public IReadOnlyList<TimeOnly> Times => _times ?? (IReadOnlyList<TimeOnly>)Array.Empty<TimeOnly>();
    public bool IsSimple => _times is not null;

    private WorkflowSchedule(List<int> days, List<TimeOnly> times)
    {
        _times = times.Distinct().OrderBy(t => t).ToList();
        Days = days.Distinct().OrderBy(d => d).ToList();
        foreach (var d in Days.Count == 0 ? Enumerable.Range(0, 7) : Days) _daysOfWeek[d] = true;
        _anyDayOfMonth = true;
        _anyDayOfWeek = Days.Count == 0;
        for (var m = 1; m <= 12; m++) _months[m] = true;
        for (var d = 1; d <= 31; d++) _daysOfMonth[d] = true;
    }

    private WorkflowSchedule(string cron)
    {
        Cron = cron;
        var fields = cron.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length != 5)
            throw new FormatException("A cron needs 5 parts: minute hour day-of-month month day-of-week (e.g. \"0 7 * * *\").");

        Fill(_minutes, fields[0], 0, 59, "minute");
        Fill(_hours, fields[1], 0, 23, "hour");
        Fill(_daysOfMonth, fields[2], 1, 31, "day of month");
        Fill(_months, fields[3], 1, 12, "month");
        var dow = new bool[8];
        Fill(dow, fields[4], 0, 7, "day of week");
        for (var d = 0; d < 7; d++) _daysOfWeek[d] = dow[d];
        if (dow[7]) _daysOfWeek[0] = true;   // 7 = Sunday too
        _anyDayOfMonth = fields[2] == "*";
        _anyDayOfWeek = fields[4] == "*";
    }

    // ------------------------------------------------------------------ reading / writing

    /// <summary>Reads TriggerConfigJson. Null (with a plain-English reason) when there is no valid schedule.</summary>
    public static WorkflowSchedule? Parse(string? triggerConfigJson, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(triggerConfigJson)) { error = "No schedule set."; return null; }

        try
        {
            using var doc = JsonDocument.Parse(triggerConfigJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) { error = "The schedule must be a JSON object."; return null; }

            if (root.TryGetProperty("cron", out var cronEl) && cronEl.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(cronEl.GetString()))
                return FromCron(cronEl.GetString()!, out error);

            if (!root.TryGetProperty("times", out var timesEl) || timesEl.ValueKind != JsonValueKind.Array)
            { error = "No schedule set."; return null; }

            var times = new List<TimeOnly>();
            foreach (var t in timesEl.EnumerateArray())
            {
                if (t.ValueKind != JsonValueKind.String || !TryTime(t.GetString(), out var time))
                { error = $"\"{t}\" is not a time - use HH:mm, e.g. 07:00."; return null; }
                times.Add(time);
            }
            if (times.Count == 0) { error = "Add at least one time."; return null; }

            var days = new List<int>();
            if (root.TryGetProperty("days", out var daysEl) && daysEl.ValueKind == JsonValueKind.Array)
                foreach (var d in daysEl.EnumerateArray())
                {
                    if (!d.TryGetInt32(out var day) || day is < 0 or > 6) { error = "Days must be 0 (Sunday) to 6 (Saturday)."; return null; }
                    days.Add(day);
                }

            return new WorkflowSchedule(days, times);
        }
        catch (JsonException)
        {
            error = "The schedule is not valid JSON.";
            return null;
        }
    }

    public static WorkflowSchedule? FromCron(string cron, out string? error)
    {
        error = null;
        try
        {
            return new WorkflowSchedule(cron.Trim());
        }
        catch (FormatException ex)
        {
            error = ex.Message;
            return null;
        }
    }

    /// <summary>The simple form: days (0 = Sunday; empty = every day) and "HH:mm" times. Null + error when not valid.</summary>
    public static WorkflowSchedule? FromSimple(IEnumerable<int>? days, string? times, out string? error)
    {
        error = null;
        var list = new List<TimeOnly>();
        foreach (var part in (times ?? "").Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (!TryTime(part, out var t)) { error = $"\"{part}\" is not a time - use HH:mm, e.g. 07:00 or 07:00, 15:30."; return null; }
            list.Add(t);
        }
        if (list.Count == 0) { error = "Enter at least one time, e.g. 07:00."; return null; }
        if (list.Distinct().Count() > 24) { error = "At most 24 times a day."; return null; }
        return new WorkflowSchedule((days ?? Array.Empty<int>()).Where(d => d is >= 0 and <= 6).ToList(), list);
    }

    public string ToJson() => IsSimple
        ? JsonSerializer.Serialize(new { days = Days, times = Times.Select(t => t.ToString("HH:mm", CultureInfo.InvariantCulture)) })
        : JsonSerializer.Serialize(new { cron = Cron });

    private static bool TryTime(string? value, out TimeOnly time) =>
        TimeOnly.TryParseExact((value ?? "").Trim(), new[] { "HH:mm", "H:mm" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out time);

    // ------------------------------------------------------------------ when does it run

    /// <summary>
    /// The first run strictly after afterUtc, as a UTC time, worked out on the client's local clock.
    /// Null when the schedule can never run (e.g. 31 February).
    /// </summary>
    public DateTime? NextAfter(DateTime afterUtc, string? timeZoneId)
    {
        var local = LocalTime.ToLocal(DateTime.SpecifyKind(afterUtc, DateTimeKind.Utc), timeZoneId);
        // Start from the next whole minute (local).
        var candidate = new DateTime(local.Year, local.Month, local.Day, local.Hour, local.Minute, 0, DateTimeKind.Unspecified).AddMinutes(1);

        // Day by day (at most ~5 years), then the earliest matching time that day.
        for (var dayOffset = 0; dayOffset < 366 * 5; dayOffset++)
        {
            var day = candidate.Date.AddDays(dayOffset);
            if (!DayMatches(day)) continue;

            var fromMinute = dayOffset == 0 ? candidate.Hour * 60 + candidate.Minute : 0;
            var minuteOfDay = FirstMinuteOn(fromMinute);
            if (minuteOfDay is null) continue;

            var runLocal = day.AddMinutes(minuteOfDay.Value);
            var runUtc = LocalTime.FromLocal(runLocal, timeZoneId).UtcDateTime;
            if (runUtc > afterUtc) return DateTime.SpecifyKind(runUtc, DateTimeKind.Utc);
        }
        return null;
    }

    private bool DayMatches(DateTime day)
    {
        if (!_months[day.Month]) return false;
        var dom = _daysOfMonth[day.Day];
        var dow = _daysOfWeek[(int)day.DayOfWeek];
        // Standard cron rule: when both day fields are restricted, either one may match.
        if (!_anyDayOfMonth && !_anyDayOfWeek) return dom || dow;
        return dom && dow;
    }

    private int? FirstMinuteOn(int fromMinuteOfDay)
    {
        if (_times is not null)
        {
            foreach (var t in _times)
            {
                var m = t.Hour * 60 + t.Minute;
                if (m >= fromMinuteOfDay) return m;
            }
            return null;
        }

        for (var m = fromMinuteOfDay; m < 24 * 60; m++)
            if (_hours[m / 60] && _minutes[m % 60]) return m;
        return null;
    }

    // ------------------------------------------------------------------ plain English

    /// <summary>"Every day at 07:00", "Mon-Fri at 07:00 and 15:30", "Sat, Sun at 09:00", or "Cron: 0 7 * * *".</summary>
    public string Describe()
    {
        if (!IsSimple) return $"Cron: {Cron}";

        var times = Times.Select(t => t.ToString("HH:mm", CultureInfo.InvariantCulture)).ToList();
        var at = times.Count == 1 ? times[0] : string.Join(", ", times.Take(times.Count - 1)) + " and " + times[^1];

        string days;
        if (Days.Count is 0 or 7) days = "Every day";
        else if (Days.SequenceEqual(new[] { 1, 2, 3, 4, 5 })) days = "Mon-Fri";
        else if (Days.SequenceEqual(new[] { 0, 6 })) days = "Sat, Sun";
        else days = string.Join(", ", Days.OrderBy(d => d == 0 ? 7 : d).Select(d => DayNames[d]));

        return $"{days} at {at}";
    }

    // ------------------------------------------------------------------ cron field parsing

    private static void Fill(bool[] target, string field, int min, int max, string name)
    {
        foreach (var part in field.Split(','))
        {
            var step = 1;
            var range = part;
            var slash = part.IndexOf('/');
            if (slash >= 0)
            {
                if (!int.TryParse(part[(slash + 1)..], out step) || step < 1) throw Bad(name, field);
                range = part[..slash];
            }

            int from, to;
            if (range == "*") { from = min; to = max; }
            else if (range.Contains('-'))
            {
                var bits = range.Split('-');
                if (bits.Length != 2 || !int.TryParse(bits[0], out from) || !int.TryParse(bits[1], out to)) throw Bad(name, field);
            }
            else
            {
                if (!int.TryParse(range, out from)) throw Bad(name, field);
                to = slash >= 0 ? max : from;
            }

            if (from < min || to > max || from > to) throw Bad(name, field);
            for (var v = from; v <= to; v += step) target[v] = true;
        }
    }

    private static FormatException Bad(string name, string field) =>
        new($"The {name} part \"{field}\" is not valid.");
}
