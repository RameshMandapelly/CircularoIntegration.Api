namespace CircularoIntegration.Api.Middleware;

/// <summary>
/// Validates a shared-secret header (X-Webhook-Key) on all inbound calls.
/// Swagger, health check and root are excluded so the app stays browsable.
/// Configure the expected key in appsettings: Security:WebhookApiKey.
/// The same value must be added as a custom header in the LeadSquared
/// webhook config and the Circularo webhook config.
/// </summary>
public class ApiKeyMiddleware
{
    private const string HeaderName = "X-Webhook-Key";
    private readonly RequestDelegate _next;
    private readonly IConfiguration _config;
    private readonly ILogger<ApiKeyMiddleware> _logger;

    public ApiKeyMiddleware(RequestDelegate next, IConfiguration config, ILogger<ApiKeyMiddleware> logger)
    {
        _next = next;
        _config = config;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? string.Empty;

        // Public endpoints
        if (path.StartsWith("/swagger") || path.StartsWith("/logs") || path == "/health" || path == "/")
        {
            await _next(context);
            return;
        }

        var expected = _config["Security:WebhookApiKey"];

        // If no key configured, allow (useful for first local run) but warn loudly.
        if (string.IsNullOrWhiteSpace(expected))
        {
            _logger.LogWarning("Security:WebhookApiKey is NOT configured – all requests are being allowed!");
            await _next(context);
            return;
        }

        // Accept the key from the X-Webhook-Key header (LeadSquared supports
        // custom headers) OR from the ?webhookKey= query parameter (Circularo's
        // webhook config has no custom-header option, so the key is embedded
        // in the registered callback URL instead).
        var provided = context.Request.Headers[HeaderName].FirstOrDefault()
                    ?? context.Request.Query["webhookKey"].FirstOrDefault();

        if (string.IsNullOrEmpty(provided) || provided != expected)
        {
            _logger.LogWarning("Rejected request to {Path} – missing/invalid {Header}", path, HeaderName);
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { message = "Invalid or missing API key", status = 401 });
            return;
        }

        await _next(context);
    }
}
