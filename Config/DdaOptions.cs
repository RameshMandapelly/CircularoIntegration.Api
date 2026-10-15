namespace CircularoIntegration.Api.Config;

/// <summary>
/// Binds to the "Dda" section of appsettings.json – NUQI Gold X Mawarid, DD Marketplace (DDA) demo module.
/// A field mapping that is empty or starts with "TODO" is treated as "not configured".
/// Secrets (Gateway:ClientId / ClientSecret) belong in appsettings.Local.json, never in source control.
/// </summary>
public class DdaOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>LeadSquared stage that triggers mandate creation (demo: "DDA").</summary>
    public string TriggerStage { get; set; } = "DDA";

    public string ServiceActor { get; set; } = "svc-circularo-integration";

    /// <summary>
    /// UAE PASS signing link of DD Marketplace. {ddarId} is replaced with the ddarId returned by Create DDA.
    /// UAT: https://test.directdebit.ae/... ; production host will differ. Empty = no link is built.
    /// </summary>
    public string SigningUrlTemplate { get; set; } =
        "https://test.directdebit.ae/api/appApi/v1/customer/uaepass-signer-process?mode=web&ddarId={ddarId}&redirectUrl=https://www.mawarid.ae/en";

    /// <summary>UAT/demo only: when a lead field is empty, use Dda:Fallbacks[bodyField] instead of blocking. Keep false in production.</summary>
    public bool AllowFallbacks { get; set; } = false;
    public Dictionary<string, string> Fallbacks { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public DdaGatewayOptions Gateway { get; set; } = new();
    public DdaLsqFieldMap LsqFields { get; set; } = new();
    public DdaMandateDefaults Mandate { get; set; } = new();
    public DdaTrackingOptions Tracking { get; set; } = new();
}

public class DdaGatewayOptions
{
    /// <summary>e.g. https://uatapp.mawarid.ae/FinDDA</summary>
    public string BaseUrl { get; set; } = "https://uatapp.mawarid.ae/FinDDA";
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    /// <summary>JWT lives 60 minutes; refresh a little earlier.</summary>
    public int TokenCacheMinutes { get; set; } = 55;
    public int TimeoutSeconds { get; set; } = 60;
}

/// <summary>LeadSquared schema names. >>> CONFIRM with Devinder / LSQ admin. <<<</summary>
public class DdaLsqFieldMap
{
    // ---- read ----
    public string FirstName { get; set; } = "FirstName";
    public string LastName { get; set; } = "LastName";
    public string FullName { get; set; } = "";                          // if set and filled, wins over First+Last
    public string Email { get; set; } = "EmailAddress";
    public string Mobile { get; set; } = "Mobile";
    public string EmiratesId { get; set; } = "mx_Emirates_Identification_Number";
    public string CustomerType { get; set; } = "LeadType";              // LSQ dropdown: Individual / Non-Individual (sent as it is)
    public string DdaReferenceNumber { get; set; } = "mx_DDAReferenceNumber"; // entered by the Maker (unique, e.g. CIF-based) - not generated
    public string PaymentMethod { get; set; } = "";                     // empty = Mandate:PaymentMethod
    public string CustomerIdType { get; set; } = "";                    // empty = Mandate:CustomerIdType
    public string CustomerNid { get; set; } = "";                       // custNid; empty = same as the Emirates ID
    public string BankName { get; set; } = "TODO_mx_Bank_Name";
    public string BankAccountNumber { get; set; } = "TODO_mx_IBAN";    // IBAN, AE + 21 digits
    public string BankAccountType { get; set; } = "";                   // empty = Mandate:BankAccountType
    public string BankAccountTitle { get; set; } = "";                  // empty = customer full name
    public string CardNumber { get; set; } = "TODO_mx_Card_Number";    // only if Mandate:PaymentMethod = "Credit Card"
    public string CardHolderName { get; set; } = "";                    // empty = customer full name
    public string CommencesOn { get; set; } = "";                       // empty = today + Mandate:StartOffsetDays
    public string ExpiresOn { get; set; } = "";                         // empty = commences + Mandate:TenorYears
    public string AmountType { get; set; } = "";                        // empty = Mandate:AmountType
    public string PaymentFrequency { get; set; } = "";                  // empty = Mandate:PaymentFrequency
    public string MinAmount { get; set; } = "";                         // empty = Mandate:MinAmount
    public string MaxAmount { get; set; } = "TODO_mx_Credit_Approved_Amount"; // empty/missing = Mandate:DefaultMaxAmount

