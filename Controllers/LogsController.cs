using System.Net;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using MySqlConnector;

namespace CircularoIntegration.Api.Controllers;

/// <summary>
/// Simple built-in log viewer for the mwf_circlero_logs table (Serilog MySQL sink).
/// Usage:  GET /logs?key=SECRET            – last 100 entries
///         GET /logs?key=SECRET&take=500   – last 500 (max 2000)
///         GET /logs?key=SECRET&level=Error – filter by level (Information/Warning/Error)
///         GET /logs?key=SECRET&search=initiate – text search in message
/// The key must equal Security:WebhookApiKey (query param, since browsers can't send custom headers).
/// NOTE: add "/logs" to the ApiKeyMiddleware public paths – auth is handled here via ?key=.
/// </summary>
[ApiController]
[Route("logs")]
public class LogsController : ControllerBase
{
          private readonly IConfiguration _cfg;
          private readonly ILogger<LogsController> _log;

          public LogsController(IConfiguration cfg, ILogger<LogsController> log)
          {
                    _cfg = cfg;
                    _log = log;
          }

          [HttpGet]
          public async Task<IActionResult> Index(
              [FromQuery] string? key,
              [FromQuery] int take = 100,
              [FromQuery] string? level = null,
              [FromQuery] string? search = null)
          {
                    // --- auth: same secret as the webhook key, passed as query param ---
                    var expected = _cfg["Security:WebhookApiKey"];
                    if (!string.IsNullOrWhiteSpace(expected) && key != expected)
                              return Unauthorized(new { message = "Invalid or missing key", status = 401 });

                    take = Math.Clamp(take, 1, 2000);

                    var sql = new StringBuilder(
                        "SELECT id, Timestamp, Level, Message, Exception FROM mwf_circlero_logs WHERE 1=1");
                    var cmdParams = new List<MySqlParameter>();

                    if (!string.IsNullOrWhiteSpace(level))
                    {
                              sql.Append(" AND Level = @level");
                              cmdParams.Add(new MySqlParameter("@level", level));
                    }
                    if (!string.IsNullOrWhiteSpace(search))
                    {
                              sql.Append(" AND Message LIKE @search");
                              cmdParams.Add(new MySqlParameter("@search", $"%{search}%"));
                    }
                    sql.Append(" ORDER BY id DESC LIMIT @take");
                    cmdParams.Add(new MySqlParameter("@take", take));

                    var rows = new List<(long Id, string Ts, string Level, string Message, string? Exception)>();

                    await using (var conn = new MySqlConnection(_cfg.GetConnectionString("MySql")))
                    {
                              await conn.OpenAsync();
                              await using var cmd = new MySqlCommand(sql.ToString(), conn);
                              cmd.Parameters.AddRange(cmdParams.ToArray());
                              await using var reader = await cmd.ExecuteReaderAsync();
                              while (await reader.ReadAsync())
                              {
                                        rows.Add((
                                            reader.GetInt64(0),
                                            reader.GetValue(1)?.ToString() ?? "",
                                            reader.IsDBNull(2) ? "" : reader.GetString(2),
                                            reader.IsDBNull(3) ? "" : reader.GetString(3),
                                            reader.IsDBNull(4) ? null : reader.GetString(4)));
                              }
                    }

                    // --- render a self-contained HTML page ---
                    var html = new StringBuilder();
                    html.Append("""
<!DOCTYPE html><html><head><meta charset="utf-8"><title>Circularo Integration Logs</title>
<meta http-equiv="refresh" content="30">
<style>
 body{font-family:Segoe UI,Arial,sans-serif;margin:16px;background:#f7f9fb;color:#222}
 h2{color:#1F4E79;margin:0 0 4px} .sub{color:#777;font-size:12px;margin-bottom:12px}
 form{margin-bottom:12px} input,select{padding:4px 8px;margin-right:6px}
 table{border-collapse:collapse;width:100%;background:#fff;font-size:13px}
 th{background:#1F4E79;color:#fff;padding:6px 8px;text-align:left;position:sticky;top:0}
 td{padding:5px 8px;border-bottom:1px solid #e3e8ee;vertical-align:top}
 tr:hover{background:#eef4fb}
 .INF{color:#1a7f37}.WRN,.Warning{color:#b7791f;font-weight:600}.ERR,.Error{color:#c0392b;font-weight:700}
 .msg{max-width:900px;word-break:break-word;white-space:pre-wrap}
 .exc{color:#c0392b;font-size:12px;white-space:pre-wrap}
</style></head><body>
<h2>Circularo Integration – Logs</h2>
<div class="sub">Source: mwf_circlero_logs &middot; auto-refreshes every 30s &middot; newest first</div>
""");
                    html.Append($"""
<form method="get">
 <input type="hidden" name="key" value="{WebUtility.HtmlEncode(key ?? "")}"/>
 Level: <select name="level">
   <option value="">All</option>
   <option {(level == "Information" ? "selected" : "")}>Information</option>
   <option {(level == "Warning" ? "selected" : "")}>Warning</option>
   <option {(level == "Error" ? "selected" : "")}>Error</option>
 </select>
 Search: <input name="search" value="{WebUtility.HtmlEncode(search ?? "")}" placeholder="e.g. initiate"/>
 Rows: <input name="take" type="number" value="{take}" style="width:70px"/>
 <button type="submit">Apply</button>
</form>
<table><tr><th>Id</th><th>Timestamp</th><th>Level</th><th>Message</th></tr>
""");
                    foreach (var r in rows)
                    {
                              var lvlClass = r.Level.StartsWith("Err") ? "ERR" : r.Level.StartsWith("Warn") ? "WRN" : "INF";
                              html.Append($"<tr><td>{r.Id}</td><td>{WebUtility.HtmlEncode(r.Ts)}</td>")
                                  .Append($"<td class=\"{lvlClass}\">{WebUtility.HtmlEncode(r.Level)}</td>")
                                  .Append($"<td class=\"msg\">{WebUtility.HtmlEncode(r.Message)}");
                              if (r.Exception is not null)
                                        html.Append($"<div class=\"exc\">{WebUtility.HtmlEncode(r.Exception)}</div>");
                              html.Append("</td></tr>");
                    }
                    html.Append($"</table><div class=\"sub\">{rows.Count} rows shown</div></body></html>");

                    return Content(html.ToString(), "text/html; charset=utf-8");
          }
}
