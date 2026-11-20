using MadeeAutomation.Application.Billing;

namespace MadeeAutomation.WebApi.Background;

/// <summary>
/// Sends plan emails (trial / paid period ending, ended, paused, chat usage) - see SubscriptionRunner.
/// Checks every Billing:PollMinutes (default 60). Uses the Scheduler:Enabled switch like the other jobs.
/// Two WebApi copies are safe: every notice is claimed before it is sent.
/// </summary>
public class SubscriptionWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SubscriptionWorker> _logger;
    private readonly TimeSpan _pollInterval;
    private readonly bool _enabled;

    public SubscriptionWorker(IServiceScopeFactory scopeFactory, IConfiguration configuration, ILogger<SubscriptionWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        // Seconds can be set for tests (Billing:PollSeconds); otherwise minutes.
        var seconds = configuration.GetValue<int?>("Billing:PollSeconds");
        _pollInterval = seconds is > 0
            ? TimeSpan.FromSeconds(Math.Clamp(seconds.Value, 1, 3600))
            : TimeSpan.FromMinutes(Math.Clamp(configuration.GetValue("Billing:PollMinutes", 60), 1, 720));
        _enabled = configuration.GetValue("Scheduler:Enabled", true) && configuration.GetValue("Billing:NoticesEnabled", true);
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
                var sent = await scope.ServiceProvider.GetRequiredService<SubscriptionRunner>().RunAsync(stoppingToken);
                if (sent > 0) _logger.LogInformation("Emailed {Count} plan notice(s)", sent);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogError(ex, "Plan notices failed - trying again in {Poll}", _pollInterval);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }
}
