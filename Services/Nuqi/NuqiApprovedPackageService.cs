using System.Globalization;
using System.Text;
using CircularoIntegration.Api.Config;
using CircularoIntegration.Api.Data;
using CircularoIntegration.Api.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CircularoIntegration.Api.Services.Nuqi;

/// <summary>Result handed back to the controller (HTTP status + JSON body).</summary>
public record NuqiResult(int HttpStatus, object Body)
{
    public static NuqiResult Ok(object body) => new(200, body);
}

/// <summary>
/// ORCHESTRATOR – NUQI Gold X Mawarid "Approved Stage Automation" (BRD §6, §7, §8).
///
///   LSQ  Under Approval → Approved ──► HandleApprovedWebhookAsync
///        validate (§6.1, DOC-14) → idempotent approval event (DOC-01) → build T&amp;C + MMA/Exhibit A
///        → Circularo: customer first, Mawarid BD second (DOC-05) → status write-back (DOC-09)
///   Circularo callback ──► HandleCircularoCallbackAsync
///        Sent / Viewed / Customer Signed / Mawarid Signed / Completed / Declined / Expired
///        → executed PDFs + certificate attached (DOC-10) → completion gate (DOC-06, §12)
///   Exceptions (§8) ──► amount changed (DOC-13), moved backward, decline / expiry,
///        reconciliation when a callback is not received.
///   v3 LEAN: config + templates come from appsettings (no config tables); 3 DB tables only.
/// </summary>
public class NuqiApprovedPackageService
{
    private readonly NuqiDbContext _db;
    private readonly NuqiLeadClient _lead;
    private readonly LeadSquaredService _lsq;
    private readonly CircularoService _circularo;
    private readonly NuqiCircularoClient _nuqiCirc;
    private readonly NuqiPackageValidator _validator;
    private readonly NuqiDocumentBuilder _builder;
    private readonly NuqiNotificationService _notify;
    private readonly NuqiAuditService _audit;
    private readonly NuqiApprovedOptions _opt;
    private readonly ILogger<NuqiApprovedPackageService> _log;

    public NuqiApprovedPackageService(
        NuqiDbContext db, NuqiLeadClient lead, LeadSquaredService lsq, CircularoService circularo,
        NuqiCircularoClient nuqiCirc, NuqiPackageValidator validator, NuqiDocumentBuilder builder,
        NuqiNotificationService notify, NuqiAuditService audit, IOptions<NuqiApprovedOptions> opt,
        ILogger<NuqiApprovedPackageService> log)
    {
        _db = db;
        _lead = lead;
        _lsq = lsq;
        _circularo = circularo;
        _nuqiCirc = nuqiCirc;
        _validator = validator;
        _builder = builder;
        _notify = notify;
        _audit = audit;
        _opt = opt.Value;
        _log = log;
    }

    private string Svc => _opt.ServiceActor;
    private static string NewCorrelation() => Guid.NewGuid().ToString("N");

    // =================================================================================
    // 1. LSQ WEBHOOK – Under Approval → Approved
    // =================================================================================
    public async Task<NuqiResult> HandleApprovedWebhookAsync(string raw, CancellationToken ct = default)
    {
        var corr = NewCorrelation();
        var payload = NuqiJson.ParseOrEmpty(raw);
        _log.LogInformation("NUQI APPROVED inbound [{Corr}]: {Payload}", corr, NuqiRedactor.Redact(raw));

        var leadId = NuqiJson.FindFirstString(payload, "LeadId", "RelatedId", "ProspectID", "ProspectId", "leadId", "Id");
        if (string.IsNullOrWhiteSpace(leadId))
            return await IgnoredAsync(corr, null, "no lead id in payload", NuqiSource.LsqWebhook);

        var after = NuqiJson.ReadStage(payload, "After", "AfterStage", "NewValue", "ProspectStage");
        var before = NuqiJson.ReadStage(payload, "Before", "BeforeStage", "OldValue");
        var actor = NuqiJson.FindFirstString(payload, "ModifiedByEmailAddress", "ModifiedByEmail", "ModifiedBy", "ModifiedByName") ?? "LSQ";

        var cfg = await ActiveConfigAsync(ct);
        if (cfg is null)
        {
            _log.LogError("NUQI config '{Product}' missing or inactive (appsettings NuqiApproved:Package)", _opt.ProductCode);
            return await IgnoredAsync(corr, leadId, $"NUQI package config '{_opt.ProductCode}' not found/active", NuqiSource.LsqWebhook);
        }

        if (!StageEquals(after, cfg.TriggerStage))
            return await IgnoredAsync(corr, leadId, $"stage '{after}' is not the NUQI trigger '{cfg.TriggerStage}'", NuqiSource.LsqWebhook);

        // BRD §6.1: fire only on Under Approval → Approved. Saving/editing an Approved lead must not re-issue.
        if (before is not null && _opt.StrictPreviousStage && !StageEquals(before, cfg.PreviousStage))
            return await IgnoredAsync(corr, leadId, $"transition '{before}' → '{after}' is not '{cfg.PreviousStage}' → '{cfg.TriggerStage}'", NuqiSource.LsqWebhook);
        if (before is null && _opt.RequireBeforeStageInPayload)
            return await IgnoredAsync(corr, leadId, "payload has no Before stage (RequireBeforeStageInPayload=true)", NuqiSource.LsqWebhook);

        await _audit.WriteAsync(corr, leadId, "APPROVED_WEBHOOK_RECEIVED", actor, NuqiSource.LsqWebhook, NuqiOutcome.Success,
            before, after, $"Transition {before ?? "(not in payload)"} → {after}");

        return await StartPackageAsync(leadId, cfg, corr, NuqiSource.LsqWebhook, "LSQ_WEBHOOK", actor, replacing: null, ct);
    }

    /// <summary>
    /// Creates the approval event (idempotent), validates, and sends the package.
    /// Used by the webhook and by amount-change regeneration.
    /// </summary>
    private async Task<NuqiResult> StartPackageAsync(
        string leadId, NuqiPackageConfig cfg, string corr, string source, string triggerSource, string actor,
        NuqiApprovalEvent? replacing, CancellationToken ct)
    {
        Dictionary<string, string?> lead;
        try { lead = await _lead.GetLeadAsync(leadId, ct); }
        catch (DownstreamException dex)
        {
            await _audit.WriteAsync(corr, leadId, "LSQ_FETCH_FAILED", Svc, source, NuqiOutcome.Failed, detail: $"{dex.StatusCode} {dex.ResponseBody}");
            return new NuqiResult(502, new { status = "FAILED", leadId, error = "LeadSquared lead fetch failed", downstreamStatus = dex.StatusCode });
        }

        if (NuqiText.IsConfigured(_opt.ProductFilterField) && !string.IsNullOrWhiteSpace(_opt.ProductFilterValue))
        {
            var product = NuqiText.Get(lead, _opt.ProductFilterField);
            if (!string.Equals(product, _opt.ProductFilterValue, StringComparison.OrdinalIgnoreCase))
                return await IgnoredAsync(corr, leadId, $"lead product '{product}' is not '{_opt.ProductFilterValue}'", source);
        }

        var templates = ActiveTemplates();
        var validation = _validator.Validate(lead, cfg, templates);

        // ---------------- idempotency (DOC-01, DOC-11, UAT-03) ----------------
        var latest = await LatestEventAsync(leadId, ct);
        int version;
        if (triggerSource == "LSQ_WEBHOOK" && validation.Data.LsqApprovalEventVersion is int lsqVersion)
        {
            version = lsqVersion;
            var lsqKey = Key(leadId, version);
            var existing = await _db.ApprovalEvents.AsNoTracking().FirstOrDefaultAsync(e => e.IdempotencyKey == lsqKey, ct);
            if (existing is not null) return await DuplicateAsync(corr, existing, source);

            if (latest is not null && !NuqiStatus.Terminal.Contains(latest.PackageStatus))
            {
                if (latest.PackageStatus == NuqiStatus.Completed)
                    return await ReviewRequiredAsync(corr, latest, cfg, "New approval event received but the previous package is already fully executed", source);
                await SupersedeAsync(latest, cfg, $"Replaced by LSQ approval event version {version}", corr, source, NuqiStatus.Superseded, ct);
                replacing = latest;
            }
        }
        else
        {
            if (replacing is null && latest is not null && !NuqiStatus.Terminal.Contains(latest.PackageStatus))
                return await DuplicateAsync(corr, latest, source);
            version = (latest?.ApprovalEventVersion ?? 0) + 1;
        }

        var d = validation.Data;
        var ev = new NuqiApprovalEvent
        {
            LeadId = leadId,
            ApprovalEventVersion = version,
            IdempotencyKey = Key(leadId, version),
            CorrelationId = corr,
            ApplicationId = d.ApplicationId,
            ApprovedAmount = d.Amount > 0 ? d.Amount : null,
            CashWithdrawalLimit = d.CashWithdrawalLimit,
            Currency = d.Currency,
            CustomerEmail = d.CustomerEmail,
            MawaridSignerName = d.MawaridSignerName,
            MawaridSignerEmail = d.MawaridSignerEmail,
            PackageStatus = NuqiStatus.Validating,
            LastEventAtUtc = DateTime.UtcNow,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        };
        _db.ApprovalEvents.Add(ev);
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Two webhooks raced – the unique IdempotencyKey stopped the second one.
            _db.Entry(ev).State = EntityState.Detached;
            var raceKey = ev.IdempotencyKey;
            var winner = await _db.ApprovalEvents.AsNoTracking().FirstOrDefaultAsync(e => e.IdempotencyKey == raceKey, ct);
            if (winner is not null) return await DuplicateAsync(corr, winner, source);
            throw;
        }

