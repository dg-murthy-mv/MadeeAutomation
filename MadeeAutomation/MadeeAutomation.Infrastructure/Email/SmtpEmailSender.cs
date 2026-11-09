using System.Net;
using System.Net.Mail;
using MadeeAutomation.Application.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace MadeeAutomation.Infrastructure.Email;

/// <summary>
/// Sends plain-text email through SMTP (e.g. Zoho Mail: smtp.zoho.com, port 587, STARTTLS).
///
///   Email:Host, Email:Port (587), Email:UseSsl (true), Email:UserName, Email:Password,
///   Email:FromAddress, Email:FromName ("MadeeAutomation")
///   Email:PickupDirectory - instead of sending, write each email as an .eml file into this folder
///                           (used by the E2E tests; also handy on a dev PC)
/// </summary>
public class SmtpEmailSender : IEmailSender
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<SmtpEmailSender> _logger;

    public SmtpEmailSender(IConfiguration configuration, ILogger<SmtpEmailSender> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    private string? Get(string key) => _configuration[$"Email:{key}"] is { Length: > 0 } v ? v.Trim() : null;

    private string? PickupDirectory => Get("PickupDirectory");

    public bool IsConfigured => PickupDirectory is not null || (Get("Host") is not null && Get("FromAddress") is not null);

    public async Task<EmailSendResult> SendAsync(string to, string subject, string textBody, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            _logger.LogWarning("Email is not configured (Email:Host / Email:FromAddress). Would have sent to {To}: {Subject}", to, subject);
            return new EmailSendResult { Success = false, Mode = "not-configured", Error = "Email is not configured." };
        }

        var from = Get("FromAddress") ?? "noreply@madeeautomation.local";
        using var message = new MailMessage
        {
            From = new MailAddress(from, Get("FromName") ?? "MadeeAutomation"),
            Subject = subject,
            Body = textBody,
            IsBodyHtml = false,
            BodyEncoding = System.Text.Encoding.UTF8,
            SubjectEncoding = System.Text.Encoding.UTF8
        };
        message.To.Add(to);

        try
        {
            if (PickupDirectory is { } folder)
            {
                Directory.CreateDirectory(folder);
                using var pickup = new SmtpClient
                {
                    DeliveryMethod = SmtpDeliveryMethod.SpecifiedPickupDirectory,
                    PickupDirectoryLocation = Path.GetFullPath(folder)
                };
                await pickup.SendMailAsync(message, cancellationToken);
                _logger.LogInformation("Email to {To} written to pickup folder {Folder}: {Subject}", to, folder, subject);
                return new EmailSendResult { Success = true, Mode = "pickup" };
            }

            using var smtp = new SmtpClient(Get("Host"), int.TryParse(Get("Port"), out var port) ? port : 587)
            {
                EnableSsl = !string.Equals(Get("UseSsl"), "false", StringComparison.OrdinalIgnoreCase),
                DeliveryMethod = SmtpDeliveryMethod.Network
            };
            if (Get("UserName") is { } user)
                smtp.Credentials = new NetworkCredential(user, Get("Password"));

            await smtp.SendMailAsync(message, cancellationToken);
            _logger.LogInformation("Email sent to {To}: {Subject}", to, subject);
            return new EmailSendResult { Success = true, Mode = "smtp" };
        }
        catch (Exception ex) when (ex is SmtpException or InvalidOperationException or IOException or FormatException)
        {
            _logger.LogError(ex, "Email to {To} failed: {Subject}", to, subject);
            return new EmailSendResult { Success = false, Mode = "smtp", Error = ex.Message };
        }
    }
}
