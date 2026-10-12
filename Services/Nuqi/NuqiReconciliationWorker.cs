using CircularoIntegration.Api.Config;
using Microsoft.Extensions.Options;

namespace CircularoIntegration.Api.Services.Nuqi;

/// <summary>
/// BRD §8 "Callback not received → perform secure status enquiry or reconciliation before retrying".
/// Every NuqiApproved:Reconciliation:IntervalMinutes it checks packages with no callback for
/// StaleAfterHours, asks Circularo for the document state, and also retries any completion gate
/// that failed half-way (e.g. LSQ was down). It never marks anything complete without Circularo evidence.
///
/// IIS note: an in-process timer only runs while the app pool is alive. On IIS set the app pool to
/// "AlwaysRunning" + site "Preload Enabled"; on Azure App Service enable "Always On".
/// The manual endpoint POST /api/v1/nuqi/approved/{leadId}/reconcile works regardless.
/// </summary>
public class NuqiReconciliationWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly NuqiApprovedOptions _opt;
    private readonly ILogger<NuqiReconciliationWorker> _log;

    public NuqiReconciliationWorker(IServiceScopeFactory scopes, IOptions<NuqiApprovedOptions> opt, ILogger<NuqiReconciliationWorker> log)
    {
        _scopes = scopes;
        _opt = opt.Value;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_opt.Reconciliation.Enabled)
        {
            _log.LogInformation("NUQI reconciliation worker disabled (NuqiApproved:Reconciliation:Enabled=false)");
            return;
        }

        // Let the app finish starting before the first sweep.
        try { await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken); } catch (TaskCanceledException) { return; }

        var interval = TimeSpan.FromMinutes(Math.Max(5, _opt.Reconciliation.IntervalMinutes));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopes.CreateScope();
                var svc = scope.ServiceProvider.GetRequiredService<NuqiApprovedPackageService>();
                var n = await svc.RunReconciliationSweepAsync(stoppingToken);
                if (n > 0) _log.LogInformation("NUQI reconciliation sweep checked {Count} package(s)", n);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                // e.g. NUQI tables not created yet – log and keep the app running.
                _log.LogError(ex, "NUQI reconciliation sweep failed");
            }

            try { await Task.Delay(interval, stoppingToken); } catch (TaskCanceledException) { break; }
        }
    }
}
