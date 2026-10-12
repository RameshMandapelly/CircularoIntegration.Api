using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CircularoIntegration.Api.Services.Nuqi;

/// <summary>Small shared helpers for the NUQI module (no state, no DI).</summary>
public static class NuqiJson
{
    /// <summary>Same depth-first search as SigningController.FindFirstString (copied so the controller is not touched).</summary>
    public static string? FindFirstString(JsonElement el, params string[] names)
    {
        if (el.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in el.EnumerateObject())
            {
                if (names.Any(n => string.Equals(n, prop.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    if (prop.Value.ValueKind == JsonValueKind.String) return prop.Value.GetString();
                    if (prop.Value.ValueKind == JsonValueKind.Number) return prop.Value.GetRawText();
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

    /// <summary>Reads a stage from a child object ("After"/"Before") or a plain string property.</summary>
    public static string? ReadStage(JsonElement payload, string objectName, params string[] flatNames)
    {
        if (payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty(objectName, out var obj))
        {
            if (obj.ValueKind == JsonValueKind.Object)
            {
                var s = FindFirstString(obj, "ProspectStage", "Stage", "CurrentStage");
                if (!string.IsNullOrWhiteSpace(s)) return s.Trim();
            }
            else if (obj.ValueKind == JsonValueKind.String)
            {
                return obj.GetString()?.Trim();
            }
        }
        foreach (var n in flatNames)
        {
            if (payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String)
                return v.GetString()?.Trim();
        }
        return null;
    }

    public static JsonElement ParseOrEmpty(string raw)
    {
        try { return JsonDocument.Parse(string.IsNullOrWhiteSpace(raw) ? "{}" : raw).RootElement.Clone(); }
        catch { return JsonDocument.Parse("{}").RootElement.Clone(); }
    }
}

public static class NuqiText
{
    /// <summary>A mapping is "configured" when it is not empty and not a TODO placeholder.</summary>
    public static bool IsConfigured(string? fieldName) =>
        !string.IsNullOrWhiteSpace(fieldName) && !fieldName.StartsWith("TODO", StringComparison.OrdinalIgnoreCase);

    public static string? Get(IReadOnlyDictionary<string, string?> lead, string? fieldName) =>
        IsConfigured(fieldName) && lead.TryGetValue(fieldName!, out var v) && !string.IsNullOrWhiteSpace(v) ? v!.Trim() : null;

    public static bool TryParseAmount(string? raw, out decimal amount)
    {
        amount = 0;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        var cleaned = raw.Replace("AED", "", StringComparison.OrdinalIgnoreCase).Replace(",", "").Replace(" ", "").Trim();
        return decimal.TryParse(cleaned, NumberStyles.Number, CultureInfo.InvariantCulture, out amount);
    }

    public static string Sha256Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    public static bool EmailEquals(string? a, string? b) =>
        !string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b) &&
        string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    public static bool LooksLikeEmail(string? s) =>
        !string.IsNullOrWhiteSpace(s) && Regex.IsMatch(s.Trim(), @"^[^@\s]+@[^@\s]+\.[^@\s]+$");

    public static string NormaliseMobile(string? s) =>
        Regex.Replace(s ?? string.Empty, @"[\s\-\(\)]", "");

    /// <summary>UAE local date for document dates (Windows: "Arabian Standard Time", Linux: "Asia/Dubai").</summary>
    public static DateTime DubaiNow()
    {
        foreach (var id in new[] { "Arabian Standard Time", "Asia/Dubai" })
        {
            try { return TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, TimeZoneInfo.FindSystemTimeZoneById(id)); }
            catch { /* try next */ }
        }
        return DateTime.UtcNow.AddHours(4);
    }

    public static string Truncate(string? s, int max) =>
        string.IsNullOrEmpty(s) ? string.Empty : (s.Length <= max ? s : s[..max]);
}

/// <summary>
/// Masks personal data before anything is written to logs / audit (BRD §14:
/// "Logs must not expose full Emirates ID, IBAN, document content or authentication tokens").
/// </summary>
public static class NuqiRedactor
{
    private static readonly Regex EmiratesId = new(@"\b784[- ]?\d{4}[- ]?\d{7}[- ]?\d\b", RegexOptions.Compiled);
    private static readonly Regex Iban = new(@"\bAE\d{2}\s?(?:\d{4}\s?){4}\d{3}\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex CardNumber = new(@"\b(?:\d[ -]?){13,19}\b", RegexOptions.Compiled);
    private static readonly Regex Email = new(@"\b([A-Za-z0-9._%+-]{1,2})[A-Za-z0-9._%+-]*@([A-Za-z0-9.-]+\.[A-Za-z]{2,})\b", RegexOptions.Compiled);
    private static readonly Regex Mobile = new(@"(?<!\d)(?:\+?971|00971|0)5\d{8}(?!\d)", RegexOptions.Compiled);
    private static readonly Regex Secrets = new(@"(?i)(""?(?:accessKey|secretKey|token|apiKey|password|webhookKey)""?\s*[:=]\s*""?)([^""&\s,}]+)", RegexOptions.Compiled);

    public static string Redact(string? input)
    {
        if (string.IsNullOrEmpty(input)) return string.Empty;
        var s = Secrets.Replace(input, m => m.Groups[1].Value + "***");
        s = EmiratesId.Replace(s, m => "784-****-*******-" + m.Value[^1]);
        s = Iban.Replace(s, m => "AE**" + new string('*', 8) + m.Value.Replace(" ", "")[^4..]);
        s = Mobile.Replace(s, m => MaskMobile(m.Value));
        s = CardNumber.Replace(s, m =>
        {
            var digits = new string(m.Value.Where(char.IsDigit).ToArray());
            return digits.Length >= 13 ? "****" + digits[^4..] : m.Value;
        });
        s = Email.Replace(s, m => $"{m.Groups[1].Value}***@{m.Groups[2].Value}");
        return s;
    }

    public static string MaskEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email) || !email.Contains('@')) return string.Empty;
        var parts = email.Split('@');
        return (parts[0].Length <= 2 ? parts[0] : parts[0][..2]) + "***@" + parts[1];
    }

    public static string MaskMobile(string? mobile)
    {
        var m = NuqiText.NormaliseMobile(mobile);
        return m.Length < 6 ? "***" : m[..3] + new string('*', m.Length - 6) + m[^3..];
    }
}
