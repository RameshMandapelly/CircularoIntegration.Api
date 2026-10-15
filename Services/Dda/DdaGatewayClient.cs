using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CircularoIntegration.Api.Config;
using CircularoIntegration.Api.Models;
using CircularoIntegration.Api.Services.Nuqi;
using Microsoft.Extensions.Options;

namespace CircularoIntegration.Api.Services.Dda;

public record DdaHttpResult(int Status, byte[] Bytes, string? ContentType)
{
    public bool Ok => Status >= 200 && Status < 300;
    public string Text => Encoding.UTF8.GetString(Bytes);
}

/// <summary>
/// Typed HttpClient for the DD Marketplace gateway (https://uatapp.mawarid.ae/FinDDA).
/// JWT: POST /api/auth/token {clientId, clientSecret} → accessToken (60 min). The token is cached in memory,
/// refreshed before expiry, and on a 401 it is refreshed once and the call retried. The token and the client
/// secret are never logged.
/// </summary>
public class DdaGatewayClient
{
    private const string Root = "/api/v1/merchant/direct-debit-authorities";

    private static string? _token;
    private static DateTime _tokenExpiresUtc = DateTime.MinValue;
    private static readonly SemaphoreSlim TokenLock = new(1, 1);

    private readonly HttpClient _http;
    private readonly DdaGatewayOptions _opt;
    private readonly ILogger<DdaGatewayClient> _log;

    public DdaGatewayClient(HttpClient http, IOptions<DdaOptions> opt, ILogger<DdaGatewayClient> log)
    {
        _http = http;
        _opt = opt.Value.Gateway;
        _log = log;
        _http.Timeout = TimeSpan.FromSeconds(Math.Max(10, _opt.TimeoutSeconds));
    }

    private string Base => _opt.BaseUrl.TrimEnd('/');

    /// <summary>For the debug endpoint: true when a token can be obtained. Never returns the token.</summary>
    public async Task<(bool Ok, string Message)> TestAuthAsync(CancellationToken ct)
    {
        try { await GetTokenAsync(force: true, ct); return (true, "token obtained"); }
        catch (DownstreamException dex) { return (false, $"{dex.StatusCode} {Trim(dex.ResponseBody)}"); }
        catch (Exception ex) { return (false, ex.Message); }
    }

    // ------------------------------------------------------------------ operations
    public Task<DdaHttpResult> CreateAsync(string jsonBody, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, Root, jsonBody, ct);

    public Task<DdaHttpResult> GetStatusAsync(string ddaId, CancellationToken ct) =>
        SendAsync(HttpMethod.Get, $"{Root}/{Uri.EscapeDataString(ddaId)}/status", null, ct);

    public Task<DdaHttpResult> GetUnsignedPdfAsync(string ddaId, CancellationToken ct) =>
        SendAsync(HttpMethod.Get, $"{Root}/{Uri.EscapeDataString(ddaId)}/unsigned-pdf", null, ct);

    public Task<DdaHttpResult> GetSignedPdfAsync(string ddaId, CancellationToken ct) =>
        SendAsync(HttpMethod.Get, $"{Root}/{Uri.EscapeDataString(ddaId)}/signed-pdf", null, ct);

    // ------------------------------------------------------------------ plumbing
    private async Task<DdaHttpResult> SendAsync(HttpMethod method, string path, string? jsonBody, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            var token = await GetTokenAsync(force: false, ct);
            using var req = new HttpRequestMessage(method, Base + path);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (jsonBody is not null)
                req.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");

            using var res = await _http.SendAsync(req, ct);
            var bytes = await res.Content.ReadAsByteArrayAsync(ct);
            _log.LogInformation("DDA gateway {Method} {Path} → {Status}", method.Method, path, (int)res.StatusCode);

            if ((int)res.StatusCode == 401 && attempt == 1)
            {
                _token = null; _tokenExpiresUtc = DateTime.MinValue;
                continue;   // refresh the token once and retry
            }
            return new DdaHttpResult((int)res.StatusCode, bytes, res.Content.Headers.ContentType?.MediaType);
        }
    }

    private async Task<string> GetTokenAsync(bool force, CancellationToken ct)
    {
        if (!force && _token is not null && DateTime.UtcNow < _tokenExpiresUtc) return _token;

        await TokenLock.WaitAsync(ct);
        try
        {
            if (!force && _token is not null && DateTime.UtcNow < _tokenExpiresUtc) return _token;

            if (string.IsNullOrWhiteSpace(_opt.ClientId) || string.IsNullOrWhiteSpace(_opt.ClientSecret))
                throw new DownstreamException(500, "Dda:Gateway:ClientId / ClientSecret are not configured (appsettings.Local.json).");

            var body = JsonSerializer.Serialize(new { clientId = _opt.ClientId, clientSecret = _opt.ClientSecret });
            using var res = await _http.PostAsync($"{Base}/api/auth/token",
                new StringContent(body, Encoding.UTF8, "application/json"), ct);
            var text = await res.Content.ReadAsStringAsync(ct);
            _log.LogInformation("DDA gateway POST /api/auth/token → {Status}", (int)res.StatusCode);
            if (!res.IsSuccessStatusCode) throw new DownstreamException((int)res.StatusCode, text);

            using var doc = JsonDocument.Parse(text);
            var token = NuqiJson.FindFirstString(doc.RootElement, "accessToken", "access_token", "token");
            if (string.IsNullOrWhiteSpace(token))
                throw new DownstreamException(502, "Gateway token response has no accessToken.");

            var minutes = _opt.TokenCacheMinutes;
            var exp = NuqiJson.FindFirstString(doc.RootElement, "expiresIn", "expires_in");
            if (int.TryParse(exp, out var seconds) && seconds > 0)
                minutes = Math.Min(minutes, Math.Max(1, seconds / 60 - 5));

            _token = token;
            _tokenExpiresUtc = DateTime.UtcNow.AddMinutes(minutes);
            return token;
        }
        finally { TokenLock.Release(); }
    }

    private static string Trim(string? s) => NuqiText.Truncate(s, 300);
}
