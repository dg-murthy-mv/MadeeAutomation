using System.Globalization;
using System.Text.Json;
using MadeeAutomation.Application.Abstractions;
using MadeeAutomation.Application.Common;
using MadeeAutomation.Domain.Entities;
using MadeeAutomation.Domain.Enums;
using MadeeAutomation.Domain.Regional;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace MadeeAutomation.Application.Scheduling;

/// <summary>
/// Runs ONE scheduled workflow when it is due, in the client's own time zone, and works out the
/// next run. Called by the SchedulerWorker in WebApi (one call per due workflow, each in its own scope).
///
///   - A workflow with no next run yet gets one worked out (it does not run straight away).
///   - A run is "claimed" in the database first, so two copies of WebApi can never both run it.
///   - WebApi was off when it was due: it runs late if it is less than Scheduler:MissedRunGraceMinutes
///     (default 60) late; otherwise it is skipped and recorded as "Missed", so a 7am report does not
///     arrive at 3pm.
///   - A failed run is retried after Scheduler:RetryDelaysMinutes (default 2, 10, 30). After the last
///     retry the client's staff are told, and it waits for its next normal time.
///   - The client is Paused / Cancelled: nothing runs, the next time is still worked out.
/// </summary>
public class ScheduleRunner
{
    private readonly IWorkflowRepository _workflows;
    private readonly WorkflowEngine _engine;
    private readonly IStaffAlertService _alerts;
    private readonly ILogger<ScheduleRunner> _logger;
    private readonly TimeSpan _grace;
    private readonly TimeSpan[] _retryDelays;

    public ScheduleRunner(IWorkflowRepository workflows, WorkflowEngine engine, IStaffAlertService alerts,
        IConfiguration configuration, ILogger<ScheduleRunner> logger)
    {
        _workflows = workflows;
        _engine = engine;
        _alerts = alerts;
        _logger = logger;
        _grace = TimeSpan.FromMinutes(Math.Clamp(configuration.GetValue("Scheduler:MissedRunGraceMinutes", 60), 1, 24 * 60));
        _retryDelays = ParseDelays(configuration["Scheduler:RetryDelaysMinutes"]);
    }

    public IReadOnlyList<TimeSpan> RetryDelays => _retryDelays;

