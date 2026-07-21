using Microsoft.Extensions.Options;
using MimeKit;
using CircularoIntegration.Api.Config;

namespace CircularoIntegration.Api.Services;

public interface IEmailService
{
    Task SendEmailWithAttachmentAsync(
        string toEmail,
        string subject,
        string htmlBody,
        string attachmentPath,
        CancellationToken ct = default);
}