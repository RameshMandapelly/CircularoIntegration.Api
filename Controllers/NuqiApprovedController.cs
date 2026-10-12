using CircularoIntegration.Api.Services.Nuqi;
using Microsoft.AspNetCore.Mvc;

namespace CircularoIntegration.Api.Controllers;

/// <summary>
/// NUQI Gold X Mawarid – Approved Stage Automation endpoints.
/// All routes are protected by the existing ApiKeyMiddleware (X-Webhook-Key header or ?webhookKey=).
///
///  LSQ automation (Under Approval → Approved)   POST /api/v1/nuqi/approved/initiate
///  LSQ automation (Credit Approved Amount edit)  POST /api/v1/nuqi/approved/amount-changed
///  LSQ automation (Approved → earlier stage)     POST /api/v1/nuqi/approved/stage-reverted
///  Circularo webhook                             existing POST /api/v1/signing/completed (hook forwards NUQI docs)
///                                                or POST /api/v1/nuqi/approved/callback (direct / local simulation)
///  Operations                                    GET  /api/v1/nuqi/approved/{leadId}/status
///                                                POST /api/v1/nuqi/approved/{leadId}/reconcile
///  Testing (no Circularo / no DB writes)         GET  /api/v1/nuqi/approved/debug/validate/{leadId}
///                                                GET  /api/v1/nuqi/approved/debug/document/{leadId}/{docCode}
/// </summary>
[ApiController]
[Route("api/v1/nuqi/approved")]
public class NuqiApprovedController : ControllerBase
{
          private readonly NuqiApprovedPackageService _svc;

          public NuqiApprovedController(NuqiApprovedPackageService svc) => _svc = svc;

          private async Task<string> ReadBodyAsync()
          {
                    using var reader = new StreamReader(Request.Body);
                    return await reader.ReadToEndAsync();
          }

          private IActionResult ToResult(NuqiResult r) => StatusCode(r.HttpStatus, r.Body);

          // ---------------------------------------------------------------- LSQ webhooks
          [HttpPost("initiate")]
          public async Task<IActionResult> Initiate(CancellationToken ct) =>
              ToResult(await _svc.HandleApprovedWebhookAsync(await ReadBodyAsync(), ct));

          [HttpPost("amount-changed")]
          public async Task<IActionResult> AmountChanged(CancellationToken ct) =>
              ToResult(await _svc.HandleAmountChangedAsync(await ReadBodyAsync(), ct));

          [HttpPost("stage-reverted")]
          public async Task<IActionResult> StageReverted(CancellationToken ct) =>
              ToResult(await _svc.HandleStageRevertedAsync(await ReadBodyAsync(), ct));

          // ---------------------------------------------------------------- Circularo
          [HttpPost("callback")]
          public async Task<IActionResult> Callback(CancellationToken ct) =>
              ToResult(await _svc.HandleCircularoCallbackAsync(await ReadBodyAsync(), NuqiSource.CircularoCallback, ct));

          // ---------------------------------------------------------------- Operations
          [HttpGet("{leadId}/status")]
          public async Task<IActionResult> Status(string leadId, CancellationToken ct) =>
              Ok(await _svc.GetStatusAsync(leadId, ct));

          [HttpPost("{leadId}/reconcile")]
          public async Task<IActionResult> Reconcile(string leadId, CancellationToken ct) =>
              ToResult(await _svc.ReconcileLeadAsync(leadId, NuqiSource.ManualApi, ct));

          // ---------------------------------------------------------------- Testing helpers
          [HttpGet("debug/validate/{leadId}")]
          public async Task<IActionResult> DebugValidate(string leadId, CancellationToken ct) =>
              ToResult(await _svc.DebugValidateAsync(leadId, ct));

          /// <summary>docCode = TNC or MMA. Open the PDF and check Exhibit A item 1 (credit limit) and item 2 (cash withdrawal).</summary>
          [HttpGet("debug/document/{leadId}/{docCode}")]
          public async Task<IActionResult> DebugDocument(string leadId, string docCode, CancellationToken ct)
          {
                    var (pdf, fileName, error) = await _svc.DebugRenderAsync(leadId, docCode, ct);
                    return pdf is null ? BadRequest(error) : File(pdf, "application/pdf", fileName);
          }

          // LSQ webhook URL validation pings
          [HttpHead("initiate"), HttpGet("initiate")]
          public IActionResult Probe() => Ok(new { status = "READY", module = "NUQI_APPROVED" });
}
