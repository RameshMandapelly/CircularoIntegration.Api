using System.ComponentModel.DataAnnotations;

namespace CircularoIntegration.Api.Models;

public class PhaseDocumentConfig
{
    [Key]
    public int Id { get; set; }
    public int PhaseNo { get; set; }

    /// <summary>Lead stage that triggers sending, e.g. "OFFER ACCEPTED"</summary>
    public string LeadStageTrigger { get; set; } = string.Empty;

    /// <summary>Stage to move the lead to after signing, e.g. "MURABAHA STAGE 1"</summary>
    public string NextLeadStage { get; set; } = string.Empty;

    /// <summary>Circularo document id (from dashboard / Create document API)</summary>
    public string CircularoDocumentId { get; set; } = string.Empty;

    public string DocumentName { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Circularo file hash of the template PDF; a fresh document is created from it per lead.</summary>
    public string CircularoFileHash { get; set; } = string.Empty;

    // true = merge lead fields into a docx template and convert to PDF
    // (Offer Letter). false = send the static local template as-is.
    public bool RequiresFieldMerge { get; set; } = false;
    public int SignaturePage { get; set; } = 1;
}

public enum SigningStatus { Sent = 0, Signed = 1, Attached = 2, Failed = 9 }

public class SigningTransaction
{
    [Key]
    public long Id { get; set; }
    public string LeadId { get; set; } = string.Empty;
    public int PhaseNo { get; set; }

    /// <summary>0 for legacy single-document phases (Phase 1). For multi-doc sets,
    /// matches PhaseDocumentItem.DocIndex.</summary>
    public int DocIndex { get; set; }

    /// <summary>Circularo document / share reference used for correlation on callback</summary>
    public string CircularoRef { get; set; } = string.Empty;

    public string CustomerEmail { get; set; } = string.Empty;
    public SigningStatus Status { get; set; } = SigningStatus.Sent;

    public DateTime SentAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? SignedAtUtc { get; set; }
    public DateTime? AttachedAtUtc { get; set; }

    public string? ErrorMessage { get; set; }
    public int RetryCount { get; set; }

    public string? InboundPayload { get; set; }
    public string? CallbackPayload { get; set; }
}

public enum DocMergeStrategy { None, DocxTokens, PdfOverlay }

public class PhaseDocumentItem
{
    [Key]
    public int Id { get; set; }
    public int PhaseDocumentConfigId { get; set; }

    /// <summary>Stable 1-based identity for this doc within the set — used to correlate
    /// SigningTransaction rows and must not be reused/reordered once a lead is mid-flow.</summary>
    public int DocIndex { get; set; }

    public string DocumentName { get; set; } = string.Empty;

    /// <summary>Source file — .docx template (DocxTokens) or the static/legal PDF (PdfOverlay/None).
    /// BUG 1 FIX: this is the ONLY path property now. TemplatePdfPath has been deleted.</summary>
    public string TemplatePath { get; set; } = string.Empty;

    public DocMergeStrategy MergeStrategy { get; set; } = DocMergeStrategy.None;

    /// <summary>Only for PdfOverlay: JSON array of {"Token":"CustomerName","Page":1,"X":106,"Top":641.5,"FontSize":10}.
    /// Page is 1-based. X/Top are in PDF points, top-left origin (matches pdfplumber).</summary>
    public string? OverlayFieldsJson { get; set; }

    /// <summary>Page the Circularo signature box is placed on in the rendered PDF.</summary>
    public int SignaturePage { get; set; } = 1;

    public bool IsActive { get; set; } = true;
}
