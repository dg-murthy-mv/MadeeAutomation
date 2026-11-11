using System.ComponentModel.DataAnnotations;
using MadeeAutomation.Domain.Enums;

namespace MadeeAutomation.Admin.Web.Models;

public class WorkflowFormModel
{
    public Guid? Id { get; set; }
    public Guid ClientConfigId { get; set; }
    public string? ClientName { get; set; }

    [Required, StringLength(200)]
    [Display(Name = "Workflow name")]
    public string Name { get; set; } = string.Empty;

    [Required, Display(Name = "Handler")]
    public string HandlerKey { get; set; } = "customer-message";

    public VerticalPack Vertical { get; set; } = VerticalPack.FnBRetail;

    [Display(Name = "Trigger type")]
    public TriggerType TriggerType { get; set; } = TriggerType.MessageReceived;

    /// <summary>"" = any channel, otherwise line / whatsapp / messenger. Only used by message handlers.</summary>
    public string? Channel { get; set; } = "line";

    [Display(Name = "Trigger config (JSON)")]
    public string TriggerConfigJson { get; set; } = "{}";

    // ---- Scheduled workflows: when to run, in the client's time zone ----

    /// <summary>0 = Sunday ... 6 = Saturday. None ticked = every day.</summary>
    public List<int> ScheduleDays { get; set; } = new();

    [Display(Name = "Times")]
    public string? ScheduleTimes { get; set; } = "07:00";

    [Display(Name = "Advanced: cron (instead of days and times)")]
    public string? ScheduleCron { get; set; }

    /// <summary>For the page: the client's time zone and the next run, worked out from the saved schedule.</summary>
    public string? ClientTimeZoneId { get; set; }
    public string? NextRunText { get; set; }
    public string? LastRunText { get; set; }

    [Display(Name = "Enabled")]
    public bool IsEnabled { get; set; } = true;

    public bool IsEdit => Id.HasValue;

    /// <summary>Fills the schedule fields from a saved workflow (or the defaults for a new one).</summary>
    public WorkflowFormModel WithSchedule(MadeeAutomation.Domain.Entities.WorkflowDefinition? workflow, string? timeZoneId = null)
    {
        ClientTimeZoneId = workflow?.ClientConfig?.TimeZoneId ?? timeZoneId;
        if (workflow is null) return this;

        var schedule = MadeeAutomation.Domain.Regional.WorkflowSchedule.Parse(workflow.TriggerConfigJson, out _);
        if (schedule is { IsSimple: true })
        {
            ScheduleDays = schedule.Days.ToList();
            ScheduleTimes = string.Join(", ", schedule.Times.Select(t => t.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture)));
            ScheduleCron = null;
        }
        else if (schedule is not null)
        {
            ScheduleCron = schedule.Cron;
            ScheduleTimes = null;
        }

        NextRunText = ScheduleText.Next(workflow);
        LastRunText = ScheduleText.Last(workflow);
        return this;
    }
}

/// <summary>Plain-English schedule lines for the client page and the workflow form (client's own time zone).</summary>
public static class ScheduleText
{
    private static string Local(DateTime utc, string? timeZoneId) =>
        MadeeAutomation.Domain.Regional.LocalTime.ToLocal(utc, timeZoneId)
            .ToString("ddd d MMM yyyy, HH:mm", System.Globalization.CultureInfo.InvariantCulture);

    public static string Describe(MadeeAutomation.Domain.Entities.WorkflowDefinition w) =>
        MadeeAutomation.Domain.Regional.WorkflowSchedule.Parse(w.TriggerConfigJson, out var error)?.Describe() ?? (error ?? "No schedule set.");

    public static string Next(MadeeAutomation.Domain.Entities.WorkflowDefinition w)
    {
        if (!w.IsEnabled) return "Not running - the workflow is disabled.";
        if (w.NextRunAtUtc is null) return "Worked out by WebApi within a minute.";
        if (w.NextRunAtUtc.Value.Year >= 9999) return "Never - check the schedule.";
        return Local(w.NextRunAtUtc.Value, w.ClientConfig?.TimeZoneId) + (w.RetryAttempt > 0 ? $" (retry {w.RetryAttempt})" : "");
    }

    public static string? Last(MadeeAutomation.Domain.Entities.WorkflowDefinition w) =>
        w.LastRunStatus is null ? null
            : (w.LastRunAtUtc is { } at ? Local(at, w.ClientConfig?.TimeZoneId) + ": " : "") + w.LastRunStatus;
}
