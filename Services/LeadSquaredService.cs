using System.Text;
using System.Text.Json;
using CircularoIntegration.Api.Models;


namespace CircularoIntegration.Api.Services;

/// <summary>
/// Wraps the LeadSquared v2 REST APIs we consume.
/// Auth: accessKey + secretKey as query-string parameters (LSQ convention).
/// Base URL differs per region – for UAE/Gulf tenants confirm the API host
/// from LeadSquared Settings → API and Webhooks (e.g. https://api-in21.leadsquared.com).
/// </summary>
public class LeadSquaredService
{
    private readonly HttpClient _http;
    private readonly IConfiguration _cfg;
    private readonly ILogger<LeadSquaredService> _log;

    public LeadSquaredService(HttpClient http, IConfiguration cfg, ILogger<LeadSquaredService> log)
    {
        _http = http;
        _cfg = cfg;
        _log = log;
    }

    private string BaseUrl   => _cfg["LeadSquared:BaseUrl"]!.TrimEnd('/');
    private string AccessKey => _cfg["LeadSquared:AccessKey"]!;
    private string SecretKey => _cfg["LeadSquared:SecretKey"]!;
    private string Keys      => $"accessKey={AccessKey}&secretKey={SecretKey}";

    /// <summary>
    /// GET Leads.GetById – returns the lead attributes; we extract the email.
    /// The attribute name holding the email is configurable
    /// (LeadSquared:EmailField, default "EmailAddress") because Siddhesh
    /// will confirm the exact field used on this tenant.
    /// </summary>
    public async Task<(string? Email, string? FirstName)> GetLeadEmailAsync(string leadId)
    {
        var url = $"{BaseUrl}/v2/LeadManagement.svc/Leads.GetById?{Keys}&id={Uri.EscapeDataString(leadId)}";
        _log.LogInformation("LSQ Leads.GetById URL → {url}", url);
        var res = await _http.GetAsync(url);
        var body = await res.Content.ReadAsStringAsync();
        _log.LogInformation("LSQ Leads.GetById {LeadId} → {Status}", leadId, (int)res.StatusCode);

        if (!res.IsSuccessStatusCode)
            throw new DownstreamException((int)res.StatusCode, body);

        var emailField = _cfg["LeadSquared:EmailField"] ?? "EmailAddress";

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        // Response may be a single object OR an array with one object
        var lead = root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0
            ? root[0]
            : root;

        if (lead.ValueKind == JsonValueKind.Object)
        {
            var email = lead.TryGetProperty(emailField, out var e) && e.ValueKind == JsonValueKind.String
                ? e.GetString() : null;
            var name = lead.TryGetProperty("FirstName", out var n) && n.ValueKind == JsonValueKind.String
                ? n.GetString() : null;

            if (email is not null)
                return (email, name);
        }

        _log.LogWarning("Email field '{Field}' not found in GetById response for lead {LeadId}", emailField, leadId);
       

        return (null, null);
    }