        if (replacing is not null)
        {
            var replacingId = replacing.Id;
            var tracked = await _db.ApprovalEvents.FirstAsync(e => e.Id == replacingId, ct);
            tracked.SupersededByEventId = ev.Id;
            tracked.UpdatedAtUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
        }

        await _audit.WriteAsync(corr, leadId, "APPROVAL_EVENT_CREATED", actor, source, NuqiOutcome.Success,
            replacing?.PackageReference, ev.PackageReference, $"version {version}, trigger {triggerSource}");

        // ---------------- validation result (UAT-02) ----------------
        if (!validation.IsValid)
        {
            ev.PackageStatus = NuqiStatus.Failed;
            ev.FailureReason = NuqiText.Truncate(validation.Summary, 2000);
            ev.UpdatedAtUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);

            var f = _opt.LsqFields;
            await SafeWriteBackAsync(leadId, new Dictionary<string, string?>
            {
                [f.PackageStatus] = "Failed",
                [f.ExceptionReason] = NuqiText.Truncate(validation.Summary, 500),
                [f.DocsCompletedFlag] = "No"
            });
            await SafeExceptionAsync(leadId, $"NUQI package NOT sent – data validation failed ({ev.PackageReference})",
                $"No document was sent. Correct the fields below and move the lead to '{cfg.TriggerStage}' again.\n{validation.Summary}");
            await _notify.NotifyAsync(_opt.ProductCode, NuqiNotificationService.PackageException,
                $"NUQI package blocked – {ev.ApplicationId ?? leadId}",
                $"<p>Package <b>{ev.PackageReference}</b> was not sent.</p><p>{System.Net.WebUtility.HtmlEncode(validation.Summary)}</p>", ct);
            await _audit.WriteAsync(corr, leadId, "VALIDATION_FAILED", Svc, source, NuqiOutcome.Blocked,
                NuqiStatus.Validating, NuqiStatus.Failed, validation.Summary);

