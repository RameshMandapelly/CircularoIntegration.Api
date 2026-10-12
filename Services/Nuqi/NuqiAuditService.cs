using CircularoIntegration.Api.Data;
using CircularoIntegration.Api.Models;

namespace CircularoIntegration.Api.Services.Nuqi;

/// <summary>
/// Append-only audit trail (BRD §14: "Every transition and automated action must record old and
/// new value, user or service account, timestamp, source channel, request or correlation ID and outcome").
/// Never throws – an audit failure must not break the business flow, but it is logged as an error.
/// Uses its own DbContext instance via a scope so a failed business SaveChanges cannot swallow audit rows.
/// </summary>
public class NuqiAuditService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<NuqiAuditService> _log;

    public NuqiAuditService(IServiceScopeFactory scopes, ILogger<NuqiAuditService> log)
    {
        _scopes = scopes;
        _log = log;
    }

    public async Task WriteAsync(
        string correlationId, string? leadId, string action, string actor, string source, string outcome,
        string? oldValue = null, string? newValue = null, string? detail = null)
    {
        try
        {
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<NuqiDbContext>();
            db.Audits.Add(new IntegrationAudit
            {
                CorrelationId = correlationId,
                LeadId = leadId,
                Module = "NUQI_APPROVED",
                Action = NuqiText.Truncate(action, 60),
                OldValue = NuqiText.Truncate(NuqiRedactor.Redact(oldValue), 500),
                NewValue = NuqiText.Truncate(NuqiRedactor.Redact(newValue), 500),
                Actor = NuqiText.Truncate(actor, 150),
                SourceChannel = source,
                Outcome = outcome,
                Detail = NuqiText.Truncate(NuqiRedactor.Redact(detail), 2000),
                CreatedAtUtc = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "AUDIT WRITE FAILED corr={Corr} lead={Lead} action={Action} outcome={Outcome}",
                correlationId, leadId, action, outcome);
        }
    }
}

public static class NuqiSource
{
    public const string LsqWebhook = "LSQ_WEBHOOK";
    public const string CircularoCallback = "CIRCULARO_CALLBACK";
    public const string ReconcileJob = "RECONCILE_JOB";
    public const string ManualApi = "MANUAL_API";
}

public static class NuqiOutcome
{
    public const string Success = "SUCCESS";
    public const string Failed = "FAILED";
    public const string Ignored = "IGNORED";
    public const string Duplicate = "DUPLICATE";
    public const string Blocked = "BLOCKED";
}
