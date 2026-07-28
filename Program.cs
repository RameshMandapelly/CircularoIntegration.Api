using CircularoIntegration.Api.Data;
using CircularoIntegration.Api.Middleware;
using CircularoIntegration.Api.Models;
using CircularoIntegration.Api.Services;
using Microsoft.EntityFrameworkCore;
using Serilog;
using CircularoIntegration.Api.Config;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);

// ---------- Serilog (console + rolling file) ----------
var connStr = builder.Configuration.GetConnectionString("MySql");

Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .WriteTo.File("logs/circularo-integration-.log",
        rollingInterval: RollingInterval.Day,
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj} {Properties:j}{NewLine}{Exception}")
    .WriteTo.MySQL(connStr, tableName: "MWF_Circlero_logs")
    .CreateLogger();

builder.Host.UseSerilog();

// ---------- EF Core / MySQL ----------
var connStrMySQL = builder.Configuration.GetConnectionString("MySql");
builder.Services.AddDbContext<AppDbContext>(opt =>
    opt.UseMySql(connStrMySQL, ServerVersion.AutoDetect(connStrMySQL)));

// ---------- Typed HttpClients for external systems ----------
builder.Services.AddHttpClient<LeadSquaredService>();
builder.Services.AddHttpClient<CircularoService>();
builder.Services.Configure<KfsEmailOptions>(builder.Configuration.GetSection("KfsEmail"));
builder.Services.Configure<SmtpSettings>(builder.Configuration.GetSection("SmtpSettings"));
builder.Services.AddScoped<IEmailService, EmailService>();
builder.Services.AddScoped<IKfsNotificationService, KfsNotificationService>();
builder.Services.AddScoped<OfferLetterDocumentService>();

// BUG 4 FIX: register the PDF overlay stamping service (stateless → singleton).
builder.Services.AddSingleton<PdfOverlayService>();

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

// ======================================================================
// BUG 3 FIX: real overlay coordinate JSON (was the literal "...").
// Coordinates are pdfplumber-measured points, top-left origin, 1-based pages.
// NOTE: seeds run once — later edits to these constants do NOT update rows
// already in the DB. To tune coordinates after first seed, UPDATE the
// phase_document_item.OverlayFieldsJson column directly.
// ======================================================================
const string CoolingOffOverlayJson = """
[
  {"Token":"CustomerName","Page":1,"X":140,"Top":630.5,"FontSize":10},
  {"Token":"SignatureDate","Page":1,"X":90,"Top":704.5,"FontSize":10}
]
""";

const string MasterMurabahaOverlayJson = """
[
  {"Token":"CustomerName","Page":1,"X":295,"Top":184.1,"FontSize":10},
  {"Token":"AgreementDay","Page":1,"X":135,"Top":372.1,"FontSize":10},
  {"Token":"AgreementMonth","Page":1,"X":226,"Top":372.1,"FontSize":10},
  {"Token":"AgreementYear","Page":1,"X":293,"Top":372.1,"FontSize":10},
  {"Token":"CustomerName","Page":1,"X":281,"Top":462.1,"FontSize":10},
  {"Token":"CustomerAddress","Page":1,"X":42,"Top":473.1,"FontSize":9},
  {"Token":"CustomerNoticeAddress","Page":13,"X":118,"Top":104.8,"FontSize":9},
  {"Token":"CustomerAttention","Page":13,"X":121,"Top":134.8,"FontSize":9},
  {"Token":"CustomerTelNo","Page":13,"X":112,"Top":164.8,"FontSize":9},
  {"Token":"CustomerFaxNo","Page":13,"X":116,"Top":194.8,"FontSize":9},
  {"Token":"Signatory1Name","Page":17,"X":85,"Top":327.1,"FontSize":9},
  {"Token":"Signatory1Designation","Page":17,"X":113,"Top":350.1,"FontSize":9},
  {"Token":"Signatory2Name","Page":17,"X":85,"Top":396.1,"FontSize":9},
  {"Token":"Signatory2Designation","Page":17,"X":113,"Top":419.1,"FontSize":9}
]
""";

