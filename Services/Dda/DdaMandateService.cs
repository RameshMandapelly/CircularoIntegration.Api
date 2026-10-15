using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CircularoIntegration.Api.Config;
using CircularoIntegration.Api.Data;
using CircularoIntegration.Api.Models;
using CircularoIntegration.Api.Services.Nuqi;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CircularoIntegration.Api.Services.Dda;

/// <summary>Everything Build() learned about the lead → Create-DDA body.</summary>
public class DdaBuild
{
    public Dictionary<string, object?> Body { get; } = new();
    public List<string> Missing { get; } = new();
    public List<string> Invalid { get; } = new();
    public List<string> FallbacksUsed { get; } = new();
    public bool Ready => Missing.Count == 0 && Invalid.Count == 0;
}

/// <summary>
/// DEMO FLOW – LeadSquared stage → DDA  ──►  Create mandate in DD Marketplace  ──►  track status.
///
///   1. HandleStageWebhookAsync  LSQ automation (stage = DDA) → read lead → build body → Create DDA → note on lead.
///      DD Marketplace then e-mails / SMS the customer to sign with UAE PASS (BRD §9.1.4) – we do not send that mail.
///   2. RefreshRowAsync          Get Status (called by the poller, the callback, or the manual endpoint)
///      → normalised status → audit + lead note (+ optional lead fields) → signed PDF + 23-digit reference on ACTIVE.
/// A callback is never trusted for the status: it only triggers a Get Status call (BRD DDA-06).
/// </summary>
public class DdaMandateService
{
    private static readonly string[] DateFormats =
    {
        "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd", "dd/MM/yyyy", "dd-MM-yyyy", "MM/dd/yyyy", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-ddTHH:mm:ssZ"
    };

    private readonly DdaDbContext _db;
    private readonly NuqiLeadClient _lead;
    private readonly LeadSquaredService _lsq;
    private readonly DdaGatewayClient _gw;
    private readonly DdaAuditService _audit;
    private readonly DdaOptions _opt;
    private readonly ILogger<DdaMandateService> _log;

    public DdaMandateService(DdaDbContext db, NuqiLeadClient lead, LeadSquaredService lsq, DdaGatewayClient gw,
        DdaAuditService audit, IOptions<DdaOptions> opt, ILogger<DdaMandateService> log)
    {
        _db = db; _lead = lead; _lsq = lsq; _gw = gw; _audit = audit; _opt = opt.Value; _log = log;
    }

    private string Svc => _opt.ServiceActor;
    private DdaTrackingOptions T => _opt.Tracking;
    private static string NewCorr() => Guid.NewGuid().ToString("N");
    private static bool StageEquals(string? a, string? b) =>
        a is not null && b is not null && string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    // =================================================================================
    // 1. LSQ WEBHOOK – stage changed to DDA
    // =================================================================================
    public async Task<NuqiResult> HandleStageWebhookAsync(string raw, CancellationToken ct)
    {
        var corr = NewCorr();
        var payload = NuqiJson.ParseOrEmpty(raw);
        _log.LogInformation("DDA inbound [{Corr}]: {Payload}", corr, NuqiText.Truncate(NuqiRedactor.Redact(raw), 1500));

        var leadId = NuqiJson.FindFirstString(payload, "LeadId", "RelatedId", "ProspectID", "ProspectId", "leadId", "Id");
        if (string.IsNullOrWhiteSpace(leadId))
            return await IgnoredAsync(corr, null, "no lead id in payload", NuqiSource.LsqWebhook);
        if (!_opt.Enabled)
            return await IgnoredAsync(corr, leadId, "Dda:Enabled=false", NuqiSource.LsqWebhook);

        var after = NuqiJson.ReadStage(payload, "After", "AfterStage", "NewValue", "ProspectStage");
        var actor = NuqiJson.FindFirstString(payload, "ModifiedByEmailAddress", "ModifiedByEmail", "ModifiedBy", "ModifiedByName") ?? "LSQ";

        if (!StageEquals(after, _opt.TriggerStage))
            return await IgnoredAsync(corr, leadId, $"stage '{after}' is not the DDA trigger '{_opt.TriggerStage}'", NuqiSource.LsqWebhook);

        await _audit.WriteAsync(corr, leadId, "DDA_WEBHOOK_RECEIVED", actor, NuqiSource.LsqWebhook, NuqiOutcome.Success, null, after);
        return await CreateMandateAsync(leadId, corr, NuqiSource.LsqWebhook, actor, ct);
    }

    /// <summary>Manual trigger for the demo (no stage check). POST api/v1/nuqi/dda/{leadId}/create</summary>
    public Task<NuqiResult> CreateManualAsync(string leadId, CancellationToken ct) =>
        CreateMandateAsync(leadId, NewCorr(), NuqiSource.ManualApi, "MANUAL_API", ct);

