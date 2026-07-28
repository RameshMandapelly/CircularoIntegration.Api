using System.Text.Json;
using PdfSharpCore.Drawing;
using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.IO;

namespace CircularoIntegration.Api.Services;

public record OverlayField(string Token, int Page, double X, double Top, double FontSize = 10);

public class PdfOverlayService
{
          private readonly ILogger<PdfOverlayService> _log;
          public PdfOverlayService(ILogger<PdfOverlayService> log) => _log = log;

          /// <summary>Stamps token values onto a copy of the source PDF at fixed coordinates and
          /// returns the resulting bytes. Does not alter the original template file on disk.</summary>
          public byte[] Stamp(string sourcePdfPath, string overlayFieldsJson, Dictionary<string, string> tokenValues)
          {
                    var fields = JsonSerializer.Deserialize<List<OverlayField>>(overlayFieldsJson)
                        ?? throw new InvalidOperationException("OverlayFieldsJson did not parse to a field list.");

                    using var document = PdfReader.Open(sourcePdfPath, PdfDocumentOpenMode.Modify);
                    var font = new XFont("Book Antiqua", 10);

                    foreach (var group in fields.GroupBy(f => f.Page))
                    {
                              var pageIndex = group.Key - 1;
                              if (pageIndex < 0 || pageIndex >= document.PageCount)
                              {
                                        _log.LogWarning("Overlay field references page {Page} but PDF has {Count} pages — skipping.",
                                            group.Key, document.PageCount);
                                        continue;
                              }
                              var page = document.Pages[pageIndex];
                              using var gfx = XGraphics.FromPdfPage(page);

                              foreach (var field in group)
                              {
                                        if (!tokenValues.TryGetValue(field.Token, out var value) || string.IsNullOrEmpty(value))
                                        {
                                                  _log.LogWarning("No value supplied for overlay token '{Token}' — leaving blank.", field.Token);
                                                  continue;
                                        }
                                        var f = field.FontSize > 0 ? new XFont("Book Antiqua", field.FontSize) : font;
                                        // PdfSharp uses points from the TOP-LEFT for XGraphics.DrawString by default (unlike raw PDF
                                        // coordinate space), matching pdfplumber's Top convention directly — no flip needed here.
                                        gfx.DrawString(value, f, XBrushes.Black, new XPoint(field.X, field.Top + field.FontSize));
                              }
                    }

                    using var ms = new MemoryStream();
                    document.Save(ms);
                    return ms.ToArray();
          }
}