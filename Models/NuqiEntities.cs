using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace CircularoIntegration.Api.Models;

// =====================================================================================
// NUQI Gold X Mawarid – Approved Stage Automation entities.
// Mapped by NuqiDbContext (separate from AppDbContext so the existing flows are untouched).
// Column names == property names (PascalCase), same convention as phase_document_config.
//
// v3 LEAN: only 3 tables (nuqi_approval_event, nuqi_package_document, integration_audit).
// Package config + templates are NOT tables any more: they are plain POCOs bound from
// appsettings.json (section NuqiApproved: Package / Templates / Notifications).
// =====================================================================================

/// <summary>Document- and package-level statuses (BRD DOC-09). Stored as strings.</summary>
public static class NuqiStatus
{
          public const string NotInitiated = "NotInitiated";
          public const string Validating = "Validating";
          public const string Failed = "Failed";
          public const string Sent = "Sent";
          public const string Delivered = "Delivered";
          public const string Viewed = "Viewed";
          public const string CustomerSigned = "CustomerSigned";
          public const string MawaridSigned = "MawaridSigned";
          public const string Completed = "Completed";
          public const string Declined = "Declined";
          public const string Expired = "Expired";
          public const string Cancelled = "Cancelled";
          public const string Superseded = "Superseded";

          /// <summary>Statuses after which nothing else may happen on that package.</summary>
          public static readonly string[] Terminal = { Failed, Cancelled, Superseded };

          /// <summary>Statuses where the package is still waiting for signatures.</summary>
          public static readonly string[] InFlight = { Sent, Delivered, Viewed, CustomerSigned, MawaridSigned };

          /// <summary>Progress order used to aggregate document statuses into a package status.</summary>
          public static int Rank(string s) => s switch
          {
                    NotInitiated => 0,
                    Sent => 1,
                    Delivered => 2,
                    Viewed => 3,
                    CustomerSigned => 4,
                    MawaridSigned => 5,
                    Completed => 6,
                    _ => -1
          };

          /// <summary>Friendly text written to LeadSquared (BRD DOC-09 wording).</summary>
          public static string ToLsqText(string s) => s switch
          {
                    NotInitiated => "Not Initiated",
                    CustomerSigned => "Customer Signed",
                    MawaridSigned => "Mawarid Signed",
                    Superseded => "Cancelled",
                    Validating => "Not Initiated",
                    _ => s
          };
}

public static class NuqiDocCode
{
          public const string TermsAndConditions = "TNC";
          public const string MasterMurabaha = "MMA";
          public static readonly string[] AllowList = { TermsAndConditions, MasterMurabaha };
}

// =====================================================================================
// CONFIGURATION POCOs – bound from appsettings.json (NuqiApproved:Package / :Templates).
// NOT database tables.
// =====================================================================================

public class NuqiPackageConfig
{
          public string PreviousStage { get; set; } = "Under Approval";
          public string TriggerStage { get; set; } = "Approved";
          public string NextStage { get; set; } = "Operations Evaluation";
          public decimal ProfitRatePercent { get; set; } = 3.10m;
          public string Currency { get; set; } = "AED";
          public string? DefaultMawaridSignerName { get; set; }
          public string? DefaultMawaridSignerEmail { get; set; }
          public int LinkValidDays { get; set; } = 7;
          public bool AutoAdvanceOnCompletion { get; set; } = true;
          public bool AutoRegenerateOnAmountChange { get; set; } = true;
          public bool CancelSiblingsOnDecline { get; set; } = true;
          /// <summary>Customer approval email (simplified flow step 2). Placeholders: {Amount} {CashLimit} {CustomerName} {ApplicationId}.</summary>
          public bool SendApprovalEmail { get; set; } = true;
          public string? ApprovalEmailSubject { get; set; }
          public string? ApprovalEmailBodyHtml { get; set; }
          public bool IsActive { get; set; } = true;
}

/// <summary>One Circularo signature box in a template. Page -1 = last page. X/Y/W/H are fractions of the page (0..1).</summary>
public class NuqiSignatureFieldConfig
{
          public string Role { get; set; } = string.Empty;      // Customer | Mawarid
          public int Page { get; set; } = -1;
          public double X { get; set; }
          public double Y { get; set; }
          public double W { get; set; }
          public double H { get; set; }
          public bool Timestamp { get; set; }
}