// ---------- Create DB objects & seed configs on first run ----------
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.EnsureCreated();

    // ---- Phase 1 (Offer Letter) — unchanged, already live in production ----
    if (!db.PhaseDocumentConfigs.Any())
    {
        db.PhaseDocumentConfigs.Add(new PhaseDocumentConfig
        {
            PhaseNo = 1,
            LeadStageTrigger = "Phase 1 Doc Sign",
            NextLeadStage = "Phase 2 Doc Sign",
            CircularoDocumentId = "REPLACE_WITH_REAL_DOC_ID",
            DocumentName = "Phase 1 - Offer Letter (demo)",
            IsActive = true
        });
        db.SaveChanges();
        Log.Information("Seeded Phase 1 document configuration");
    }

    // BUG 5 FIX: the old placeholder "Phase 2" seed block has been DELETED.
    // It duplicated the same 3 documents with junk stage names and used the
    // removed TemplatePdfPath property. If your dev DB already has those rows:
    //   DELETE FROM phase_document_item WHERE PhaseDocumentConfigId =
    //     (SELECT Id FROM phase_document_config WHERE PhaseNo = 2);
    //   DELETE FROM phase_document_config WHERE PhaseNo = 2;

    // ---- Stage 7 — Murabaha Stage 1 (Pre-Sale Documentation), per Qlub guide §3 ----
    if (!db.PhaseDocumentConfigs.Any(c => c.PhaseNo == 7))
    {
        var stage7 = new PhaseDocumentConfig
        {
            PhaseNo = 7,
            LeadStageTrigger = "OFFER ACCEPTED",   // Stage 7 trigger per Qlub Implementation Guide
            NextLeadStage = "MURABAHA STAGE 1",    // resulting status transition per guide
            DocumentName = "Murabaha Stage 1 - Pre-Sale Documentation",
            IsActive = true
        };
        db.PhaseDocumentConfigs.Add(stage7);
        db.SaveChanges();

        db.PhaseDocumentItems.AddRange(
            new PhaseDocumentItem
            {
                PhaseDocumentConfigId = stage7.Id,
                DocIndex = 1,
                DocumentName = "Promise to Purchase",
                TemplatePath = @"C:\inetpub\CircularoIntegrationApi\templates\promise-to-purchase-template.docx",
                MergeStrategy = DocMergeStrategy.DocxTokens,
                SignaturePage = 5 // last page of current rendering — verify on first merged PDF
            },
            new PhaseDocumentItem
            {
                PhaseDocumentConfigId = stage7.Id,
                DocIndex = 2,
                DocumentName = "Cooling-off Period Waiver",
                TemplatePath = @"C:\inetpub\CircularoIntegrationApi\templates\cooling-off-waiver.pdf",
                MergeStrategy = DocMergeStrategy.PdfOverlay,
                OverlayFieldsJson = CoolingOffOverlayJson,
                SignaturePage = 1
            },
            new PhaseDocumentItem
            {
                PhaseDocumentConfigId = stage7.Id,
                DocIndex = 3,
                DocumentName = "Master Murabaha Agreement",
                TemplatePath = @"C:\inetpub\CircularoIntegrationApi\templates\master-murabaha.pdf",
                MergeStrategy = DocMergeStrategy.PdfOverlay,
                OverlayFieldsJson = MasterMurabahaOverlayJson,
                SignaturePage = 17
            }
        );
        db.SaveChanges();
        Log.Information("Seeded Stage 7 (Murabaha Stage 1) document set");
    }
}

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}
app.UseSerilogRequestLogging();

// Shared-secret validation on inbound webhook calls
app.UseMiddleware<ApiKeyMiddleware>();

app.MapControllers();
app.MapGet("/health", () => Results.Ok(new { status = "UP", timeUtc = DateTime.UtcNow }));

Log.Information("Circularo Integration Middleware starting…");
app.Run();
