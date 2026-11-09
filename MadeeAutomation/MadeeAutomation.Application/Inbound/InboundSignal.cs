namespace MadeeAutomation.Application.Inbound;

/// <summary>
/// Wakes the background worker as soon as a webhook stores a new event, so customers get
/// their reply in about a second instead of waiting for the next poll. Registered as a singleton.
/// (The worker also polls the database every few seconds, so nothing is lost if a signal is missed.)
/// </summary>
public class InboundSignal
{
    private readonly SemaphoreSlim _signal = new(0, int.MaxValue);

    public void Notify() => _signal.Release();

    /// <summary>Waits until notified or the timeout passes. Returns true if notified.</summary>
    public async Task<bool> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var notified = await _signal.WaitAsync(timeout, cancellationToken);
        // Drain extra signals - one wake-up processes everything that is pending.
        while (_signal.CurrentCount > 0 && _signal.Wait(0)) { }
        return notified;
    }
}
