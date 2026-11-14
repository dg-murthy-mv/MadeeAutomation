using System.Globalization;
using MadeeAutomation.Application.Abstractions;
using MadeeAutomation.Domain.Entities;
using MadeeAutomation.Domain.Enums;
using MadeeAutomation.Domain.Regional;

namespace MadeeAutomation.Application.Reports;

/// <summary>One client's line on the MadeeVision report.</summary>
public sealed class AdminClientRow
{
    public required ClientConfig Client { get; init; }
    public required ClientActivity Activity { get; init; }
    public int MessagesBefore { get; init; }
    public string TypeText => AdminReportBuilder.TypeText(Client.Vertical);

    /// <summary>Days since the last customer message (null = never).</summary>
    public int? QuietDays { get; init; }

    /// <summary>A trial / active client nobody has written to for 7 days or more - worth a call.</summary>
    public bool NeedsAttention => Client.Status is ClientStatus.Active or ClientStatus.Trial && (QuietDays is null || QuietDays >= 7);

    public int? ChangePercent => MessagesBefore > 0 ? (int)Math.Round((Activity.Messages - MessagesBefore) * 100m / MessagesBefore) : null;
}

public sealed record GroupTotal(string Name, int Clients, int Messages, int Orders, int Bookings, decimal AiCostUsd);

/// <summary>The MadeeVision report: every client side by side, plus AI cost.</summary>
public sealed class AdminReport
{
    public required ReportPeriod Period { get; init; }
    public required ReportPeriod PreviousPeriod { get; init; }
    public List<AdminClientRow> Rows { get; } = new();
    public List<ReportTile> Tiles { get; } = new();
    public List<ReportPoint> MessagesPerDay { get; } = new();
    public List<ReportPoint> AiCostPerDay { get; } = new();
    public List<GroupTotal> ByCountry { get; } = new();
    public List<GroupTotal> ByType { get; } = new();
    public List<AiPurposeTotal> AiByPurpose { get; } = new();
    public decimal AiCostUsd => Rows.Sum(r => r.Activity.AiCostUsd);
    public decimal AiTestCostUsd => AiByPurpose.Where(a => a.IsTest).Sum(a => a.CostUsd);
}

public class AdminReportBuilder
{
    private readonly IReportRepository _reports;
    private readonly IClientRepository _clients;

    public AdminReportBuilder(IReportRepository reports, IClientRepository clients)
    {
        _reports = reports;
        _clients = clients;
    }

    public static string TypeText(VerticalPack v) => v switch
    {
        VerticalPack.FnBRetail => "Food & retail",
        VerticalPack.BeautyWellness => "Beauty & wellness",
        VerticalPack.CrmSales => "CRM & sales",
        VerticalPack.FinanceChit => "Finance",
        VerticalPack.CustomerSupport => "Customer support",
        _ => v.ToString()
    };

    /// <summary>What an AI call was for, in words (the purpose is the workflow's handler key).</summary>
    public static string PurposeText(string purpose) => purpose switch
    {
        "customer-message" => "Customer chat (orders)",
        "booking-message" => "Customer chat (bookings)",
        "order-parser" => "Orders only",
        "faq-responder" => "FAQ answers",
        "notice-translate" => "Translating order / booking notices",
        "lead-scoring" => "Lead scoring",
        "invoice-extraction" => "Invoice extraction",
        "low-stock-alert" => "Low-stock alert",
        "other" => "Other",
        _ => purpose
    };

    public static string CountryText(string? code) => (code ?? "").ToUpperInvariant() switch
    {
        "TH" => "Thailand",
        "IN" => "India",
        "PH" => "Philippines",
        "AE" => "UAE",
        "" => "(not set)",
        var other => other
    };

