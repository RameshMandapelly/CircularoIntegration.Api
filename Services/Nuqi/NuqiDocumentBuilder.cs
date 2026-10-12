using System.Globalization;
using System.Text.Json;
using CircularoIntegration.Api.Config;
using CircularoIntegration.Api.Models;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Microsoft.Extensions.Options;
using PdfSharpCore.Pdf.IO;

namespace CircularoIntegration.Api.Services.Nuqi;

public class NuqiRenderedDocument
{
    public NuqiPackageTemplate Template { get; init; } = default!;
    public byte[] Pdf { get; init; } = Array.Empty<byte>();
    public int PageCount { get; init; }
    public string Sha256 { get; init; } = string.Empty;
}

/// <summary>
/// Builds the T&amp;C and MMA PDFs. Reuses the existing, UAT-proven services:
///   DocxTokens   → OfferLetterDocumentService.GenerateFromDocxTemplateAsync (LibreOffice)
///   PdfOverlay   → PdfOverlayService.Stamp
///   AcroFormFill → AcroFormFillService.FillAndFlattenAsync (pdftk)
/// For DocxTokens the tokens are merged HERE first with run-merging (Word splits "{{Token}}"
/// across runs – the known Stage 10 hazard), then the already-merged docx is handed to the
/// existing converter with an empty token list. After merging, the docx is re-scanned and
/// generation FAILS if a required Exhibit A token is still unresolved (DOC-07 guarantee).
/// </summary>
public class NuqiDocumentBuilder
{
    private readonly OfferLetterDocumentService _docx;
    private readonly PdfOverlayService _overlay;
    private readonly AcroFormFillService _acro;
    private readonly NuqiApprovedOptions _opt;
    private readonly ILogger<NuqiDocumentBuilder> _log;

    public NuqiDocumentBuilder(
        OfferLetterDocumentService docx, PdfOverlayService overlay, AcroFormFillService acro,
        IOptions<NuqiApprovedOptions> opt, ILogger<NuqiDocumentBuilder> log)
    {
        _docx = docx;
        _overlay = overlay;
        _acro = acro;
        _opt = opt.Value;
        _log = log;
    }

