using System.Text;
using System.Text.Json;
using CircularoIntegration.Api.Models;

namespace CircularoIntegration.Api.Services;

/// <summary>
/// Wraps the Circularo REST APIs (per the "Circularo Developers Documentation"
/// Postman collection). Auth: API key sent as query parameter "token".
/// Sandbox base URL: https://app.sandbox.circularo.com
/// </summary>
public class CircularoService
{
    private readonly HttpClient _http;
    private readonly IConfiguration _cfg;
    private readonly ILogger<CircularoService> _log;

    public CircularoService(HttpClient http, IConfiguration cfg, ILogger<CircularoService> log)
    {
        _http = http;
        _cfg = cfg;
        _log = log;
    }

    private string BaseUrl => _cfg["Circularo:BaseUrl"]!.TrimEnd('/');
    private string Token   => _cfg["Circularo:ApiKey"]!;

    /// <summary>
    /// POST /api/v1/share with sharePurpose "sign" – sends the document to the
    /// customer's email for e-signature. Returns the raw response JSON so the
    /// caller can store any reference ids Circularo returns.
    /// </summary>
    public async Task<string> SendForSignatureAsync(
     string documentId, string recipientEmail, string? message = null, int signaturePage = 1)
    {
        var url = $"{BaseUrl}/api/v1/share?token={Uri.EscapeDataString(Token)}";
        var payload = new
        {
            objectType = "document",
            type = _cfg["Circularo:DocumentType"] ?? "d_default",
            id = documentId,
            data = new object[] {
            new {
                sharePurpose = "sign",
                shareTo = recipientEmail,
                message = message ?? "Please sign the attached document.",
                validUntil = DateTime.UtcNow.AddDays(7).ToString("yyyy-MM-ddTHH:mm:ss.fffZ")
            }
        },
            signatureFields = new object[] {
            new {
                user = new[] { recipientEmail },
                timestamp = false,
                required = true,
                type = "signature",
                pages = new[] { signaturePage },   
                position = new { percentX = 0.5, percentY = 0.8, percentWidth = 0.3, percentHeight = 0.08 }
            }
        }
        };
        _log.LogInformation("Circularo SendForSignatureAsync URL → {url} ", url);
        var json = JsonSerializer.Serialize(payload);
        _log.LogInformation("Circularo SendForSignatureAsync Payload → {payload} ", payload);
        _log.LogInformation("Circularo share → doc {DocId} to {Email}", documentId, recipientEmail);

        var res = await _http.PostAsync(url, new StringContent(json, Encoding.UTF8, "application/json"));
        var body = await res.Content.ReadAsStringAsync();
        _log.LogInformation("Circularo share response {Status}: {Body}", (int)res.StatusCode, Truncate(body));

        if (!res.IsSuccessStatusCode)
            throw new DownstreamException((int)res.StatusCode, body);

        return body;
    }

    /// <summary>GET /api/v1/documents/{id} – full document detail incl. status and file hashes.</summary>
    public async Task<JsonDocument> GetDocumentAsync(string documentId)
    {
        var url = $"{BaseUrl}/api/v1/documents/{Uri.EscapeDataString(documentId)}?token={Uri.EscapeDataString(Token)}";

        _log.LogInformation("Circularo GetDocumentAsync URL → {url}", url);

        var res = await _http.GetAsync(url);
        var body = await res.Content.ReadAsStringAsync();
        _log.LogInformation("Circularo getDocument {DocId} → {Status}", documentId, (int)res.StatusCode);

        if (!res.IsSuccessStatusCode)
            throw new DownstreamException((int)res.StatusCode, body);
        return JsonDocument.Parse(body);
    }
    /// <summary>POST /api/v1/documents – creates a fresh document from a template file hash.
    /// Returns the new document id. Required because Circularo documents are single-use per signing.</summary>
    public async Task<string> CreateDocumentFromFileAsync(string fileHash, string title)
    {
        var url = $"{BaseUrl}/api/v1/documents?token={Uri.EscapeDataString(Token)}";

        // var payload = new
        // {
        //     definitionType = "d_default",
        //     workflow = "wf_archive",
        //     document = new
        //     {
        //         documentTitle = title,
        //         pdfFile = fileHash        
        //     }
        // };
        var payload = new
        {
            body = new
            {
                documentType = _cfg["Circularo:DocumentType"] ?? "d_default",
                documentTitle = title,
                pdfFile = new { content = fileHash }
            },
            definitionType = "ext",
            workflow = "wf_archive"
        };

        var json = JsonSerializer.Serialize(payload);
        _log.LogInformation("Circularo createDocument request body → {Json}", json);
        var res = await _http.PostAsync(url, new StringContent(json, Encoding.UTF8, "application/json"));
        var body = await res.Content.ReadAsStringAsync();
        _log.LogInformation("Circularo createDocument from {Hash} → {Status}: {Body}", fileHash, (int)res.StatusCode, Truncate(body));

        if (!res.IsSuccessStatusCode)
            throw new DownstreamException((int)res.StatusCode, body);
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        string? newId =
            TryGetId(root) ??
            (root.TryGetProperty("document", out var d) ? TryGetId(d) : null) ??
            (root.TryGetProperty("results", out var r) && r.ValueKind == JsonValueKind.Array && r.GetArrayLength() > 0
                ? TryGetId(r[0]) : null);

        if (newId is null)
            throw new DownstreamException(500, $"Could not find document id in Circularo create response: {Truncate(body)}");
        return newId;

    }
    /// <summary>
    /// Downloads a file by hash. Endpoint template configurable
    /// (Circularo:FileDownloadTemplate, default /api/v1/files/{hash}) –
    /// verify the exact path in the Circularo docs / Postman collection.
    /// </summary>
    public async Task<byte[]> DownloadFileAsync(string fileHash)
    {
        var template = _cfg["Circularo:FileDownloadTemplate"] ?? "/api/v1/files/loadFile/hash/{hash}";

        var url = $"{BaseUrl}{template.Replace("{hash}", Uri.EscapeDataString(fileHash))}?token={Uri.EscapeDataString(Token)}";
        _log.LogInformation("Circularo downloadFile URL  → {Status}", url);

        var res = await _http.GetAsync(url);
        _log.LogInformation("Circularo downloadFile {Hash} → {Status}", fileHash, (int)res.StatusCode);

        if (!res.IsSuccessStatusCode)
        {
            var body = await res.Content.ReadAsStringAsync();
            throw new DownstreamException((int)res.StatusCode, body);
        }

        return await res.Content.ReadAsByteArrayAsync();
    }