    private static TimeSpan[] ParseDelays(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return new[] { TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(30) };
        return value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(v => double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var m) && m > 0 ? TimeSpan.FromMinutes(m) : (TimeSpan?)null)
            .Where(t => t is not null).Select(t => t!.Value).Take(10).ToArray();
    }

    private static string Local(DateTime utc, string? timeZoneId) =>
        LocalTime.ToLocal(utc, timeZoneId).ToString("ddd d MMM HH:mm", CultureInfo.InvariantCulture);

    /// <summary>Returns what happened, in plain English (also written to the log).</summary>
    public async Task<string> RunOneAsync(Guid workflowId, DateTime utcNow, CancellationToken cancellationToken = default)
    {
        var workflow = await _workflows.GetByIdAsync(workflowId, cancellationToken);
        if (workflow is null || !workflow.IsEnabled || workflow.TriggerType != TriggerType.Scheduled)
            return "Not a due scheduled workflow.";

        var client = workflow.ClientConfig;
        var timeZoneId = client?.TimeZoneId;
        var schedule = WorkflowSchedule.Parse(workflow.TriggerConfigJson, out var scheduleError);

        // ---- No valid schedule: nothing to run. Recorded once, then left alone (the due query stops finding it). ----
        if (schedule is null)
        {
            var status = $"Not scheduled - {scheduleError} Set the schedule on the workflow.";
            if (workflow.LastRunStatus != status)
                await _workflows.RecordRunAsync(workflow.Id, null, status, DateTime.MaxValue, 0, cancellationToken);
            return status;
        }

        var due = workflow.NextRunAtUtc;
        var regularNext = schedule.NextAfter(utcNow, timeZoneId);

        // ---- First time (new workflow, or saved before this scheduler existed): just work out the next run. ----
        if (due is null)
        {
            await _workflows.TryClaimRunAsync(workflow.Id, null, regularNext ?? DateTime.MaxValue, cancellationToken);
            return regularNext is { } n ? $"Next run worked out: {Local(n, timeZoneId)}." : "This schedule never runs.";
        }

        if (due > utcNow) return "Not due yet.";

        // ---- Take this run. If another copy of WebApi got there first, stop. ----
        if (!await _workflows.TryClaimRunAsync(workflow.Id, due, regularNext ?? DateTime.MaxValue, cancellationToken))
            return "Already taken by another scheduler.";

        var nextText = regularNext is { } next ? $" Next: {Local(next, timeZoneId)}." : "";

        if (client is { CanRunWorkflows: false })
        {
            var status = $"Skipped - the client is {client.Status}.{nextText}";
            await _workflows.RecordRunAsync(workflow.Id, null, status, regularNext ?? DateTime.MaxValue, 0, cancellationToken);
            return status;
        }

        // ---- WebApi was not running when it was due: run late, or skip if far too late. ----
        var late = utcNow - due.Value;
        if (late > _grace)
        {
            var status = $"Missed {Local(due.Value, timeZoneId)} - WebApi was not running.{nextText}";
            _logger.LogWarning("Scheduled workflow {WorkflowId} ({Name}) missed its run at {Due:u} by {Late} - skipped", workflow.Id, workflow.Name, due, late);
            await _workflows.RecordRunAsync(workflow.Id, null, status, regularNext ?? DateTime.MaxValue, 0, cancellationToken);
            return status;
        }

        // ---- Run it ----
        var attempt = workflow.RetryAttempt;
        var context = new TriggerContext
        {
            ClientConfigId = workflow.ClientConfigId,
            WorkflowDefinitionId = workflow.Id,
            TriggerType = TriggerType.Scheduled,
            PayloadJson = JsonSerializer.Serialize(new
            {
                scheduledForUtc = due.Value.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
                scheduledForLocal = LocalTime.ToLocal(due.Value, timeZoneId).ToString("yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture),
                timeZone = timeZoneId,
                retry = attempt
            })
        };

        WorkflowRunOutcome outcome;
        try
        {
            outcome = await _engine.RunAsync(context, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Scheduled workflow {WorkflowId} threw", workflow.Id);
            outcome = new WorkflowRunOutcome { Status = ExecutionStatus.Failed, Error = ex.Message };
        }

        var retryText = attempt > 0 ? $" (retry {attempt} of {_retryDelays.Length})" : "";

        if (outcome.Status != ExecutionStatus.Failed)
        {
            var status = $"{outcome.Status}{retryText}.{nextText}";
            await _workflows.RecordRunAsync(workflow.Id, utcNow, status, regularNext ?? DateTime.MaxValue, 0, cancellationToken);
            return status;
        }

        // ---- Failed: try again soon, unless the next normal run comes first ----
        var nextAttempt = attempt + 1;
        if (nextAttempt <= _retryDelays.Length)
        {
            var retryAt = utcNow + _retryDelays[nextAttempt - 1];
            if (regularNext is null || retryAt < regularNext)
            {
                var status = $"Failed{retryText} - retry {nextAttempt} of {_retryDelays.Length} at {Local(retryAt, timeZoneId)}. {Short(outcome.Error)}";
                await _workflows.RecordRunAsync(workflow.Id, utcNow, status, retryAt, nextAttempt, cancellationToken);
                return status;
            }

            var noRetry = $"Failed{retryText} - not retried because the next normal run comes first.{nextText} {Short(outcome.Error)}";
            await _workflows.RecordRunAsync(workflow.Id, utcNow, noRetry, regularNext ?? DateTime.MaxValue, 0, cancellationToken);
            return noRetry;
        }

        // ---- Out of retries: tell a person ----
        var gaveUp = $"Failed after {_retryDelays.Length} retries - staff were told.{nextText} {Short(outcome.Error)}";
        await _workflows.RecordRunAsync(workflow.Id, utcNow, gaveUp, regularNext ?? DateTime.MaxValue, 0, cancellationToken);
        try
        {
            await _alerts.SendAsync(workflow.ClientConfigId,
                $"[{client?.ClientName ?? "client"}] The scheduled job \"{workflow.Name}\" failed {_retryDelays.Length + 1} times " +
                $"(due {Local(due.Value, timeZoneId)}): {Short(outcome.Error)} It will try again at its next normal time.", cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Could not alert staff about scheduled workflow {WorkflowId}", workflow.Id);
        }
        return gaveUp;
    }

    private static string Short(string? error) =>
        string.IsNullOrWhiteSpace(error) ? "" : (error.Length > 150 ? error[..150] + "..." : error).Trim();
}
