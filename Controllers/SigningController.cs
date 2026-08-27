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
    private readonly OfferLetterDocumentService _offerLetterDocs;
    private readonly PdfOverlayService _overlay;          // BUG 2/4 FIX: injected
    private readonly AcroFormFillService _acroForm;        // Stage 9: real AcroForm field fill
    private readonly IConfiguration _cfg;                 // BUG 2 FIX: for MurabahaStage1Fields
    private readonly ILogger<SigningController> _log;

    public SigningController(
        AppDbContext db,
        LeadSquaredService lsq,
        CircularoService circularo,
        IKfsNotificationService kfs,
        OfferLetterDocumentService offerLetterDocs,
        PdfOverlayService overlay,
        AcroFormFillService acroForm,
        IConfiguration cfg,
        ILogger<SigningController> log)
    {
        _db = db;
        _lsq = lsq;
        _circularo = circularo;
        _kfs = kfs;
        _offerLetterDocs = offerLetterDocs;
        _overlay = overlay;
        _acroForm = acroForm;
        _cfg = cfg;
        _log = log;
    }

    // =====================================================================
    // API #1 — called by the LEADSQUARED webhook on lead stage change.
    // Sends every active document in the matched phase set via Circularo.
    // =====================================================================
    [HttpPost("initiate")]
    public async Task<IActionResult> Initiate()
    {
        string raw;
        using (var reader = new StreamReader(Request.Body))
            raw = await reader.ReadToEndAsync();

        _log.LogInformation("INITIATE inbound payload: {raw}", raw);

        JsonElement payload;
        try { payload = JsonDocument.Parse(string.IsNullOrWhiteSpace(raw) ? "{}" : raw).RootElement.Clone(); }
        catch { return Ok(new { status = "IGNORED", reason = "empty or invalid body" }); }

        var leadId = FindFirstString(payload, "LeadId", "RelatedId", "ProspectID", "ProspectId", "Id", "leadId");

        string? afterStage = null;
        if (payload.ValueKind == JsonValueKind.Object &&
            payload.TryGetProperty("After", out var afterObj) &&
            afterObj.ValueKind == JsonValueKind.Object)
        {
            afterStage = FindFirstString(afterObj, "ProspectStage", "Stage", "CurrentStage");
        }
        afterStage ??= FindFirstString(payload, "AfterStage", "NewValue", "ProspectStage");

        if (string.IsNullOrWhiteSpace(leadId))
            return Ok(new { status = "IGNORED", reason = "no lead id in payload" });

        await _kfs.HandleStageChangeAsync(leadId, afterStage ?? string.Empty);

        var config = afterStage is null
            ? await _db.PhaseDocumentConfigs.Where(c => c.IsActive).OrderBy(c => c.PhaseNo).FirstOrDefaultAsync()
            : await _db.PhaseDocumentConfigs.FirstOrDefaultAsync(c => c.IsActive && c.LeadStageTrigger == afterStage);

        if (config is null)
            return Ok(new { status = "IGNORED", reason = $"no mapping for stage '{afterStage}'" });
        return await SendPhaseDocumentSetAsync(leadId, config, raw);
        //to send  offer letter 
        // // if (config.RequiresFieldMerge)
        // {
        //     var existingOfferLetter = await _db.SigningTransactions.FirstOrDefaultAsync(
        //         t => t.LeadId == leadId && t.PhaseNo == config.PhaseNo && t.DocIndex == 0 && t.Status != SigningStatus.Failed);
        //     if (existingOfferLetter is not null)
        //     {
        //         _log.LogInformation("Duplicate initiate for lead {LeadId} phase {Phase} — already {Status}.",
        //             leadId, config.PhaseNo, existingOfferLetter.Status);
        //         return Ok(new { status = "DUPLICATE", leadId, phase = config.PhaseNo, currentStatus = existingOfferLetter.Status.ToString() });
        //     }

        //     string offerDocumentId = string.Empty;
        //     try
        //     {
        //         var offerDetails = await _lsq.GetLeadOfferDetailsAsync(leadId);

        //         if (string.IsNullOrWhiteSpace(offerDetails.AuthorizedSignatoryEmail))
        //             return UnprocessableEntity(new { message = $"Lead {leadId} has no authorized signatory email", status = 422 });

        //         if (!offerDetails.IsComplete)
        //             _log.LogWarning("Lead {LeadId} offer letter fields incomplete — sending with blanks.", leadId);

        //         var recipientEmail = offerDetails.AuthorizedSignatoryEmail!;
        //         var offerFirstName = offerDetails.CustomerName;

        //         var pdfBytes = await _offerLetterDocs.GenerateOfferLetterPdfAsync(offerDetails);
        //         offerDocumentId = await _circularo.CreateDocumentFromBytesAsync(
        //             pdfBytes, $"offer-letter-{leadId}.pdf", $"{config.DocumentName} - Lead {leadId}");

        //         await _circularo.SendForSignatureAsync(
        //             offerDocumentId, recipientEmail,
        //             $"Dear {offerFirstName ?? "Customer"}, please sign: {config.DocumentName}", signaturePage: config.SignaturePage);

        //         _db.SigningTransactions.Add(new SigningTransaction
        //         {
        //             LeadId = leadId,
        //             PhaseNo = config.PhaseNo,
        //             DocIndex = 0,
        //             CircularoRef = offerDocumentId,
        //             CustomerEmail = recipientEmail,
        //             Status = SigningStatus.Sent,
        //             InboundPayload = raw
        //         });
        //         await _db.SaveChangesAsync();

        //         _log.LogInformation("Phase {Phase} document sent for lead {LeadId} to {Email}", config.PhaseNo, leadId, recipientEmail);
        //         return Ok(new { status = "SENT", leadId, phase = config.PhaseNo, email = recipientEmail });
        //     }
        //     catch (DownstreamException dex)
        //     {
        //         _log.LogError("Initiate failed for lead {LeadId}: downstream {Status} {Body}", leadId, dex.StatusCode, dex.ResponseBody);
        //         _db.SigningTransactions.Add(new SigningTransaction
        //         {
        //             LeadId = leadId,
        //             PhaseNo = config.PhaseNo,
        //             DocIndex = 0,
        //             CircularoRef = offerDocumentId,
        //             Status = SigningStatus.Failed,
        //             ErrorMessage = dex.ResponseBody,
        //             InboundPayload = raw
        //         });
        //         await _db.SaveChangesAsync();
        //         return new ContentResult
        //         {
        //             StatusCode = dex.StatusCode,
        //             Content = dex.ResponseBody,
        //             ContentType = "application/json"
        //         };
        //     }
        //     catch (Exception ex)
        //     {
        //         _log.LogError(ex, "Initiate failed for lead {LeadId}", leadId);
        //         _db.SigningTransactions.Add(new SigningTransaction
        //         {
        //             LeadId = leadId,
        //             PhaseNo = config.PhaseNo,
        //             DocIndex = 0,
        //             CircularoRef = offerDocumentId,
        //             Status = SigningStatus.Failed,
        //             ErrorMessage = ex.Message,
        //             InboundPayload = raw
        //         });
        //         await _db.SaveChangesAsync();
        //         return StatusCode(500, new { error = ex.Message });
        //     }
        // }
        /////////////////////
        /// ///////////////////////////////
        var items = await _db.PhaseDocumentItems
            .Where(i => i.PhaseDocumentConfigId == config.Id && i.IsActive)
            .OrderBy(i => i.DocIndex)
            .ToListAsync();

        if (!items.Any())
            return Ok(new { status = "IGNORED", reason = $"phase {config.PhaseNo} has no active document items" });

        var (email, firstName) = await _lsq.GetLeadEmailAsync(leadId);
        if (string.IsNullOrWhiteSpace(email))
            return UnprocessableEntity(new { message = $"Lead {leadId} has no email address", status = 422 });

        // BUG 2 FIX: build CRM token values ONCE per webhook if any item needs merging,
        // instead of re-fetching the lead per document.
        Dictionary<string, string>? tokenValues = null;
        if (items.Any(i => i.MergeStrategy != DocMergeStrategy.None))
        {
            
            // Stage 9 (Sale Contract / Agency Letter / Sale Instruction) and 
            // Stage 10 (Standing Instruction) each read their own config section; everything
            // else keeps using MurabahaStage1Fields exactly as before — additive only,
            // not a behavior change for Stage 7 or Stage 9.
            
            var section = config.PhaseNo switch
            {
                9 => "MurabahaStage2Fields",
                10 => "StandingInstructionFields",
                _ => "MurabahaStage1Fields"
            };
            tokenValues = await BuildMurabahaTokenValuesAsync(leadId, section);
        }

        var results = new List<object>();

        foreach (var item in items)
        {
            var existing = await _db.SigningTransactions.FirstOrDefaultAsync(
                t => t.LeadId == leadId && t.PhaseNo == config.PhaseNo &&
                     t.DocIndex == item.DocIndex && t.Status != SigningStatus.Failed);

            if (existing is not null)
            {
                _log.LogInformation("Duplicate initiate for lead {LeadId} phase {Phase} doc {DocIndex} — already {Status}.",
                    leadId, config.PhaseNo, item.DocIndex, existing.Status);
                results.Add(new { item.DocIndex, item.DocumentName, status = "DUPLICATE", currentStatus = existing.Status.ToString() });
                continue;
            }

            string documentId = string.Empty;
            try
            {
                var docTitle = $"{item.DocumentName} - Lead {leadId}";

                // ==========================================================
                // BUG 2 FIX: per-item merge strategy branch (was: every item
                // sent as a raw file regardless of MergeStrategy).
                // ==========================================================
                switch (item.MergeStrategy)
                {
                    case DocMergeStrategy.PdfOverlay:
                        {
                            if (string.IsNullOrWhiteSpace(item.OverlayFieldsJson))
                                throw new InvalidOperationException(
                                    $"Doc {item.DocIndex} '{item.DocumentName}' is PdfOverlay but OverlayFieldsJson is empty.");

                            var stamped = _overlay.Stamp(item.TemplatePath, item.OverlayFieldsJson, tokenValues!);
                            documentId = await _circularo.CreateDocumentFromBytesAsync(
                                stamped, $"{item.DocumentName}.pdf", docTitle);
                            break;
                        }
                    case DocMergeStrategy.DocxTokens:
                        {
                            var pdfBytes = await _offerLetterDocs.GenerateFromDocxTemplateAsync(
                                item.TemplatePath, tokenValues!);
                            documentId = await _circularo.CreateDocumentFromBytesAsync(
                                pdfBytes, $"{item.DocumentName}.pdf", docTitle);
                            break;
                        }
                    case DocMergeStrategy.AcroFormFill:
                        {
                            if (string.IsNullOrWhiteSpace(item.FormFieldMappingJson))
                                throw new InvalidOperationException(
                                    $"Doc {item.DocIndex} '{item.DocumentName}' is AcroFormFill but FormFieldMappingJson is empty.");

                            var fieldValues = ResolveAcroFormFieldValues(item.FormFieldMappingJson, tokenValues!);
                            var filled = await _acroForm.FillAndFlattenAsync(item.TemplatePath, fieldValues);
                            documentId = await _circularo.CreateDocumentFromBytesAsync(
                                filled, $"{item.DocumentName}.pdf", docTitle);
                            break;
                        }
                    default:
                        documentId = await _circularo.CreateDocumentFromTemplatePathAsync(item.TemplatePath, docTitle);
                        break;
                }

                await _circularo.SendForSignatureAsync(
                    documentId, email,
                    $"Dear {firstName ?? "Customer"}, please sign: {item.DocumentName}",
                    signaturePage: item.SignaturePage);

                _db.SigningTransactions.Add(new SigningTransaction
                {
                    LeadId = leadId,
                    PhaseNo = config.PhaseNo,
                    DocIndex = item.DocIndex,
                    CircularoRef = documentId,
                    CustomerEmail = email,
                    Status = SigningStatus.Sent,
                    InboundPayload = raw
                });
                await _db.SaveChangesAsync();

                _log.LogInformation("Set doc {DocIndex} '{Name}' sent for lead {LeadId} to {Email}",
                    item.DocIndex, item.DocumentName, leadId, email);
                results.Add(new { item.DocIndex, item.DocumentName, status = "SENT" });
            }
            catch (DownstreamException dex)
            {
                _log.LogError("Set doc {DocIndex} failed for lead {LeadId}: {Status} {Body}",
                    item.DocIndex, leadId, dex.StatusCode, dex.ResponseBody);
                _db.SigningTransactions.Add(new SigningTransaction
                {
                    LeadId = leadId,
                    PhaseNo = config.PhaseNo,
                    DocIndex = item.DocIndex,
                    CircularoRef = documentId,
                    Status = SigningStatus.Failed,
                    ErrorMessage = dex.ResponseBody,
                    InboundPayload = raw
                });
                await _db.SaveChangesAsync();
                results.Add(new { item.DocIndex, item.DocumentName, status = "FAILED", error = dex.ResponseBody });
            }
            catch (Exception ex)
            {
                // Local failures (missing template file, bad overlay JSON, LibreOffice
                // convert error) — record and continue with the rest of the set.
                _log.LogError(ex, "Set doc {DocIndex} failed locally for lead {LeadId}", item.DocIndex, leadId);
                _db.SigningTransactions.Add(new SigningTransaction
                {
                    LeadId = leadId,
                    PhaseNo = config.PhaseNo,
                    DocIndex = item.DocIndex,
                    CircularoRef = documentId,
                    Status = SigningStatus.Failed,
                    ErrorMessage = ex.Message,
                    InboundPayload = raw
                });
                await _db.SaveChangesAsync();
                results.Add(new { item.DocIndex, item.DocumentName, status = "FAILED", error = ex.Message });
            }
        }

        return Ok(new { status = "SENT_SET", leadId, phase = config.PhaseNo, totalDocs = items.Count, results });
    }
    // =====================================================================
    // Shared core — sends every active document for a matched phase config.
    // Called by API #1 (real LSQ webhook). From Step 3 onward it will ALSO be
    // called internally by API #2 (Completed) to auto-chain into the next
    // phase without waiting for a manual LSQ stage click. Behavior is
    // IDENTICAL either way — this is a straight extraction, no logic changed.
    // =====================================================================
    private async Task<IActionResult> SendPhaseDocumentSetAsync(string leadId, PhaseDocumentConfig config, string raw)
    {
        // ---- Source gate: some phases (currently only Phase 10 / Standing
        // Instruction) are restricted to a specific Lead Source per Hakima's
        // fintech-by-fintech rules. NULL = no restriction, unaffected phases
        // skip this entirely. If the lead's source doesn't match, we skip the
        // document but still advance the stage so the lead doesn't get stuck.
        if (!string.IsNullOrWhiteSpace(config.RequiredLeadSource))
        {
            var sourceFields = await _lsq.GetLeadFieldsAsync(leadId, new[] { "Source" });
            var leadSource = sourceFields.TryGetValue("Source", out var s) ? s : null;

            if (!string.Equals(leadSource, config.RequiredLeadSource, StringComparison.OrdinalIgnoreCase))
            {
                _log.LogInformation("Skipping document for phase {Phase}, lead {LeadId}: source '{Source}' does not match required '{Required}'. Advancing stage anyway.",
                    config.PhaseNo, leadId, leadSource ?? "(blank)", config.RequiredLeadSource);

                await _lsq.UpdateLeadStageAsync(leadId, config.NextLeadStage);

                return Ok(new
                {
                    status = "SKIPPED_ADVANCED",
                    leadId,
                    phase = config.PhaseNo,
                    reason = $"Lead source '{leadSource}' does not match required '{config.RequiredLeadSource}' — document skipped",
                    nextStage = config.NextLeadStage
                });
            }
        }

        //to send  offer letter 
        if (config.RequiresFieldMerge)
            //to send  offer letter 
            if (config.RequiresFieldMerge)
        {
            var existingOfferLetter = await _db.SigningTransactions.FirstOrDefaultAsync(
                t => t.LeadId == leadId && t.PhaseNo == config.PhaseNo && t.DocIndex == 0 && t.Status != SigningStatus.Failed);
            if (existingOfferLetter is not null)
            {
                _log.LogInformation("Duplicate initiate for lead {LeadId} phase {Phase} — already {Status}.",
                    leadId, config.PhaseNo, existingOfferLetter.Status);
                return Ok(new { status = "DUPLICATE", leadId, phase = config.PhaseNo, currentStatus = existingOfferLetter.Status.ToString() });
            }

            string offerDocumentId = string.Empty;
            try
            {
                var offerDetails = await _lsq.GetLeadOfferDetailsAsync(leadId);

                if (string.IsNullOrWhiteSpace(offerDetails.AuthorizedSignatoryEmail))
                    return UnprocessableEntity(new { message = $"Lead {leadId} has no authorized signatory email", status = 422 });

                if (!offerDetails.IsComplete)
                    _log.LogWarning("Lead {LeadId} offer letter fields incomplete — sending with blanks.", leadId);

                var recipientEmail = offerDetails.AuthorizedSignatoryEmail!;
                var offerFirstName = offerDetails.CustomerName;

                var pdfBytes = await _offerLetterDocs.GenerateOfferLetterPdfAsync(offerDetails);
                offerDocumentId = await _circularo.CreateDocumentFromBytesAsync(
                    pdfBytes, $"offer-letter-{leadId}.pdf", $"{config.DocumentName} - Lead {leadId}");

                await _circularo.SendForSignatureAsync(
                    offerDocumentId, recipientEmail,
                    $"Dear {offerFirstName ?? "Customer"}, please sign: {config.DocumentName}", signaturePage: config.SignaturePage);

                _db.SigningTransactions.Add(new SigningTransaction
                {
                    LeadId = leadId,
                    PhaseNo = config.PhaseNo,
                    DocIndex = 0,
                    CircularoRef = offerDocumentId,
                    CustomerEmail = recipientEmail,
                    Status = SigningStatus.Sent,
                    InboundPayload = raw
                });
                await _db.SaveChangesAsync();

                _log.LogInformation("Phase {Phase} document sent for lead {LeadId} to {Email}", config.PhaseNo, leadId, recipientEmail);
                return Ok(new { status = "SENT", leadId, phase = config.PhaseNo, email = recipientEmail });
            }
            catch (DownstreamException dex)
            {
                _log.LogError("Initiate failed for lead {LeadId}: downstream {Status} {Body}", leadId, dex.StatusCode, dex.ResponseBody);
                _db.SigningTransactions.Add(new SigningTransaction
                {
                    LeadId = leadId,
                    PhaseNo = config.PhaseNo,
                    DocIndex = 0,
                    CircularoRef = offerDocumentId,
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
                _db.SigningTransactions.Add(new SigningTransaction
                {
                    LeadId = leadId,
                    PhaseNo = config.PhaseNo,
                    DocIndex = 0,
                    CircularoRef = offerDocumentId,
                    Status = SigningStatus.Failed,
                    ErrorMessage = ex.Message,
                    InboundPayload = raw
                });
                await _db.SaveChangesAsync();
                return StatusCode(500, new { error = ex.Message });
            }
        }

        var items = await _db.PhaseDocumentItems
            .Where(i => i.PhaseDocumentConfigId == config.Id && i.IsActive)
            .OrderBy(i => i.DocIndex)
            .ToListAsync();

        if (!items.Any())
            return Ok(new { status = "IGNORED", reason = $"phase {config.PhaseNo} has no active document items" });

        var (email, firstName) = await _lsq.GetLeadEmailAsync(leadId);
        if (string.IsNullOrWhiteSpace(email))
            return UnprocessableEntity(new { message = $"Lead {leadId} has no email address", status = 422 });

        Dictionary<string, string>? tokenValues = null;
        if (items.Any(i => i.MergeStrategy != DocMergeStrategy.None))
        {
            var section = config.PhaseNo switch
            {
                9 => "MurabahaStage2Fields",
                10 => "StandingInstructionFields",
                _ => "MurabahaStage1Fields"
            };
            tokenValues = await BuildMurabahaTokenValuesAsync(leadId, section);
        }

        var results = new List<object>();

        foreach (var item in items)
        {
            var existing = await _db.SigningTransactions.FirstOrDefaultAsync(
                t => t.LeadId == leadId && t.PhaseNo == config.PhaseNo &&
                     t.DocIndex == item.DocIndex && t.Status != SigningStatus.Failed);

            if (existing is not null)
            {
                _log.LogInformation("Duplicate initiate for lead {LeadId} phase {Phase} doc {DocIndex} — already {Status}.",
                    leadId, config.PhaseNo, item.DocIndex, existing.Status);
                results.Add(new { item.DocIndex, item.DocumentName, status = "DUPLICATE", currentStatus = existing.Status.ToString() });
                continue;
            }

            string documentId = string.Empty;
            try
            {
                var docTitle = $"{item.DocumentName} - Lead {leadId}";

                switch (item.MergeStrategy)
                {
                    case DocMergeStrategy.PdfOverlay:
                        {
                            if (string.IsNullOrWhiteSpace(item.OverlayFieldsJson))
                                throw new InvalidOperationException(
                                    $"Doc {item.DocIndex} '{item.DocumentName}' is PdfOverlay but OverlayFieldsJson is empty.");

                            var stamped = _overlay.Stamp(item.TemplatePath, item.OverlayFieldsJson, tokenValues!);
                            documentId = await _circularo.CreateDocumentFromBytesAsync(
                                stamped, $"{item.DocumentName}.pdf", docTitle);
                            break;
                        }
                    case DocMergeStrategy.DocxTokens:
                        {
                            var pdfBytes = await _offerLetterDocs.GenerateFromDocxTemplateAsync(
                                item.TemplatePath, tokenValues!);
                            documentId = await _circularo.CreateDocumentFromBytesAsync(
                                pdfBytes, $"{item.DocumentName}.pdf", docTitle);
                            break;
                        }
                    case DocMergeStrategy.AcroFormFill:
                        {
                            if (string.IsNullOrWhiteSpace(item.FormFieldMappingJson))
                                throw new InvalidOperationException(
                                    $"Doc {item.DocIndex} '{item.DocumentName}' is AcroFormFill but FormFieldMappingJson is empty.");

                            var fieldValues = ResolveAcroFormFieldValues(item.FormFieldMappingJson, tokenValues!);
                            var filled = await _acroForm.FillAndFlattenAsync(item.TemplatePath, fieldValues);
                            documentId = await _circularo.CreateDocumentFromBytesAsync(
                                filled, $"{item.DocumentName}.pdf", docTitle);
                            break;
                        }
                    default:
                        documentId = await _circularo.CreateDocumentFromTemplatePathAsync(item.TemplatePath, docTitle);
                        break;
                }

                await _circularo.SendForSignatureAsync(
                    documentId, email,
                    $"Dear {firstName ?? "Customer"}, please sign: {item.DocumentName}",
                    signaturePage: item.SignaturePage);

                _db.SigningTransactions.Add(new SigningTransaction
                {
                    LeadId = leadId,
                    PhaseNo = config.PhaseNo,
                    DocIndex = item.DocIndex,
                    CircularoRef = documentId,
                    CustomerEmail = email,
                    Status = SigningStatus.Sent,
                    InboundPayload = raw
                });
                await _db.SaveChangesAsync();

                _log.LogInformation("Set doc {DocIndex} '{Name}' sent for lead {LeadId} to {Email}",
                    item.DocIndex, item.DocumentName, leadId, email);
                results.Add(new { item.DocIndex, item.DocumentName, status = "SENT" });
            }
            catch (DownstreamException dex)
            {
                _log.LogError("Set doc {DocIndex} failed for lead {LeadId}: {Status} {Body}",
                    item.DocIndex, leadId, dex.StatusCode, dex.ResponseBody);
                _db.SigningTransactions.Add(new SigningTransaction
                {
                    LeadId = leadId,
                    PhaseNo = config.PhaseNo,
                    DocIndex = item.DocIndex,
                    CircularoRef = documentId,
                    Status = SigningStatus.Failed,
                    ErrorMessage = dex.ResponseBody,
                    InboundPayload = raw
                });
                await _db.SaveChangesAsync();
                results.Add(new { item.DocIndex, item.DocumentName, status = "FAILED", error = dex.ResponseBody });
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Set doc {DocIndex} failed locally for lead {LeadId}", item.DocIndex, leadId);
                _db.SigningTransactions.Add(new SigningTransaction
                {
                    LeadId = leadId,
                    PhaseNo = config.PhaseNo,
                    DocIndex = item.DocIndex,
                    CircularoRef = documentId,
                    Status = SigningStatus.Failed,
                    ErrorMessage = ex.Message,
                    InboundPayload = raw
                });
                await _db.SaveChangesAsync();
                results.Add(new { item.DocIndex, item.DocumentName, status = "FAILED", error = ex.Message });
            }
        }

        return Ok(new { status = "SENT_SET", leadId, phase = config.PhaseNo, totalDocs = items.Count, results });
    }
    // =====================================================================
    // API #2 — called by the CIRCULARO webhook when a document is signed.
    // Attaches the signed PDF; advances the stage only when ALL docs in
    // the set are signed (order-independent).
    // =====================================================================
    [HttpPost("completed")]
    public async Task<IActionResult> Completed()
    {
        string raw;
        using (var reader = new StreamReader(Request.Body))
            raw = await reader.ReadToEndAsync();

        JsonElement payload;
        try { payload = JsonDocument.Parse(string.IsNullOrWhiteSpace(raw) ? "{}" : raw).RootElement.Clone(); }
        catch { return Ok(new { status = "IGNORED", reason = "empty or invalid body" }); }

        _log.LogInformation("COMPLETED inbound payload: {Payload}", raw);

        var documentId = FindFirstString(payload, "documentId", "docId", "id", "document", "objectId");
        var eventName = FindFirstString(payload, "event", "action", "status", "type");

        var completionEvents = new[] { "documentSigned", "documentCompleted" };
        if (eventName is not null && !completionEvents.Contains(eventName, StringComparer.OrdinalIgnoreCase))
            return Ok(new { status = "IGNORED", reason = $"event '{eventName}' is not a signing completion" });

        if (string.IsNullOrWhiteSpace(documentId))
            return Ok(new { status = "IGNORED", reason = "no document id in payload" });

        var txn = await _db.SigningTransactions
            .Where(t => t.CircularoRef == documentId && t.Status == SigningStatus.Sent)
            .OrderByDescending(t => t.SentAtUtc)
            .FirstOrDefaultAsync();

        if (txn is null)
            return Ok(new { status = "IGNORED", reason = "no matching open transaction" });

        var config = await _db.PhaseDocumentConfigs.FirstAsync(c => c.PhaseNo == txn.PhaseNo);
        var item = await _db.PhaseDocumentItems.FirstOrDefaultAsync(
            i => i.PhaseDocumentConfigId == config.Id && i.DocIndex == txn.DocIndex);

        try
        {
            txn.Status = SigningStatus.Signed;
            txn.SignedAtUtc = DateTime.UtcNow;
            txn.CallbackPayload = raw;
            await _db.SaveChangesAsync();

            // Per-document attach — best effort, doesn't block the set-completion check below.
            try
            {
                var detail = await _circularo.GetDocumentAsync(documentId);
                var hash = _circularo.TryExtractPdfHash(detail);
                if (hash is not null)
                {
                    var pdf = await _circularo.DownloadFileAsync(hash);
                    var fileName = $"{item?.DocumentName ?? "document"}-signed.pdf";
                    await _lsq.AttachDocumentAsync(txn.LeadId, fileName, pdf);
                }
                txn.Status = SigningStatus.Attached;
                txn.AttachedAtUtc = DateTime.UtcNow;
                await _db.SaveChangesAsync();
            }
            catch (Exception attachEx)
            {
                _log.LogError(attachEx, "Attach step failed for lead {LeadId} doc {DocIndex} — continuing.", txn.LeadId, txn.DocIndex);
            }

            // ---- Set-completion check: advance only when every active document in
            // this phase has reached Signed or Attached, regardless of signing order.
            var activeDocIndexes = await _db.PhaseDocumentItems
                .Where(i => i.PhaseDocumentConfigId == config.Id && i.IsActive)
                .Select(i => i.DocIndex)
                .ToListAsync();

            var completedDocIndexes = await _db.SigningTransactions
                .Where(t => t.LeadId == txn.LeadId && t.PhaseNo == txn.PhaseNo &&
                            (t.Status == SigningStatus.Signed || t.Status == SigningStatus.Attached))
                .Select(t => t.DocIndex)
                .ToListAsync();

            var remaining = activeDocIndexes.Except(completedDocIndexes).ToList();

            if (remaining.Any())
            {
                _log.LogInformation("Lead {LeadId} phase {Phase}: doc {DocIndex} signed, {Remaining} of {Total} still pending.",
                    txn.LeadId, txn.PhaseNo, txn.DocIndex, remaining.Count, activeDocIndexes.Count);
                return Ok(new
                {
                    status = "PARTIAL",
                    leadId = txn.LeadId,
                    phase = txn.PhaseNo,
                    signedDoc = item?.DocumentName,
                    remainingCount = remaining.Count
                });
            }

            // await _lsq.UpdateLeadStageAsync(txn.LeadId, config.NextLeadStage);
            // _log.LogInformation("Lead {LeadId} completed ALL {Total} docs in phase {Phase}: stage → '{Stage}'",
            //     txn.LeadId, activeDocIndexes.Count, txn.PhaseNo, config.NextLeadStage);

            // return Ok(new { status = "COMPLETED", leadId = txn.LeadId, nextStage = config.NextLeadStage });

            ////////
            /// 
            await _lsq.UpdateLeadStageAsync(txn.LeadId, config.NextLeadStage);
            _log.LogInformation("Lead {LeadId} completed ALL {Total} docs in phase {Phase}: stage → '{Stage}'",
                txn.LeadId, activeDocIndexes.Count, txn.PhaseNo, config.NextLeadStage);

            // ---- Auto-chain: if the stage we just wrote is itself a trigger for
            // another phase, send that phase's documents now instead of waiting
            // for a manual LSQ stage click. SendPhaseDocumentSetAsync's own
            // duplicate-transaction guard makes this safe even if an LSQ webhook
            // for that same stage also fires separately.
            object? chainResult = null;
            var nextConfig = await _db.PhaseDocumentConfigs
                .FirstOrDefaultAsync(c => c.IsActive && c.LeadStageTrigger == config.NextLeadStage);

            if (nextConfig is not null)
            {
                _log.LogInformation("Auto-chaining lead {LeadId} → phase {NextPhase} ('{Stage}') — no manual LSQ click needed.",
                    txn.LeadId, nextConfig.PhaseNo, config.NextLeadStage);

                await _kfs.HandleStageChangeAsync(txn.LeadId, config.NextLeadStage);
                var chainedActionResult = await SendPhaseDocumentSetAsync(txn.LeadId, nextConfig, raw);
                if (chainedActionResult is ObjectResult objResult)
                    chainResult = objResult.Value;
            }

            return Ok(new
            {
                status = "COMPLETED",
                leadId = txn.LeadId,
                nextStage = config.NextLeadStage,
                chainedPhase = nextConfig?.PhaseNo,
                chainedResult = chainResult
            });
        }
        catch (DownstreamException dex)
        {
            _log.LogError("Completed failed for lead {LeadId}: downstream {Status} {Body}", txn.LeadId, dex.StatusCode, dex.ResponseBody);
            txn.ErrorMessage = dex.ResponseBody;
            txn.RetryCount++;
            await _db.SaveChangesAsync();
            return new ContentResult { StatusCode = dex.StatusCode, Content = dex.ResponseBody, ContentType = "application/json" };
        }
        catch (Exception ex)
        {
            // RESTORED: generic fallback so unexpected errors still update the txn
            // instead of returning a bare 500 with no audit trail.
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
            "Purpose", "Tenor", "Profit Rate", "Finance Tenure", "Payment Terms",
            // Murabaha Stage 1 additions — helps you discover the schema names to
            // paste into appsettings MurabahaStage1Fields:
            "Commodity", "Cost Price", "Sale Price", "Address", "Designation",
            "Signatory", "Attention", "Tel", "Fax"
        };

        var matches = all.Where(f =>
            targets.Any(t => f.DisplayName.Contains(t, StringComparison.OrdinalIgnoreCase)));

        return Ok(matches.Select(m => new { m.DisplayName, m.SchemaName }));
    }

    // ------------------------- helpers -------------------------

    /// <summary>
    /// BUG 2 FIX: builds the token → value dictionary for Murabaha Stage 1 merges.
    /// Config keys in "MurabahaStage1Fields" follow the pattern "{Token}Field" = "{LSQ schema name}".
    /// Keys still set to TODO_* are skipped (their tokens render blank, with a log warning
    /// from the overlay/docx services). Day/Month/Year fall back to today's date when
    /// no CRM field is mapped for them.
    /// </summary>
    private async Task<Dictionary<string, string>> BuildMurabahaTokenValuesAsync(string leadId, string configSection = "MurabahaStage1Fields")
    {
        var section = _cfg.GetSection(configSection);

        var fieldMap = section.GetChildren()
            .Where(c => c.Key.EndsWith("Field")
                        && !string.IsNullOrWhiteSpace(c.Value)
                        && !c.Value!.StartsWith("TODO", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(c => c.Key[..^"Field".Length], c => c.Value!);

        var tokens = new Dictionary<string, string>();

        if (fieldMap.Count > 0)
        {
            var leadFields = await _lsq.GetLeadFieldsAsync(leadId, fieldMap.Values.ToArray());
            foreach (var (token, schemaName) in fieldMap)
                tokens[token] = leadFields.TryGetValue(schemaName, out var v) ? v ?? string.Empty : string.Empty;
        }
        else
        {
            _log.LogWarning("{Section} has no confirmed schema mappings yet — all merge tokens will be blank.", configSection);
        }

        // Agreement date parts default to today when not CRM-mapped.
        var now = DateTime.UtcNow;
        tokens.TryAdd("AgreementDay", now.Day.ToString());
        tokens.TryAdd("AgreementMonth", now.ToString("MMMM"));
        tokens.TryAdd("AgreementYear", now.Year.ToString());
        tokens.TryAdd("PromiseDate", now.ToString("dd/MM/yyyy"));
        tokens.TryAdd("SignatureDate", now.ToString("dd/MM/yyyy"));

        return tokens;
    }

    /// <summary>
    /// Stage 9: translates a PhaseDocumentItem's FormFieldMappingJson
    /// ({"AcroFieldName":"CanonicalToken"}) into the fieldName→value dictionary
    /// AcroFormFillService needs. Fields whose token has no resolved value (not
    /// yet confirmed by Hakima, or genuinely blank) are set to empty string rather
    /// than thrown — same "render blank, log a warning" posture as the other two
    /// merge strategies, so an unconfirmed field never blocks the whole document.
    /// </summary>
    private Dictionary<string, string> ResolveAcroFormFieldValues(string formFieldMappingJson, Dictionary<string, string> tokenValues)
    {
        var mapping = JsonSerializer.Deserialize<Dictionary<string, string>>(formFieldMappingJson)
            ?? new Dictionary<string, string>();

        var fieldValues = new Dictionary<string, string>();
        foreach (var (acroFieldName, token) in mapping)
        {
            if (tokenValues.TryGetValue(token, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                fieldValues[acroFieldName] = value;
            }
            else
            {
                fieldValues[acroFieldName] = string.Empty;
                _log.LogWarning("AcroForm field '{Field}' → token '{Token}' has no value — leaving blank.", acroFieldName, token);
            }
        }
        return fieldValues;
    }

    /// <summary>
    /// Depth-first search for the first string value under any of the given
    /// property names (case-insensitive).
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

    // TEMP — local testing only. Stamps the overlay PDFs / merges the docx for a
    // real lead and returns the result, WITHOUT touching Circularo or the DB.
    // Usage: GET /api/v1/signing/debug/murabaha-doc/{leadId}/{docIndex}
    // docIndex: 1 = Promise to Purchase, 2 = Cooling-off Waiver, 3 = Master Murabaha
    [HttpGet("debug/murabaha-doc/{leadId}/{docIndex:int}")]
    public async Task<IActionResult> DebugGenerateMurabahaDoc(string leadId, int docIndex)
    {
        var config = await _db.PhaseDocumentConfigs.FirstOrDefaultAsync(c => c.PhaseNo == 7);
        if (config is null) return NotFound(new { error = "Stage 7 config not seeded" });

        var item = await _db.PhaseDocumentItems.FirstOrDefaultAsync(
            i => i.PhaseDocumentConfigId == config.Id && i.DocIndex == docIndex);
        if (item is null) return NotFound(new { error = $"No doc item {docIndex} in Stage 7" });

        var tokens = await BuildMurabahaTokenValuesAsync(leadId);
        _log.LogInformation("DEBUG murabaha tokens for {LeadId}: {@Tokens}", leadId, tokens);

        byte[] bytes = item.MergeStrategy switch
        {
            DocMergeStrategy.PdfOverlay => _overlay.Stamp(item.TemplatePath, item.OverlayFieldsJson!, tokens),
            DocMergeStrategy.DocxTokens => await _offerLetterDocs.GenerateFromDocxTemplateAsync(item.TemplatePath, tokens),
            DocMergeStrategy.AcroFormFill => await _acroForm.FillAndFlattenAsync(item.TemplatePath, ResolveAcroFormFieldValues(item.FormFieldMappingJson!, tokens)),
            _ => await System.IO.File.ReadAllBytesAsync(item.TemplatePath)
        };

        return File(bytes, "application/pdf", $"{item.DocumentName}-{leadId}-debug.pdf");
    }

    // TEMP — local testing only. Same idea as DebugGenerateMurabahaDoc but for
    // Stage 9 (Sale Contract / Agency Letter / Sale Instruction). Returns the
    // filled PDF directly, WITHOUT touching Circularo or the DB.
    // Usage: GET /api/v1/signing/debug/stage9-doc/{leadId}/{docIndex}
    // docIndex: 1 = Sale Contract, 2 = Agency Appointment Letter, 3 = Sale Instruction
    [HttpGet("debug/stage9-doc/{leadId}/{docIndex:int}")]
    public async Task<IActionResult> DebugGenerateStage9Doc(string leadId, int docIndex)
    {
        var config = await _db.PhaseDocumentConfigs.FirstOrDefaultAsync(c => c.PhaseNo == 9);
        if (config is null) return NotFound(new { error = "Stage 9 config not seeded" });

        var item = await _db.PhaseDocumentItems.FirstOrDefaultAsync(
            i => i.PhaseDocumentConfigId == config.Id && i.DocIndex == docIndex);
        if (item is null) return NotFound(new { error = $"No doc item {docIndex} in Stage 9" });

        var tokens = await BuildMurabahaTokenValuesAsync(leadId, "MurabahaStage2Fields");
        _log.LogInformation("DEBUG stage9 tokens for {LeadId}: {@Tokens}", leadId, tokens);

        if (item.MergeStrategy != DocMergeStrategy.AcroFormFill || string.IsNullOrWhiteSpace(item.FormFieldMappingJson))
            return BadRequest(new { error = $"Doc {docIndex} is not a properly-configured AcroFormFill item" });

        var fieldValues = ResolveAcroFormFieldValues(item.FormFieldMappingJson, tokens);
        var bytes = await _acroForm.FillAndFlattenAsync(item.TemplatePath, fieldValues);

        return File(bytes, "application/pdf", $"{item.DocumentName}-{leadId}-debug.pdf");
    }

    // LeadSquared/Circularo webhook URL validation pings (HEAD/GET) — answer 200
    [HttpHead("initiate"), HttpGet("initiate")]
    public IActionResult InitiateProbe() => Ok(new { status = "READY" });

    [HttpHead("completed"), HttpGet("completed")]
    public IActionResult CompletedProbe() => Ok(new { status = "READY" });
}