            return new NuqiResult(422, new
            {
                status = "BLOCKED",
                leadId,
                packageReference = ev.PackageReference,
                errors = validation.Errors
            });
        }

        // ---------------- simplified flow step 2: approval notification email ----------------
        await SendApprovalEmailAsync(ev, cfg, d, corr, source, ct);

        // ---------------- steps 3-4: one package (T&C + Murabaha Documents) → customer, then Mawarid ----------------
        return await SendPackageAsync(ev, cfg, templates, d, corr, source, ct);
    }

    /// <summary>
    /// "Your NUQI Gold X Mawarid Card has been approved with a limit of AED XXXX. Please sign the Murabaha
    /// documents sent to your registered email address." – sent by the API (Office 365). The SMS with the same
    /// text is sent by the LSQ automation. A failed email does NOT stop the documents: it is recorded,
    /// written to LSQ as an [ACTION REQUIRED] note and audited.
    /// </summary>
    private async Task SendApprovalEmailAsync(NuqiApprovalEvent ev, NuqiPackageConfig cfg, NuqiApprovalData d, string corr, string source, CancellationToken ct)
    {
        if (!cfg.SendApprovalEmail)
        {
            ev.ApprovalEmailStatus = "SKIPPED";
            await _db.SaveChangesAsync(ct);
            return;
        }

        string Fill(string template) => template
            .Replace("{Amount}", d.Amount.ToString(_opt.AmountFormat, CultureInfo.InvariantCulture))
            .Replace("{CashLimit}", d.CashWithdrawalLimit.ToString(_opt.AmountFormat, CultureInfo.InvariantCulture))
            .Replace("{CustomerName}", System.Net.WebUtility.HtmlEncode(d.CustomerName))
            .Replace("{ApplicationId}", d.ApplicationId ?? ev.LeadId);

        var subject = Fill(string.IsNullOrWhiteSpace(cfg.ApprovalEmailSubject)
            ? "Your NUQI Gold X Mawarid Card has been approved"
            : cfg.ApprovalEmailSubject);
        var body = Fill(string.IsNullOrWhiteSpace(cfg.ApprovalEmailBodyHtml)
            ? "<p>Dear {CustomerName},</p><p>Your NUQI Gold X Mawarid Card has been approved with a limit of AED {Amount}. " +
              "Please sign the Murabaha documents sent to your registered email address.</p><p>Regards,<br/>Mawarid Finance</p>"
            : cfg.ApprovalEmailBodyHtml);

        var (ok, error) = await _notify.SendCustomerApprovalEmailAsync(d.CustomerEmail, subject, body, ct);
        ev.ApprovalEmailStatus = ok ? "SENT" : "FAILED";
        ev.ApprovalEmailSentAtUtc = ok ? DateTime.UtcNow : null;
        ev.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        await _audit.WriteAsync(corr, ev.LeadId, "APPROVAL_EMAIL", Svc, source, ok ? NuqiOutcome.Success : NuqiOutcome.Failed,
            null, ev.ApprovalEmailStatus, ok ? $"to {NuqiRedactor.MaskEmail(d.CustomerEmail)}" : error);
        if (!ok)
            await SafeExceptionAsync(ev.LeadId, $"NUQI approval email NOT delivered ({ev.PackageReference})",
                $"The approval email to the customer failed: {NuqiText.Truncate(error, 300)}. Documents were still sent via Circularo. Inform the customer manually.");
    }

    /// <summary>Builds the 2 documents and sends them via Circularo with sequential signing (customer → Mawarid).</summary>
    private async Task<NuqiResult> SendPackageAsync(
        NuqiApprovalEvent ev, NuqiPackageConfig cfg, IReadOnlyList<NuqiPackageTemplate> templates,
        NuqiApprovalData d, string corr, string source, CancellationToken ct)
    {
        var createdDocIds = new List<string>();
        var rows = new List<NuqiPackageDocument>();
        var displayRef = ev.ApplicationId ?? ev.LeadId;

        try
        {
            var tokens = _builder.BuildTokens(d, cfg, ev);
            var rendered = new List<NuqiRenderedDocument>();
            foreach (var t in templates.OrderBy(t => t.DocIndex))
                rendered.Add(await _builder.RenderAsync(t, tokens, ct));

            var signers = new List<NuqiSigner>
            {
                new("Customer", d.CustomerEmail,
                    $"Dear {d.CustomerName}, your NUQI Gold X Mawarid Card has been approved. Please review and sign the Terms and Conditions and the Murabaha Documents."),
                new("Mawarid", d.MawaridSignerEmail,
                    $"Please review and sign the NUQI Gold X Mawarid Card Terms and Conditions and Murabaha Documents for application {displayRef}.")
            };
            var validUntil = DateTime.UtcNow.AddDays(Math.Max(1, cfg.LinkValidDays));

            // ONE package = T&C + Murabaha Documents (2 separate documents), customer first → Mawarid Business second.
            // 1) create both Circularo documents and persist rows BEFORE sharing (so every callback can be matched)
            var toShare = new List<(string DocumentId, IReadOnlyList<NuqiSignatureBox> Boxes)>();
            foreach (var r in rendered)
            {
                var docId = await _circularo.CreateDocumentFromBytesAsync(r.Pdf,
                    $"{r.Template.DocumentCode}-{ev.PackageReference}.pdf",
                    $"{r.Template.DocumentName} - {displayRef}");
                createdDocIds.Add(docId);

                var row = NewRow(ev, r, docId);
                rows.Add(row);
                _db.PackageDocuments.Add(row);
                toShare.Add((docId, _builder.SignatureBoxes(r.Template, r.PageCount)));
            }
            await _db.SaveChangesAsync(ct);

            // 2) send the package to BOTH signers at the same time (customer + Mawarid Business, any signing order).
            await _nuqiCirc.SharePackageAsync(toShare, signers, validUntil, ct);
            foreach (var row in rows) MarkSent(row);
            await _db.SaveChangesAsync(ct);

            ev.PackageStatus = NuqiStatus.Sent;
            ev.FailureReason = null;
            ev.LastEventAtUtc = DateTime.UtcNow;
            ev.UpdatedAtUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);

            var f = _opt.LsqFields;
            await SafeWriteBackAsync(ev.LeadId, new Dictionary<string, string?>
            {
                [f.PackageStatus] = NuqiStatus.ToLsqText(NuqiStatus.Sent),
                [f.TncStatus] = NuqiStatus.ToLsqText(NuqiStatus.Sent),
                [f.MmaStatus] = NuqiStatus.ToLsqText(NuqiStatus.Sent),
                [f.PackageReference] = ev.PackageReference,
                [f.PackageAmount] = d.Amount.ToString("0.00", CultureInfo.InvariantCulture),
                [f.DocsCompletedFlag] = "No",
                [f.ExceptionReason] = ""
            });

            var tplList = string.Join(", ", templates.OrderBy(t => t.DocIndex).Select(t => $"{t.DocumentCode} {t.TemplateVersion}"));
            await SafeNoteAsync(ev.LeadId,
                $"NUQI package {ev.PackageReference} sent for e-signature.\n" +
                $"Sent at the same time to the customer ({NuqiRedactor.MaskEmail(d.CustomerEmail)}) and Mawarid ({d.MawaridSignerName}); either may sign first. Each document is complete only when both have signed.\n" +
                $"Exhibit A: 1) credit limit not exceeding {d.Currency} {d.Amount.ToString(_opt.AmountFormat, CultureInfo.InvariantCulture)}, 2) cash withdrawal {d.Currency} {d.CashWithdrawalLimit.ToString(_opt.AmountFormat, CultureInfo.InvariantCulture)}.\n" +
                $"Templates: {tplList}. Circularo: {string.Join(", ", createdDocIds.Distinct())}. Link valid until {validUntil:yyyy-MM-dd}.");

            await _audit.WriteAsync(corr, ev.LeadId, "PACKAGE_SENT", Svc, source, NuqiOutcome.Success,
                NuqiStatus.Validating, NuqiStatus.Sent, $"{ev.PackageReference} docs {string.Join(",", createdDocIds.Distinct())} amount {d.Amount} templates {tplList}");

            return NuqiResult.Ok(new
            {
                status = "SENT",
                leadId = ev.LeadId,
                packageReference = ev.PackageReference,
                version = ev.ApprovalEventVersion,
                creditLimit = d.Amount,
                cashWithdrawalLimit = d.CashWithdrawalLimit,
                approvalEmail = ev.ApprovalEmailStatus,
                profitRate = cfg.ProfitRatePercent,
                documents = rows.Select(r => new { r.DocumentCode, r.TemplateVersion, r.CircularoDocumentId, r.DocumentStatus })
            });
        }
        catch (Exception ex)
        {
            var reason = ex is DownstreamException dex ? $"Downstream {dex.StatusCode}: {dex.ResponseBody}" : ex.Message;
            _log.LogError(ex, "NUQI package {Ref} failed for lead {LeadId}", ev.PackageReference, ev.LeadId);

            var notCancelled = new List<string>();
            foreach (var id in createdDocIds.Distinct())
                if (!await _nuqiCirc.TryCancelAsync(id, "NUQI package generation failed")) notCancelled.Add(id);

            foreach (var row in rows) { row.DocumentStatus = NuqiStatus.Failed; row.UpdatedAtUtc = DateTime.UtcNow; }
            ev.PackageStatus = NuqiStatus.Failed;
            ev.FailureReason = NuqiText.Truncate(reason, 2000);
            ev.UpdatedAtUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync(CancellationToken.None);

            var f = _opt.LsqFields;
            await SafeWriteBackAsync(ev.LeadId, new Dictionary<string, string?>
            {
                [f.PackageStatus] = "Failed",
                [f.ExceptionReason] = NuqiText.Truncate(reason, 500)
            });
            await SafeExceptionAsync(ev.LeadId, $"NUQI package generation failed ({ev.PackageReference})",
                $"{NuqiText.Truncate(reason, 800)}" +
                (notCancelled.Count > 0 ? $"\nRevoke these Circularo documents manually: {string.Join(", ", notCancelled)}" : string.Empty) +
                $"\nAfter the cause is fixed, move the lead back to '{cfg.PreviousStage}' and then to '{cfg.TriggerStage}' again to issue a new package.");
            await _notify.NotifyAsync(_opt.ProductCode, NuqiNotificationService.PackageException,
                $"NUQI package failed – {displayRef}", $"<p>{System.Net.WebUtility.HtmlEncode(NuqiText.Truncate(reason, 800))}</p>");
            await _audit.WriteAsync(corr, ev.LeadId, "PACKAGE_SEND_FAILED", Svc, source, NuqiOutcome.Failed,
                NuqiStatus.Validating, NuqiStatus.Failed, reason);

            return new NuqiResult(ex is DownstreamException ? 502 : 500, new
            {
                status = "FAILED",
                leadId = ev.LeadId,
                packageReference = ev.PackageReference,
                error = NuqiText.Truncate(reason, 500)
            });
        }
    }

    // =================================================================================
    // 2. CIRCULARO CALLBACK
    // =================================================================================
    public Task<bool> OwnsCircularoDocumentAsync(string documentId, CancellationToken ct = default) =>
        _db.PackageDocuments.AnyAsync(d => d.CircularoDocumentId == documentId, ct);

    public async Task<NuqiResult> HandleCircularoCallbackAsync(string raw, string source = NuqiSource.CircularoCallback, CancellationToken ct = default)
    {
        var corr = NewCorrelation();
        var payload = NuqiJson.ParseOrEmpty(raw);
        var documentId = NuqiJson.FindFirstString(payload, "documentId", "docId", "id", "document", "objectId");
        var eventName = NuqiJson.FindFirstString(payload, "event", "action", "status", "type");
        var actor = NuqiJson.FindFirstString(payload, "actor", "signer", "signedBy", "username", "userName", "user", "email", "shareTo");

        if (string.IsNullOrWhiteSpace(documentId))
            return NuqiResult.Ok(new { status = "IGNORED", reason = "no document id" });

        var docs = await _db.PackageDocuments.Where(x => x.CircularoDocumentId == documentId).ToListAsync(ct);
        if (docs.Count == 0)
            return NuqiResult.Ok(new { status = "IGNORED", reason = "not a NUQI document" });

        var ev = await _db.ApprovalEvents.FirstAsync(e => e.Id == docs[0].ApprovalEventId, ct);
        var cfg = _opt.Package;
        var mapped = MapEvent(eventName);

        // v3 lean: no callback table – every callback is logged (redacted) and audited.
        _log.LogInformation("NUQI callback [{Corr}] doc {DocId} event '{Event}' → {Mapped}, actor {Actor}, source {Source}: {Payload}",
            corr, documentId, eventName, mapped, NuqiRedactor.MaskEmail(actor), source, NuqiText.Truncate(NuqiRedactor.Redact(raw), 1500));

        if (NuqiStatus.Terminal.Contains(ev.PackageStatus))
        {
            await _audit.WriteAsync(corr, ev.LeadId, "CALLBACK_ON_CLOSED_PACKAGE", "circularo", source, NuqiOutcome.Ignored,
                detail: $"{ev.PackageReference} is {ev.PackageStatus}; event {eventName} on {documentId}");
            return NuqiResult.Ok(new { status = "IGNORED", reason = $"package {ev.PackageReference} is {ev.PackageStatus}" });
        }

        var oldPackage = ev.PackageStatus;
        var now = DateTime.UtcNow;
        var role = SignerRole(actor, ev);

        foreach (var doc in docs)
            ApplyEvent(doc, mapped, role, eventName, now);

        var outcome = mapped == "Unknown" ? "IGNORED" : "APPLIED";
        await _db.SaveChangesAsync(ct);

        await _audit.WriteAsync(corr, ev.LeadId, "DOC_EVENT", NuqiRedactor.MaskEmail(actor) is { Length: > 0 } a ? a : "circularo",
            source, outcome == "APPLIED" ? NuqiOutcome.Success : NuqiOutcome.Ignored,
            null, string.Join(",", docs.Select(x => $"{x.DocumentCode}={x.DocumentStatus}")), $"{eventName} → {mapped} (role {role ?? "unknown"}) on {documentId}");

        // Executed copy only when there is evidence: both signers identified, or Circularo said "completed".
        if (docs.Any(ReadyToFinalize))
            await FinalizeCircularoDocumentAsync(ev, docs, corr, source, ct);

        await AfterDocumentChangesAsync(ev, cfg, oldPackage, corr, source, ct);

        return NuqiResult.Ok(new
        {
            status = outcome,
            packageReference = ev.PackageReference,
            packageStatus = ev.PackageStatus,
            gateOpened = ev.GateOpened,
            documents = await _db.PackageDocuments.AsNoTracking().Where(x => x.ApprovalEventId == ev.Id)
                .Select(x => new { x.DocumentCode, x.DocumentStatus }).ToListAsync(ct)
        });
    }

    private void ApplyEvent(NuqiPackageDocument doc, string mapped, string? role, string? eventName, DateTime now)
    {
        if (doc.DocumentStatus is NuqiStatus.Cancelled or NuqiStatus.Failed) return;
        var rank = NuqiStatus.Rank(doc.DocumentStatus);

        switch (mapped)
        {
            case "Sent":
                if (doc.DocumentStatus == NuqiStatus.NotInitiated) doc.DocumentStatus = NuqiStatus.Sent;
                break;

            case "Viewed":
                if (rank is >= 0 and < 3) doc.DocumentStatus = NuqiStatus.Viewed;
                break;

            case "Signed":
                if (role == "Mawarid")
                {
                    doc.MawaridSignedAtUtc ??= now;
                    if (rank < 5) doc.DocumentStatus = NuqiStatus.MawaridSigned;
                }
                else if (role == "Customer")
                {
                    doc.CustomerSignedAtUtc ??= now;
                    if (rank < 4) doc.DocumentStatus = NuqiStatus.CustomerSigned;
                }
                else
                {
                    // Both signers receive the document at the same time and may sign in any order, so an
                    // unidentified signer can be either of them – do NOT guess. Wait for the actor's e-mail,
                    // for documentCompleted, or for reconciliation.
                    _log.LogWarning("NUQI {Code}: signed event with unidentified actor – waiting for actor / completion evidence", doc.DocumentCode);
                }
                break;

            case "Completed":
                doc.CustomerSignedAtUtc ??= now;
                doc.MawaridSignedAtUtc ??= now;
                if (rank < 5) doc.DocumentStatus = NuqiStatus.MawaridSigned; // becomes Completed after executed copy is stored
                break;

            case "Declined":
                if (doc.DocumentStatus != NuqiStatus.Completed) doc.DocumentStatus = NuqiStatus.Declined;
                break;

            case "Expired":
                if (doc.DocumentStatus != NuqiStatus.Completed) doc.DocumentStatus = NuqiStatus.Expired;
                break;
        }

        doc.LastEvent = NuqiText.Truncate(eventName, 60);
        doc.LastEventAtUtc = now;
        doc.UpdatedAtUtc = now;
    }

    private static bool ReadyToFinalize(NuqiPackageDocument d) =>
        !d.ExecutedAttached &&
        d.CustomerSignedAtUtc is not null && d.MawaridSignedAtUtc is not null &&
        d.DocumentStatus is not (NuqiStatus.Declined or NuqiStatus.Expired or NuqiStatus.Cancelled or NuqiStatus.Failed);

    /// <summary>Downloads the executed PDF (+ certificate) and attaches it to the lead (DOC-10). Evidence first.</summary>
    private async Task<bool> FinalizeCircularoDocumentAsync(
        NuqiApprovalEvent ev, List<NuqiPackageDocument> docsSharingId, string corr, string source, CancellationToken ct)
    {
        var circId = docsSharingId[0].CircularoDocumentId!;
        try
        {
            using var detail = await _circularo.GetDocumentAsync(circId);
            var hash = _circularo.TryExtractPdfHash(detail);
            if (string.IsNullOrWhiteSpace(hash))
            {
                _log.LogWarning("NUQI finalize: executed PDF hash not available yet for Circularo doc {DocId} – will retry via reconciliation", circId);
                return false;
            }

            var pdf = await _circularo.DownloadFileAsync(hash);
            var first = docsSharingId.OrderBy(x => x.DocIndex).First();
            var fileName = $"NUQI-{first.DocumentCode}-Executed-{ev.PackageReference}-{first.TemplateVersion}.pdf";
            await _lsq.AttachDocumentAsync(ev.LeadId, fileName, pdf);

            var cert = await _nuqiCirc.TryDownloadCertificateAsync(circId, ct);
            if (cert is not null)
                await _lsq.AttachDocumentAsync(ev.LeadId, fileName.Replace("-Executed-", "-CompletionCertificate-"), cert);

            var now = DateTime.UtcNow;
            foreach (var d in docsSharingId)
            {
                d.ExecutedAttached = true;
                d.DocumentStatus = NuqiStatus.Completed;
                d.CompletedAtUtc = now;
                d.UpdatedAtUtc = now;
            }
            await _db.SaveChangesAsync(ct);

            await _audit.WriteAsync(corr, ev.LeadId, "DOC_EXECUTED_ATTACHED", Svc, source, NuqiOutcome.Success,
                null, NuqiStatus.Completed, $"{string.Join("+", docsSharingId.Select(x => x.DocumentCode))} {circId} → {fileName}, certificate {(cert is null ? "not available" : "attached")}");
            return true;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "NUQI finalize failed for Circularo doc {DocId}", circId);
            await _audit.WriteAsync(corr, ev.LeadId, "DOC_FINALIZE_FAILED", Svc, source, NuqiOutcome.Failed, detail: ex.Message);
            return false;
        }
    }

    /// <summary>Recomputes package status, writes it to LSQ, handles decline/expiry and opens the completion gate.</summary>
    private async Task AfterDocumentChangesAsync(NuqiApprovalEvent ev, NuqiPackageConfig cfg, string oldPackage, string corr, string source, CancellationToken ct)
    {
        var all = await _db.PackageDocuments.Where(x => x.ApprovalEventId == ev.Id).ToListAsync(ct);
        var newPackage = Aggregate(all);

        ev.LastEventAtUtc = DateTime.UtcNow;
        ev.UpdatedAtUtc = DateTime.UtcNow;
        if (newPackage != ev.PackageStatus) ev.PackageStatus = newPackage;
        await _db.SaveChangesAsync(ct);

        if (newPackage != oldPackage)
        {
            var f = _opt.LsqFields;
            var tnc = all.FirstOrDefault(x => x.DocumentCode == NuqiDocCode.TermsAndConditions);
            var mma = all.FirstOrDefault(x => x.DocumentCode == NuqiDocCode.MasterMurabaha);
            await SafeWriteBackAsync(ev.LeadId, new Dictionary<string, string?>
            {
                [f.PackageStatus] = NuqiStatus.ToLsqText(newPackage),
                [f.TncStatus] = tnc is null ? null : NuqiStatus.ToLsqText(tnc.DocumentStatus),
                [f.MmaStatus] = mma is null ? null : NuqiStatus.ToLsqText(mma.DocumentStatus)
            });
            await _audit.WriteAsync(corr, ev.LeadId, "PACKAGE_STATUS_CHANGED", Svc, source, NuqiOutcome.Success, oldPackage, newPackage, ev.PackageReference);

            if (newPackage is NuqiStatus.Declined or NuqiStatus.Expired)
                await HandleDeclinedOrExpiredAsync(ev, cfg, all, newPackage, corr, source, ct);
        }

        if (newPackage == NuqiStatus.Completed && ev.GateProcessedAtUtc is null)
            await OpenGateAsync(ev, cfg, corr, source, ct);
    }

    /// <summary>BRD §8: customer declines / document expires → set status, stop downstream, route to Business.</summary>
    private async Task HandleDeclinedOrExpiredAsync(
        NuqiApprovalEvent ev, NuqiPackageConfig cfg, List<NuqiPackageDocument> all, string status, string corr, string source, CancellationToken ct)
    {
        var stillOpen = all.Where(x => x.DocumentStatus is not (NuqiStatus.Completed or NuqiStatus.Declined or NuqiStatus.Expired or NuqiStatus.Cancelled)
                                       && !string.IsNullOrWhiteSpace(x.CircularoDocumentId)).ToList();
        var manual = new List<string>();
        if (cfg.CancelSiblingsOnDecline)
        {
            foreach (var g in stillOpen.GroupBy(x => x.CircularoDocumentId!))
            {
                if (!await _nuqiCirc.TryCancelAsync(g.Key, $"NUQI package {status.ToLowerInvariant()}")) manual.Add(g.Key);
                foreach (var d in g) { d.DocumentStatus = NuqiStatus.Cancelled; d.UpdatedAtUtc = DateTime.UtcNow; }
            }
            await _db.SaveChangesAsync(ct);
        }

        var f = _opt.LsqFields;
        await SafeWriteBackAsync(ev.LeadId, new Dictionary<string, string?>
        {
            [f.ExceptionReason] = $"Customer package {status}",
            [f.DocsCompletedFlag] = "No"
        });
        await SafeExceptionAsync(ev.LeadId, $"NUQI documents {status.ToLowerInvariant()} – Business follow-up ({ev.PackageReference})",
            $"Package {ev.PackageReference} is {status}. Downstream automation is stopped. Contact the customer; " +
            $"after resolution move the lead back to '{cfg.PreviousStage}' and then to '{cfg.TriggerStage}' again to issue a new package." +
            (manual.Count > 0 ? $"\nRevoke manually in Circularo: {string.Join(", ", manual)}" : string.Empty));
        await _notify.NotifyAsync(_opt.ProductCode, NuqiNotificationService.PackageException,
            $"NUQI documents {status.ToLowerInvariant()} – {ev.ApplicationId ?? ev.LeadId}",
            $"<p>Package <b>{ev.PackageReference}</b> is <b>{status}</b>. Business follow-up required.</p>", ct);
        await _audit.WriteAsync(corr, ev.LeadId, $"PACKAGE_{status.ToUpperInvariant()}", Svc, source, NuqiOutcome.Success,
            detail: $"siblings cancelled: {stillOpen.Count}, manual revoke: {manual.Count}");
    }

    /// <summary>
    /// Completion gate (DOC-06, §12 Approved → Operations Evaluation). Runs ONCE per event (atomic claim).
    /// Re-checks that the executed amount still equals LSQ Credit Approved Amount before opening.
    /// </summary>
    private async Task OpenGateAsync(NuqiApprovalEvent ev, NuqiPackageConfig cfg, string corr, string source, CancellationToken ct)
    {
        var claimed = await _db.ApprovalEvents
            .Where(e => e.Id == ev.Id && e.GateProcessedAtUtc == null)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.GateProcessedAtUtc, (DateTime?)DateTime.UtcNow), ct);
        if (claimed == 0) return;                                   // another callback already did it
        ev.GateProcessedAtUtc = DateTime.UtcNow;

        var f = _opt.LsqFields;
        try
        {
            var lead = await _lead.GetLeadAsync(ev.LeadId, ct);
            var ok = NuqiText.TryParseAmount(NuqiText.Get(lead, f.CreditApprovedAmount), out var currentAmount);
            var cashChanged = NuqiText.IsConfigured(f.CashWithdrawalLimit)
                              && NuqiText.TryParseAmount(NuqiText.Get(lead, f.CashWithdrawalLimit), out var currentCash)
                              && currentCash != (ev.CashWithdrawalLimit ?? 0m);
            if (cashChanged)
            {
                ok = false;
                _log.LogWarning("NUQI gate: cash withdrawal limit changed in LSQ after package {Ref} was generated", ev.PackageReference);
            }
            if (!ok || currentAmount != ev.ApprovedAmount)
            {
                var msg = $"Executed Exhibit A amounts (credit limit {ev.Currency} {ev.ApprovedAmount:0.00}, cash withdrawal {ev.CashWithdrawalLimit:0.00}) do not match LeadSquared (Credit Approved Amount ({(ok ? currentAmount.ToString("0.00", CultureInfo.InvariantCulture) : "invalid")}). Credit, Legal and Sharia review required (DOC-13).";
                ev.FailureReason = msg;
                ev.UpdatedAtUtc = DateTime.UtcNow;
                await _db.SaveChangesAsync(ct);
                await SafeWriteBackAsync(ev.LeadId, new Dictionary<string, string?> { [f.ExceptionReason] = NuqiText.Truncate(msg, 500), [f.DocsCompletedFlag] = "No" });
                await SafeExceptionAsync(ev.LeadId, $"NUQI completion gate BLOCKED – amount mismatch ({ev.PackageReference})", msg);
                await _notify.NotifyAsync(_opt.ProductCode, NuqiNotificationService.PackageException, $"NUQI gate blocked – {ev.ApplicationId ?? ev.LeadId}", $"<p>{msg}</p>", ct);
                await _audit.WriteAsync(corr, ev.LeadId, "GATE_BLOCKED", Svc, source, NuqiOutcome.Blocked, detail: msg);
                return;
            }

            var docs = await _db.PackageDocuments.AsNoTracking().Where(x => x.ApprovalEventId == ev.Id).OrderBy(x => x.DocIndex).ToListAsync(ct);
            var sb = new StringBuilder();
            sb.AppendLine($"NUQI package {ev.PackageReference} FULLY EXECUTED (customer + Mawarid).");
            sb.AppendLine($"Exhibit A: 1) credit limit not exceeding {ev.Currency} {ev.ApprovedAmount?.ToString(_opt.AmountFormat, CultureInfo.InvariantCulture)}, 2) cash withdrawal {ev.Currency} {ev.CashWithdrawalLimit?.ToString(_opt.AmountFormat, CultureInfo.InvariantCulture)}.");
            foreach (var d in docs)
                sb.AppendLine($"- {d.DocumentCode} {d.TemplateVersion}: Circularo {d.CircularoDocumentId}, customer signed {d.CustomerSignedAtUtc:yyyy-MM-dd HH:mm} UTC, Mawarid signed {d.MawaridSignedAtUtc:yyyy-MM-dd HH:mm} UTC");
            await SafeNoteAsync(ev.LeadId, sb.ToString());

            await _lead.UpdateLeadFieldsAsync(ev.LeadId, new Dictionary<string, string?>
            {
                [f.DocsCompletedFlag] = "Yes",
                [f.PackageStatus] = NuqiStatus.ToLsqText(NuqiStatus.Completed),
                [f.ExceptionReason] = ""
            }, ct);

            var stageNote = "gate flag set (Operations moves the stage)";
            if (cfg.AutoAdvanceOnCompletion)
            {
                var currentStage = NuqiText.Get(lead, f.CurrentStage);
                if (currentStage is null || StageEquals(currentStage, cfg.TriggerStage))
                {
                    await _lsq.UpdateLeadStageAsync(ev.LeadId, cfg.NextStage);
                    stageNote = $"stage → '{cfg.NextStage}'";
                }
                else
                {
                    stageNote = $"stage NOT advanced – lead is in '{currentStage}', not '{cfg.TriggerStage}'";
                    await SafeExceptionAsync(ev.LeadId, $"NUQI package executed but lead not in '{cfg.TriggerStage}'", stageNote);
                }
            }

            ev.CompletedAtUtc = DateTime.UtcNow;
            ev.FailureReason = null;
            ev.UpdatedAtUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);

            await _audit.WriteAsync(corr, ev.LeadId, "GATE_OPENED", Svc, source, NuqiOutcome.Success, cfg.TriggerStage, cfg.NextStage, stageNote);
            await _notify.NotifyAsync(_opt.ProductCode, NuqiNotificationService.PackageCompleted,
                $"NUQI documents signed – {ev.ApplicationId ?? ev.LeadId}",
                $"<p>Package <b>{ev.PackageReference}</b> is fully executed by the customer and Mawarid.</p><p>{System.Net.WebUtility.HtmlEncode(stageNote)}</p>", ct);
        }
        catch (Exception ex)
        {
            // Release the claim so the reconciliation job retries the gate.
            _log.LogError(ex, "NUQI gate failed for {Ref} – will retry", ev.PackageReference);
            await _db.ApprovalEvents.Where(e => e.Id == ev.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(e => e.GateProcessedAtUtc, (DateTime?)null), CancellationToken.None);
            ev.GateProcessedAtUtc = null;
            await _audit.WriteAsync(corr, ev.LeadId, "GATE_FAILED_RETRY", Svc, source, NuqiOutcome.Failed, detail: ex.Message);
        }
    }

    // =================================================================================
    // 3. EXCEPTIONS (§8)
    // =================================================================================

    /// <summary>DOC-13 / UAT-14 – Credit Approved Amount changed after the package was generated.</summary>
    public async Task<NuqiResult> HandleAmountChangedAsync(string raw, CancellationToken ct = default)
    {
        var corr = NewCorrelation();
        var payload = NuqiJson.ParseOrEmpty(raw);
        _log.LogInformation("NUQI AMOUNT-CHANGED inbound [{Corr}]: {Payload}", corr, NuqiRedactor.Redact(raw));
        var leadId = NuqiJson.FindFirstString(payload, "LeadId", "RelatedId", "ProspectID", "ProspectId", "leadId", "Id");
        var actor = NuqiJson.FindFirstString(payload, "ModifiedByEmailAddress", "ModifiedBy") ?? "LSQ";
        if (string.IsNullOrWhiteSpace(leadId)) return await IgnoredAsync(corr, null, "no lead id", NuqiSource.LsqWebhook);

        var cfg = await ActiveConfigAsync(ct);
        if (cfg is null) return await IgnoredAsync(corr, leadId, "NUQI config not active", NuqiSource.LsqWebhook);

        var ev = await LatestEventAsync(leadId, ct, tracked: true);
        if (ev is null || NuqiStatus.Terminal.Contains(ev.PackageStatus))
            return await IgnoredAsync(corr, leadId, "no active NUQI package for this lead", NuqiSource.LsqWebhook);

        var lead = await _lead.GetLeadAsync(leadId, ct);
        if (!NuqiText.TryParseAmount(NuqiText.Get(lead, _opt.LsqFields.CreditApprovedAmount), out var newAmount) || newAmount <= 0)
        {
            await SafeExceptionAsync(leadId, "NUQI: Credit Approved Amount changed to an invalid value", "Package left unchanged. Correct the amount.");
            return await IgnoredAsync(corr, leadId, "new Credit Approved Amount is invalid", NuqiSource.LsqWebhook);
        }

        decimal? newCash = null;
        if (NuqiText.IsConfigured(_opt.LsqFields.CashWithdrawalLimit) &&
            NuqiText.TryParseAmount(NuqiText.Get(lead, _opt.LsqFields.CashWithdrawalLimit), out var parsedCash))
            newCash = parsedCash;

        var creditChanged = newAmount != ev.ApprovedAmount;
        var cashChanged = newCash is not null && newCash != (ev.CashWithdrawalLimit ?? 0m);
        if (!creditChanged && !cashChanged)
            return NuqiResult.Ok(new { status = "NO_CHANGE", leadId, packageReference = ev.PackageReference, creditLimit = newAmount, cashWithdrawalLimit = newCash });

        var change = creditChanged
            ? $"Credit Approved Amount changed from {ev.Currency} {ev.ApprovedAmount:0.00} to {ev.Currency} {newAmount:0.00}"
            : $"Cash Withdrawal Limit changed from {ev.Currency} {ev.CashWithdrawalLimit:0.00} to {ev.Currency} {newCash:0.00}";
        var docs = await _db.PackageDocuments.AsNoTracking().Where(x => x.ApprovalEventId == ev.Id).ToListAsync(ct);
        if (ev.PackageStatus == NuqiStatus.Completed || docs.Any(x => x.DocumentStatus == NuqiStatus.Completed))
            return await ReviewRequiredAsync(corr, ev, cfg, $"{change} after documents were executed", NuqiSource.LsqWebhook);

        await SupersedeAsync(ev, cfg, change, corr, NuqiSource.LsqWebhook, NuqiStatus.Superseded, ct);

        if (!cfg.AutoRegenerateOnAmountChange)
        {
            await SafeExceptionAsync(leadId, $"NUQI package cancelled – {change}",
                $"Unsigned documents were invalidated. Move the lead back to '{cfg.PreviousStage}' and then to '{cfg.TriggerStage}' again to issue the corrected package.");
            return NuqiResult.Ok(new { status = "CANCELLED_AWAITING_REAPPROVAL", leadId, cancelled = ev.PackageReference });
        }

        var regenerated = await StartPackageAsync(leadId, cfg, corr, NuqiSource.LsqWebhook, "AMOUNT_CHANGE", actor, replacing: ev, ct);
        return new NuqiResult(regenerated.HttpStatus, new { status = "REGENERATED", cancelled = ev.PackageReference, reason = change, result = regenerated.Body });
    }

    /// <summary>§8 – Application moved backward from Approved: cancel the unsigned package, keep executed docs + audit.</summary>
    public async Task<NuqiResult> HandleStageRevertedAsync(string raw, CancellationToken ct = default)
    {
        var corr = NewCorrelation();
        var payload = NuqiJson.ParseOrEmpty(raw);
        _log.LogInformation("NUQI STAGE-REVERTED inbound [{Corr}]: {Payload}", corr, NuqiRedactor.Redact(raw));
        var leadId = NuqiJson.FindFirstString(payload, "LeadId", "RelatedId", "ProspectID", "ProspectId", "leadId", "Id");
        if (string.IsNullOrWhiteSpace(leadId)) return await IgnoredAsync(corr, null, "no lead id", NuqiSource.LsqWebhook);

        var cfg = await ActiveConfigAsync(ct);
        if (cfg is null) return await IgnoredAsync(corr, leadId, "NUQI config not active", NuqiSource.LsqWebhook);

        var after = NuqiJson.ReadStage(payload, "After", "AfterStage", "NewValue", "ProspectStage");
        var before = NuqiJson.ReadStage(payload, "Before", "BeforeStage", "OldValue");
        if (after is null || StageEquals(after, cfg.TriggerStage) || StageEquals(after, cfg.NextStage))
            return await IgnoredAsync(corr, leadId, $"'{after}' is not a backward move from '{cfg.TriggerStage}'", NuqiSource.LsqWebhook);
        if (before is not null && !StageEquals(before, cfg.TriggerStage))
            return await IgnoredAsync(corr, leadId, $"lead was in '{before}', not '{cfg.TriggerStage}'", NuqiSource.LsqWebhook);

        var ev = await LatestEventAsync(leadId, ct, tracked: true);
        if (ev is null || NuqiStatus.Terminal.Contains(ev.PackageStatus))
            return await IgnoredAsync(corr, leadId, "no active NUQI package", NuqiSource.LsqWebhook);
        if (ev.PackageStatus == NuqiStatus.Completed)
        {
            await _audit.WriteAsync(corr, leadId, "MOVED_BACK_AFTER_EXECUTION", "LSQ", NuqiSource.LsqWebhook, NuqiOutcome.Ignored,
                cfg.TriggerStage, after, "Executed documents preserved – nothing cancelled");
            return NuqiResult.Ok(new { status = "PRESERVED", reason = "package already fully executed", packageReference = ev.PackageReference });
        }

        await SupersedeAsync(ev, cfg, $"Lead moved backward from '{before ?? cfg.TriggerStage}' to '{after}'", corr, NuqiSource.LsqWebhook, NuqiStatus.Cancelled, ct);
        return NuqiResult.Ok(new { status = "CANCELLED", leadId, packageReference = ev.PackageReference });
    }

    /// <summary>Cancels every unsigned document of an event and closes it (Superseded / Cancelled). Executed docs are kept.</summary>
    private async Task SupersedeAsync(NuqiApprovalEvent ev, NuqiPackageConfig cfg, string reason, string corr, string source, string finalStatus, CancellationToken ct)
    {
        var tracked = _db.Entry(ev).State == EntityState.Detached
            ? await _db.ApprovalEvents.FirstAsync(e => e.Id == ev.Id, ct)
            : ev;

        var docs = await _db.PackageDocuments.Where(x => x.ApprovalEventId == tracked.Id).ToListAsync(ct);
        var manual = new List<string>();
        foreach (var g in docs.Where(x => x.DocumentStatus != NuqiStatus.Completed && !string.IsNullOrWhiteSpace(x.CircularoDocumentId))
                              .GroupBy(x => x.CircularoDocumentId!))
        {
            if (!await _nuqiCirc.TryCancelAsync(g.Key, reason)) manual.Add(g.Key);
            foreach (var d in g) { d.DocumentStatus = NuqiStatus.Cancelled; d.UpdatedAtUtc = DateTime.UtcNow; }
        }

        var old = tracked.PackageStatus;
        tracked.PackageStatus = finalStatus;
        tracked.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        ev.PackageStatus = finalStatus;

        var f = _opt.LsqFields;
        await SafeWriteBackAsync(tracked.LeadId, new Dictionary<string, string?>
        {
            [f.PackageStatus] = NuqiStatus.ToLsqText(NuqiStatus.Cancelled),
            [f.TncStatus] = NuqiStatus.ToLsqText(NuqiStatus.Cancelled),
            [f.MmaStatus] = NuqiStatus.ToLsqText(NuqiStatus.Cancelled),
            [f.DocsCompletedFlag] = "No",
            [f.ExceptionReason] = NuqiText.Truncate(reason, 500)
        });
        await SafeNoteAsync(tracked.LeadId, $"NUQI package {tracked.PackageReference} cancelled: {reason}." +
            (manual.Count > 0 ? $" Revoke manually in Circularo: {string.Join(", ", manual)}." : string.Empty));
        await _audit.WriteAsync(corr, tracked.LeadId, "PACKAGE_CANCELLED", Svc, source, NuqiOutcome.Success, old, finalStatus,
            $"{tracked.PackageReference}: {reason}; manual revoke {manual.Count}");
    }

    // =================================================================================
    // 4. RECONCILIATION (§8 "callback not received" – status enquiry before any retry)
    // =================================================================================
    public async Task<NuqiResult> ReconcileLeadAsync(string leadId, string source = NuqiSource.ManualApi, CancellationToken ct = default)
    {
        var ev = await LatestEventAsync(leadId, ct, tracked: true);
        if (ev is null) return new NuqiResult(404, new { status = "NOT_FOUND" });
        return NuqiResult.Ok(await ReconcileEventAsync(ev, source, ct));
    }

    public async Task<object> ReconcileEventAsync(NuqiApprovalEvent ev, string source, CancellationToken ct)
    {
        var corr = NewCorrelation();
        var cfg = _opt.Package;
        var oldPackage = ev.PackageStatus;
        var checkedDocs = new List<object>();

        if (NuqiStatus.InFlight.Contains(ev.PackageStatus))
        {
            var docs = await _db.PackageDocuments.Where(x => x.ApprovalEventId == ev.Id).ToListAsync(ct);
            foreach (var g in docs.Where(x => x.DocumentStatus != NuqiStatus.Completed && !string.IsNullOrWhiteSpace(x.CircularoDocumentId))
                                  .GroupBy(x => x.CircularoDocumentId!))
            {
                NuqiDocState state;
                try
                {
                    using var detail = await _circularo.GetDocumentAsync(g.Key);
                    state = _nuqiCirc.InferState(detail);
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "NUQI reconcile: Circularo lookup failed for {DocId}", g.Key);
                    state = NuqiDocState.Unknown;
                }

                var list = g.ToList();
                var now = DateTime.UtcNow;
                var mapped = state switch
                {
                    NuqiDocState.Completed => "Completed",
                    NuqiDocState.Declined => "Declined",
                    NuqiDocState.Expired => "Expired",
                    _ => "Unknown"
                };
                foreach (var d in list) ApplyEvent(d, mapped, null, $"reconcile:{state}", now);

                _log.LogInformation("NUQI reconcile [{Corr}] doc {DocId}: Circularo state {State} → {Mapped}", corr, g.Key, state, mapped);
                await _db.SaveChangesAsync(ct);

                if (state == NuqiDocState.Completed && list.Any(ReadyToFinalize))
                    await FinalizeCircularoDocumentAsync(ev, list, corr, source, ct);

                checkedDocs.Add(new { circularoDocumentId = g.Key, state = state.ToString() });
            }
        }

        ev.LastEventAtUtc = DateTime.UtcNow;          // next automatic check after StaleAfterHours
        await _db.SaveChangesAsync(ct);
        await AfterDocumentChangesAsync(ev, cfg, oldPackage, corr, source, ct);

        // No evidence after MaxAgeDays → raise ONE exception and stop automatic checking (FailureReason is the marker).
        var ageDays = (DateTime.UtcNow - ev.CreatedAtUtc).TotalDays;
        if (ageDays >= _opt.Reconciliation.MaxAgeDays && NuqiStatus.InFlight.Contains(ev.PackageStatus) && ev.FailureReason is null)
        {
            ev.FailureReason = $"No Circularo completion evidence after {ageDays:0} days";
            ev.UpdatedAtUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
            await SafeExceptionAsync(ev.LeadId, $"NUQI signing status not confirmed – {ev.PackageReference}",
                $"No Circularo completion evidence after {ageDays:0} days. Verify in Circularo. " +
                "Manual completion without evidence is not permitted (BRD §8).");
        }

        await _audit.WriteAsync(corr, ev.LeadId, "RECONCILED", Svc, source, NuqiOutcome.Success, oldPackage, ev.PackageStatus,
            $"docs checked {checkedDocs.Count}");
        return new { status = "RECONCILED", packageReference = ev.PackageReference, packageStatus = ev.PackageStatus, gateOpened = ev.GateOpened, checkedDocs };
    }

    /// <summary>Called by NuqiReconciliationWorker.</summary>
    public async Task<int> RunReconciliationSweepAsync(CancellationToken ct)
    {
        var r = _opt.Reconciliation;
        var cutoff = DateTime.UtcNow.AddHours(-Math.Max(0, r.StaleAfterHours));
        var due = await _db.ApprovalEvents
            .Where(e => (NuqiStatus.InFlight.Contains(e.PackageStatus) && (e.LastEventAtUtc ?? e.CreatedAtUtc) < cutoff && e.FailureReason == null)
                        || (e.PackageStatus == NuqiStatus.Completed && e.GateProcessedAtUtc == null))
            .OrderBy(e => e.LastEventAtUtc)
            .Take(50)
            .ToListAsync(ct);

        foreach (var ev in due)
        {
            try { await ReconcileEventAsync(ev, NuqiSource.ReconcileJob, ct); }
            catch (Exception ex) { _log.LogError(ex, "NUQI reconcile failed for {Ref}", ev.PackageReference); }
        }
        return due.Count;
    }

    // =================================================================================
    // 5. READ / DEBUG
    // =================================================================================
    public async Task<object> GetStatusAsync(string leadId, CancellationToken ct = default)
    {
        var events = await _db.ApprovalEvents.AsNoTracking().Where(e => e.LeadId == leadId)
            .OrderByDescending(e => e.ApprovalEventVersion).ToListAsync(ct);
        var ids = events.Select(e => e.Id).ToList();
        var docs = await _db.PackageDocuments.AsNoTracking().Where(d => ids.Contains(d.ApprovalEventId)).ToListAsync(ct);

        return new
        {
            leadId,
            packages = events.Select(e => new
            {
                e.Id,
                e.PackageReference,
                e.ApprovalEventVersion,
                e.PackageStatus,
                e.ApprovalEmailStatus,
                e.ApprovalEmailSentAtUtc,
                e.CashWithdrawalLimit,
                e.ApprovedAmount,
                e.Currency,
                e.GateOpened,
                e.GateProcessedAtUtc,
                e.CompletedAtUtc,
                e.FailureReason,
                e.SupersededByEventId,
                e.CreatedAtUtc,
                customer = NuqiRedactor.MaskEmail(e.CustomerEmail),
                mawaridSigner = e.MawaridSignerName,
                documents = docs.Where(d => d.ApprovalEventId == e.Id).OrderBy(d => d.DocIndex).Select(d => new
                {
                    d.DocumentCode,
                    d.TemplateVersion,
                    d.DocumentStatus,
                    d.CircularoDocumentId,
                    d.CustomerSignedAtUtc,
                    d.MawaridSignedAtUtc,
                    d.CompletedAtUtc,
                    d.ExecutedAttached,
                    d.LastEvent
                })
            })
        };
    }

    /// <summary>Dry run of §6.1 / DOC-14 validation – sends nothing.</summary>
    public async Task<NuqiResult> DebugValidateAsync(string leadId, CancellationToken ct = default)
    {
        var cfg = await ActiveConfigAsync(ct);
        if (cfg is null) return new NuqiResult(404, new { error = "NUQI config not active" });
        var lead = await _lead.GetLeadAsync(leadId, ct);
        var templates = ActiveTemplates();
        var v = _validator.Validate(lead, cfg, templates);
        return NuqiResult.Ok(new
        {
            leadId,
            isValid = v.IsValid,
            errors = v.Errors,
            extracted = new
            {
                v.Data.Amount,
                v.Data.CashWithdrawalLimit,
                v.Data.Currency,
                v.Data.CustomerName,
                customerEmail = NuqiRedactor.MaskEmail(v.Data.CustomerEmail),
                customerMobile = NuqiRedactor.MaskMobile(v.Data.CustomerMobile),
                v.Data.ApplicationId,
                v.Data.PartnerReference,
                v.Data.MawaridSignerName,
                mawaridSignerEmail = NuqiRedactor.MaskEmail(v.Data.MawaridSignerEmail),
                v.Data.LsqApprovalEventVersion
            },
            profitRate = cfg.ProfitRatePercent,
            templates = templates.Select(t => new { t.DocumentCode, t.TemplateVersion, t.IsApproved, t.MergeStrategy, t.TemplatePath })
        });
    }

    /// <summary>Renders one document for a real lead WITHOUT Circularo/DB writes (check Exhibit A visually – UAT-06/07).</summary>
    public async Task<(byte[]? Pdf, string FileName, object? Error)> DebugRenderAsync(string leadId, string docCode, CancellationToken ct = default)
    {
        var cfg = await ActiveConfigAsync(ct);
        if (cfg is null) return (null, "", new { error = "NUQI config not active" });
        var templates = ActiveTemplates();
        var t = templates.FirstOrDefault(x => string.Equals(x.DocumentCode, docCode, StringComparison.OrdinalIgnoreCase));
        if (t is null) return (null, "", new { error = $"no active template {docCode}" });

        var lead = await _lead.GetLeadAsync(leadId, ct);
        var v = _validator.Validate(lead, cfg, templates);
        if (v.Data.Amount <= 0) return (null, "", new { error = "Credit Approved Amount missing/invalid", v.Errors });

        var fake = new NuqiApprovalEvent { LeadId = leadId, ApprovalEventVersion = 0 };
        var rendered = await _builder.RenderAsync(t, _builder.BuildTokens(v.Data, cfg, fake), ct);
        return (rendered.Pdf, $"{t.DocumentCode}-{leadId}-DEBUG.pdf", null);
    }

    // =================================================================================
    // helpers
    // =================================================================================
    /// <summary>Package configuration from appsettings (NuqiApproved:Package). null when switched off.</summary>
    private Task<NuqiPackageConfig?> ActiveConfigAsync(CancellationToken ct) =>
        Task.FromResult<NuqiPackageConfig?>(_opt.Package.IsActive ? _opt.Package : null);

    /// <summary>The 2 documents of the package from appsettings (NuqiApproved:Templates), in signing order.</summary>
    private List<NuqiPackageTemplate> ActiveTemplates() =>
        _opt.Templates.Where(t => t.IsActive).OrderBy(t => t.DocIndex).ToList();

    private async Task<NuqiApprovalEvent?> LatestEventAsync(string leadId, CancellationToken ct, bool tracked = false)
    {
        var q = _db.ApprovalEvents.Where(e => e.LeadId == leadId);
        if (!tracked) q = q.AsNoTracking();
        return await q.OrderByDescending(e => e.ApprovalEventVersion).FirstOrDefaultAsync(ct);
    }

    private static string Key(string leadId, int version) => $"{leadId}:{version}";

    private bool StageEquals(string? a, string? b)
    {
        if (a is null || b is null) return false;
        if (string.Equals(a.Trim(), b.Trim(), StringComparison.Ordinal)) return true;
        if (string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            _log.LogWarning("NUQI stage matched only case-insensitively: '{A}' vs '{B}' – fix the configured stage name", a, b);
            return true;
        }
        return false;
    }

    private string MapEvent(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Unknown";
        var e = _opt.CircularoEvents;
        bool In(string[] list) => list.Any(x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase));
        if (In(e.Completed)) return "Completed";
        if (In(e.Signed)) return "Signed";
        if (In(e.Declined)) return "Declined";
        if (In(e.Expired)) return "Expired";
        if (In(e.Viewed)) return "Viewed";
        if (In(e.Sent)) return "Sent";
        return "Unknown";
    }

    private static string? SignerRole(string? actor, NuqiApprovalEvent ev)
    {
        if (NuqiText.EmailEquals(actor, ev.CustomerEmail)) return "Customer";
        if (NuqiText.EmailEquals(actor, ev.MawaridSignerEmail)) return "Mawarid";
        return null;
    }

    private static string Aggregate(List<NuqiPackageDocument> docs)
    {
        if (docs.Count == 0) return NuqiStatus.NotInitiated;
        foreach (var s in new[] { NuqiStatus.Declined, NuqiStatus.Expired, NuqiStatus.Failed, NuqiStatus.Cancelled })
            if (docs.Any(d => d.DocumentStatus == s)) return s;
        return docs.OrderBy(d => NuqiStatus.Rank(d.DocumentStatus)).First().DocumentStatus;
    }

    private static NuqiPackageDocument NewRow(NuqiApprovalEvent ev, NuqiRenderedDocument r, string circularoId) => new()
    {
        ApprovalEventId = ev.Id,
        DocIndex = r.Template.DocIndex,
        DocumentCode = r.Template.DocumentCode.ToUpperInvariant(),
        TemplateVersion = r.Template.TemplateVersion,
        CircularoDocumentId = circularoId,
        GeneratedSha256 = r.Sha256,
        PageCount = r.PageCount,
        DocumentStatus = NuqiStatus.NotInitiated,
        CreatedAtUtc = DateTime.UtcNow,
        UpdatedAtUtc = DateTime.UtcNow
    };

    private static void MarkSent(NuqiPackageDocument row)
    {
        row.DocumentStatus = NuqiStatus.Sent;
        row.LastEvent = "shared";
        row.LastEventAtUtc = DateTime.UtcNow;
        row.UpdatedAtUtc = DateTime.UtcNow;
    }

    private async Task<NuqiResult> IgnoredAsync(string corr, string? leadId, string reason, string source)
    {
        _log.LogInformation("NUQI IGNORED [{Corr}] lead {LeadId}: {Reason}", corr, leadId, reason);
        await _audit.WriteAsync(corr, leadId, "IGNORED", Svc, source, NuqiOutcome.Ignored, detail: reason);
        return NuqiResult.Ok(new { status = "IGNORED", leadId, reason });
    }

    private async Task<NuqiResult> DuplicateAsync(string corr, NuqiApprovalEvent existing, string source)
    {
        _log.LogInformation("NUQI DUPLICATE approval event for lead {LeadId} → original {Ref} ({Status})",
            existing.LeadId, existing.PackageReference, existing.PackageStatus);
        await _audit.WriteAsync(corr, existing.LeadId, "DUPLICATE_APPROVAL_EVENT", Svc, source, NuqiOutcome.Duplicate,
            detail: $"returned original {existing.PackageReference} ({existing.PackageStatus}) – no SMS/package re-sent");
        return NuqiResult.Ok(new
        {
            status = "DUPLICATE",
            leadId = existing.LeadId,
            packageReference = existing.PackageReference,
            version = existing.ApprovalEventVersion,
            packageStatus = existing.PackageStatus
        });
    }

    private async Task<NuqiResult> ReviewRequiredAsync(string corr, NuqiApprovalEvent ev, NuqiPackageConfig cfg, string reason, string source)
    {
        await SafeExceptionAsync(ev.LeadId, $"NUQI executed documents affected – Credit/Legal/Sharia review ({ev.PackageReference})",
            $"{reason}. Executed documents were NOT changed (DOC-13).");
        await _notify.NotifyAsync(_opt.ProductCode, NuqiNotificationService.PackageException,
            $"NUQI review required – {ev.ApplicationId ?? ev.LeadId}", $"<p>{System.Net.WebUtility.HtmlEncode(reason)}</p>");
        await _audit.WriteAsync(corr, ev.LeadId, "REVIEW_REQUIRED", Svc, source, NuqiOutcome.Blocked, detail: reason);
        return new NuqiResult(409, new { status = "REVIEW_REQUIRED", leadId = ev.LeadId, packageReference = ev.PackageReference, reason });
    }

    private async Task SafeWriteBackAsync(string leadId, Dictionary<string, string?> fields)
    {
        try { await _lead.UpdateLeadFieldsAsync(leadId, fields); }
        catch (Exception ex) { _log.LogError(ex, "NUQI LSQ write-back failed for {LeadId} – continuing", leadId); }
    }

    private async Task SafeNoteAsync(string leadId, string note)
    {
        try { await _lead.CreateNoteAsync(leadId, note); }
        catch (Exception ex) { _log.LogError(ex, "NUQI LSQ note failed for {LeadId} – continuing", leadId); }
    }

    private async Task SafeExceptionAsync(string leadId, string subject, string description)
    {
        try { await _lead.RaiseExceptionAsync(leadId, subject, description); }
        catch (Exception ex) { _log.LogError(ex, "NUQI LSQ exception task/note failed for {LeadId}: {Subject}", leadId, subject); }
    }
}
