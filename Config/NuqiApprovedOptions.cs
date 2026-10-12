using CircularoIntegration.Api.Models;

namespace CircularoIntegration.Api.Config;

/// <summary>
/// Binds to the "NuqiApproved" section in appsettings.json (v3 lean – no config tables):
///   Package        stages, signer, link days, approval email text  (was nuqi_package_config)
///   Templates      the 2 documents, signature boxes, approval flag (was nuqi_package_template)
///   Notifications  internal recipient emails                       (was nuqi_notification_recipient)
///   LsqFields / Validation / CircularoEvents / Reconciliation / LsqTask = technical mapping + behaviour flags.
/// Changes need an appsettings edit + app restart (keep under change control).
/// A field mapping left empty or starting with "TODO" is treated as "not configured".
/// </summary>
public class NuqiApprovedOptions
{
          public string ProductCode { get; set; } = "NUQI_GOLD";

          /// <summary>If the webhook payload carries a "Before" stage, it must equal PreviousStage (BRD §6.1).</summary>
          public bool StrictPreviousStage { get; set; } = true;

          /// <summary>true = reject webhooks that do NOT carry a Before stage. Keep false until LSQ payload is confirmed.</summary>
          public bool RequireBeforeStageInPayload { get; set; } = false;

          /// <summary>Optional product guard so a generic "Approved" stage of another product is ignored.</summary>
          public string? ProductFilterField { get; set; }
          public string? ProductFilterValue { get; set; }

          /// <summary>Exhibit A amount format. "#,##0" → 24,900 ; "#,##0.00" → 24,900.00 (Credit to confirm rounding).</summary>
          public string AmountFormat { get; set; } = "#,##0";

          /// <summary>Service account written into the audit trail (BRD §13 Integration Service).</summary>
          public string ServiceActor { get; set; } = "svc-circularo-integration";

          public NuqiPackageConfig Package { get; set; } = new();
          public List<NuqiPackageTemplate> Templates { get; set; } = new();
          public NuqiNotificationOptions Notifications { get; set; } = new();

          public NuqiLsqFieldMap LsqFields { get; set; } = new();
          public NuqiValidationRules Validation { get; set; } = new();
          public NuqiCircularoEventMap CircularoEvents { get; set; } = new();
          public NuqiReconcileOptions Reconciliation { get; set; } = new();
          public NuqiLsqTaskOptions LsqTask { get; set; } = new();
}

/// <summary>Internal emails (BRD §15). Placeholders containing "CONFIRM" are ignored.</summary>
public class NuqiNotificationOptions
{
          public List<string> CompletedEmails { get; set; } = new();   // package fully signed
          public List<string> ExceptionEmails { get; set; } = new();   // blocked / failed / declined / mismatch
}

/// <summary>LeadSquared schema names. >>> CONFIRM all with Devinder / LSQ admin. <<<</summary>
public class NuqiLsqFieldMap
{
          // ---- read (trigger + validation + merge) ----
          public string CreditApprovedAmount { get; set; } = "TODO_mx_Credit_Approved_Amount";
          public string CashWithdrawalLimit { get; set; } = "TODO_mx_Cash_Withdrawal_Limit";
          public string Currency { get; set; } = "";                         // empty = assume config currency (AED)
          public string CustomerFirstName { get; set; } = "FirstName";
          public string CustomerLastName { get; set; } = "LastName";
          public string CustomerFullName { get; set; } = "";                 // if set, wins over First+Last
          public string CustomerEmail { get; set; } = "EmailAddress";
          public string CustomerMobile { get; set; } = "Mobile";
          public string ApplicationId { get; set; } = "ProspectAutoId";
          public string PartnerReference { get; set; } = "TODO_mx_Nuqi_Reference";
          public string ApprovalEventVersion { get; set; } = "";             // empty = middleware generates the version
          public string FinalApprover { get; set; } = "TODO_mx_Final_Approver";
          public string FinalApprovalDate { get; set; } = "TODO_mx_Final_Approval_Date";
          public string FinalDecision { get; set; } = "TODO_mx_Final_Decision";
          public string MawaridSignerName { get; set; } = "TODO_mx_BD_Signer_Name";
          public string MawaridSignerEmail { get; set; } = "TODO_mx_BD_Signer_Email";
          public string CurrentStage { get; set; } = "ProspectStage";                 // same as LeadSquared:StageField