    private async Task<NuqiResult> CreateMandateAsync(string leadId, string corr, string source, string actor, CancellationToken ct)
    {
        // ---- 1. lead data from LeadSquared
        Dictionary<string, string?> lead;
        try { lead = await _lead.GetLeadAsync(leadId, ct); }
        catch (DownstreamException dex)
        {
            await _audit.WriteAsync(corr, leadId, "LSQ_FETCH_FAILED", Svc, source, NuqiOutcome.Failed, detail: $"{dex.StatusCode} {dex.ResponseBody}");
            return new NuqiResult(502, new { status = "FAILED", leadId, error = "LeadSquared lead fetch failed", downstreamStatus = dex.StatusCode });
        }

        // ---- 2. idempotency (BRD DDA-08): one live mandate per lead
        var latest = await _db.Requests.AsNoTracking().Where(r => r.LeadId == leadId)
            .OrderByDescending(r => r.Version).FirstOrDefaultAsync(ct);
        if (latest is not null && !IsRetryable(latest))
        {
            await _audit.WriteAsync(corr, leadId, "DDA_DUPLICATE", actor, source, NuqiOutcome.Duplicate, null, latest.Status, $"existing ddaId {latest.DdaId}, v{latest.Version}");
            return NuqiResult.Ok(new { status = "DUPLICATE", leadId, ddaId = latest.DdaId, ddaStatus = latest.Status, version = latest.Version });
        }
        var version = (latest?.Version ?? 0) + 1;

        // ---- 3. map + validate (the Maker enters the unique ddaReferenceNumber on the lead)
        var build = Build(lead);
        await CheckReferenceNotUsedAsync(build, ct);
        var reference = build.Ready ? (string)build.Body["ddaReferenceNumber"]! : string.Empty;
        if (!build.Ready)
        {
            var msg = DescribeProblems(build);
            await _audit.WriteAsync(corr, leadId, "DDA_BLOCKED", actor, source, NuqiOutcome.Blocked, detail: msg);
            await SafeNoteAsync(leadId, $"[ACTION REQUIRED] DDA mandate was NOT created. {msg} Correct the lead and move the stage again.");
            return NuqiResult.Ok(new { status = "BLOCKED", leadId, missing = build.Missing, invalid = build.Invalid });
        }

        // ---- 4. save the request row first (unique key stops a racing duplicate)
        var row = new DdaRequest
        {
            LeadId = leadId,
            Version = version,
            IdempotencyKey = $"{leadId}:dda:v{version}",
            CorrelationId = corr,
            DdaReferenceNumber = reference,
            Status = DdaStatus.Creating,
            RequestSummary = NuqiText.Truncate(JsonSerializer.Serialize(MaskBody(build.Body)), 2000),
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        };
        _db.Requests.Add(row);
        try { await _db.SaveChangesAsync(ct); }
        catch (DbUpdateException)
        {
            _db.Entry(row).State = EntityState.Detached;
            var key = row.IdempotencyKey;
            var winner = await _db.Requests.AsNoTracking().FirstOrDefaultAsync(r => r.IdempotencyKey == key, ct);
            if (winner is not null)
                return NuqiResult.Ok(new { status = "DUPLICATE", leadId, ddaId = winner.DdaId, ddaStatus = winner.Status });
            throw;
        }

        // ---- 5. Create DDA in DD Marketplace
        DdaHttpResult res;
        try { res = await _gw.CreateAsync(JsonSerializer.Serialize(build.Body), ct); }
        catch (Exception ex) when (ex is DownstreamException or HttpRequestException or TaskCanceledException)
        {
            return await FailAsync(row, corr, source, actor, ex is DownstreamException d ? $"{d.StatusCode} {d.ResponseBody}" : ex.Message, ct);
        }
        if (!res.Ok)
            return await FailAsync(row, corr, source, actor, $"gateway {res.Status}: {res.Text}", ct);

        var root = NuqiJson.ParseOrEmpty(res.Text);
        var ddaId = NuqiJson.FindFirstString(root, T.DdaIdFields);
        if (string.IsNullOrWhiteSpace(ddaId))
            return await FailAsync(row, corr, source, actor, $"gateway {res.Status} but no ddaId in response: {res.Text}", ct);

        var rawStatus = NuqiJson.FindFirstString(root, T.StatusFields);
        var ddarId = NuqiJson.FindFirstString(root, T.DdarIdFields);
        row.DdaId = ddaId;
        row.DdarId = ddarId;
        row.GatewayStatus = NuqiText.Truncate(rawStatus, 150);
        row.Status = Normalise(rawStatus);
        row.LastResponse = NuqiText.Truncate(NuqiRedactor.Redact(res.Text), 3500);
        row.LastStatusChangeAtUtc = DateTime.UtcNow;
        row.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        await _audit.WriteAsync(corr, leadId, "DDA_CREATED", actor, source, NuqiOutcome.Success, null, row.Status,
            $"ddaId {ddaId}, ddarId {ddarId}, ref {reference}, gateway status '{rawStatus}'{(build.FallbacksUsed.Count > 0 ? ", DEMO FALLBACKS: " + string.Join(",", build.FallbacksUsed) : "")}");

        // ---- 6. best-effort follow-ups – the mandate already exists, so none of these may fail the call
        var note = new StringBuilder();
        note.Append($"DDA mandate created in DD Marketplace. DDA Id: {ddaId}. DDAR Id: {ddarId ?? "(not returned)"}. Reference: {reference}. Status: {row.Status}. ");
        var signingUrl = BuildSigningUrl(ddarId);
        if (signingUrl is not null) note.Append($"UAE PASS signing link: {signingUrl} ");
        note.Append("DD Marketplace will also send the customer an e-mail/SMS to sign with UAE PASS.");
        if (build.FallbacksUsed.Count > 0)
            note.Append($" [DEMO] default values used for: {string.Join(", ", build.FallbacksUsed)}.");
        await SafeNoteAsync(leadId, note.ToString());
        await SafeWriteFieldsAsync(row);

        if (T.AttachUnsignedPdf)
        {
            try
            {
                var pdf = await FetchPdfAsync(() => _gw.GetUnsignedPdfAsync(ddaId, ct));
                if (pdf is not null)
                {
                    await _lsq.AttachDocumentAsync(leadId, $"DDA-Unsigned-{SafeName(reference)}.pdf", pdf);
                    row.UnsignedPdfAttached = true;
                    await _db.SaveChangesAsync(ct);
                }
                else _log.LogWarning("DDA unsigned PDF not available yet for {DdaId}", ddaId);
            }
            catch (Exception ex) { _log.LogWarning(ex, "DDA unsigned PDF attach failed for lead {LeadId}", leadId); }
        }

        return NuqiResult.Ok(new { status = "CREATED", leadId, ddaId, ddarId, signingUrl, ddaReference = reference, ddaStatus = row.Status, gatewayStatus = rawStatus, version });
    }

