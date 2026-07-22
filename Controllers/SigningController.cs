using System.Text.Json;
using CircularoIntegration.Api.Data;
using CircularoIntegration.Api.Models;
using CircularoIntegration.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;


namespace CircularoIntegration.Api.Controllers;

[ApiController]
[Route("api/v1/signing")]
public class SigningController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly LeadSquaredService _lsq;
    private readonly CircularoService _circularo;
    private readonly IKfsNotificationService _kfs;
    private readonly ILogger<SigningController> _log;
    private readonly OfferLetterDocumentService _offerLetterDocs;

    public SigningController(AppDbContext db, LeadSquaredService lsq, CircularoService circularo,
        IKfsNotificationService kfs, OfferLetterDocumentService offerLetterDocs, ILogger<SigningController> log)
    {
        _db = db; _lsq = lsq;
         _circularo = circularo; 
         _kfs = kfs;
        _offerLetterDocs = offerLetterDocs; 
        _log = log;
    }

    // =====================================================================
    // API #1 — called by the LEADSQUARED webhook on lead stage change.
    // Sends the phase document to the customer for signature via Circularo.
    // =====================================================================
    [HttpPost("initiate")]
    public async Task<IActionResult> Initiate()          // ← no [FromBody] parameter
    {
        string raw;
        using (var reader = new StreamReader(Request.Body))
            raw = await reader.ReadToEndAsync();

        _log.LogInformation("INITIATE inbound payload: {raw}", raw);

        JsonElement payload;
        try
        {
            payload = JsonDocument.Parse(string.IsNullOrWhiteSpace(raw) ? "{}" : raw).RootElement.Clone();
            _log.LogInformation("Initiate() - Payload is  {payload}.", payload);
        }
        catch
        {
            _log.LogInformation("Initiate received empty/non-JSON body (validation ping) – acknowledging.");
            return Ok(new { status = "IGNORED", reason = "empty or invalid body" });
        }


        /////////////////////////////
        var leadId = FindFirstString(payload,
            "LeadId", "RelatedId", "ProspectID", "ProspectId", "Id", "leadId");
        // var afterStage = FindFirstString(payload,
        //     "After", "AfterStage", "CurrentStage", "ProspectStage", "NewValue", "Current");
        // LSQ payload has Before/After as OBJECTS – read the stage from inside After
        string? afterStage = null;
        if (payload.ValueKind == JsonValueKind.Object &&
            payload.TryGetProperty("After", out var afterObj) &&
            afterObj.ValueKind == JsonValueKind.Object)
        {
            afterStage = FindFirstString(afterObj, "ProspectStage", "Stage", "CurrentStage");
        }
        afterStage ??= FindFirstString(payload, "AfterStage", "NewValue", "ProspectStage");

        ////////////////

        if (string.IsNullOrWhiteSpace(leadId))
        {
            _log.LogInformation("Initiate called without lead id (likely webhook validation ping) – acknowledging.");
            return Ok(new { status = "IGNORED", reason = "no lead id in payload" });
        }
        // New: fire the KFS email in parallel with the signing flow. No-ops
        // immediately if afterStage isn't the KFS trigger stage, and is
        // idempotent (checked against KfsEmailLogs), so safe on every call.
        await _kfs.HandleStageChangeAsync(leadId, afterStage ?? string.Empty);
        // if (string.IsNullOrWhiteSpace(leadId))
        //     return BadRequest(new { message = "Could not find lead id in payload – check logs and adjust field mapping", status = 400 });

        // Resolve phase config from the stage. If the webhook only fires for one
        // stage, afterStage may be absent – fall back to the single active config.
        var config = afterStage is null
            ? await _db.PhaseDocumentConfigs.Where(c => c.IsActive).OrderBy(c => c.PhaseNo).FirstOrDefaultAsync()
            : await _db.PhaseDocumentConfigs.FirstOrDefaultAsync(c => c.IsActive && c.LeadStageTrigger == afterStage);

        if (config is null)
        {
            // Not a stage we manage – acknowledge so LSQ doesn't retry.
            _log.LogInformation("No phase mapping for stage '{Stage}' – ignoring.", afterStage);
            return Ok(new { status = "IGNORED", reason = $"no mapping for stage '{afterStage}'" });
        }

        // Idempotency – don't re-send if we already have an open transaction.
        var existing = await _db.SigningTransactions
            .FirstOrDefaultAsync(t => t.LeadId == leadId && t.PhaseNo == config.PhaseNo && t.Status != SigningStatus.Failed);
        if (existing is not null)
        {
            _log.LogInformation("Duplicate initiate for lead {LeadId} phase {Phase} – already {Status}.",
                leadId, config.PhaseNo, existing.Status);
            return Ok(new { status = "DUPLICATE", leadId, phase = config.PhaseNo, currentStatus = existing.Status.ToString() });
        }
        string documentId = string.Empty;
        try
        {
            var (email, firstName) = await _lsq.GetLeadEmailAsync(leadId);
            string recipientEmail = email ?? string.Empty;
            var docTitle = $"{config.DocumentName} - Lead {leadId}";

            if (config.RequiresFieldMerge)
            {
                var offerDetails = await _lsq.GetLeadOfferDetailsAsync(leadId);

                if (string.IsNullOrWhiteSpace(offerDetails.AuthorizedSignatoryEmail))
                    return UnprocessableEntity(new { message = $"Lead {leadId} has no authorized signatory email", status = 422 });

                if (!offerDetails.IsComplete)
                    _log.LogWarning("Lead {LeadId} offer letter fields incomplete — sending with blanks.", leadId);

                recipientEmail = offerDetails.AuthorizedSignatoryEmail!;
                firstName = offerDetails.CustomerName ?? firstName;

                var pdfBytes = await _offerLetterDocs.GenerateOfferLetterPdfAsync(offerDetails);
                documentId = await _circularo.CreateDocumentFromBytesAsync(pdfBytes, $"offer-letter-{leadId}.pdf", docTitle);
            }
            else
            {
                if (string.IsNullOrWhiteSpace(recipientEmail))
                    return UnprocessableEntity(new { message = $"Lead {leadId} has no email address", status = 422 });

                documentId = await _circularo.CreateDocumentFromLocalTemplateAsync(docTitle);
            }

            var shareResponse = await _circularo.SendForSignatureAsync(
                documentId, recipientEmail,
                $"Dear {firstName ?? "Customer"}, please sign: {config.DocumentName}");
           

            // 3. Record the transaction (correlation backbone for the callback)
            _db.SigningTransactions.Add(new SigningTransaction
            {
                LeadId = leadId,
                PhaseNo = config.PhaseNo,
                CircularoRef = documentId,
                CustomerEmail = email,
                Status = SigningStatus.Sent,
                InboundPayload = raw
            });
            await _db.SaveChangesAsync();

            _log.LogInformation("Phase {Phase} document sent for lead {LeadId} to {Email}", config.PhaseNo, leadId, email);
            return Ok(new { status = "SENT", leadId, phase = config.PhaseNo, email });
        }
        catch (DownstreamException dex)
        {
            _log.LogError("Initiate failed for lead {LeadId}: downstream {Status} {Body}", leadId, dex.StatusCode, dex.ResponseBody);
            _db.SigningTransactions.Add(new SigningTransaction
            {
                LeadId = leadId,
                PhaseNo = config.PhaseNo,
                CircularoRef = documentId,
                Status = SigningStatus.Failed,
                ErrorMessage = dex.ResponseBody,
                InboundPayload = raw
            });
            await _db.SaveChangesAsync();
            return new ContentResult
            {
                StatusCode = dex.StatusCode,
                Content = dex.ResponseBody,
                ContentType = "application/json"
            };
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Initiate failed for lead {LeadId}", leadId);
            return StatusCode(500, new { error = ex.Message });
        }
    }

    // =====================================================================
    // API #2 — called by the CIRCULARO webhook when the document is signed.
    // Attaches the signed PDF to the lead and moves it to the next stage.
    // =====================================================================
    [HttpPost("completed")]
    public async Task<IActionResult> Completed()
    {

        string raw;
        using (var reader = new StreamReader(Request.Body))
            raw = await reader.ReadToEndAsync();
        
       // _log.LogInformation("INITIATE inbound payload: {raw}", raw);

        JsonElement payload;
        try
        {
            payload = JsonDocument.Parse(string.IsNullOrWhiteSpace(raw) ? "{}" : raw).RootElement.Clone();
        }
        catch
        {
            _log.LogInformation("Initiate received empty/non-JSON body (validation ping) – acknowledging.");
            return Ok(new { status = "IGNORED", reason = "empty or invalid body" });
        }
        //////
        //var raw = payload.GetRawText();
        _log.LogInformation("COMPLETED inbound payload: {Payload}", raw);

        var documentId = FindFirstString(payload,
            "documentId", "docId", "id", "document", "objectId");
        var eventName = FindFirstString(payload,
            "event", "action", "status", "type");

        _log.LogInformation("Circularo callback parsed: documentId={DocId}, event={Event}", documentId, eventName);

        // Circularo fires webhooks for the whole document lifecycle. Confirmed
        // observed events: documentCreated, signRequest (share created — NOT a
        // signature!), documentSigned, documentCompleted. Only the last two mean
        // the customer actually signed. Exact match — 'signRequest' contains
        // "sign" but must NOT pass. eventName == null still proceeds so manual
        // Postman tests keep working.
        var completionEvents = new[] { "documentSigned", "documentCompleted" };
        if (eventName is not null &&
            !completionEvents.Contains(eventName, StringComparer.OrdinalIgnoreCase))
        {
            _log.LogInformation("Circularo event '{Event}' for doc {DocId} is not a signing completion – ignoring.",
                eventName, documentId);
            return Ok(new { status = "IGNORED", reason = $"event '{eventName}' is not a signing completion" });
        }



        // if (string.IsNullOrWhiteSpace(documentId))
        //     return BadRequest(new { message = "Could not find document id in payload – check logs", status = 400 });
        if (string.IsNullOrWhiteSpace(documentId))
        {
            _log.LogInformation("Initiate called without lead id (likely webhook validation ping) – acknowledging.");
            return Ok(new { status = "IGNORED", reason = "no lead id in payload" });
        }
        // Correlate back to the lead via our transaction record
        var txn = await _db.SigningTransactions
            .Where(t => t.CircularoRef == documentId && t.Status == SigningStatus.Sent)
            .OrderByDescending(t => t.SentAtUtc)
            .FirstOrDefaultAsync();

        if (txn is null)
        {
            _log.LogWarning("No open transaction for Circularo document {DocId} – acknowledging.", documentId);
            return Ok(new { status = "IGNORED", reason = "no matching open transaction" });
        }

        var config = await _db.PhaseDocumentConfigs.FirstAsync(c => c.PhaseNo == txn.PhaseNo);

        try
        {
            txn.Status = SigningStatus.Signed;
            txn.SignedAtUtc = DateTime.UtcNow;
            txn.CallbackPayload = raw;
            await _db.SaveChangesAsync();

            // 1. Try to download the signed PDF and attach it to the lead
            try
            {
                var detail = await _circularo.GetDocumentAsync(documentId);
                var hash = _circularo.TryExtractPdfHash(detail);
                if (hash is not null)
                {
                    var pdf = await _circularo.DownloadFileAsync(hash);
                    await _lsq.AttachDocumentAsync(txn.LeadId, $"{config.DocumentName}-signed.pdf", pdf);
                }
            }
            catch (Exception attachEx)
            {
                // Attach failure should not block the stage move for the demo –
                // it is logged and the txn stays at Signed for a repair run.
                _log.LogError(attachEx, "Attach step failed for lead {LeadId} – continuing to stage update.", txn.LeadId);
            }

            // 2. Move the lead to the next stage
            await _lsq.UpdateLeadStageAsync(txn.LeadId, config.NextLeadStage);

            txn.Status = SigningStatus.Attached;
            txn.AttachedAtUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync();

            _log.LogInformation("Lead {LeadId} completed phase {Phase}: signed doc processed, stage → '{Stage}'",
                txn.LeadId, txn.PhaseNo, config.NextLeadStage);

            return Ok(new { status = "COMPLETED", leadId = txn.LeadId, nextStage = config.NextLeadStage });
        }
        catch (DownstreamException dex)
        {
            _log.LogError("Completed failed for lead {LeadId}: downstream {Status} {Body}", txn.LeadId, dex.StatusCode, dex.ResponseBody);
            txn.ErrorMessage = dex.ResponseBody;
            txn.RetryCount++;
            await _db.SaveChangesAsync();
            return new ContentResult
            {
                StatusCode = dex.StatusCode,
                Content = dex.ResponseBody,
                ContentType = "application/json"
            };
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Completed handler failed for lead {LeadId}", txn.LeadId);
            txn.ErrorMessage = ex.Message;
            txn.RetryCount++;
            await _db.SaveChangesAsync();
            return StatusCode(500, new { error = ex.Message });
        }
    }

    [HttpGet("field-mapping")]
    public async Task<IActionResult> GetFieldMapping([FromServices] LeadSquaredService lsq)
    {
        var all = await lsq.GetAllFieldsMetadataAsync();

        var targets = new[]
        {
        "First Name", "Funded Facility Limit", "Finance Amount",
        "Purpose", "Tenor", "Profit Rate", "Finance Tenure", "Payment Terms"
    };

        var matches = all.Where(f =>
            targets.Any(t => f.DisplayName.Contains(t, StringComparison.OrdinalIgnoreCase)));

        return Ok(matches.Select(m => new { m.DisplayName, m.SchemaName }));
    }

    // ------------------------- helpers -------------------------

    /// <summary>
    /// Depth-first search for the first string value under any of the given
    /// property names (case-insensitive). Makes the webhook parsing tolerant
    /// to the exact payload shapes of LeadSquared and Circularo, which we
    /// lock down after capturing one real payload each.
    /// </summary>
    private static string? FindFirstString(JsonElement el, params string[] names)
    {
        if (el.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in el.EnumerateObject())
            {
                if (names.Any(n => string.Equals(n, prop.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    if (prop.Value.ValueKind == JsonValueKind.String) return prop.Value.GetString();
                    if (prop.Value.ValueKind is JsonValueKind.Number) return prop.Value.GetRawText();
                }
            }
            foreach (var prop in el.EnumerateObject())
            {
                var found = FindFirstString(prop.Value, names);
                if (found is not null) return found;
            }
        }
        else if (el.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in el.EnumerateArray())
            {
                var found = FindFirstString(item, names);
                if (found is not null) return found;
            }
        }
        return null;
    }

    // TEMP — local testing only. Bypasses DB/Circularo, returns the merged PDF
    // directly so you can eyeball the field substitution. Delete before demo.
    [HttpGet("debug/offer-letter/{leadId}")]
    public async Task<IActionResult> DebugGenerateOfferLetter(
        string leadId,
        [FromServices] OfferLetterDocumentService offerDocs)
    {
        var details = await _lsq.GetLeadOfferDetailsAsync(leadId);
        _log.LogInformation("DEBUG offer details for {LeadId}: {@Details}", leadId, details);

        var pdfBytes = await offerDocs.GenerateOfferLetterPdfAsync(details);
        return File(pdfBytes, "application/pdf", $"offer-letter-{leadId}-debug.pdf");
    }



    // LeadSquared/Circularo webhook URL validation pings (HEAD/GET) — answer 200
    [HttpHead("initiate"), HttpGet("initiate")]
    public IActionResult InitiateProbe() => Ok(new { status = "READY" });

    [HttpHead("completed"), HttpGet("completed")]
    public IActionResult CompletedProbe() => Ok(new { status = "READY" });



}