    // ---- write-back (optional – skipped while TODO/empty; a lead note is always written) ----
    public string WriteDdaStatus { get; set; } = "TODO_mx_DDA_Status";
    public string WriteDdaId { get; set; } = "TODO_mx_DDA_Id";
    /// <summary>Receives the ddarId returned by Create DDA. Skipped while TODO/empty.</summary>
    public string WriteDdarId { get; set; } = "TODO_mx_DDAR_Id";
    /// <summary>Receives the UAE PASS signing link. Skipped while TODO/empty.</summary>
    public string WriteSigningUrl { get; set; } = "TODO_mx_DDA_Signing_Url";
    /// <summary>Receives the 23-digit DDA reference on ACTIVE. Must NOT be the Maker's DdaReferenceNumber field.</summary>
    public string WriteDdaReference { get; set; } = "TODO_mx_DDA_Active_Reference";
}

/// <summary>Constants of the Create-DDA body, from the DD Marketplace UAT Postman sample.</summary>
public class DdaMandateDefaults
{
    /// <summary>"Bank Account" (Postman-proven) or "Credit Card" (BRD Table 9A). >>> CONFIRM which one DDMP expects for NUQI. <<<</summary>
    public string PaymentMethod { get; set; } = "Bank Account";
    public string CustomerIdType { get; set; } = "UAE Emirates Identity Card";
    /// <summary>Optional LeadSquared value → DD Marketplace customerType. A value that is not listed is sent as it is.</summary>
    public Dictionary<string, string> CustomerTypeMap { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string BankAccountType { get; set; } = "Current";
    public string AmountType { get; set; } = "Variable";
    public string PaymentFrequency { get; set; } = "Monthly";
    public decimal MinAmount { get; set; } = 1;
    /// <summary>Used when the lead has no max-amount value. 0 = none (then a missing value blocks).</summary>
    public decimal DefaultMaxAmount { get; set; } = 0;
    public int StartOffsetDays { get; set; } = 1;
    public int TenorYears { get; set; } = 2;
    public string DateFormat { get; set; } = "dd/MM/yyyy";
}

public class DdaTrackingOptions
{
    public bool Enabled { get; set; } = true;
    /// <summary>Demo: 1-2. Production: 60 (or rely on the callback).</summary>
    public int IntervalMinutes { get; set; } = 2;
    /// <summary>BRD DDA-19 "after 5 PM": production 17. 0 = poll around the clock (demo).</summary>
    public int PollWindowStartHourDubai { get; set; } = 0;
    public int MaxAgeDays { get; set; } = 10;
    public int MaxPerSweep { get; set; } = 50;
    public bool AttachUnsignedPdf { get; set; } = true;
    public bool AttachSignedPdf { get; set; } = true;

    /// <summary>JSON property names searched (recursively, case-insensitive) in gateway responses.</summary>
    public string[] DdaIdFields { get; set; } = { "ddaId", "id", "mandateId" };
    public string[] DdarIdFields { get; set; } = { "ddarId" };
    public string[] StatusFields { get; set; } = { "status", "ddaStatus", "mandateStatus", "state" };
    public string[] ReasonFields { get; set; } = { "rejectionReason", "rejectReason", "reason", "errorMessage", "message", "error" };
    public string[] ReferenceFields { get; set; } = { "ddaReference", "mandateReference", "ddaMandateId", "uaeddsReference", "referenceNumber" };

    // status words (lower-case, compared against the gateway status text) → our normalised status
    public string[] ActiveValues { get; set; } = { "active", "activated", "accepted" };
    public string[] RejectedWords { get; set; } = { "reject", "fail", "declin", "error", "expire" };
    public string[] CancelledWords { get; set; } = { "cancel", "discard" };
    public string[] BankWords { get; set; } = { "bank", "cbuae", "submitted", "processing", "signed" };
    public string[] ConsentWords { get; set; } = { "consent", "unsigned", "awaiting", "pending customer", "pending signature" };
}