    /// <summary>Token dictionary. Amount and profit come ONLY from the validated snapshot + package config.</summary>
    public Dictionary<string, string> BuildTokens(NuqiApprovalData d, NuqiPackageConfig cfg, NuqiApprovalEvent ev)
    {
        var amountText = d.Amount.ToString(_opt.AmountFormat, CultureInfo.InvariantCulture);
        var cashText = d.CashWithdrawalLimit.ToString(_opt.AmountFormat, CultureInfo.InvariantCulture);
        var profit = cfg.ProfitRatePercent.ToString("0.00", CultureInfo.InvariantCulture);
        var now = NuqiText.DubaiNow();

        return new Dictionary<string, string>
        {
            // Exhibit A (simplified flow)
            ["ExhibitA_CreditLimit"] = amountText,                                    // 1. total amount (credit limit) not exceeding AED 24,900
            ["ExhibitA_CashWithdrawalLimit"] = cashText,                              // 2. total amount for cash withdrawal AED 5,000
            ["ExhibitA_MaxAmount"] = amountText,                                      // alias of ExhibitA_CreditLimit
            ["ExhibitA_MaxAmountWithCurrency"] = $"{d.Currency} {amountText}",       // AED 24,900
            ["ExhibitA_NotExceedingText"] = $"not exceeding {d.Currency} {amountText}",
            ["ExhibitA_ProfitRate"] = $"{profit}%",                                  // 3.10%
            ["ExhibitA_ProfitRateNumber"] = profit,                                  // 3.10
            ["Currency"] = d.Currency,
            ["CustomerName"] = d.CustomerName,
            ["ApplicationId"] = d.ApplicationId ?? string.Empty,
            ["PartnerReference"] = d.PartnerReference ?? string.Empty,
            ["PackageReference"] = ev.PackageReference,
            ["MawaridSignerName"] = d.MawaridSignerName,
            ["AgreementDate"] = now.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture),
            ["AgreementDay"] = now.Day.ToString(CultureInfo.InvariantCulture),
            ["AgreementMonth"] = now.ToString("MMMM", CultureInfo.InvariantCulture),
            ["AgreementYear"] = now.Year.ToString(CultureInfo.InvariantCulture)
        };
    }

    public async Task<NuqiRenderedDocument> RenderAsync(NuqiPackageTemplate t, Dictionary<string, string> tokens, CancellationToken ct = default)
    {
        byte[] pdf;
        switch (t.MergeStrategy)
        {
            case DocMergeStrategy.DocxTokens:
                pdf = await RenderDocxAsync(t, tokens, ct);
                break;

            case DocMergeStrategy.PdfOverlay:
                if (string.IsNullOrWhiteSpace(t.OverlayFieldsJson))
                    throw new InvalidOperationException($"{t.DocumentCode} is PdfOverlay but OverlayFieldsJson is empty");
                EnsureRequiredValues(t, tokens);
                pdf = _overlay.Stamp(t.TemplatePath, t.OverlayFieldsJson, tokens);
                break;

            case DocMergeStrategy.AcroFormFill:
                if (string.IsNullOrWhiteSpace(t.FormFieldMappingJson))
                    throw new InvalidOperationException($"{t.DocumentCode} is AcroFormFill but FormFieldMappingJson is empty");
                EnsureRequiredValues(t, tokens);
                var mapping = JsonSerializer.Deserialize<Dictionary<string, string>>(t.FormFieldMappingJson) ?? new();
                var values = mapping.ToDictionary(kv => kv.Key, kv => tokens.TryGetValue(kv.Value, out var v) ? v : string.Empty);
                pdf = await _acro.FillAndFlattenAsync(t.TemplatePath, values);
                break;

            default:
                if (t.RequiredTokenList.Any())
                    throw new InvalidOperationException($"{t.DocumentCode} is a static PDF but requires tokens {string.Join(", ", t.RequiredTokenList)}");
                pdf = await File.ReadAllBytesAsync(t.TemplatePath, ct);
                break;
        }

        var pages = CountPages(pdf);
        if (pages <= 0) throw new InvalidOperationException($"{t.DocumentCode}: generated PDF has no pages");

        _log.LogInformation("NUQI rendered {Code} {Version}: {Bytes} bytes, {Pages} pages", t.DocumentCode, t.TemplateVersion, pdf.Length, pages);
        return new NuqiRenderedDocument { Template = t, Pdf = pdf, PageCount = pages, Sha256 = NuqiText.Sha256Hex(pdf) };
    }

    /// <summary>
    /// Signature boxes for a template (appsettings NuqiApproved:Templates[].SignatureFields). Page -1 = last page of that document.
    /// Default when none are configured: customer bottom-RIGHT, Mawarid Business bottom-LEFT of last page.
    /// </summary>
    public List<NuqiSignatureBox> SignatureBoxes(NuqiPackageTemplate t, int pageCount, int pageOffset = 0)
    {
        List<NuqiSignatureBox> boxes;
        if (t.SignatureFields.Count > 0)
        {
            boxes = t.SignatureFields
                .Select(f => new NuqiSignatureBox(f.Role, f.Page, f.X, f.Y, f.W, f.H, f.Timestamp))
                .ToList();
        }
        else
        {
            boxes = new List<NuqiSignatureBox>
            {
                new("Customer", -1, 0.58, 0.82, 0.30, 0.08),   // customer signs on the RIGHT
                new("Mawarid",  -1, 0.08, 0.82, 0.30, 0.08)    // Mawarid Business signs on the LEFT
            };
        }

        if (!boxes.Any(b => b.Role == "Customer") || !boxes.Any(b => b.Role == "Mawarid"))
            throw new InvalidOperationException($"{t.DocumentCode}: SignatureFields must contain a Customer and a Mawarid box");

        return boxes.Select(b =>
        {
            var page = b.Page <= 0 ? pageCount : Math.Min(b.Page, pageCount);
            return b with { Page = page + pageOffset };
        }).ToList();
    }

    // ------------------------------------------------------------------ private

    private async Task<byte[]> RenderDocxAsync(NuqiPackageTemplate t, Dictionary<string, string> tokens, CancellationToken ct)
    {
        EnsureRequiredValues(t, tokens);

        var workDir = Path.Combine(Path.GetTempPath(), $"nuqi-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);
        var merged = Path.Combine(workDir, Path.GetFileName(t.TemplatePath));
        try
        {
            File.Copy(t.TemplatePath, merged, overwrite: true);
            MergeDocxWithRunJoin(merged, tokens);

            var unresolved = FindUnresolvedTokens(merged);
            var requiredUnresolved = unresolved.Intersect(t.RequiredTokenList).ToList();
            if (requiredUnresolved.Count > 0)
                throw new InvalidOperationException($"{t.DocumentCode}: required token(s) not replaced: {string.Join(", ", requiredUnresolved)}");
            if (unresolved.Count > 0)
                _log.LogWarning("NUQI {Code}: optional token(s) left unresolved: {Tokens}", t.DocumentCode, string.Join(", ", unresolved));

            // Already merged → existing converter only does docx → pdf.
            return await _docx.GenerateFromDocxTemplateAsync(merged, new Dictionary<string, string>(), ct);
        }
        finally
        {
            try { Directory.Delete(workDir, recursive: true); } catch { /* best effort */ }
        }
    }

    private static void EnsureRequiredValues(NuqiPackageTemplate t, Dictionary<string, string> tokens)
    {
        var empty = t.RequiredTokenList.Where(k => !tokens.TryGetValue(k, out var v) || string.IsNullOrWhiteSpace(v)).ToList();
        if (empty.Count > 0)
            throw new InvalidOperationException($"{t.DocumentCode}: required token value(s) empty: {string.Join(", ", empty)}");
    }

    /// <summary>Paragraph-level replace: joins all runs' text, replaces, writes into first run, removes the rest.</summary>
    private static void MergeDocxWithRunJoin(string docxPath, Dictionary<string, string> tokens)
    {
        using var doc = WordprocessingDocument.Open(docxPath, true);
        var parts = new List<OpenXmlPartRootElement>();
        if (doc.MainDocumentPart?.Document is { } main) parts.Add(main);
        if (doc.MainDocumentPart is not null)
        {
            parts.AddRange(doc.MainDocumentPart.HeaderParts.Select(h => (OpenXmlPartRootElement)h.Header));
            parts.AddRange(doc.MainDocumentPart.FooterParts.Select(f => (OpenXmlPartRootElement)f.Footer));
        }

        foreach (var root in parts)
        {
            foreach (var paragraph in root.Descendants<Paragraph>().ToList())
            {
                var texts = paragraph.Descendants<Text>().ToList();
                if (texts.Count == 0) continue;
                var combined = string.Concat(texts.Select(x => x.Text));
                if (!combined.Contains("{{")) continue;

                var replaced = combined;
                foreach (var (key, value) in tokens)
                    replaced = System.Text.RegularExpressions.Regex.Replace(
                        replaced, @"\{\{\s*" + System.Text.RegularExpressions.Regex.Escape(key) + @"\s*\}\}", value ?? string.Empty);

                if (replaced == combined) continue;

                texts[0].Text = replaced;
                texts[0].Space = SpaceProcessingModeValues.Preserve;
                foreach (var extra in texts.Skip(1)) extra.Text = string.Empty;
            }
            root.Save();
        }
    }

    private static List<string> FindUnresolvedTokens(string docxPath)
    {
        using var doc = WordprocessingDocument.Open(docxPath, false);
        var body = doc.MainDocumentPart?.Document?.Body;
        if (body is null) return new List<string>();
        var all = string.Join("\n", body.Descendants<Paragraph>().Select(p => string.Concat(p.Descendants<Text>().Select(t => t.Text))));
        return System.Text.RegularExpressions.Regex.Matches(all, @"\{\{\s*([A-Za-z0-9_]+)\s*\}\}")
            .Select(m => m.Groups[1].Value).Distinct().ToList();
    }

    private static int CountPages(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf);
        using var doc = PdfReader.Open(ms, PdfDocumentOpenMode.Import);
        return doc.PageCount;
    }
}
