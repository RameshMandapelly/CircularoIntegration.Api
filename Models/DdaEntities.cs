using System.ComponentModel.DataAnnotations;

namespace CircularoIntegration.Api.Models;

/// <summary>Normalised mandate statuses stored in nuqi_dda_request.Status (BRD §9.1.5 wording).</summary>
public static class DdaStatus
{
    public const string Creating       = "Creating";
    public const string Created        = "Created";
    public const string PendingConsent = "Pending Customer Consent";
    public const string PendingBank    = "Pending Bank Approval";
    public const string Active         = "ACTIVE";
    public const string Rejected       = "REJECTED";
    public const string Cancelled      = "CANCELLED";
    public const string Failed         = "FAILED";

    /// <summary>No more polling after these.</summary>
    public static readonly string[] Final = { Active, Rejected, Cancelled, Failed };

    /// <summary>A new mandate may be created for the lead after one of these.</summary>
    public static readonly string[] Retryable = { Failed, Rejected, Cancelled };
}

/// <summary>One DD Marketplace mandate request for a lead (table nuqi_dda_request).</summary>
public class DdaRequest
{
    [Key] public long Id { get; set; }
    public string LeadId { get; set; } = string.Empty;
    public int Version { get; set; }
    public string IdempotencyKey { get; set; } = string.Empty;
    public string CorrelationId { get; set; } = string.Empty;
    /// <summary>Our reference sent as ddaReferenceNumber.</summary>
    public string? DdaReferenceNumber { get; set; }
    /// <summary>Id returned by DD Marketplace on Create.</summary>
    public string? DdaId { get; set; }
    /// <summary>ddarId returned by DD Marketplace on Create (the DDA request id the customer signs against).</summary>
    public string? DdarId { get; set; }
    public string Status { get; set; } = DdaStatus.Creating;
    /// <summary>Raw status text from the gateway.</summary>
    public string? GatewayStatus { get; set; }
    /// <summary>23-digit DDA reference stored on activation (BRD DDA-18).</summary>
    public string? ActiveReference { get; set; }
    public string? FailureReason { get; set; }
    /// <summary>Masked summary of what was sent (no full EID / IBAN / card).</summary>
    public string? RequestSummary { get; set; }
    /// <summary>Last gateway status response (redacted, truncated).</summary>
    public string? LastResponse { get; set; }
    public bool UnsignedPdfAttached { get; set; }
    public bool SignedPdfAttached { get; set; }
    public int PollCount { get; set; }
    public DateTime? LastStatusCheckAtUtc { get; set; }
    public DateTime? LastStatusChangeAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}