public class NuqiPackageTemplate
{
          public int DocIndex { get; set; }
          public string DocumentCode { get; set; } = string.Empty;          // TNC | MMA
          public string DocumentName { get; set; } = string.Empty;
          public string TemplatePath { get; set; } = string.Empty;
          public string TemplateVersion { get; set; } = string.Empty;
          /// <summary>Legal/Sharia approval. false → the package is blocked.</summary>
          public bool IsApproved { get; set; }
          /// <summary>Same int values as the existing DocMergeStrategy enum (0 None, 1 DocxTokens, 2 PdfOverlay, 3 AcroFormFill).</summary>
          public DocMergeStrategy MergeStrategy { get; set; } = DocMergeStrategy.None;
          /// <summary>JSON text (escaped in appsettings.json) – used by PdfOverlay.</summary>
          public string? OverlayFieldsJson { get; set; }
          /// <summary>JSON text (escaped in appsettings.json) – used by AcroFormFill.</summary>
          public string? FormFieldMappingJson { get; set; }
          public List<string> RequiredTokens { get; set; } = new();
          public List<NuqiSignatureFieldConfig> SignatureFields { get; set; } = new();
          public bool IsActive { get; set; } = true;

          public IEnumerable<string> RequiredTokenList => RequiredTokens.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim());
}

// =====================================================================================
// DATABASE TABLES (3)
// =====================================================================================

public class NuqiApprovalEvent
{
          [Key] public long Id { get; set; }
          public string LeadId { get; set; } = string.Empty;
          public int ApprovalEventVersion { get; set; }
          public string IdempotencyKey { get; set; } = string.Empty;
          public string CorrelationId { get; set; } = string.Empty;
          public string? ApplicationId { get; set; }
          public decimal? ApprovedAmount { get; set; }
          public decimal? CashWithdrawalLimit { get; set; }
          public string? Currency { get; set; }
          public string? CustomerEmail { get; set; }
          public string? MawaridSignerName { get; set; }
          public string? MawaridSignerEmail { get; set; }
          public string? ApprovalEmailStatus { get; set; }          // SENT | FAILED | SKIPPED
          public DateTime? ApprovalEmailSentAtUtc { get; set; }
          public string PackageStatus { get; set; } = NuqiStatus.Validating;
          public string? FailureReason { get; set; }
          public long? SupersededByEventId { get; set; }
          public DateTime? GateProcessedAtUtc { get; set; }
          public DateTime? LastEventAtUtc { get; set; }
          public DateTime? CompletedAtUtc { get; set; }
          public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
          public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

          /// <summary>Display reference, e.g. NUQI-3d768a35-v1 (derived – no column).</summary>
          [NotMapped]
          public string PackageReference => $"NUQI-{(LeadId.Length >= 8 ? LeadId[..8] : LeadId)}-v{ApprovalEventVersion}";

          /// <summary>true once the completion gate finished successfully (derived – no column).</summary>
          [NotMapped]
          public bool GateOpened => GateProcessedAtUtc is not null && CompletedAtUtc is not null;
}

public class NuqiPackageDocument
{
          [Key] public long Id { get; set; }
          public long ApprovalEventId { get; set; }
          public int DocIndex { get; set; }
          public string DocumentCode { get; set; } = string.Empty;
          public string TemplateVersion { get; set; } = string.Empty;
          public string? CircularoDocumentId { get; set; }
          public string? GeneratedSha256 { get; set; }
          /// <summary>Pages of the generated PDF – needed later to place Mawarid's signature box (T&amp;C: last page).</summary>
          public int? PageCount { get; set; }
          public string DocumentStatus { get; set; } = NuqiStatus.NotInitiated;
          public DateTime? CustomerSignedAtUtc { get; set; }
          public DateTime? MawaridSignedAtUtc { get; set; }
          /// <summary>When this document was shared with the Mawarid signer (after the customer signed).</summary>
          public DateTime? MawaridSharedAtUtc { get; set; }
          public DateTime? CompletedAtUtc { get; set; }
          public bool ExecutedAttached { get; set; }
          public string? LastEvent { get; set; }
          public DateTime? LastEventAtUtc { get; set; }
          public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
          public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

public class IntegrationAudit
{
          [Key] public long Id { get; set; }
          public string CorrelationId { get; set; } = string.Empty;
          public string? LeadId { get; set; }
          public string Module { get; set; } = "NUQI_APPROVED";
          public string Action { get; set; } = string.Empty;
          public string? OldValue { get; set; }
          public string? NewValue { get; set; }
          public string Actor { get; set; } = string.Empty;
          public string SourceChannel { get; set; } = string.Empty;
          public string Outcome { get; set; } = string.Empty;
          public string? Detail { get; set; }
          public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
