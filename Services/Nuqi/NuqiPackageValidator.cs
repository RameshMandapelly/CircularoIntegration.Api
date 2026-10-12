using System.Text.Json;
using System.Text.RegularExpressions;
using CircularoIntegration.Api.Config;
using CircularoIntegration.Api.Models;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Microsoft.Extensions.Options;

namespace CircularoIntegration.Api.Services.Nuqi;

/// <summary>Values extracted from the lead and used for the package (one snapshot per approval event).</summary>
public class NuqiApprovalData
{
    public decimal Amount { get; set; }                 // Exhibit A item 1 – credit limit
    public decimal CashWithdrawalLimit { get; set; }    // Exhibit A item 2 – cash withdrawal
    public string Currency { get; set; } = "AED";
    public string CustomerName { get; set; } = string.Empty;
    public string CustomerEmail { get; set; } = string.Empty;
    public string CustomerMobile { get; set; } = string.Empty;
    public string? ApplicationId { get; set; }
    public string? PartnerReference { get; set; }
    public string MawaridSignerName { get; set; } = string.Empty;
    public string MawaridSignerEmail { get; set; } = string.Empty;
    public int? LsqApprovalEventVersion { get; set; }
    public string? FinalApprover { get; set; }
    public string? FinalApprovalDate { get; set; }
    public string? FinalDecision { get; set; }
}

public record NuqiValidationError(string Field, string Code, string Message);

public class NuqiValidationResult
{
    public NuqiApprovalData Data { get; } = new();
    public List<NuqiValidationError> Errors { get; } = new();
    public bool IsValid => Errors.Count == 0;
    public void Add(string field, string code, string message) => Errors.Add(new NuqiValidationError(field, code, message));

    /// <summary>Field-level reason for LSQ / DB (no PII – only field names and codes).</summary>
    public string Summary => string.Join("; ", Errors.Select(e => $"{e.Field}: {e.Message}"));
}

/// <summary>
/// Pre-generation validation – BRD §6.1 table and DOC-14:
///  * final approved credit limit &gt; 0 and currency AED
///  * customer full name, mobile, email
///  * Mawarid Business/BD signatory name + email
///  * approved template versions (allow-list TNC + MMA, approved flag, file exists, Exhibit A tokens present)
///  * 3.10 % profit rate
///  * unique application / partner identifiers
///  * final approval user, date and decision (when configured)
/// </summary>
public class NuqiPackageValidator
{
    private readonly NuqiApprovedOptions _opt;
    private readonly ILogger<NuqiPackageValidator> _log;

    public NuqiPackageValidator(IOptions<NuqiApprovedOptions> opt, ILogger<NuqiPackageValidator> log)
    {
        _opt = opt.Value;
        _log = log;
    }

