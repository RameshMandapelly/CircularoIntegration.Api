using System.Diagnostics;
using System.Text;

namespace CircularoIntegration.Api.Services;

/// <summary>
/// Fills real AcroForm PDF fields (Sale Contract, Agency Appointment Letter,
/// Sale Instruction Confirmation) via pdftk, shelled out as a subprocess —
/// same operational pattern as the existing LibreOffice docx→pdf conversion,
/// so no new licensing question and no new deployment model to reason about.
///
/// pdftk must be installed and on PATH on the IIS box (same class of
/// dependency as LibreOffice — verify with `pdftk --version` on the server
/// before first use, same as the loadUserProfile lesson learned earlier).
/// </summary>
public class AcroFormFillService
{
          private readonly ILogger<AcroFormFillService> _log;
          private readonly IConfiguration _cfg;

          public AcroFormFillService(ILogger<AcroFormFillService> log, IConfiguration cfg)
          {
                    _log = log;
                    _cfg = cfg;
          }

          private string PdftkPath => _cfg["AcroForm:PdftkPath"] ?? "pdftk";

          /// <summary>
          /// Fills the named AcroForm fields in templatePath with values from fieldValues
          /// (already resolved from FormFieldMappingJson + canonical tokens by the caller),
          /// flattens the result so it's no longer editable, and returns the PDF bytes.
          /// Fields present in the PDF but absent from fieldValues are left as-is (blank).
          /// </summary>
          public async Task<byte[]> FillAndFlattenAsync(string templatePath, Dictionary<string, string> fieldValues)
          {
                    if (!File.Exists(templatePath))
                              throw new FileNotFoundException($"AcroForm template not found: {templatePath}");

                    var workDir = Path.Combine(Path.GetTempPath(), $"acroform-{Guid.NewGuid():N}");
                    Directory.CreateDirectory(workDir);
                    var fdfPath = Path.Combine(workDir, "data.fdf");
                    var outPath = Path.Combine(workDir, "filled.pdf");

                    try
                    {
                              await File.WriteAllTextAsync(fdfPath, BuildFdf(fieldValues), Encoding.UTF8);

                              var psi = new ProcessStartInfo
                              {
                                        FileName = PdftkPath,
                                        RedirectStandardOutput = true,
                                        RedirectStandardError = true,
                                        UseShellExecute = false,
                                        CreateNoWindow = true
                              };
                              psi.ArgumentList.Add(templatePath);
                              psi.ArgumentList.Add("fill_form");
                              psi.ArgumentList.Add(fdfPath);
                              psi.ArgumentList.Add("output");
                              psi.ArgumentList.Add(outPath);
                              psi.ArgumentList.Add("flatten");

                              using var proc = Process.Start(psi)
                                  ?? throw new InvalidOperationException("Failed to start pdftk process.");
                              var stdout = await proc.StandardOutput.ReadToEndAsync();
                              var stderr = await proc.StandardError.ReadToEndAsync();
                              await proc.WaitForExitAsync();

                              _log.LogInformation("pdftk fill_form exit {Code} for {Template}: {Out} {Err}",
                                  proc.ExitCode, templatePath, stdout, stderr);

                              if (proc.ExitCode != 0 || !File.Exists(outPath))
                                        throw new InvalidOperationException(
                                            $"pdftk fill_form failed (exit {proc.ExitCode}) for {templatePath}: {stderr}");

                              var bytes = await File.ReadAllBytesAsync(outPath);

                              // Same lesson as the LibreOffice guard: a nonzero exit is caught above,
                              // but a "successful" empty/corrupt output would slip through silently.
                              if (bytes.Length == 0)
                                        throw new InvalidOperationException(
                                            $"pdftk reported success but produced a 0-byte PDF for {templatePath}.");

                              _log.LogInformation("AcroForm fill produced {Bytes} bytes from {Template}", bytes.Length, templatePath);
                              return bytes;
                    }
                    finally
                    {
                              try { Directory.Delete(workDir, recursive: true); } catch { /* best-effort cleanup */ }
                    }
          }

          /// <summary>
          /// Builds a minimal FDF (Forms Data Format) document from field/value pairs.
          /// Values are escaped per FDF/PDF string rules: backslash, then parens.
          /// </summary>
          private static string BuildFdf(Dictionary<string, string> fieldValues)
          {
                    var sb = new StringBuilder();
                    sb.Append("%FDF-1.2\n1 0 obj<</FDF<</Fields[\n");
                    foreach (var (name, value) in fieldValues)
                    {
                              sb.Append("<</T(").Append(EscapeFdf(name)).Append(")/V(").Append(EscapeFdf(value ?? string.Empty)).Append(")>>\n");
                    }
                    sb.Append("]>>>>\nendobj\ntrailer<</Root 1 0 R>>\n%%EOF\n");
                    return sb.ToString();
          }

          private static string EscapeFdf(string s) =>
              s.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");
}
