using MadeeAutomation.Application.Abstractions;
using MadeeAutomation.Application.Scheduling;

namespace MadeeAutomation.WebApi.Background;

/// <summary>
/// Runs scheduled workflows at their times, in each client's own time zone (see ScheduleRunner).
/// Checks every Scheduler:PollSeconds (default 30). Set Scheduler:Enabled = false to switch it off
/// (e.g. on a second copy of WebApi - though two copies are safe: each run is claimed in the database).
/// Up to Scheduler:MaxParallel (default 4) workflows run at once, each in its own DI scope.
/// </summary>
public class SchedulerWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _clock;
    private readonly ILogger<SchedulerWorker> _logger;
    private readonly TimeSpan _pollInterval;
    private readonly int _maxParallel;
    private readonly bool _enabled;

    public SchedulerWorker(IServiceScopeFactory scopeFactory, TimeProvider clock, IConfiguration configuration, ILogger<SchedulerWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _clock = clock;
        _logger = logger;
        _pollInterval = TimeSpan.FromSeconds(Math.Clamp(configuration.GetValue("Scheduler:PollSeconds", 30), 1, 300));
        _maxParallel = Math.Clamp(configuration.GetValue("Scheduler:MaxParallel", 4), 1, 16);
        _enabled = configuration.GetValue("Scheduler:Enabled", true);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_enabled)
        {
            _logger.LogInformation("Scheduler is switched off (Scheduler:Enabled = false)");
            return;
        }

        await Task.Yield(); // let the app finish starting
        _logger.LogInformation("Scheduler started (checks every {Poll}s)", _pollInterval.TotalSeconds);

        using var timer = new PeriodicTimer(_pollInterval);
        do
        {
            try
            {
                await RunDueAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                // A database blip must not stop the scheduler: try again next tick.
                _logger.LogError(ex, "Scheduler loop failed - trying again in {Poll}s", _pollInterval.TotalSeconds);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    private async Task RunDueAsync(CancellationToken stoppingToken)
    {
        var utcNow = _clock.GetUtcNow().UtcDateTime;

        List<Guid> due;
        using (var scope = _scopeFactory.CreateScope())
        {
            due = await scope.ServiceProvider.GetRequiredService<IWorkflowRepository>().GetDueScheduledIdsAsync(utcNow, 100, stoppingToken);
        }
        if (due.Count == 0) return;

        await Parallel.ForEachAsync(due, new ParallelOptions { MaxDegreeOfParallelism = _maxParallel, CancellationToken = stoppingToken },
            async (id, ct) =>
            {
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    var result = await scope.ServiceProvider.GetRequiredService<ScheduleRunner>().RunOneAsync(id, utcNow, ct);
                    _logger.LogInformation("Scheduled workflow {WorkflowId}: {Result}", id, result);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    _logger.LogError(ex, "Scheduled workflow {WorkflowId} could not be run", id);
                }
            });
    }
}
