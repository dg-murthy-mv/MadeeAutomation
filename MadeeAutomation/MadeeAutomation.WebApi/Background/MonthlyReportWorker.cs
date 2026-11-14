using MadeeAutomation.Application.Reports;

namespace MadeeAutomation.WebApi.Background;

/// <summary>
/// Emails each client last month's report on the 1st of the month, from 8 AM in the client's own time
/// zone (see MonthlyReportRunner). Checks every Reports:MonthlyPollMinutes (default 30). Uses the same
/// Scheduler:Enabled switch as the scheduler. Two WebApi copies are safe: each month is claimed first.
/// </summary>
public class MonthlyReportWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _clock;
    private readonly ILogger<MonthlyReportWorker> _logger;
    private readonly TimeSpan _pollInterval;
    private readonly bool _enabled;

    public MonthlyReportWorker(IServiceScopeFactory scopeFactory, TimeProvider clock, IConfiguration configuration, ILogger<MonthlyReportWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _clock = clock;
        _logger = logger;
        // Seconds can be set for tests (Reports:MonthlyPollSeconds); otherwise minutes.
        var seconds = configuration.GetValue<int?>("Reports:MonthlyPollSeconds");
        _pollInterval = seconds is > 0
            ? TimeSpan.FromSeconds(Math.Clamp(seconds.Value, 1, 3600))
            : TimeSpan.FromMinutes(Math.Clamp(configuration.GetValue("Reports:MonthlyPollMinutes", 30), 1, 360));
        _enabled = configuration.GetValue("Scheduler:Enabled", true) && configuration.GetValue("Reports:MonthlyEmailEnabled", true);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_enabled) return;
        await Task.Yield();

        using var timer = new PeriodicTimer(_pollInterval);
        do
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var sent = await scope.ServiceProvider.GetRequiredService<MonthlyReportRunner>()
                    .RunAsync(_clock.GetUtcNow().UtcDateTime, stoppingToken);
                if (sent > 0) _logger.LogInformation("Emailed {Count} monthly report(s)", sent);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogError(ex, "Monthly reports failed - trying again in {Poll}", _pollInterval);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }
}
