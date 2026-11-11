using System.Net;
using Microsoft.Extensions.Logging;

namespace MadeeAutomation.Connectors;

/// <summary>
/// Retries an outgoing call that failed for a TEMPORARY reason. Waits 1s then 3s (or what the other
/// side asks for in Retry-After, up to 10s) - short, because a customer may be waiting. A real error
/// (wrong token, bad message) is never retried.
///
/// safeToRepeat = true - the other side drops duplicates, so ANY temporary failure is retried
///   (connection lost, 429, 500, 502, 503, 504):
///     LINE push (same X-Line-Retry-Key every try), Resend email (same Idempotency-Key every try)
/// safeToRepeat = false - a repeat could do it twice (create a second order, send a second message),
///   so only failures where the call certainly did NOT arrive are retried:
///     could not connect at all, 429 "too many requests", 503 "unavailable"
///   (POSFirst orders, WhatsApp / Messenger messages)
/// </summary>
public static class HttpRetry
{
    public static readonly TimeSpan[] DefaultDelays = { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3) };

    /// <summary>For tests: no waiting.</summary>
    public static bool NoWait { get; set; }

    /// <param name="send">Builds and sends a NEW request each time (a request can only be sent once).</param>
    public static async Task<HttpResponseMessage> SendAsync(Func<CancellationToken, Task<HttpResponseMessage>> send, string what,
        ILogger logger, CancellationToken cancellationToken, bool safeToRepeat, TimeSpan[]? delays = null)
    {
        delays ??= DefaultDelays;
        for (var attempt = 0; ; attempt++)
        {
            HttpResponseMessage? response = null;
            HttpRequestException? error = null;
            try
            {
                response = await send(cancellationToken);
            }
            catch (HttpRequestException ex)
            {
                error = ex;
            }

            var temporary = error is not null
                ? safeToRepeat || NeverArrived(error)
                : IsTemporary(response!.StatusCode, safeToRepeat);
            if (!temporary || attempt >= delays.Length)
            {
                if (error is not null) throw error;
                return response!;
            }

            var wait = RetryAfter(response) ?? delays[attempt];
            logger.LogWarning("{What} failed ({Reason}) - try {Next} of {Max} in {Seconds}s",
                what, error?.Message ?? $"HTTP {(int)response!.StatusCode}", attempt + 2, delays.Length + 1, wait.TotalSeconds);
            response?.Dispose();
            if (!NoWait) await Task.Delay(wait, cancellationToken);
        }
    }

    /// <summary>The connection was never made, so the other side cannot have received anything.</summary>
    private static bool NeverArrived(HttpRequestException ex) =>
        ex.HttpRequestError is HttpRequestError.ConnectionError or HttpRequestError.NameResolutionError or HttpRequestError.SecureConnectionError;

    public static bool IsTemporary(HttpStatusCode status, bool safeToRepeat) => (int)status switch
    {
        429 or 503 => true,
        500 or 502 or 504 => safeToRepeat,
        _ => false
    };

    private static TimeSpan? RetryAfter(HttpResponseMessage? response)
    {
        var header = response?.Headers.RetryAfter;
        var wait = header?.Delta ?? (header?.Date is { } date ? date - DateTimeOffset.UtcNow : null);
        return wait is { } w && w > TimeSpan.Zero ? (w > TimeSpan.FromSeconds(10) ? TimeSpan.FromSeconds(10) : w) : null;
    }
}
