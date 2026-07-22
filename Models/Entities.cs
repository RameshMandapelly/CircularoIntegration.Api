using System.ComponentModel.DataAnnotations;

namespace CircularoIntegration.Api.Models;

public class PhaseDocumentConfig
{
    [Key]
    public int Id { get; set; }
    public int PhaseNo { get; set; }

    /// <summary>Lead stage that triggers sending, e.g. "Phase 1 Doc Sign"</summary>
    public string LeadStageTrigger { get; set; } = string.Empty;

    /// <summary>Stage to move the lead to after signing, e.g. "Phase 2 Doc Sign"</summary>
    public string NextLeadStage { get; set; } = string.Empty;

    /// <summary>Circularo document id (from dashboard / Create document API)</summary>
    public string CircularoDocumentId { get; set; } = string.Empty;

    public string DocumentName { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    /// <summary>Circularo file hash of the template PDF; a fresh document is created from it per lead.</summary>
    public string CircularoFileHash { get; set; } = string.Empty;
    // NEW: true = merge lead fields into a docx template and convert to PDF
    // (Offer Letter). false = send the static local template as-is (current behavior).
    public bool RequiresFieldMerge { get; set; } = false;
}

public enum SigningStatus { Sent = 0, Signed = 1, Attached = 2, Failed = 9 }

public class SigningTransaction
{
    [Key]
    public long Id { get; set; }
    public string LeadId { get; set; } = string.Empty;
    public int PhaseNo { get; set; }

    /// <summary>Circularo document / share reference used for correlation on callback</summary>
    public string CircularoRef { get; set; } = string.Empty;

    public string CustomerEmail { get; set; } = string.Empty;
    public SigningStatus Status { get; set; } = SigningStatus.Sent;

    public DateTime SentAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? SignedAtUtc { get; set; }
    public DateTime? AttachedAtUtc { get; set; }

    public string? ErrorMessage { get; set; }
    public int RetryCount { get; set; }

    /// <summary>Raw inbound webhook payloads kept for troubleshooting/audit</summary>
    public string? InboundPayload { get; set; }
    public string? CallbackPayload { get; set; }
}