    private async Task<NuqiResult> FailAsync(DdaRequest row, string corr, string source, string actor, string reason, CancellationToken ct)
    {
        row.Status = DdaStatus.Failed;
        row.FailureReason = NuqiText.Truncate(NuqiRedactor.Redact(reason), 1900);
        row.CompletedAtUtc = DateTime.UtcNow;
        row.UpdatedAtUtc = DateTime.UtcNow;
        try { await _db.SaveChangesAsync(ct); } catch (Exception ex) { _log.LogError(ex, "DDA could not save FAILED row for {LeadId}", row.LeadId); }

        await _audit.WriteAsync(corr, row.LeadId, "DDA_CREATE_FAILED", actor, source, NuqiOutcome.Failed, null, DdaStatus.Failed, reason);
        await SafeNoteAsync(row.LeadId, $"[ACTION REQUIRED] DDA mandate creation FAILED. {NuqiText.Truncate(row.FailureReason, 600)} Fix the issue and move the stage again (or call the create endpoint).");
        return new NuqiResult(502, new { status = "FAILED", leadId = row.LeadId, error = NuqiText.Truncate(row.FailureReason, 600) });
    }

    // =================================================================================
    // 2. TRACKING – Get Status
    // =================================================================================

    /// <summary>Authenticated gateway callback. The payload is only used to find the mandate; the status is re-read with Get Status.</summary>
    public async Task<NuqiResult> HandleCallbackAsync(string raw, CancellationToken ct)
    {
        var corr = NewCorr();
        var payload = NuqiJson.ParseOrEmpty(raw);
        _log.LogInformation("DDA callback [{Corr}]: {Payload}", corr, NuqiText.Truncate(NuqiRedactor.Redact(raw), 1500));

        var ddaId = NuqiJson.FindFirstString(payload, T.DdaIdFields);
        var reference = NuqiJson.FindFirstString(payload, "ddaReferenceNumber", "reference");
        var row = await _db.Requests.OrderByDescending(r => r.Version).FirstOrDefaultAsync(r =>
            (ddaId != null && r.DdaId == ddaId) || (reference != null && r.DdaReferenceNumber == reference), ct);
        if (row is null)
        {
            await _audit.WriteAsync(corr, null, "DDA_CALLBACK_UNMATCHED", "GATEWAY", "DDMP_CALLBACK", NuqiOutcome.Ignored, detail: $"ddaId={ddaId} ref={reference}");
            return NuqiResult.Ok(new { status = "IGNORED", reason = "no matching mandate" });
        }

        await _audit.WriteAsync(corr, row.LeadId, "DDA_CALLBACK_RECEIVED", "GATEWAY", "DDMP_CALLBACK", NuqiOutcome.Success, detail: $"ddaId {row.DdaId}");
        var changed = await RefreshRowAsync(row, corr, "DDMP_CALLBACK", ct);
        return NuqiResult.Ok(new { status = "OK", leadId = row.LeadId, ddaId = row.DdaId, ddaStatus = row.Status, changed });
    }

