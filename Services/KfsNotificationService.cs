using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using CircularoIntegration.Api.Config;
using CircularoIntegration.Api.Data;

namespace CircularoIntegration.Api.Services;

public class KfsNotificationService : IKfsNotificationService
{
    private readonly KfsEmailOptions _options;
    private readonly IEmailService _emailService;
    private readonly LeadSquaredService _leadSquaredService;
    private readonly AppDbContext _dbContext;
    private readonly ILogger<KfsNotificationService> _logger;

    public KfsNotificationService(
        IOptions<KfsEmailOptions> options,
        IEmailService emailService,
        LeadSquaredService leadSquaredService,
        AppDbContext dbContext,
        ILogger<KfsNotificationService> logger)
    {
        _options = options.Value;
        _emailService = emailService;
        _leadSquaredService = leadSquaredService;
        _dbContext = dbContext;
        _logger = logger;
    }

    public async Task HandleStageChangeAsync(string leadId, string newStageName, CancellationToken ct = default)
    {
        if (!string.Equals(newStageName, _options.TriggerStageName, StringComparison.OrdinalIgnoreCase))
        {
            return; // not the KFS trigger stage — ignore
        }

        var alreadySent = await _dbContext.KfsEmailLogs
            .AnyAsync(x => x.LeadId == leadId && x.Success, ct);

        if (alreadySent)
        {
            _logger.LogInformation("KFS already sent for lead {LeadId}, skipping duplicate trigger", leadId);
            return;
        }

        var log = new KfsEmailLog { LeadId = leadId, SentAtUtc = DateTime.UtcNow };

        try
        {
            var (email, _) = await _leadSquaredService.GetLeadEmailAsync(leadId);

            if (string.IsNullOrWhiteSpace(email))
                throw new InvalidOperationException($"No email found for lead {leadId}.");

            log.RecipientEmail = email;

            await _emailService.SendEmailWithAttachmentAsync(
                toEmail: email,
                subject: "Key Fact Statement - Mawarid Finance",
                htmlBody: "Dear Customer,<br/><br/>" +
                          "Please find attached your Key Fact Statement (KFS) for your reference." +
                          "<br/><br/>Regards,<br/>Mawarid Finance",
                attachmentPath: _options.KfsFilePath,
                ct: ct);

            log.Success = true;
            try
            {
                var kfsBytes = await File.ReadAllBytesAsync(_options.KfsFilePath, ct);
                var kfsFileName = $"Key Fact Statement-{leadId}.pdf";
                await _leadSquaredService.AttachDocumentAsync(leadId, kfsFileName, kfsBytes);
            }
            catch (Exception attachEx)
            {
                _logger.LogWarning(attachEx, "KFS email sent for lead {LeadId} but attaching to LSQ failed — continuing.", leadId);
            }
        }
        catch (Exception ex)
        {
            log.Success = false;
            log.ErrorMessage = ex.Message;
            _logger.LogError(ex, "Failed to send KFS email for lead {LeadId}", leadId);
        }
        finally
        {
            _dbContext.KfsEmailLogs.Add(log);
            await _dbContext.SaveChangesAsync(ct);
        }
    }
}