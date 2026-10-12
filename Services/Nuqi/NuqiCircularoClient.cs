using System.Text;
using System.Text.Json;
using CircularoIntegration.Api.Config;
using CircularoIntegration.Api.Models;
using Microsoft.Extensions.Options;

namespace CircularoIntegration.Api.Services.Nuqi;

public record NuqiSigner(string Role, string Email, string Message);

/// <summary>One Circularo signature box. X/Y/W/H are page percentages (0..1). Page is 1-based on the FINAL pdf.</summary>
public record NuqiSignatureBox(string Role, int Page, double X, double Y, double W, double H, bool Timestamp = false);

public enum NuqiDocState { Unknown, InProgress, Completed, Declined, Expired }

/// <summary>
/// New Circularo calls for NUQI. Existing CircularoService is still used for
/// upload / create document / get document / download file (already proven in UAT).
///
/// Two signers on one document (customer + Mawarid Business) use the documented share API WITHOUT "sequential":
///   POST /api/v1/share  { data:[{shareTo:customer},{shareTo:mawarid}], signatureFields:[...] }
/// Both receive the document at the same time and may sign in any order (see ShareToSignersAsync).
/// </summary>
public class NuqiCircularoClient
{
    private readonly HttpClient _http;
    private readonly IConfiguration _cfg;
    private readonly NuqiApprovedOptions _opt;
    private readonly ILogger<NuqiCircularoClient> _log;

    public NuqiCircularoClient(HttpClient http, IConfiguration cfg, IOptions<NuqiApprovedOptions> opt, ILogger<NuqiCircularoClient> log)
    {
        _http = http;
        _cfg = cfg;
        _opt = opt.Value;
        _log = log;
    }

    private string BaseUrl => _cfg["Circularo:BaseUrl"]!.TrimEnd('/');
    private string Token => _cfg["Circularo:ApiKey"]!;
    private string DocType => _cfg["Circularo:DocumentType"] ?? "d_default";

    /// <summary>
    /// Shares ONE Circularo document with ALL signers at the same time (customer + Mawarid Business),
    /// one signature box per signer. NO "sequential" flag: the Circularo sandbox rejects every share that combines
    /// "sequential": true with signatureFields (400 VALIDATION:REFERENCE_NOT_FOUND "User for sign field not found."),
    /// and a second signer cannot be added later (409 DOCUMENT:STATE_CONFLICT once the first signer has signed).
    /// DECIDED (8 Oct 2026): both signers receive the document at the same time and may sign in any order;
    /// a document counts as signed only when BOTH have signed.
    /// Returns Circularo's raw response body.
    /// </summary>
    public async Task<string> ShareToSignersAsync(
        string documentId, IReadOnlyList<NuqiSigner> signers, IReadOnlyList<NuqiSignatureBox> boxes,
        DateTime validUntilUtc, CancellationToken ct = default)
    {
        var url = $"{BaseUrl}/api/v1/share?token={Uri.EscapeDataString(Token)}";

        // Circularo user names are lower-case: use ONE normalised (trimmed, lower-case) e-mail per signer in the whole request.
        var list = signers
            .Select(s => s with { Email = (s.Email ?? "").Trim().ToLowerInvariant() })
            .ToList();

        var data = list.Select(s => new
        {
            sharePurpose = "sign",
            shareTo = s.Email,
            message = s.Message,
            validUntil = validUntilUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ")
        }).ToArray();

        var signatureFields = boxes.Select(b =>
        {
            var signer = list.FirstOrDefault(s => s.Role == b.Role)
                         ?? throw new InvalidOperationException($"No signer for role '{b.Role}' on document {documentId}");
            return new
            {
                user = new[] { signer.Email },
                timestamp = b.Timestamp,
                required = true,
                type = "signature",
                pages = new[] { b.Page },
                position = new { percentX = b.X, percentY = b.Y, percentWidth = b.W, percentHeight = b.H }
            };
        }).ToArray();

        var payload = new
        {
            objectType = "document",
            type = DocType,
            id = documentId,
            data,
            signatureFields
        };

        var json = JsonSerializer.Serialize(payload);
        _log.LogInformation("NUQI Circularo share doc {DocId} → {Count} signers, {Boxes} boxes, validUntil {Until:o}",
            documentId, list.Count, boxes.Count, validUntilUtc);
        // payload holds e-mails and box positions only (the token is in the URL, not in the body)
        _log.LogInformation("NUQI Circularo share payload: {Json}", json);

        var res = await _http.PostAsync(url, new StringContent(json, Encoding.UTF8, "application/json"), ct);
        var body = await res.Content.ReadAsStringAsync(ct);
        _log.LogInformation("NUQI Circularo share response {Status}: {Body}", (int)res.StatusCode, NuqiRedactor.Redact(NuqiText.Truncate(body, 800)));

        if (!res.IsSuccessStatusCode)
            throw new DownstreamException((int)res.StatusCode, body);
        return body;
    }

