using Microsoft.Extensions.Options;
using MimeKit;
using CircularoIntegration.Api.Config;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;

namespace CircularoIntegration.Api.Services;

public class EmailService : IEmailService
{
    private readonly SmtpSettings _smtp;
    private readonly ILogger<EmailService> _logger;

    public EmailService(IOptions<SmtpSettings> smtpOptions, ILogger<EmailService> logger)
    {
        _smtp = smtpOptions.Value;
        _logger = logger;
    }

    public async Task SendEmailWithAttachmentAsync(
        string toEmail,
        string subject,
        string htmlBody,
        string attachmentPath,
        CancellationToken ct = default)
    {
        _logger.LogInformation("KFS Email Process Started to {Email}", toEmail);

        if (!File.Exists(attachmentPath))
            throw new FileNotFoundException($"KFS attachment not found at '{attachmentPath}'.", attachmentPath);

        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(_smtp.SenderName, _smtp.SenderEmail));
        message.To.Add(MailboxAddress.Parse(toEmail));
        message.Subject = subject;

        var builder = new BodyBuilder { HtmlBody = htmlBody };
        await builder.Attachments.AddAsync(attachmentPath, ct);
        message.Body = builder.ToMessageBody();

        using var client = new SmtpClient();

        // Office 365 (smtp.office365.com:587) expects STARTTLS, not implicit SSL.
        var socketOptions = _smtp.UseStartTls
            ? SecureSocketOptions.StartTls
            : SecureSocketOptions.Auto;

        _logger.LogInformation("KFS SMTP connecting to {Host}:{Port} (UseStartTls={UseStartTls})",
            _smtp.Host, _smtp.Port, _smtp.UseStartTls);

        await client.ConnectAsync(_smtp.Host, _smtp.Port, socketOptions, ct);

        _logger.LogInformation("KFS SMTP connected. Server capabilities: {Capabilities}", client.Capabilities);

        // Office 365 requires authentication (unlike the internal relay this
        // used to point at). Still guarded by a capability check so this
        // EmailService keeps working unchanged if it's ever pointed back at
        // a relay that doesn't support/require AUTH.
        var serverSupportsAuth = client.Capabilities.HasFlag(SmtpCapabilities.Authentication);

        if (!string.IsNullOrWhiteSpace(_smtp.Username) && serverSupportsAuth)
        {
            _logger.LogInformation("KFS SMTP authenticating as {Username}", _smtp.Username);
            await client.AuthenticateAsync(_smtp.Username, _smtp.Password, ct);
            _logger.LogInformation("KFS SMTP authentication succeeded");
        }
        else if (!serverSupportsAuth)
        {
            _logger.LogWarning(
                "KFS SMTP server at {Host} did not advertise AUTH support — sending unauthenticated",
                _smtp.Host);
        }
        else
        {
            _logger.LogWarning("KFS SMTP no username configured — sending unauthenticated");
        }

        _logger.LogInformation("KFS SMTP sending message to {Email}", toEmail);
        await client.SendAsync(message, ct);

        await client.DisconnectAsync(true, ct);

        _logger.LogInformation("KFS email sent to {Email}", toEmail);
    }
}
