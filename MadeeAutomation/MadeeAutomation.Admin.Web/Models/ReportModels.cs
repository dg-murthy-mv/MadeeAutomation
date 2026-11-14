using MadeeAutomation.Application.Abstractions;
using MadeeAutomation.Application.Reports;
using MadeeAutomation.Domain.Regional;

namespace MadeeAutomation.Admin.Web.Models;

/// <summary>A column chart (one bar per day / hour). Peak is the tallest bar's value.</summary>
public class ChartModel
{
    public string TestId { get; set; } = "chart";
    public string Title { get; set; } = string.Empty;
    public List<ReportPoint> Points { get; set; } = new();
    /// <summary>"messages", "orders"... used in the hover text.</summary>
    public string Unit { get; set; } = string.Empty;
    public string? Footnote { get; set; }
    public decimal Peak => Points.Count == 0 ? 0 : Points.Max(p => p.Value);
}

/// <summary>A ranked list with a bar per line (top items, services, staff, channels).</summary>
public class RankModel
{
    public string TestId { get; set; } = "rank";
    public string Title { get; set; } = string.Empty;
    public List<ReportPoint> Points { get; set; } = new();
    public string Empty { get; set; } = "Nothing yet in this period.";
    public string? Footnote { get; set; }
}

/// <summary>The period buttons and custom dates above a report.</summary>
public class PeriodPickerModel
{
    public required ReportPeriod Period { get; init; }
    public required string Controller { get; init; }
    public Guid? ClientConfigId { get; init; }
    public string? Error { get; init; }
    public string? CsvUrl { get; init; }
    public string CsvLabel { get; init; } = "Download CSV";
    public DateOnly Today { get; init; }
}

public class ClientReportPageModel
{
    public required ClientReport Report { get; init; }
    public required PeriodPickerModel Picker { get; init; }

    /// <summary>MadeeVision staff looking at a client's report: they also see the AI cost.</summary>
    public bool ShowAiCost { get; init; }
    public List<AiPurposeTotal> AiByPurpose { get; init; } = new();

    /// <summary>Where "Email this report" sends it (the client's staff alert email), or null.</summary>
    public string? EmailTo { get; init; }
}

public class AdminReportPageModel
{
    public required AdminReport Report { get; init; }
    public required PeriodPickerModel Picker { get; init; }
    public required string TimeZoneId { get; init; }
}
