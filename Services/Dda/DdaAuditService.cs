using CircularoIntegration.Api.Data;
using CircularoIntegration.Api.Models;
using CircularoIntegration.Api.Services.Nuqi;

namespace CircularoIntegration.Api.Services.Dda;

/// <summary>Append-only audit rows (module "DDA") in the shared integration_audit table. Never throws.</summary>
public class DdaAuditService
{
    public const string Module = "DDA";
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<DdaAuditService> _log;

    public DdaAuditService(IServiceScopeFactory scopes, ILogger<DdaAuditService> log)
    {
        _scopes = scopes;
        _log = log;
    }

    public async Task WriteAsync(string corr, string? leadId, string action, string actor, string source, string outcome,
        string? oldValue = null, string? newValue = null, string? detail = null)
    {
        try
        {
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<DdaDbContext>();
            db.Audits.Add(new IntegrationAudit
            {
                CorrelationId = corr,
                LeadId = leadId,
                Module = Module,
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
            _log.LogError(ex, "DDA AUDIT WRITE FAILED corr={Corr} lead={Lead} action={Action}", corr, leadId, action);
        }
    }
}
