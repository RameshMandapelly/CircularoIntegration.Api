using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using System.Diagnostics;
using CircularoIntegration.Api.Models;
using DocumentFormat.OpenXml;

namespace CircularoIntegration.Api.Services;

public class OfferLetterDocumentService
{
          private readonly IConfiguration _cfg;
          private readonly ILogger<OfferLetterDocumentService> _log;

          public OfferLetterDocumentService(IConfiguration cfg, ILogger<OfferLetterDocumentService> log)
          {
                    _cfg = cfg;
                    _log = log;
          }

          public async Task<byte[]> GenerateOfferLetterPdfAsync(LeadOfferDetails details, CancellationToken ct = default)
          {
                    var templatePath = _cfg["Templates:OfferLetterDocxPath"]
                        ?? throw new InvalidOperationException("Templates:OfferLetterDocxPath is not configured");

                    var workDir = Path.Combine(Path.GetTempPath(), $"offerletter-{Guid.NewGuid():N}");
                    Directory.CreateDirectory(workDir);
                    var docxPath = Path.Combine(workDir, "offer-letter.docx");
                    File.Copy(templatePath, docxPath);

                    var tokens = new Dictionary<string, string>
                    {
                              ["FundedFacilityLimit"] = details.FundedFacilityLimit ?? string.Empty,
                              ["RequestedFinanceAmount"] = details.RequestedFinanceAmount ?? string.Empty,
                              ["Purpose"] = details.Purpose ?? string.Empty,
                              ["Tenure"] = details.Tenure ?? string.Empty,
                              ["ProfitRate"] = details.ProfitRate ?? string.Empty,
                              ["FirstName"] = details.FirstName ?? "Customer"
                    };

                    FillTemplate(docxPath, tokens);
                    var pdfPath = await ConvertDocxToPdfAsync(docxPath, workDir, ct);
                    var bytes = await File.ReadAllBytesAsync(pdfPath, ct);

                    try { Directory.Delete(workDir, recursive: true); } catch { /* best-effort */ }
                    return bytes;
          }

          private void FillTemplate(string docxPath, Dictionary<string, string> tokens)
          {
                    using var wordDoc = WordprocessingDocument.Open(docxPath, isEditable: true);
                    var body = wordDoc.MainDocumentPart!.Document.Body!;

                    foreach (var paragraph in body.Descendants<Paragraph>())
                    {
                              var runs = paragraph.Descendants<Run>().ToList();
                              if (runs.Count == 0) continue;

                              var combinedText = string.Concat(runs.Select(r => r.InnerText));
                              if (!combinedText.Contains("{{")) continue;

                              var replaced = combinedText;
                              foreach (var (key, value) in tokens)
                                        replaced = replaced.Replace("{{" + key + "}}", value);

                              if (replaced == combinedText) continue;

                              // Word splits placeholder text across multiple runs (autocorrect) —
                              // merge into run[0] and drop the rest so we don't leave orphan tokens.
                              runs[0].GetFirstChild<Text>()!.Text = replaced;
                              runs[0].GetFirstChild<Text>()!.Space = SpaceProcessingModeValues.Preserve;
                              foreach (var extraRun in runs.Skip(1))
                                        extraRun.Remove();
                    }

                    wordDoc.MainDocumentPart.Document.Save();
          }

          private async Task<string> ConvertDocxToPdfAsync(string docxPath, string outDir, CancellationToken ct)
          {
                    var soffice = _cfg["Templates:LibreOfficePath"] ?? "soffice";
                    var psi = new ProcessStartInfo
                    {
                              FileName = soffice,
                              Arguments = $"--headless --convert-to pdf --outdir \"{outDir}\" \"{docxPath}\"",
                              RedirectStandardOutput = true,
                              RedirectStandardError = true,
                              UseShellExecute = false
                    };

                    using var proc = Process.Start(psi)!;
                    var stdout = await proc.StandardOutput.ReadToEndAsync(ct);
                    var stderr = await proc.StandardError.ReadToEndAsync(ct);
                    await proc.WaitForExitAsync(ct);

                    _log.LogInformation("LibreOffice convert exit {Code}: {Out} {Err}", proc.ExitCode, stdout, stderr);

                    var pdfPath = Path.Combine(outDir, Path.GetFileNameWithoutExtension(docxPath) + ".pdf");
                    if (proc.ExitCode != 0 || !File.Exists(pdfPath))
                              throw new InvalidOperationException($"docx→pdf conversion failed (exit {proc.ExitCode}): {stderr}");

                    return pdfPath;
          }
}