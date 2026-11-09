using MadeeAutomation.Application.Abstractions;
using MadeeAutomation.Application.Inbound;

namespace MadeeAutomation.WebApi.Background;

/// <summary>
/// Runs stored InboundEvents. Wakes immediately when a webhook stores something (InboundSignal)
/// and also polls the database every Worker:PollSeconds as a safety net.
/// Up to Worker:MaxParallel events run at once, each in its own DI scope (own DbContext).
/// </summary>
public class InboundEventWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly InboundSignal _signal;
    private readonly ILogger<InboundEventWorker> _logger;
    private readonly TimeSpan _pollInterval;
    private readonly int _maxParallel;

    public InboundEventWorker(IServiceScopeFactory scopeFactory, InboundSignal signal, IConfiguration configuration,
        ILogger<InboundEventWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _signal = signal;
        _logger = logger;
        _pollInterval = TimeSpan.FromSeconds(Math.Max(1, configuration.GetValue("Worker:PollSeconds", 5)));
        _maxParallel = Math.Clamp(configuration.GetValue("Worker:MaxParallel", 4), 1, 32);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield(); // let the app finish starting

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IInboundEventRepository>();
            var stuck = await repo.FailStuckAsync(TimeSpan.FromMinutes(5), stoppingToken);
            if (stuck > 0) _logger.LogWarning("{Count} inbound event(s) were left running by the last shutdown - marked Failed", stuck);
        }
        catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
        {
            _logger.LogError(ex, "Could not check for interrupted inbound events (is the database up and script 001 applied?)");
        }

        _logger.LogInformation("Inbound event worker started (poll {Poll}s, {Parallel} at a time)", _pollInterval.TotalSeconds, _maxParallel);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                List<Guid> pending;
                using (var scope = _scopeFactory.CreateScope())
                {
                    var repo = scope.ServiceProvider.GetRequiredService<IInboundEventRepository>();
                    pending = await repo.GetPendingIdsAsync(50, stoppingToken);
                }

                if (pending.Count == 0)
                {
                    await _signal.WaitAsync(_pollInterval, stoppingToken);
                    continue;
                }

                await Parallel.ForEachAsync(pending,
                    new ParallelOptions { MaxDegreeOfParallelism = _maxParallel, CancellationToken = stoppingToken },
                    async (id, ct) =>
                    {
                        using var scope = _scopeFactory.CreateScope();
                        var processor = scope.ServiceProvider.GetRequiredService<InboundEventProcessor>();
                        await processor.ProcessAsync(id, ct);
                    });
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Inbound event worker loop error - retrying shortly");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ContinueWith(_ => { });
            }
        }
    }
}
