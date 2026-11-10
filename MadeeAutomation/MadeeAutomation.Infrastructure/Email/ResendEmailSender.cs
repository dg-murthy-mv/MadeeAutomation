using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MadeeAutomation.Application.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace MadeeAutomation.Infrastructure.Email;

/// <summary>
/// Sends plain-text email through Resend (https://resend.com) - the same service Merkai CRM and
/// the MadeeVision website use. One HTTPS call, no SMTP sign-in.
///
///   Resend:ApiKey        - the Resend API key ("re_..."). Keep it in user-secrets / App Service settings.
///   Email:FromAddress    - must be on a domain verified in Resend (e.g. no-reply@madeevision.com)
///   Email:FromName       - shown as the sender ("MadeeAutomation")
///
/// What is used, in this order:
///   1. Email:PickupDirectory set -> each email is written as an .eml file (E2E tests, dev PC)
///   2. Resend:ApiKey set         -> Resend
///   3. Email:Host set            -> SMTP (the old way, kept as a fallback)
/// </summary>
public sealed class ResendEmailSender : IEmailSender, IDisposable
{
    private readonly IConfiguration _configuration;
    private readonly SmtpEmailSender _smtp;
    private readonly ILogger<ResendEmailSender> _logger;

    // One HttpClient for the life of the app; connections are refreshed so DNS changes are picked up.
    private readonly HttpClient _http = new(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(10) })
    {
        Timeout = TimeSpan.FromSeconds(20)
    };

    public ResendEmailSender(IConfiguration configuration, SmtpEmailSender smtp, ILogger<ResendEmailSender> logger)
    {
        _configuration = configuration;
        _smtp = smtp;
        _logger = logger;
    }

    private string? Get(string key) => _configuration[key] is { Length: > 0 } v ? v.Trim() : null;

    private string? ApiKey => Get("Resend:ApiKey");
    private bool UsePickup => Get("Email:PickupDirectory") is not null;
    private bool UseResend => !UsePickup && ApiKey is not null;

    public bool IsConfigured => UseResend ? Get("Email:FromAddress") is not null : _smtp.IsConfigured;

    public async Task<EmailSendResult> SendAsync(string to, string subject, string textBody, CancellationToken cancellationToken = default)
    {
        if (!UseResend) return await _smtp.SendAsync(to, subject, textBody, cancellationToken);   // pickup folder or SMTP

        var fromAddress = Get("Email:FromAddress");
        if (fromAddress is null)
        {
            _logger.LogWarning("Email:FromAddress is not set. Would have sent to {To}: {Subject}", to, subject);
            return new EmailSendResult { Success = false, Mode = "not-configured", Error = "Email:FromAddress is not set." };
        }

        var fromName = (Get("Email:FromName") ?? "MadeeAutomation").Replace("\"", "").Replace("<", "").Replace(">", "");
        var baseUrl = (Get("Resend:ApiBaseUrl") ?? "https://api.resend.com").TrimEnd('/');

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/emails")
            {
                Content = JsonContent.Create(new
                {
                    from = $"{fromName} <{fromAddress}>",
                    to = new[] { to.Trim() },
                    subject,
                    text = textBody
                })
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ApiKey);

            using var response = await _http.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("Email sent to {To} through Resend: {Subject}", to, subject);
                return new EmailSendResult { Success = true, Mode = "resend" };
            }

            var reason = ResendMessage(body) ?? $"HTTP {(int)response.StatusCode}";
            _logger.LogError("Resend refused the email to {To} ({Status}): {Reason} - {Subject}", to, (int)response.StatusCode, reason, subject);
            return new EmailSendResult { Success = false, Mode = "resend", Error = $"Resend: {reason}" };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            _logger.LogError(ex, "Email to {To} through Resend failed: {Subject}", to, subject);
            return new EmailSendResult { Success = false, Mode = "resend", Error = $"Resend could not be reached ({ex.Message})" };
        }
    }

    /// <summary>Resend answers errors as { "statusCode": 403, "name": "...", "message": "..." }.</summary>
    private static string? ResendMessage(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("message", out var m)
                ? m.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public void Dispose() => _http.Dispose();
}