    /// <summary>Manual refresh: POST api/v1/nuqi/dda/{leadId}/refresh</summary>
    public async Task<NuqiResult> RefreshLeadAsync(string leadId, string source, CancellationToken ct)
    {
        var row = await _db.Requests.Where(r => r.LeadId == leadId).OrderByDescending(r => r.Version).FirstOrDefaultAsync(ct);
        if (row is null) return new NuqiResult(404, new { status = "NOT_FOUND", leadId });
        if (string.IsNullOrWhiteSpace(row.DdaId)) return new NuqiResult(409, new { status = "NO_DDA_ID", leadId, ddaStatus = row.Status, failure = row.FailureReason });

        var changed = await RefreshRowAsync(row, NewCorr(), source, ct);
        return NuqiResult.Ok(new { status = "OK", leadId, ddaId = row.DdaId, ddaStatus = row.Status, gatewayStatus = row.GatewayStatus, activeReference = row.ActiveReference, changed });
    }

    /// <summary>Get Status for one mandate and apply the result. Returns true when the status changed.</summary>
    public async Task<bool> RefreshRowAsync(DdaRequest row, string corr, string source, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(row.DdaId)) return false;

        DdaHttpResult res;
        try { res = await _gw.GetStatusAsync(row.DdaId, ct); }
        catch (Exception ex) when (ex is DownstreamException or HttpRequestException or TaskCanceledException)
        {
            await _audit.WriteAsync(corr, row.LeadId, "DDA_STATUS_CHECK_FAILED", Svc, source, NuqiOutcome.Failed, detail: ex.Message);
            return false;
        }

        row.PollCount++;
        row.LastStatusCheckAtUtc = DateTime.UtcNow;
        row.UpdatedAtUtc = DateTime.UtcNow;

        if (!res.Ok)
        {
            await _db.SaveChangesAsync(ct);
            await _audit.WriteAsync(corr, row.LeadId, "DDA_STATUS_CHECK_FAILED", Svc, source, NuqiOutcome.Failed, detail: $"gateway {res.Status}: {NuqiText.Truncate(res.Text, 300)}");
            return false;
        }

        var root = NuqiJson.ParseOrEmpty(res.Text);
        if (string.IsNullOrWhiteSpace(row.DdarId))
            row.DdarId = NuqiJson.FindFirstString(root, T.DdarIdFields);   // back-fills mandates created before ddarId capture
        var rawStatus = NuqiJson.FindFirstString(root, T.StatusFields);
        var newStatus = Normalise(rawStatus);
        var old = row.Status;
        var changed = !string.Equals(old, newStatus, StringComparison.Ordinal) ||
                      !string.Equals(row.GatewayStatus, NuqiText.Truncate(rawStatus, 150), StringComparison.Ordinal);

        row.GatewayStatus = NuqiText.Truncate(rawStatus, 150);
        row.Status = newStatus;
        row.LastResponse = NuqiText.Truncate(NuqiRedactor.Redact(res.Text), 3500);

        if (newStatus == DdaStatus.Active)
        {
            var refNo = NuqiJson.FindFirstString(root, T.ReferenceFields);
            if (string.IsNullOrWhiteSpace(refNo) || !Regex.IsMatch(refNo, @"^\d{23}$"))
                refNo = Regex.Match(res.Text, @"(?<!\d)\d{23}(?!\d)").Value;     // BRD: 23-digit DDA reference
            if (!string.IsNullOrWhiteSpace(refNo)) row.ActiveReference = refNo;
        }
        if (DdaStatus.Final.Contains(newStatus)) row.CompletedAtUtc ??= DateTime.UtcNow;
        if (changed) row.LastStatusChangeAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        if (!changed) return false;

        await _audit.WriteAsync(corr, row.LeadId, "DDA_STATUS_CHANGED", Svc, source, NuqiOutcome.Success, old, newStatus, $"gateway status '{rawStatus}'");

