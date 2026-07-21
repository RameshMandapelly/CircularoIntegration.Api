using System.ComponentModel.DataAnnotations.Schema;
namespace CircularoIntegration.Api.Data;



/// <summary>
/// One row per KFS send attempt. Used to prevent duplicate sends when the
/// LeadSquared stage-change webhook fires more than once for the same lead
/// (retries, re-saves, etc.).
/// </summary>
[Table("kfs_email_logs")]
public class KfsEmailLog
{
    [Column("id")]
    public int Id { get; set; }

    [Column("lead_id")]
    public string LeadId { get; set; } = string.Empty;

    [Column("recipient_email")]
    public string? RecipientEmail { get; set; }

    [Column("sent_at_utc")]
    public DateTime SentAtUtc { get; set; }

    [Column("success")]
    public bool Success { get; set; }

    [Column("error_message")]
    public string? ErrorMessage { get; set; }

}