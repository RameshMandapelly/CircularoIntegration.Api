namespace CircularoIntegration.Api.Config;

/// <summary>
/// Bind this to the "KfsEmail" section in appsettings.json.
/// </summary>
public class KfsEmailOptions
{
    
    /// <summary>
    /// The exact LeadSquared stage name that should trigger the KFS email.
    /// Confirm the final string with Hakima once it's finalized in LeadSquared.
    /// </summary>
    public string TriggerStageName { get; set; } = "Gift Created Pending Offer Acceptance";

    /// <summary>
    /// Full path to the static KFS file on disk (or a mounted network/blob path).
    /// Same file is used for every lead.
    // /// </summary>
    public string KfsFilePath { get; set; } = string.Empty;

    // public string SmtpHost { get; set; } = string.Empty;
    // public int SmtpPort { get; set; } = 587;
    // public string SmtpUsername { get; set; } = string.Empty;
    // public string SmtpPassword { get; set; } = string.Empty;
    // public bool UseSsl { get; set; } = true;

    // public string FromAddress { get; set; } = string.Empty;
    // public string FromDisplayName { get; set; } = "Mawarid Finance";
}
