using CircularoIntegration.Api.Config;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;

namespace CircularoIntegration.Api.Services.Nuqi;

/// <summary>
/// Internal emails for the NUQI package (BRD §15 "Document exception → Business and Operations",
/// and Devinder's 28-Sep ask: someone at Mawarid must know when the customer has signed).
/// Recipients come from appsettings NuqiApproved:Notifications (CompletedEmails / ExceptionEmails).
/// Uses the same SmtpSettings (Office 365) as the KFS email, but sends without attachment,
/// so the existing EmailService/IEmailService are not modified.
/// Never throws – a notification failure must not block the package.
/// </summary>
public class NuqiNotificationService
{
    private readonly NuqiApprovedOptions _opt;
    private readonly SmtpSettings _smtp;
    private readonly ILogger<NuqiNotificationService> _log;

    public NuqiNotificationService(IOptions<NuqiApprovedOptions> opt, IOptions<SmtpSettings> smtp, ILogger<NuqiNotificationService> log)
    {
        _opt = opt.Value;
        _smtp = smtp.Value;
        _log = log;
    }

    public const string PackageCompleted = "PACKAGE_COMPLETED";
    public const string PackageException = "PACKAGE_EXCEPTION";

    public async Task NotifyAsync(string productCode, string eventCode, string subject, string htmlBody, CancellationToken ct = default)
    {
        try
        {
            var configured = eventCode == PackageCompleted ? _opt.Notifications.CompletedEmails : _opt.Notifications.ExceptionEmails;
            var recipients = configured
                .Where(e => NuqiText.LooksLikeEmail(e) && !e.Contains("CONFIRM", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (recipients.Count == 0)
            {
                _log.LogInformation("NUQI notification {Event}: no active recipients configured", eventCode);
                return;
            }

            await SendAsync(recipients, subject, htmlBody, ct);
            _log.LogInformation("NUQI notification {Event} sent to {Count} recipient(s)", eventCode, recipients.Count);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "NUQI notification {Event} failed – continuing", eventCode);
        }
    }

    /// <summary>
    /// Simplified flow step 2 – customer approval email:
    /// "Your NUQI Gold X Mawarid Card has been approved with a limit of AED XXXX. Please sign the
    ///  Murabaha documents sent to your registered email address."
    /// Returns (true, null) on success; never throws.
    /// </summary>
    public async Task<(bool Ok, string? Error)> SendCustomerApprovalEmailAsync(string toEmail, string subject, string htmlBody, CancellationToken ct = default)
    {
        try
        {
            await SendAsync(new List<string> { toEmail }, subject, htmlBody, ct);
            _log.LogInformation("NUQI approval email sent to {Email}", NuqiRedactor.MaskEmail(toEmail));
            return (true, null);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "NUQI approval email to {Email} failed", NuqiRedactor.MaskEmail(toEmail));
            return (false, ex.Message);
        }
    }

    private async Task SendAsync(List<string> recipients, string subject, string htmlBody, CancellationToken ct)
    {
        var msg = new MimeMessage();
        msg.From.Add(new MailboxAddress(_smtp.SenderName, _smtp.SenderEmail));
        foreach (var r in recipients) msg.To.Add(MailboxAddress.Parse(r));
        msg.Subject = subject;
        msg.Body = new BodyBuilder { HtmlBody = htmlBody }.ToMessageBody();

        using var client = new SmtpClient();
        await client.ConnectAsync(_smtp.Host, _smtp.Port, _smtp.UseStartTls ? SecureSocketOptions.StartTls : SecureSocketOptions.Auto, ct);
        if (!string.IsNullOrWhiteSpace(_smtp.Username) && client.Capabilities.HasFlag(SmtpCapabilities.Authentication))
            await client.AuthenticateAsync(_smtp.Username, _smtp.Password, ct);
        await client.SendAsync(msg, ct);
        await client.DisconnectAsync(true, ct);
    }
}