    /// <summary>
    /// Sends the WHOLE package (T&amp;C + Murabaha Documents – 2 separate documents) to both signers at once.
    /// DECIDED (5 Oct 2026): one Circularo email per document is acceptable (same as existing flows).
    /// </summary>
    public async Task SharePackageAsync(
        IReadOnlyList<(string DocumentId, IReadOnlyList<NuqiSignatureBox> Boxes)> documents,
        IReadOnlyList<NuqiSigner> signers, DateTime validUntilUtc, CancellationToken ct = default)
    {
        _log.LogInformation("NUQI: sharing {Count} documents of the package with {Signers} signers (one Circularo email per document and signer)",
            documents.Count, signers.Count);

        foreach (var (documentId, boxes) in documents)
            await ShareToSignersAsync(documentId, signers, boxes, validUntilUtc, ct);
    }

    /// <summary>
    /// Cancels / revokes an unsigned document (BRD §8: amount change, backward move, decline).
    /// The cancel endpoint is NOT in the Postman collection → configured via
    /// Circularo:CancelUrlTemplate (e.g. "/api/v1/documents/{id}/cancel") and Circularo:CancelHttpMethod.
    /// Returns false when not configured – caller then records "cancel in Circularo manually" in LSQ.
    /// >>> CONFIRM endpoint with Harish <<<
    /// </summary>
    public async Task<bool> TryCancelAsync(string documentId, string reason, CancellationToken ct = default)
    {
        var template = _cfg["Circularo:CancelUrlTemplate"];
        if (string.IsNullOrWhiteSpace(template))
        {
            _log.LogWarning("NUQI Circularo cancel not configured (Circularo:CancelUrlTemplate) – doc {DocId} must be revoked manually", documentId);
            return false;
        }

        try
        {
            var url = $"{BaseUrl}{template.Replace("{id}", Uri.EscapeDataString(documentId))}";
            url += (url.Contains('?') ? "&" : "?") + $"token={Uri.EscapeDataString(Token)}";
            var method = new HttpMethod((_cfg["Circularo:CancelHttpMethod"] ?? "PUT").ToUpperInvariant());
            using var req = new HttpRequestMessage(method, url)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { id = documentId, reason }), Encoding.UTF8, "application/json")
            };
            var res = await _http.SendAsync(req, ct);
            var body = await res.Content.ReadAsStringAsync(ct);
            _log.LogInformation("NUQI Circularo cancel {DocId} → {Status}: {Body}", documentId, (int)res.StatusCode, NuqiText.Truncate(body, 500));
            return res.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "NUQI Circularo cancel failed for {DocId}", documentId);
            return false;
        }
    }

    /// <summary>
    /// Completion certificate / audit trail PDF (DOC-10). Endpoint configured via
    /// Circularo:AuditTrailUrlTemplate (e.g. "/api/v1/documents/{id}/auditTrail"). Returns null when not configured.
    /// >>> CONFIRM endpoint with Harish <<<
    /// </summary>
    public async Task<byte[]?> TryDownloadCertificateAsync(string documentId, CancellationToken ct = default)
    {
        var template = _cfg["Circularo:AuditTrailUrlTemplate"];
        if (string.IsNullOrWhiteSpace(template)) return null;

        try
        {
            var url = $"{BaseUrl}{template.Replace("{id}", Uri.EscapeDataString(documentId))}";
            url += (url.Contains('?') ? "&" : "?") + $"token={Uri.EscapeDataString(Token)}";
            var res = await _http.GetAsync(url, ct);
            if (!res.IsSuccessStatusCode)
            {
                _log.LogWarning("NUQI Circularo certificate {DocId} → {Status}", documentId, (int)res.StatusCode);
                return null;
            }
            var bytes = await res.Content.ReadAsByteArrayAsync(ct);
            return bytes.Length > 0 ? bytes : null;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "NUQI Circularo certificate download failed for {DocId}", documentId);
            return null;
        }
    }

    /// <summary>
    /// Best-effort reading of a document's state from GET /api/v1/documents/{id} (used by reconciliation).
    /// Looks at the usual status-like properties and compares with configured values.
    /// </summary>
    public NuqiDocState InferState(JsonDocument detail)
    {
        var root = detail.RootElement;
        if (root.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array && results.GetArrayLength() > 0)
            root = results[0];

        var candidates = new List<string>();
        foreach (var name in new[] { "workflowStatus", "documentStatus", "workflowState", "state", "status", "signStatus" })
        {
            var v = NuqiJson.FindFirstString(root, name);
            if (!string.IsNullOrWhiteSpace(v)) candidates.Add(v);
        }

        _log.LogInformation("NUQI Circularo state candidates: [{Values}]", string.Join(", ", candidates));

        // exact (case-insensitive) match only – "unsigned".Contains("signed") must NOT count as completed
        bool Any(string[] list) => candidates.Any(c => list.Any(x => string.Equals(c.Trim(), x, StringComparison.OrdinalIgnoreCase)));
        var ev = _opt.CircularoEvents;
        if (Any(ev.DocStateDeclined)) return NuqiDocState.Declined;
        if (Any(ev.DocStateExpired)) return NuqiDocState.Expired;
        if (Any(ev.DocStateCompleted)) return NuqiDocState.Completed;
        return candidates.Count > 0 ? NuqiDocState.InProgress : NuqiDocState.Unknown;
    }
}