          // ---- write-back (DOC-06, DOC-09, DOC-10) ----
          public string PackageStatus { get; set; } = "TODO_mx_Nuqi_Package_Status";
          public string TncStatus { get; set; } = "TODO_mx_Nuqi_TnC_Status";
          public string MmaStatus { get; set; } = "TODO_mx_Nuqi_MMA_Status";
          public string PackageReference { get; set; } = "TODO_mx_Nuqi_Envelope_Id";
          public string PackageAmount { get; set; } = "TODO_mx_Nuqi_Package_Amount";
          public string DocsCompletedFlag { get; set; } = "TODO_mx_Nuqi_Docs_Completed";
          public string ExceptionReason { get; set; } = "TODO_mx_Nuqi_Exception_Reason";
}

public class NuqiValidationRules
{
          /// <summary>DOC-08 / DOC-14: package config profit rate must equal this. Two sources, both under change control.</summary>
          public decimal ExpectedProfitRate { get; set; } = 3.10m;
          public string ExpectedCurrency { get; set; } = "AED";
          /// <summary>Exhibit A item 2 must be filled (simplified flow).</summary>
          public bool RequireCashWithdrawalLimit { get; set; } = true;
          /// <summary>Block when cash withdrawal limit &gt; credit limit. >>> CONFIRM with Credit <<<</summary>
          public bool CashLimitMustNotExceedCreditLimit { get; set; } = true;
          public bool RequireApplicationId { get; set; } = true;
          public bool RequirePartnerReference { get; set; } = false;      // switch on when LSQ field exists
          public bool RequireFinalApprovalDetails { get; set; } = false;  // switch on when LSQ fields exist
          public string ExpectedFinalDecision { get; set; } = "Approved";
          /// <summary>UAE mobile: 05XXXXXXXX, 9715XXXXXXXX, +9715XXXXXXXX, 009715XXXXXXXX.</summary>
          public string MobileRegex { get; set; } = @"^(?:\+?971|00971|0)?5\d{8}$";
}

/// <summary>
/// Circularo webhook event names → our meaning. Names seen so far in logs: documentCreated,
/// signRequest, share_seen, documentSigned, documentCompleted. Decline/expire names
/// >>> CONFIRM with Harish <<< – add the real names here, no code change needed.
/// </summary>
public class NuqiCircularoEventMap
{
          public string[] Sent { get; set; } = { "signRequest", "shareCreated", "share_created" };
          public string[] Viewed { get; set; } = { "share_seen", "shareSeen", "documentViewed", "viewed" };
          public string[] Signed { get; set; } = { "documentSigned", "signed", "shareSigned" };
          public string[] Completed { get; set; } = { "documentCompleted", "completed", "workflowCompleted" };
          public string[] Declined { get; set; } = { "documentRejected", "shareRejected", "rejected", "declined", "signRejected" };
          public string[] Expired { get; set; } = { "shareExpired", "documentExpired", "expired" };

          /// <summary>Values found in GET /documents/{id} status-like fields during reconciliation.</summary>
          public string[] DocStateCompleted { get; set; } = { "completed", "finished" };
          public string[] DocStateDeclined { get; set; } = { "rejected", "declined" };
          public string[] DocStateExpired { get; set; } = { "expired" };
}

public class NuqiReconcileOptions
{
          public bool Enabled { get; set; } = true;
          public int IntervalMinutes { get; set; } = 30;
          /// <summary>Only packages with no callback for this many hours are checked (BRD §8 "callback not received").</summary>
          public int StaleAfterHours { get; set; } = 4;
          /// <summary>After this many days without Circularo completion evidence an [ACTION REQUIRED] exception is raised once and checking stops.</summary>
          public int MaxAgeDays { get; set; } = 8;
}

/// <summary>LeadSquared task creation for exceptions (§8, §15). Falls back to a lead note if disabled/failing.</summary>
public class NuqiLsqTaskOptions
{
          public bool Enabled { get; set; } = false;               // >>> enable after verifying Task API on tenant <<<
          public string CreateUrlTemplate { get; set; } = "/v2/Task.svc/Create";
          public string TaskTypeName { get; set; } = "Follow Up";
          public string? DefaultOwnerEmail { get; set; }
          public int DueInHours { get; set; } = 24;
}