    /// <param name="utcNow">For "days since the last message".</param>
    public async Task<AdminReport> BuildAsync(ReportPeriod period, DateTime utcNow, CancellationToken cancellationToken = default)
    {
        var previous = period.Previous();
        var clients = (await _clients.GetPagedAsync(null, 1, 10_000, cancellationToken)).Items;
        var now = await _reports.GetClientActivityAsync(period.FromUtc, period.ToUtc, cancellationToken);
        var before = await _reports.GetClientActivityAsync(previous.FromUtc, previous.ToUtc, cancellationToken);
        var nowById = now.ToDictionary(a => a.ClientConfigId);
        var beforeById = before.ToDictionary(a => a.ClientConfigId);

        var report = new AdminReport { Period = period, PreviousPeriod = previous };
        foreach (var c in clients)
        {
            var activity = nowById.GetValueOrDefault(c.Id) ?? new ClientActivity { ClientConfigId = c.Id };
            report.Rows.Add(new AdminClientRow
            {
                Client = c,
                Activity = activity,
                MessagesBefore = beforeById.GetValueOrDefault(c.Id)?.Messages ?? 0,
                QuietDays = activity.LastMessageUtc is { } last ? Math.Max(0, (int)(utcNow - last).TotalDays) : null
            });
        }
        // Busiest first; clients that need a call float up among equals.
        report.Rows.Sort((a, b) =>
        {
            var byMessages = b.Activity.Messages.CompareTo(a.Activity.Messages);
            return byMessages != 0 ? byMessages : string.Compare(a.Client.ClientName, b.Client.ClientName, StringComparison.OrdinalIgnoreCase);
        });

        // Per day, in the admin's own time zone (fixed offset: none of our markets uses daylight saving).
        var offset = (int)LocalTime.Find(period.TimeZoneId).GetUtcOffset(utcNow).TotalMinutes;
        var perDay = await _reports.GetMessagesPerDayAsync(period.FromUtc, period.ToUtc, offset, cancellationToken);
        var costPerDay = await _reports.GetAiCostPerDayAsync(period.FromUtc, period.ToUtc, offset, cancellationToken);
        var labelFormat = period.Days > 7 ? "d MMM" : "ddd d";
        foreach (var d in period.Dates())
        {
            var label = d.ToString(labelFormat, CultureInfo.InvariantCulture);
            report.MessagesPerDay.Add(new ReportPoint(label, perDay.GetValueOrDefault(d)));
            var cost = costPerDay.GetValueOrDefault(d);
            report.AiCostPerDay.Add(new ReportPoint(label, cost, Usd(cost)));
        }

        report.ByCountry.AddRange(Group(report.Rows, r => CountryText(r.Client.Country)));
        report.ByType.AddRange(Group(report.Rows, r => r.TypeText));
        report.AiByPurpose.AddRange((await _reports.GetAiByPurposeAsync(null, period.FromUtc, period.ToUtc, cancellationToken))
            .OrderByDescending(a => a.CostUsd));

        static string N(int n) => n.ToString("N0", CultureInfo.InvariantCulture);
        var active = report.Rows.Count(r => r.Activity.Messages > 0);
        var live = report.Rows.Count(r => r.Client.Status is ClientStatus.Active or ClientStatus.Trial);
        var messages = report.Rows.Sum(r => r.Activity.Messages);
        var messagesBefore = report.Rows.Sum(r => r.MessagesBefore);
        report.Tiles.Add(new ReportTile("active", "Clients with messages", $"{N(active)} of {N(live)}",
            $"{N(report.Rows.Count(r => r.NeedsAttention))} quiet for 7+ days"));
        report.Tiles.Add(new ReportTile("messages", "Customer messages", N(messages),
            $"{N(report.Rows.Sum(r => r.Activity.Customers))} customers", messages, messagesBefore));
        report.Tiles.Add(new ReportTile("work", "Orders + bookings", N(report.Rows.Sum(r => r.Activity.Orders + r.Activity.Bookings)),
            $"{N(report.Rows.Sum(r => r.Activity.Orders))} orders · {N(report.Rows.Sum(r => r.Activity.Bookings))} bookings"));
        var calls = report.Rows.Sum(r => r.Activity.AiCalls);
        report.Tiles.Add(new ReportTile("ai", "AI cost", Usd(report.AiCostUsd),
            calls > 0 ? $"{N(calls)} calls · {Usd(report.AiCostUsd / calls)} each" + (report.AiTestCostUsd > 0 ? $" · tests {Usd(report.AiTestCostUsd)}" : "") : "No AI calls yet",
            report.AiCostUsd, before.Sum(a => a.AiCostUsd)));
        var runs = report.Rows.Sum(r => r.Activity.Runs);
        var failed = report.Rows.Sum(r => r.Activity.FailedRuns);
        report.Tiles.Add(new ReportTile("failed", "Failed runs", N(failed),
            runs > 0 ? $"{Math.Round(failed * 100m / runs, 1).ToString(CultureInfo.InvariantCulture)}% of {N(runs)} runs" : "No runs"));
        return report;
    }

    public static string Usd(decimal amount) =>
        "$" + (amount >= 1 || amount == 0 ? amount.ToString("N2", CultureInfo.InvariantCulture) : amount.ToString("0.0000", CultureInfo.InvariantCulture));

    private static IEnumerable<GroupTotal> Group(IEnumerable<AdminClientRow> rows, Func<AdminClientRow, string> key) =>
        rows.GroupBy(key)
            .Select(g => new GroupTotal(g.Key, g.Count(), g.Sum(r => r.Activity.Messages), g.Sum(r => r.Activity.Orders),
                g.Sum(r => r.Activity.Bookings), g.Sum(r => r.Activity.AiCostUsd)))
            .OrderByDescending(g => g.Messages).ThenBy(g => g.Name);
}
