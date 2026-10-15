
using Microsoft.Extensions.Options;
using CircularoIntegration.Api.Config;


namespace CircularoIntegration.Api.Services.Dda;

/// <summary>
/// BRD DDA-19: "Get Status of a DDA shall run … until ACTIVE or REJECTED when a final authenticated callback is unavailable."
/// Demo: Dda:Tracking:IntervalMinutes = 1-2, PollWindowStartHourDubai = 0 (always). Production: 17 (after 5 PM Dubai) + 60 min.
/// IIS: set the app pool to AlwaysRunning (same note as NuqiReconciliationWorker). POST api/v1/nuqi/dda/{leadId}/refresh works regardless.
/// </summary>
public class DdaStatusWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
   
    private readonly DdaOptions _opt;
    private readonly ILogger<DdaStatusWorker> _log;

    public DdaStatusWorker(IServiceScopeFactory scopes, IOptions<DdaOptions> opt, ILogger<DdaStatusWorker> log)
    {
        _scopes = scopes; _opt = opt.Value; _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_opt.Enabled || !_opt.Tracking.Enabled)
        {
            _log.LogInformation("DDA status worker disabled (Dda:Enabled / Dda:Tracking:Enabled)");
            return;
        }

        try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); } catch (TaskCanceledException) { return; }

        var interval = TimeSpan.FromMinutes(Math.Max(1, _opt.Tracking.IntervalMinutes));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopes.CreateScope();
                var svc = scope.ServiceProvider.GetRequiredService<DdaMandateService>();
                var n = await svc.RunPollSweepAsync(stoppingToken);
                if (n > 0) _log.LogInformation("DDA poll sweep checked {Count} mandate(s)", n);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                // e.g. nuqi_dda_request not created yet – log and keep the app running.
                _log.LogError(ex, "DDA poll sweep failed");
            }

            try { await Task.Delay(interval, stoppingToken); } catch (TaskCanceledException) { break; }
        }
    }
}