    /// <summary>
    /// Best-effort extraction of the signed PDF's file hash from a document detail
    /// response. Circularo document JSON contains the pdf file reference – the exact
    /// property path can differ, so we scan common locations and log what we find.
    /// </summary>
    public string? TryExtractPdfHash(JsonDocument docDetail)
    {
        var root = docDetail.RootElement;
        // Circularo wraps document detail in a results array
        if (root.TryGetProperty("results", out var results) &&
            results.ValueKind == JsonValueKind.Array && results.GetArrayLength() > 0)
        {
            using var inner = JsonDocument.Parse(results[0].GetRawText());
            return TryExtractPdfHash(inner);
        }
        foreach (var path in new[] { "pdfFile", "signedPdf", "file" })
        {
            if (root.TryGetProperty(path, out var el))
            {
                if (el.ValueKind == JsonValueKind.String) return el.GetString();
                if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty("content", out var c)) return c.GetString();
                if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty("hash", out var h)) return h.GetString();
            }
        }

        // nested under "document"
        if (root.TryGetProperty("document", out var d) && d.ValueKind == JsonValueKind.Object)
        {
            using var inner = JsonDocument.Parse(d.GetRawText());
            return TryExtractPdfHash(inner);
        }

        _log.LogWarning("Could not locate pdf file hash in Circularo document detail – inspect the logged JSON and adjust TryExtractPdfHash / config.");
        return null;
    }
    private static string? TryGetId(JsonElement el) =>
        el.ValueKind != JsonValueKind.Object ? null
        : el.TryGetProperty("documentId", out var a) && a.ValueKind == JsonValueKind.String ? a.GetString()
        : el.TryGetProperty("id", out var b) && b.ValueKind == JsonValueKind.String ? b.GetString()
        : null;

    private static string Truncate(string s, int max = 800) => s.Length <= max ? s : s[..max] + "…";

    /// <summary>POST /api/v1/files/saveFile – uploads a PDF (multipart) and returns the fresh file hash.
    /// Circularo file hashes are single-use, so every document creation needs a new upload.</summary>
    public async Task<string> UploadFileAsync(byte[] bytes, string fileName)
    {
        var url = $"{BaseUrl}/api/v1/files/saveFile?token={Uri.EscapeDataString(Token)}";
        using var form = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(bytes);
        fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/pdf");
        form.Add(fileContent, "file", fileName);

        var res = await _http.PostAsync(url, form);
        var body = await res.Content.ReadAsStringAsync();
        _log.LogInformation("Circularo saveFile {Name} → {Status}: {Body}", fileName, (int)res.StatusCode, Truncate(body));

        if (!res.IsSuccessStatusCode)
            throw new DownstreamException((int)res.StatusCode, body);

        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty("file", out var f) && f.TryGetProperty("hash", out var h))
            return h.GetString()!;
        throw new DownstreamException(500, $"Could not find file hash in saveFile response: {Truncate(body)}");
    }
    /// <summary>Uploads freshly-generated PDF bytes and creates a Circularo document from them.</summary>
    public async Task<string> CreateDocumentFromBytesAsync(byte[] pdfBytes, string fileName, string title)
    {
        var hash = await UploadFileAsync(pdfBytes, fileName);
        return await CreateDocumentFromFileAsync(hash, title);
    }
    /// <summary>Reads the phase template PDF from local disk, uploads it for a fresh hash,
    /// then creates a new Circularo document from it.</summary>
    public async Task<string> CreateDocumentFromLocalTemplateAsync(string title)
    {
        var path = _cfg["Circularo:TemplatePdfPath"]
            ?? throw new InvalidOperationException("Circularo:TemplatePdfPath is not configured");
        var bytes = await File.ReadAllBytesAsync(path);
        var freshHash = await UploadFileAsync(bytes, Path.GetFileName(path));
        return await CreateDocumentFromFileAsync(freshHash, title);
    }



    // Replace the hardcoded CreateDocumentFromLocalTemplateAsync with a generic version;
    // keep the old one for Phase 1 back-compat.
    public async Task<string> CreateDocumentFromTemplatePathAsync(string templatePath, string title)
    {
        var bytes = await File.ReadAllBytesAsync(templatePath);
        var freshHash = await UploadFileAsync(bytes, Path.GetFileName(templatePath));
        return await CreateDocumentFromFileAsync(freshHash, title);
    }

    // public Task<string> CreateDocumentFromLocalTemplateAsync(string title)
    // {
    //     var path = _cfg["Circularo:TemplatePdfPath"]
    //         ?? throw new InvalidOperationException("Circularo:TemplatePdfPath is not configured");
    //     return CreateDocumentFromTemplatePathAsync(path, title);
    // }



}
