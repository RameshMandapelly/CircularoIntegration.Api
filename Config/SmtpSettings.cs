namespace CircularoIntegration.Api.Config;

/// <summary>
/// Bind this to the "SmtpSettings" section in appsettings.json.
/// Generic mail-server connection settings — not KFS-specific — so any
/// future email feature can reuse the same EmailService.
/// </summary>
public class SmtpSettings
{
          public string Host { get; set; } = string.Empty;
          public int Port { get; set; } = 587;
          public string SenderEmail { get; set; } = string.Empty;
          public string SenderName { get; set; } = string.Empty;
          public string Username { get; set; } = string.Empty;
          public string Password { get; set; } = string.Empty;
          public bool UseStartTls { get; set; } = true;
}