    public NuqiValidationResult Validate(
        IReadOnlyDictionary<string, string?> lead, NuqiPackageConfig cfg, IReadOnlyList<NuqiPackageTemplate> templates)
    {
        var r = new NuqiValidationResult();
        var f = _opt.LsqFields;
        var rules = _opt.Validation;

        // ---- 1. Credit Approved Amount (DOC-03, DOC-07, DOC-14) ----
        if (!NuqiText.IsConfigured(f.CreditApprovedAmount))
        {
            r.Add("CreditApprovedAmount", "NOT_MAPPED", "LSQ field for Credit Approved Amount is not configured (NuqiApproved:LsqFields:CreditApprovedAmount)");
        }
        else
        {
            var raw = NuqiText.Get(lead, f.CreditApprovedAmount);
            if (raw is null) r.Add("CreditApprovedAmount", "MISSING", "Credit Approved Amount is empty");
            else if (!NuqiText.TryParseAmount(raw, out var amt)) r.Add("CreditApprovedAmount", "INVALID", "Credit Approved Amount is not a number");
            else if (amt <= 0) r.Add("CreditApprovedAmount", "INVALID", "Credit Approved Amount must be greater than zero");
            else if (decimal.Round(amt, 2) != amt) r.Add("CreditApprovedAmount", "INVALID", "Credit Approved Amount has more than 2 decimals");
            else r.Data.Amount = amt;
        }

        // ---- 1b. Cash withdrawal limit (Exhibit A item 2) ----
        if (!NuqiText.IsConfigured(f.CashWithdrawalLimit))
        {
            if (rules.RequireCashWithdrawalLimit)
                r.Add("CashWithdrawalLimit", "NOT_MAPPED", "LSQ field for Cash Withdrawal Limit is not configured (NuqiApproved:LsqFields:CashWithdrawalLimit)");
        }
        else
        {
            var rawCash = NuqiText.Get(lead, f.CashWithdrawalLimit);
            if (rawCash is null)
            {
                if (rules.RequireCashWithdrawalLimit) r.Add("CashWithdrawalLimit", "MISSING", "Cash Withdrawal Limit is empty");
            }
            else if (!NuqiText.TryParseAmount(rawCash, out var cash)) r.Add("CashWithdrawalLimit", "INVALID", "Cash Withdrawal Limit is not a number");
            else if (cash < 0) r.Add("CashWithdrawalLimit", "INVALID", "Cash Withdrawal Limit cannot be negative");
            else if (decimal.Round(cash, 2) != cash) r.Add("CashWithdrawalLimit", "INVALID", "Cash Withdrawal Limit has more than 2 decimals");
            else if (rules.CashLimitMustNotExceedCreditLimit && r.Data.Amount > 0 && cash > r.Data.Amount)
                r.Add("CashWithdrawalLimit", "EXCEEDS_LIMIT", "Cash Withdrawal Limit is greater than the Credit Approved Amount");
            else r.Data.CashWithdrawalLimit = cash;
        }

        // ---- 2. Currency AED (DOC-14) ----
        var currency = NuqiText.Get(lead, f.Currency) ?? cfg.Currency;
        if (!string.Equals(currency, rules.ExpectedCurrency, StringComparison.OrdinalIgnoreCase))
            r.Add("Currency", "INVALID", $"Currency must be {rules.ExpectedCurrency}");
        r.Data.Currency = rules.ExpectedCurrency;

        // ---- 3. Profit rate fixed 3.10 % (DOC-08, DOC-14) ----
        if (cfg.ProfitRatePercent != rules.ExpectedProfitRate)
            r.Add("ProfitRatePercent", "MISMATCH", $"Package profit rate {cfg.ProfitRatePercent:0.00}% does not equal the approved {rules.ExpectedProfitRate:0.00}%");

        // ---- 4. Customer name / email / mobile (§6.1) ----
        var fullName = NuqiText.Get(lead, f.CustomerFullName)
                       ?? string.Join(" ", new[] { NuqiText.Get(lead, f.CustomerFirstName), NuqiText.Get(lead, f.CustomerLastName) }
                                              .Where(x => !string.IsNullOrWhiteSpace(x))).Trim();
        if (string.IsNullOrWhiteSpace(fullName)) r.Add("CustomerFullName", "MISSING", "Customer full name is empty");
        r.Data.CustomerName = fullName ?? string.Empty;

        var email = NuqiText.Get(lead, f.CustomerEmail);
        if (email is null) r.Add("CustomerEmail", "MISSING", "Customer email is empty");
        else if (!NuqiText.LooksLikeEmail(email)) r.Add("CustomerEmail", "INVALID", "Customer email format is invalid");
        r.Data.CustomerEmail = email ?? string.Empty;

        var mobile = NuqiText.NormaliseMobile(NuqiText.Get(lead, f.CustomerMobile));
        if (string.IsNullOrWhiteSpace(mobile)) r.Add("CustomerMobile", "MISSING", "Customer mobile is empty");
        else if (!Regex.IsMatch(mobile, rules.MobileRegex)) r.Add("CustomerMobile", "INVALID", "Customer mobile is not a valid UAE mobile number");
        r.Data.CustomerMobile = mobile;

        // ---- 5. Mawarid BD signer (§6.1 → "block document initiation and assign an exception task") ----
        var signerName = NuqiText.Get(lead, f.MawaridSignerName) ?? cfg.DefaultMawaridSignerName;
        var signerEmail = NuqiText.Get(lead, f.MawaridSignerEmail) ?? cfg.DefaultMawaridSignerEmail;
        if (string.IsNullOrWhiteSpace(signerName) || signerName.Contains("CONFIRM", StringComparison.OrdinalIgnoreCase))
            r.Add("MawaridSignerName", "MISSING", "Mawarid Business/BD signer name is not set");
        if (string.IsNullOrWhiteSpace(signerEmail) || signerEmail.Contains("CONFIRM", StringComparison.OrdinalIgnoreCase) || !NuqiText.LooksLikeEmail(signerEmail))
            r.Add("MawaridSignerEmail", "MISSING", "Mawarid Business/BD signer email is not set or invalid");
        else if (NuqiText.EmailEquals(signerEmail, email))
            r.Add("MawaridSignerEmail", "CONFLICT", "Mawarid signer email must differ from the customer email");
        r.Data.MawaridSignerName = signerName ?? string.Empty;
        r.Data.MawaridSignerEmail = signerEmail ?? string.Empty;

        // ---- 6. Identifiers (§6.1 "unique application, partner and correlation identifiers") ----
        r.Data.ApplicationId = NuqiText.Get(lead, f.ApplicationId);
        r.Data.PartnerReference = NuqiText.Get(lead, f.PartnerReference);
        if (rules.RequireApplicationId && r.Data.ApplicationId is null)
            r.Add("ApplicationId", "MISSING", "Application ID is empty");
        if (rules.RequirePartnerReference && r.Data.PartnerReference is null)
            r.Add("PartnerReference", "MISSING", "NUQI partner reference is empty");

        // ---- 7. Final approval user / date / decision (§6.1) ----
        r.Data.FinalApprover = NuqiText.Get(lead, f.FinalApprover);
        r.Data.FinalApprovalDate = NuqiText.Get(lead, f.FinalApprovalDate);
        r.Data.FinalDecision = NuqiText.Get(lead, f.FinalDecision);
        if (rules.RequireFinalApprovalDetails)
        {
            if (r.Data.FinalApprover is null) r.Add("FinalApprover", "MISSING", "Final approval user is empty");
            if (r.Data.FinalApprovalDate is null) r.Add("FinalApprovalDate", "MISSING", "Final approval date is empty");
            if (!string.Equals(r.Data.FinalDecision, rules.ExpectedFinalDecision, StringComparison.OrdinalIgnoreCase))
                r.Add("FinalDecision", "INVALID", $"Final decision must be '{rules.ExpectedFinalDecision}'");
        }

        // ---- 8. LSQ approval-event version (optional) ----
        var ver = NuqiText.Get(lead, f.ApprovalEventVersion);
        if (ver is not null && int.TryParse(ver, out var v) && v > 0) r.Data.LsqApprovalEventVersion = v;

        // ---- 9. Templates: allow-list, approval, version, file, Exhibit A tokens (DOC-04, DOC-14, §20) ----
        ValidateTemplates(templates, r);

        if (!r.IsValid)
            _log.LogWarning("NUQI validation failed: {Summary}", r.Summary);
        return r;
    }

