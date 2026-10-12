using System.Text;
using System.Text.Json;
using CircularoIntegration.Api.Config;
using CircularoIntegration.Api.Models;
using Microsoft.Extensions.Options;

namespace CircularoIntegration.Api.Services.Nuqi;

/// <summary>
/// Extra LeadSquared calls needed by the NUQI flow. Kept in a NEW typed client so the
/// existing LeadSquaredService (used by the 4 live flows) is not modified.
/// Uses the same LeadSquared:BaseUrl / AccessKey / SecretKey settings.
/// Existing LeadSquaredService is still used for: AttachDocumentAsync, UpdateLeadStageAsync.
/// </summary>
public class NuqiLeadClient
{
    private readonly HttpClient _http;
    private readonly IConfiguration _cfg;
    private readonly NuqiApprovedOptions _opt;
    private readonly ILogger<NuqiLeadClient> _log;

    public NuqiLeadClient(HttpClient http, IConfiguration cfg, IOptions<NuqiApprovedOptions> opt, ILogger<NuqiLeadClient> log)
    {
        _http = http;
        _cfg = cfg;
        _opt = opt.Value;
        _log = log;
    }

    private string BaseUrl => _cfg["LeadSquared:BaseUrl"]!.TrimEnd('/');
    private string AccessKey => _cfg["LeadSquared:AccessKey"]!;
    private string SecretKey => _cfg["LeadSquared:SecretKey"]!;
    private string Keys => $"accessKey={Uri.EscapeDataString(AccessKey)}&secretKey={Uri.EscapeDataString(SecretKey)}";

    /// <summary>
    /// Leads.GetById → every top-level attribute as string (numbers/bools converted),
    /// case-insensitive keys. Unlike GetLeadFieldsAsync this does not drop numeric values,
    /// which matters for the Credit Approved Amount.
    /// </summary>
    public async Task<Dictionary<string, string?>> GetLeadAsync(string leadId, CancellationToken ct = default)
    {
        var url = $"{BaseUrl}/v2/LeadManagement.svc/Leads.GetById?{Keys}&id={Uri.EscapeDataString(leadId)}";
        var res = await _http.GetAsync(url, ct);
        var body = await res.Content.ReadAsStringAsync(ct);
        _log.LogInformation("NUQI LSQ Leads.GetById {LeadId} → {Status}", leadId, (int)res.StatusCode);

        if (!res.IsSuccessStatusCode)
            throw new DownstreamException((int)res.StatusCode, body);

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var lead = root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0 ? root[0] : root;

        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (lead.ValueKind != JsonValueKind.Object)
        {
            _log.LogWarning("NUQI LSQ GetById for {LeadId} returned unexpected shape", leadId);
            return result;
        }

        foreach (var p in lead.EnumerateObject())
        {
            result[p.Name] = p.Value.ValueKind switch
            {
                JsonValueKind.String => p.Value.GetString(),
                JsonValueKind.Number => p.Value.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => null
            };
        }
        return result;
    }

    /// <summary>Lead.Update with several attributes in one call. Skips unconfigured (TODO/empty) field names.</summary>
    public async Task UpdateLeadFieldsAsync(string leadId, IDictionary<string, string?> fields, CancellationToken ct = default)
    {
        var attrs = fields
            .Where(kv => NuqiText.IsConfigured(kv.Key) && kv.Value is not null)
            .Select(kv => new { Attribute = kv.Key, Value = kv.Value })
            .ToList();

        if (attrs.Count == 0)
        {
            _log.LogDebug("NUQI LSQ write-back skipped for {LeadId}: no configured fields", leadId);
            return;
        }

        var url = $"{BaseUrl}/v2/LeadManagement.svc/Lead.Update?{Keys}&leadId={Uri.EscapeDataString(leadId)}";
        var json = JsonSerializer.Serialize(attrs);
        var res = await _http.PostAsync(url, new StringContent(json, Encoding.UTF8, "application/json"), ct);
        var body = await res.Content.ReadAsStringAsync(ct);
        _log.LogInformation("NUQI LSQ Lead.Update {LeadId} fields [{Fields}] → {Status}",
            leadId, string.Join(",", attrs.Select(a => a.Attribute)), (int)res.StatusCode);

        if (!res.IsSuccessStatusCode)
            throw new DownstreamException((int)res.StatusCode, body);
    }

    /// <summary>Plain text note on the lead (no attachment).</summary>
    public async Task CreateNoteAsync(string leadId, string note, CancellationToken ct = default)
    {
        var url = $"{BaseUrl}/v2/LeadManagement.svc/CreateNote?{Keys}";
        var json = JsonSerializer.Serialize(new { RelatedProspectId = leadId, Note = NuqiText.Truncate(note, 3800) });
        var res = await _http.PostAsync(url, new StringContent(json, Encoding.UTF8, "application/json"), ct);
        var body = await res.Content.ReadAsStringAsync(ct);
        _log.LogInformation("NUQI LSQ CreateNote {LeadId} → {Status}", leadId, (int)res.StatusCode);
        if (!res.IsSuccessStatusCode)
            throw new DownstreamException((int)res.StatusCode, body);
    }

    /// <summary>
    /// Exception task for Business/Operations (BRD §8, §15). If the Task API is disabled or fails,
    /// falls back to a lead note prefixed with [ACTION REQUIRED] so the exception is never lost.
    /// >>> VERIFY the Task.Create payload on the Mawarid LSQ tenant before enabling NuqiApproved:LsqTask:Enabled. <<<
    /// </summary>
    public async Task RaiseExceptionAsync(string leadId, string subject, string description, string? ownerEmail = null, CancellationToken ct = default)
    {
        var t = _opt.LsqTask;
        if (t.Enabled)
        {
            try
            {
                var url = $"{BaseUrl}{t.CreateUrlTemplate}?{Keys}";
                var payload = new
                {
                    Name = NuqiText.Truncate(subject, 200),
                    Description = NuqiText.Truncate(description, 2000),
                    RelatedEntity = 1,
                    RelatedEntityId = leadId,
                    TaskType = new { Name = t.TaskTypeName },
                    DueDate = DateTime.UtcNow.AddHours(t.DueInHours).ToString("yyyy-MM-dd HH:mm:ss"),
                    OwnerEmailAddress = ownerEmail ?? t.DefaultOwnerEmail
                };
                var res = await _http.PostAsync(url, new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"), ct);
                var body = await res.Content.ReadAsStringAsync(ct);
                _log.LogInformation("NUQI LSQ Task.Create {LeadId} → {Status}", leadId, (int)res.StatusCode);
                if (res.IsSuccessStatusCode) return;
                _log.LogWarning("NUQI LSQ Task.Create failed ({Status}) – falling back to note. Body: {Body}",
                    (int)res.StatusCode, NuqiText.Truncate(body, 500));
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "NUQI LSQ Task.Create threw – falling back to note for {LeadId}", leadId);
            }
        }

        await CreateNoteAsync(leadId, $"[ACTION REQUIRED] {subject}\n{description}", ct);
    }
}
