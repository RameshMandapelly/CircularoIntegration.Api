using CircularoIntegration.Api.Services.Dda;
using CircularoIntegration.Api.Services.Nuqi;
using Microsoft.AspNetCore.Mvc;

namespace CircularoIntegration.Api.Controllers;

/// <summary>
/// NUQI Gold X Mawarid – DDA (DD Marketplace) demo. Protected by the existing ApiKeyMiddleware
/// (X-Webhook-Key header or ?webhookKey=).
///
///  LSQ automation (stage → DDA)      POST /api/v1/nuqi/dda/initiate
///  DD Marketplace callback (if any)  POST /api/v1/nuqi/dda/callback
///  Demo / operations                 POST /api/v1/nuqi/dda/{leadId}/create     create now, no stage check
///                                    GET  /api/v1/nuqi/dda/{leadId}/status     add ?live=true to call Get Status first
///                                    POST /api/v1/nuqi/dda/{leadId}/refresh    Get Status now
///  Testing (nothing is created)      GET  /api/v1/nuqi/dda/debug/preview/{leadId}
///                                    GET  /api/v1/nuqi/dda/debug/auth
/// </summary>
[ApiController]
[Route("api/v1/nuqi/dda")]
public class DdaController : ControllerBase
{
          private readonly DdaMandateService _svc;
          public DdaController(DdaMandateService svc) => _svc = svc;

          private async Task<string> ReadBodyAsync()
          {
                    using var reader = new StreamReader(Request.Body);
                    return await reader.ReadToEndAsync();
          }

          private IActionResult ToResult(NuqiResult r) => StatusCode(r.HttpStatus, r.Body);

          [HttpPost("initiate")]
          public async Task<IActionResult> Initiate(CancellationToken ct) =>
              ToResult(await _svc.HandleStageWebhookAsync(await ReadBodyAsync(), ct));

          [HttpPost("callback")]
          public async Task<IActionResult> Callback(CancellationToken ct) =>
              ToResult(await _svc.HandleCallbackAsync(await ReadBodyAsync(), ct));

          [HttpPost("{leadId}/create")]
          public async Task<IActionResult> Create(string leadId, CancellationToken ct) =>
              ToResult(await _svc.CreateManualAsync(leadId, ct));

          [HttpGet("{leadId}/status")]
          public async Task<IActionResult> Status(string leadId, [FromQuery] bool live, CancellationToken ct) =>
              Ok(await _svc.GetStatusAsync(leadId, live, ct));

          [HttpPost("{leadId}/refresh")]
          public async Task<IActionResult> Refresh(string leadId, CancellationToken ct) =>
              ToResult(await _svc.RefreshLeadAsync(leadId, NuqiSource.ManualApi, ct));

          [HttpGet("debug/preview/{leadId}")]
          public async Task<IActionResult> Preview(string leadId, CancellationToken ct) =>
              ToResult(await _svc.PreviewAsync(leadId, ct));

          [HttpGet("debug/auth")]
          public async Task<IActionResult> Auth(CancellationToken ct)
          {
                    var (ok, msg) = await _svc.TestAuthAsync(ct);
                    return StatusCode(ok ? 200 : 502, new { ok, message = msg });
          }

          // LSQ webhook URL validation pings
          [HttpHead("initiate"), HttpGet("initiate")]
          public IActionResult Probe() => Ok(new { status = "READY", module = "NUQI_DDA" });
}
