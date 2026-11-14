namespace MadeeAutomation.Application.Common;

/// <summary>
/// Says which client (and what for) the AI calls made inside it belong to, so ClaudeService can
/// record the usage against the right client without every handler passing it along.
/// Flows with async/await (AsyncLocal), so parallel work for different clients never mixes.
///
///   using (AiUsageScope.Begin(client.Id, "customer-message")) { ... calls to Claude ... }
/// </summary>
public static class AiUsageScope
{
    public sealed record Context(Guid? ClientConfigId, string Purpose, bool IsTest);

    private static readonly AsyncLocal<Context?> _current = new();

    public static Context? Current => _current.Value;

    public static IDisposable Begin(Guid? clientConfigId, string purpose, bool isTest = false)
    {
        var previous = _current.Value;
        _current.Value = new Context(clientConfigId, purpose, isTest);
        return new Restore(previous);
    }

    private sealed class Restore : IDisposable
    {
        private readonly Context? _previous;
        private bool _done;
        public Restore(Context? previous) => _previous = previous;
        public void Dispose()
        {
            if (_done) return;
            _done = true;
            _current.Value = _previous;
        }
    }
}