        // ---- lead note
        string note;
        if (newStatus == DdaStatus.Active)
            note = $"DDA is ACTIVE. DDA Id: {row.DdaId}. DDA reference: {row.ActiveReference ?? "(not returned)"}. Card disbursement gate: mandate OK.";
        else if (newStatus == DdaStatus.Rejected || newStatus == DdaStatus.Cancelled)
        {
            var reason = NuqiJson.FindFirstString(root, T.ReasonFields);
            note = $"[ACTION REQUIRED] DDA {newStatus}. DDA Id: {row.DdaId}. Gateway status: {rawStatus}. Reason: {reason ?? "(none returned)"}. Correct and re-submit.";
        }
        else
            note = $"DDA status: {newStatus} (gateway: {rawStatus ?? "n/a"}). DDA Id: {row.DdaId}.";
        await SafeNoteAsync(row.LeadId, note);
        await SafeWriteFieldsAsync(row);

        // ---- signed form once the customer has signed (BRD DDA-18)
        if (T.AttachSignedPdf && !row.SignedPdfAttached && (newStatus == DdaStatus.PendingBank || newStatus == DdaStatus.Active))
        {
            try
            {
                var pdf = await FetchPdfAsync(() => _gw.GetSignedPdfAsync(row.DdaId!, ct));
                if (pdf is not null)
                {
                    await _lsq.AttachDocumentAsync(row.LeadId, $"DDA-Signed-{SafeName(row.DdaReferenceNumber)}.pdf", pdf);
                    row.SignedPdfAttached = true;
                    await _db.SaveChangesAsync(ct);
                    await _audit.WriteAsync(corr, row.LeadId, "DDA_SIGNED_PDF_ATTACHED", Svc, source, NuqiOutcome.Success);
                }
            }
            catch (Exception ex) { _log.LogWarning(ex, "DDA signed PDF attach failed for lead {LeadId}", row.LeadId); }
        }
        return true;
    }

    /// <summary>Called by DdaStatusWorker. Polls every non-final mandate (BRD DDA-19). Returns how many were checked.</summary>
    public async Task<int> RunPollSweepAsync(CancellationToken ct)
    {
        if (DateTime.UtcNow.AddHours(4).Hour < T.PollWindowStartHourDubai) return 0;   // Dubai = UTC+4

        var cutoff = DateTime.UtcNow.AddDays(-Math.Max(1, T.MaxAgeDays));
        var rows = await _db.Requests
            .Where(r => r.DdaId != null && !DdaStatus.Final.Contains(r.Status) && r.CreatedAtUtc > cutoff)
            .OrderBy(r => r.LastStatusCheckAtUtc)
            .Take(Math.Max(1, T.MaxPerSweep))
            .ToListAsync(ct);

        foreach (var row in rows)
        {
            ct.ThrowIfCancellationRequested();
            try { await RefreshRowAsync(row, NewCorr(), NuqiSource.ReconcileJob, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { _log.LogError(ex, "DDA poll failed for lead {LeadId}", row.LeadId); }
        }
        return rows.Count;
    }

    // =================================================================================
    // 3. READ-ONLY / TEST HELPERS
    // =================================================================================
    public async Task<object> GetStatusAsync(string leadId, bool live, CancellationToken ct)
    {
        if (live) await RefreshLeadAsync(leadId, NuqiSource.ManualApi, ct);

        var rows = await _db.Requests.AsNoTracking().Where(r => r.LeadId == leadId).OrderByDescending(r => r.Version).ToListAsync(ct);
        var history = await _db.Audits.AsNoTracking()
            .Where(a => a.LeadId == leadId && a.Module == DdaAuditService.Module)
            .OrderByDescending(a => a.Id).Take(30)
            .Select(a => new { a.CreatedAtUtc, a.Action, a.OldValue, a.NewValue, a.Outcome, a.SourceChannel, a.Detail })
            .ToListAsync(ct);

        return new
        {
            leadId,
            found = rows.Count > 0,
            current = rows.Select(r => new
            {
                r.Version, r.DdaId, r.DdarId, signingUrl = BuildSigningUrl(r.DdarId), ddaReference = r.DdaReferenceNumber, r.Status, r.GatewayStatus, r.ActiveReference,
                r.FailureReason, r.UnsignedPdfAttached, r.SignedPdfAttached, r.PollCount,
                r.LastStatusCheckAtUtc, r.LastStatusChangeAtUtc, r.CompletedAtUtc, r.CreatedAtUtc, r.RequestSummary, r.LastResponse
            }).FirstOrDefault(),
            previousVersions = rows.Skip(1).Select(r => new { r.Version, r.DdaId, r.Status, r.FailureReason }).ToList(),
            history
        };
    }

    /// <summary>Builds the Create body from the lead WITHOUT calling the gateway. Sensitive values are masked.</summary>
    public async Task<NuqiResult> PreviewAsync(string leadId, CancellationToken ct)
    {
        Dictionary<string, string?> lead;
        try { lead = await _lead.GetLeadAsync(leadId, ct); }
        catch (DownstreamException dex) { return new NuqiResult(502, new { error = "LeadSquared lead fetch failed", downstreamStatus = dex.StatusCode }); }

        var b = Build(lead);
        await CheckReferenceNotUsedAsync(b, ct);
        return NuqiResult.Ok(new
        {
            leadId,
            ready = b.Ready,
            leadStage = NuqiText.Get(lead, "ProspectStage"),
            missing = b.Missing,
            invalid = b.Invalid,
            fallbacksUsed = b.FallbacksUsed,
            requestBody = MaskBody(b.Body)
        });
    }

    public Task<(bool Ok, string Message)> TestAuthAsync(CancellationToken ct) => _gw.TestAuthAsync(ct);

    // =================================================================================
    // 4. MAPPING  (LSQ lead → Create DDA body, shape from the DD Marketplace Postman sample)
    // =================================================================================
    /// <summary>The Maker-entered reference must not belong to a mandate we already created (DD Marketplace needs it unique).</summary>
    private async Task CheckReferenceNotUsedAsync(DdaBuild b, CancellationToken ct)
    {
        if (!b.Body.TryGetValue("ddaReferenceNumber", out var v) || v is not string reference || string.IsNullOrWhiteSpace(reference)) return;
        var used = await _db.Requests.AsNoTracking()
            .Where(r => r.DdaReferenceNumber == reference && r.DdaId != null)
            .Select(r => r.DdaId).FirstOrDefaultAsync(ct);
        if (used is not null)
            b.Invalid.Add($"ddaReferenceNumber '{reference}' is already used by DDA {used} - the Maker must enter a new unique reference");
    }

    public DdaBuild Build(IReadOnlyDictionary<string, string?> lead)
    {
        var f = _opt.LsqFields;
        var m = _opt.Mandate;
        var b = new DdaBuild();

        string? Fb(string key, string? value)
        {
            if (value is not null) return value;
            if (_opt.AllowFallbacks && _opt.Fallbacks.TryGetValue(key, out var fb) && !string.IsNullOrWhiteSpace(fb))
            {
                b.FallbacksUsed.Add(key);
                return fb.Trim();
            }
            return null;
        }

        string? Need(string key, string? fieldName, Func<string, string>? normalise = null, Func<string, bool>? valid = null, string? fixedValue = null)
        {
            var v = Fb(key, fixedValue ?? NuqiText.Get(lead, fieldName));
            if (v is null)
            {
                b.Missing.Add(NuqiText.IsConfigured(fieldName) ? $"{key} (LSQ field {fieldName})" : $"{key} (LSQ field not configured)");
                return null;
            }
            if (normalise is not null) v = normalise(v);
            if (valid is not null && !valid(v)) { b.Invalid.Add(key); return null; }
            return v;
        }

        // ---- customer
        var full = NuqiText.Get(lead, f.FullName);
        if (full is null)
        {
            var parts = new[] { NuqiText.Get(lead, f.FirstName), NuqiText.Get(lead, f.LastName) }.Where(x => x is not null);
            var joined = string.Join(" ", parts);
            full = string.IsNullOrWhiteSpace(joined) ? null : joined;
        }
        var fullName = Need("customerFullName", null, valid: s => s.Length >= 3, fixedValue: full);
        var eid = Need("customerIdNumber", f.EmiratesId, s => Regex.Replace(s, @"[\s\-]", ""), s => Regex.IsMatch(s, @"^784\d{12}$"));
        var mobile = Need("customerMobileNumber", f.Mobile, NormaliseUaeMobile, s => Regex.IsMatch(s, @"^05\d{8}$"));
        var email = Need("customerEmail", f.Email, s => s.Trim(), NuqiText.LooksLikeEmail);

        b.Body["customerIdNumber"] = eid;
        b.Body["customerFullName"] = fullName;
        b.Body["customerMobileNumber"] = mobile;
        var customerType = Need("customerType", f.CustomerType, s => s.Trim());
        if (customerType is not null && m.CustomerTypeMap.TryGetValue(customerType, out var mappedType)) customerType = mappedType;
        b.Body["customerType"] = customerType;
        b.Body["ddaReferenceNumber"] = Need("ddaReferenceNumber", f.DdaReferenceNumber, s => s.Trim(), s => s.Length <= 80);

        // ---- payment instrument
        var paymentMethod = NuqiText.Get(lead, f.PaymentMethod) ?? m.PaymentMethod;
        b.Body["userPreferPaymentMethod"] = paymentMethod;
        if (string.Equals(paymentMethod, "Credit Card", StringComparison.OrdinalIgnoreCase))
        {
            b.Body["customerCreditCardNumber"] = Need("customerCreditCardNumber", f.CardNumber,
                s => Regex.Replace(s, @"[\s\-]", ""), s => Regex.IsMatch(s, @"^\d{13,19}$"));
            b.Body["creditCardHolderName"] = Need("creditCardHolderName", null, fixedValue: NuqiText.Get(lead, f.CardHolderName) ?? fullName);
        }
        else
        {
            b.Body["customerAccountBankName"] = Need("customerAccountBankName", f.BankName);
            b.Body["customerBankAccountType"] = Need("customerBankAccountType", null, fixedValue: NuqiText.Get(lead, f.BankAccountType) ?? m.BankAccountType);
            b.Body["customerBankAccountTitle"] = Need("customerBankAccountTitle", null, fixedValue: NuqiText.Get(lead, f.BankAccountTitle) ?? fullName);
            b.Body["customerBankAccountNumber"] = Need("customerBankAccountNumber", f.BankAccountNumber,
                s => Regex.Replace(s, @"\s", "").ToUpperInvariant(), s => Regex.IsMatch(s, @"^AE\d{21}$"));
        }

        // ---- mandate terms
        var start = ParseDate(NuqiText.Get(lead, f.CommencesOn)) ?? NuqiText.DubaiNow().Date.AddDays(m.StartOffsetDays);
        var end = ParseDate(NuqiText.Get(lead, f.ExpiresOn)) ?? start.AddYears(m.TenorYears);
        if (end <= start) b.Invalid.Add("expiresOn (must be after commencesOn)");
        b.Body["commencesOn"] = start.ToString(m.DateFormat, CultureInfo.InvariantCulture);
        b.Body["expiresOn"] = end.ToString(m.DateFormat, CultureInfo.InvariantCulture);
        b.Body["amountType"] = NuqiText.Get(lead, f.AmountType) ?? m.AmountType;
        b.Body["paymentFrequency"] = NuqiText.Get(lead, f.PaymentFrequency) ?? m.PaymentFrequency;

        decimal min = m.MinAmount;
        if (NuqiText.TryParseAmount(NuqiText.Get(lead, f.MinAmount), out var minLead) && minLead > 0) min = minLead;
        decimal max = 0;
        if (NuqiText.TryParseAmount(NuqiText.Get(lead, f.MaxAmount), out var maxLead) && maxLead > 0) max = maxLead;
        else if (m.DefaultMaxAmount > 0) max = m.DefaultMaxAmount;
        else if (_opt.AllowFallbacks && _opt.Fallbacks.TryGetValue("maxAmount", out var fbMax) &&
                 NuqiText.TryParseAmount(fbMax, out var fbMaxVal) && fbMaxVal > 0) { max = fbMaxVal; b.FallbacksUsed.Add("maxAmount"); }
        if (max <= 0) b.Missing.Add(NuqiText.IsConfigured(f.MaxAmount) ? $"maxAmount (LSQ field {f.MaxAmount})" : "maxAmount (LSQ field not configured)");
        else if (min > max) b.Invalid.Add("minAmount (greater than maxAmount)");
        b.Body["minAmount"] = min;
        b.Body["maxAmount"] = max;

        b.Body["customerEmail"] = email;
        var nid = NuqiText.Get(lead, f.CustomerNid);
        b.Body["custNid"] = nid is null ? eid : Regex.Replace(nid, @"[\s\-]", "");
        b.Body["customerIdType"] = NuqiText.Get(lead, f.CustomerIdType) ?? m.CustomerIdType;
        return b;
    }

    private static string NormaliseUaeMobile(string raw)
    {
        var s = NuqiText.NormaliseMobile(raw);
        if (s.StartsWith("+971")) return "0" + s[4..];
        if (s.StartsWith("00971")) return "0" + s[5..];
        if (s.StartsWith("971") && s.Length == 12) return "0" + s[3..];
        if (s.Length == 9 && s.StartsWith('5')) return "0" + s;
        return s;
    }

    private static DateTime? ParseDate(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        return DateTime.TryParseExact(raw.Trim(), DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d.Date
            : DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d2) ? d2.Date : null;
    }

    private static readonly HashSet<string> Sensitive = new(StringComparer.OrdinalIgnoreCase)
    {
        "customerIdNumber", "custNid", "customerBankAccountNumber", "customerCreditCardNumber", "customerMobileNumber", "customerEmail"
    };

    /// <summary>BRD §14: never log/show full EID, IBAN, card. Keeps the last 4 characters.</summary>
    private static Dictionary<string, object?> MaskBody(Dictionary<string, object?> body)
    {
        var o = new Dictionary<string, object?>();
        foreach (var kv in body)
        {
            if (kv.Value is string s && Sensitive.Contains(kv.Key))
                o[kv.Key] = kv.Key == "customerEmail" ? NuqiRedactor.MaskEmail(s) : new string('*', Math.Max(0, s.Length - 4)) + s[^Math.Min(4, s.Length)..];
            else o[kv.Key] = kv.Value;
        }
        return o;
    }

    private static string DescribeProblems(DdaBuild b)
    {
        var sb = new StringBuilder();
        if (b.Missing.Count > 0) sb.Append("Missing: ").Append(string.Join("; ", b.Missing)).Append(". ");
        if (b.Invalid.Count > 0) sb.Append("Invalid format: ").Append(string.Join("; ", b.Invalid)).Append(". ");
        return sb.ToString().Trim();
    }

    // =================================================================================
    // 5. SMALL HELPERS
    // =================================================================================
    public string Normalise(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return DdaStatus.Created;
        var s = raw.Trim().ToLowerInvariant();
        if (T.ActiveValues.Contains(s)) return DdaStatus.Active;
        if (Has(s, T.RejectedWords)) return DdaStatus.Rejected;
        if (Has(s, T.CancelledWords)) return DdaStatus.Cancelled;
        if (Has(s, T.ConsentWords)) return DdaStatus.PendingConsent;     // before Bank: "unsigned" contains "signed"
        if (Has(s, T.BankWords)) return DdaStatus.PendingBank;
        return NuqiText.Truncate(raw.Trim(), 60);   // unknown gateway status: keep the text, keep polling
    }

    private static bool Has(string s, IEnumerable<string> words) => words.Any(w => s.Contains(w, StringComparison.OrdinalIgnoreCase));

    /// <summary>The reference is typed by the Maker – keep it safe for use in a file name.</summary>
    private static string SafeName(string? s) => Regex.Replace(s ?? "dda", @"[^A-Za-z0-9_\-]", "_");

    private static bool IsRetryable(DdaRequest r) =>
        DdaStatus.Retryable.Contains(r.Status) ||
        (r.Status == DdaStatus.Creating && r.CreatedAtUtc < DateTime.UtcNow.AddMinutes(-10));   // crashed mid-create

    /// <summary>Gateway PDF endpoints may return raw PDF bytes or JSON holding base64 – accept both.</summary>
    private static async Task<byte[]?> FetchPdfAsync(Func<Task<DdaHttpResult>> call)
    {
        var r = await call();
        if (!r.Ok || r.Bytes.Length < 100) return null;
        if (r.Bytes[0] == '%' && r.Bytes[1] == 'P' && r.Bytes[2] == 'D' && r.Bytes[3] == 'F') return r.Bytes;

        var root = NuqiJson.ParseOrEmpty(r.Text);
        var b64 = NuqiJson.FindFirstString(root, "pdf", "content", "fileContent", "data", "base64", "file", "document");
        if (string.IsNullOrWhiteSpace(b64)) return null;
        try
        {
            var bytes = Convert.FromBase64String(b64);
            return bytes.Length > 4 && bytes[0] == '%' && bytes[1] == 'P' ? bytes : null;
        }
        catch (FormatException) { return null; }
    }

    /// <summary>UAE PASS signing link for a ddarId, from Dda:SigningUrlTemplate ({ddarId} placeholder). Null if not configured.</summary>
    private string? BuildSigningUrl(string? ddarId)
    {
        var t = _opt.SigningUrlTemplate;
        if (string.IsNullOrWhiteSpace(ddarId) || string.IsNullOrWhiteSpace(t)) return null;
        return t.Replace("{ddarId}", Uri.EscapeDataString(ddarId), StringComparison.OrdinalIgnoreCase);
    }

    private async Task SafeNoteAsync(string leadId, string note)
    {
        try { await _lead.CreateNoteAsync(leadId, note); }
        catch (Exception ex) { _log.LogWarning(ex, "DDA lead note failed for {LeadId}", leadId); }
    }

    private async Task SafeWriteFieldsAsync(DdaRequest row)
    {
        try
        {
            var f = _opt.LsqFields;
            await _lead.UpdateLeadFieldsAsync(row.LeadId, new Dictionary<string, string?>
            {
                [f.WriteDdaStatus] = row.Status,
                [f.WriteDdaId] = row.DdaId,
                [f.WriteDdarId] = row.DdarId,
                [f.WriteSigningUrl] = BuildSigningUrl(row.DdarId),
                [f.WriteDdaReference] = row.ActiveReference
            });
        }
        catch (Exception ex) { _log.LogWarning(ex, "DDA lead field write-back failed for {LeadId}", row.LeadId); }
    }

    private async Task<NuqiResult> IgnoredAsync(string corr, string? leadId, string reason, string source)
    {
        _log.LogInformation("DDA IGNORED [{Corr}] lead={Lead}: {Reason}", corr, leadId, reason);
        await _audit.WriteAsync(corr, leadId, "DDA_IGNORED", Svc, source, NuqiOutcome.Ignored, detail: reason);
        return NuqiResult.Ok(new { status = "IGNORED", reason });
    }
}