    private void ValidateTemplates(IReadOnlyList<NuqiPackageTemplate> templates, NuqiValidationResult r)
    {
        var codes = templates.Select(t => t.DocumentCode.ToUpperInvariant()).OrderBy(c => c).ToArray();
        var expected = NuqiDocCode.AllowList.OrderBy(c => c).ToArray();
        if (!codes.SequenceEqual(expected))
        {
            r.Add("Templates", "ALLOW_LIST", $"Package must contain exactly [{string.Join(",", expected)}] but has [{string.Join(",", codes)}]");
            return;
        }

        foreach (var t in templates)
        {
            var field = $"Template.{t.DocumentCode}";
            if (!t.IsApproved) r.Add(field, "NOT_APPROVED", $"{t.DocumentName} {t.TemplateVersion} is not marked Legal/Sharia approved");
            if (string.IsNullOrWhiteSpace(t.TemplateVersion)) r.Add(field, "NO_VERSION", $"{t.DocumentName} has no template version");
            if (!File.Exists(t.TemplatePath)) { r.Add(field, "FILE_MISSING", $"Template file not found: {t.TemplatePath}"); continue; }

            var missing = MissingRequiredTokens(t);
            if (missing.Count > 0)
                r.Add(field, "TOKENS_MISSING", $"Template does not contain required token(s): {string.Join(", ", missing)}");
        }
    }

    /// <summary>
    /// Proves the template can actually carry the Exhibit A values before we send anything (DOC-07/08).
    /// Docx: paragraph text (runs joined, so Word run-splitting doesn't hide a token).
    /// Overlay: tokens in OverlayFieldsJson. AcroForm: tokens in FormFieldMappingJson.
    /// Static PDF cannot carry tokens → every required token is reported missing.
    /// </summary>
    public List<string> MissingRequiredTokens(NuqiPackageTemplate t)
    {
        var required = t.RequiredTokenList.ToList();
        if (required.Count == 0) return new List<string>();

        var present = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            switch (t.MergeStrategy)
            {
                case DocMergeStrategy.DocxTokens:
                    using (var doc = WordprocessingDocument.Open(t.TemplatePath, false))
                    {
                        var body = doc.MainDocumentPart?.Document?.Body;
                        if (body is not null)
                        {
                            foreach (var p in body.Descendants<Paragraph>())
                            {
                                var text = string.Concat(p.Descendants<Text>().Select(x => x.Text));
                                foreach (Match m in Regex.Matches(text, @"\{\{\s*([A-Za-z0-9_]+)\s*\}\}"))
                                    present.Add(m.Groups[1].Value);
                            }
                        }
                    }
                    break;

                case DocMergeStrategy.PdfOverlay:
                    if (!string.IsNullOrWhiteSpace(t.OverlayFieldsJson))
                        foreach (var e in JsonDocument.Parse(t.OverlayFieldsJson).RootElement.EnumerateArray())
                            if (e.TryGetProperty("Token", out var tok) && tok.GetString() is { } s) present.Add(s);
                    break;

                case DocMergeStrategy.AcroFormFill:
                    if (!string.IsNullOrWhiteSpace(t.FormFieldMappingJson))
                        foreach (var p in JsonDocument.Parse(t.FormFieldMappingJson).RootElement.EnumerateObject())
                            if (p.Value.GetString() is { } s) present.Add(s);
                    break;
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "NUQI token inspection failed for template {Path}", t.TemplatePath);
        }

        return required.Where(x => !present.Contains(x)).ToList();
    }
}