    /// <summary>
    /// Fetches arbitrary lead attributes via Leads.GetById, returned as
    /// schemaName → value. Used by the Murabaha Stage 1 token builder.
    /// </summary>
    public async Task<Dictionary<string, string?>> GetLeadFieldsAsync(string leadId, string[] schemaNames)
    {
        var url = $"{BaseUrl}/v2/LeadManagement.svc/Leads.GetById?{Keys}&id={Uri.EscapeDataString(leadId)}";
        _log.LogInformation("LSQ Leads.GetById (fields) URL → {url}", url);

        var res = await _http.GetAsync(url);
        var body = await res.Content.ReadAsStringAsync();
        _log.LogInformation("LSQ Leads.GetById (fields) {LeadId} → {Status}", leadId, (int)res.StatusCode);

        if (!res.IsSuccessStatusCode)
            throw new DownstreamException((int)res.StatusCode, body);

        using var doc = System.Text.Json.JsonDocument.Parse(body);
        var root = doc.RootElement;
        var lead = root.ValueKind == System.Text.Json.JsonValueKind.Array && root.GetArrayLength() > 0
            ? root[0]
            : root;

        var result = new Dictionary<string, string?>();
        foreach (var name in schemaNames)
        {
            result[name] =
                lead.ValueKind == System.Text.Json.JsonValueKind.Object &&
                lead.TryGetProperty(name, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String
                    ? v.GetString()
                    : null;

            if (result[name] is null)
                _log.LogWarning("Murabaha field '{Field}' not found (or null) in GetById response for lead {LeadId}", name, leadId);
        }
        return result;
    }

    /// <summary>POST Lead.Update – sets the lead stage (attribute "ProspectStage" by default).</summary>
    public async Task UpdateLeadStageAsync(string leadId, string newStage)
    {
        var stageField = _cfg["LeadSquared:StageField"] ?? "ProspectStage";
        var url = $"{BaseUrl}/v2/LeadManagement.svc/Lead.Update?{Keys}&leadId={Uri.EscapeDataString(leadId)}";
        _log.LogInformation("LSQ Leads UpdateLeadStageAsync URL → {url}", url);
        var payload = JsonSerializer.Serialize(new[]
        {
            new { Attribute = stageField, Value = newStage }
        });

        var res = await _http.PostAsync(url, new StringContent(payload, Encoding.UTF8, "application/json"));
        var body = await res.Content.ReadAsStringAsync();
        _log.LogInformation("LSQ Lead.Update {LeadId} → stage '{Stage}' → {Status} {Body}",
            leadId, newStage, (int)res.StatusCode, body);

        if (!res.IsSuccessStatusCode)
            throw new DownstreamException((int)res.StatusCode, body);
    }

    /// <summary>
    /// Attaches the signed PDF to the lead.
    /// NOTE: verify the exact endpoint on your tenant (LSQ docs → Documents/Files APIs).
    /// The endpoint is configurable so it can be corrected without a code change.
    /// If LeadSquared:AttachEnabled=false, we log and skip – keeps the demo alive
    /// while the attach API is being confirmed.
    /// </summary>
    public async Task AttachDocumentAsync(string leadId, string fileName, byte[] pdfBytes)
    {
        if (!bool.TryParse(_cfg["LeadSquared:AttachEnabled"], out var enabled) || !enabled)
        {
            _log.LogWarning("Attach skipped (LeadSquared:AttachEnabled=false). Lead {LeadId}, file {File}", leadId, fileName);
            return;
        }

        // var template = _cfg["LeadSquared:AttachUrlTemplate"]
        //     ?? "/v2/LeadManagement.svc/Lead.Attachment.Create"; // VERIFY against LSQ docs
        // var url = $"{BaseUrl}{template}?{Keys}&leadId={Uri.EscapeDataString(leadId)}";
        // _log.LogInformation("LSQ AttachDocumentAsync url → {url} ", url);
        // using var form = new MultipartFormDataContent();
        // var file = new ByteArrayContent(pdfBytes);
        // file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/pdf");
        // form.Add(file, "file", fileName);

        // var res = await _http.PostAsync(url, form);
        // var body = await res.Content.ReadAsStringAsync();
        // _log.LogInformation("LSQ Attach {LeadId} {File} → {Status} {Body}", leadId, fileName, (int)res.StatusCode, body);

        // if (!res.IsSuccessStatusCode)
        //     throw new DownstreamException((int)res.StatusCode, body);


        ////////////
       

        // Step 1: upload the actual signed PDF bytes to LSQ file storage
        var uploadUrl = _cfg["LeadSquared:FileUploadUrl"] ?? "https://files-in21.leadsquared.com/File/Upload";
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(pdfBytes);
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/pdf");
        form.Add(file, "uploadFiles", fileName);
        form.Add(new StringContent("3"), "FileType");           // 3 = Lead Attachment
        form.Add(new StringContent(AccessKey), "AccessKey");
        form.Add(new StringContent(SecretKey), "SecretKey");
        form.Add(new StringContent("0"), "FileStorageType");
        form.Add(new StringContent("false"), "EnableResize");
        // form.Add(new StringContent(leadId), "ProspectId");       // confirmed correct key
        form.Add(new StringContent(leadId), "LeadId");       // confirmed correct key

        form.Add(new StringContent("0"), "Entity");
        form.Add(new StringContent("0"), "StorageVersion");

        var uploadRes = await _http.PostAsync(uploadUrl, form);
        var uploadBody = await uploadRes.Content.ReadAsStringAsync();
        _log.LogInformation("LSQ File/Upload {File} → {Status}: {Body}", fileName, (int)uploadRes.StatusCode, Truncate(uploadBody));
        if (!uploadRes.IsSuccessStatusCode)
            throw new DownstreamException((int)uploadRes.StatusCode, uploadBody);

        using var uploadDoc = JsonDocument.Parse(uploadBody);
        var relativeFilePath = uploadDoc.RootElement.GetProperty("relativeFilePath").GetString();

        // Step 2: attach it visibly to the lead via a note
        var noteUrl = $"{BaseUrl}/v2/LeadManagement.svc/CreateNote?accessKey={Uri.EscapeDataString(AccessKey)}&secretKey={Uri.EscapeDataString(SecretKey)}";
        var notePayload = new
        {
            RelatedProspectId = leadId,
            Note = "Signed document attached",
            AttachmentName = relativeFilePath
        };
        var noteJson = JsonSerializer.Serialize(notePayload);
        var noteRes = await _http.PostAsync(noteUrl, new StringContent(noteJson, Encoding.UTF8, "application/json"));
        var noteBody = await noteRes.Content.ReadAsStringAsync();
        _log.LogInformation("LSQ CreateNote {LeadId} → {Status}: {Body}", leadId, (int)noteRes.StatusCode, Truncate(noteBody));
        if (!noteRes.IsSuccessStatusCode)
            throw new DownstreamException((int)noteRes.StatusCode, noteBody);




    }

    /// <summary>Safely reads a string property from a JsonElement, or null.</summary>
    private static string? TryStr(JsonElement el, string name)
    {
        if (el.ValueKind == JsonValueKind.Object &&
            el.TryGetProperty(name, out var prop) &&
            prop.ValueKind == JsonValueKind.String)
        {
            return prop.GetString();
        }
        return null;
    }

    public async Task<LeadOfferDetails> GetLeadOfferDetailsAsync(string leadId)
    {
        var url = $"{BaseUrl}/v2/LeadManagement.svc/Leads.GetById?{Keys}&id={Uri.EscapeDataString(leadId)}";
        _log.LogInformation("LSQ Leads.GetById (offer details) URL → {url}", url);

        var res = await _http.GetAsync(url);
        var body = await res.Content.ReadAsStringAsync();
        _log.LogInformation("LSQ Leads.GetById {LeadId} → {Status}", leadId, (int)res.StatusCode);

        if (!res.IsSuccessStatusCode)
            throw new DownstreamException((int)res.StatusCode, body);

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var lead = root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0 ? root[0] : root;

        if (lead.ValueKind != JsonValueKind.Object)
        {
            _log.LogWarning("Unexpected Leads.GetById shape for lead {LeadId}", leadId);
            return new LeadOfferDetails();
        }

        string? Get(string field) =>
            lead.TryGetProperty(field, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        const string section = "LeadSquared:OfferLetterFields:";
        var details = new LeadOfferDetails
        {
            CustomerName = Get(_cfg[$"{section}CustomerNameField"] ?? "FirstName"),
            AuthorizedSignatoryEmail = Get(_cfg[$"{section}AuthorizedSignatoryEmailField"] ?? "EmailAddress"),
            FundedFacilityLimit = Get(_cfg[$"{section}FundedFacilityLimitField"] ?? "FundedFacilityLimit"),
            RequestedFinanceAmount = Get(_cfg[$"{section}RequestedFinanceAmountField"] ?? "RequestedFinanceAmount"),
            Purpose = Get(_cfg[$"{section}PurposeField"] ?? "Purpose"),
            Tenor = Get(_cfg[$"{section}TenorField"] ?? "Tenor"),
            ProfitRate = Get(_cfg[$"{section}ProfitRateField"] ?? "ProfitRate"),
            Frequency = Get(_cfg[$"{section}FrequencyField"] ?? "Frequency"),
            PaymentTerms = Get(_cfg[$"{section}PaymentTermsField"] ?? "PaymentTerms")
        };

        if (!details.IsComplete)
            _log.LogWarning("Lead {LeadId} offer details incomplete: {@Details}", leadId, details);

        return details;
    }

    /// <summary>
    /// Dumps every lead field's SchemaName + DisplayName. One-time diagnostic
    /// to resolve the mx_Custom_N mappings — not called by the webhook flow.
    /// </summary>
    public async Task<List<(string SchemaName, string DisplayName)>> GetAllFieldsMetadataAsync()
    {
        var url = $"{BaseUrl}/v2/LeadManagement.svc/LeadsMetaData.Get?{Keys}";
        _log.LogInformation("LSQ LeadsMetaData.Get URL → {url}", url);

        var res = await _http.GetAsync(url);
        var body = await res.Content.ReadAsStringAsync();
        if (!res.IsSuccessStatusCode)
            throw new DownstreamException((int)res.StatusCode, body);

        using var doc = JsonDocument.Parse(body);
        var result = new List<(string, string)>();

        foreach (var el in doc.RootElement.EnumerateArray())
        {
            var schema = el.TryGetProperty("SchemaName", out var s) ? s.GetString() : null;
            var display = el.TryGetProperty("DisplayName", out var d) ? d.GetString() : null;
            if (!string.IsNullOrEmpty(schema))
                result.Add((schema, display ?? string.Empty));
        }

        return result;
    }
   
    private static string Truncate(string s, int max = 800) => s.Length <= max ? s : s[..max] + "…";

}
