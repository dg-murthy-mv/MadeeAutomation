using MadeeAutomation.Application.VerticalPacks.Beauty;

namespace MadeeAutomation.WebApi.Background;

/// <summary>
/// Sends appointment reminders when they are due (a day before, or 2 hours before for late bookings).
/// Checks every Bookings:ReminderPollSeconds (default 60). Uses the same Scheduler:Enabled switch as
/// the scheduler. Two WebApi copies are safe: each reminder is claimed in the database first.
/// </summary>
public class BookingReminderWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<BookingReminderWorker> _logger;
    private readonly TimeSpan _pollInterval;
    private readonly bool _enabled;

    public BookingReminderWorker(IServiceScopeFactory scopeFactory, IConfiguration configuration, ILogger<BookingReminderWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _pollInterval = TimeSpan.FromSeconds(Math.Clamp(configuration.GetValue("Bookings:ReminderPollSeconds", 60), 1, 600));
        _enabled = configuration.GetValue("Scheduler:Enabled", true);
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
                var sent = await scope.ServiceProvider.GetRequiredService<BookingReminderRunner>().RunDueAsync(20, stoppingToken);
                if (sent > 0) _logger.LogInformation("Sent {Count} booking reminder(s)", sent);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogError(ex, "Booking reminders failed - trying again in {Poll}s", _pollInterval.TotalSeconds);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }
}
