using CircularoIntegration.Api.Data;
using CircularoIntegration.Api.Middleware;
using CircularoIntegration.Api.Models;
using CircularoIntegration.Api.Services;
using Microsoft.EntityFrameworkCore;
using Serilog;
using CircularoIntegration.Api.Config;

var builder = WebApplication.CreateBuilder(args);

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
builder.Services.AddScoped<IEmailService, EmailService>();
builder.Services.AddScoped<IKfsNotificationService, KfsNotificationService>();
builder.Services.Configure<SmtpSettings>(builder.Configuration.GetSection("SmtpSettings"));
builder.Services.Configure<KfsEmailOptions>(builder.Configuration.GetSection("KfsEmail"));

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

// ---------- Create DB objects & seed Phase 1 config on first run ----------
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.EnsureCreated();

    if (!db.PhaseDocumentConfigs.Any())
    {
        db.PhaseDocumentConfigs.Add(new PhaseDocumentConfig
        {
            PhaseNo = 1,
            LeadStageTrigger = "Phase 1 Doc Sign",
            NextLeadStage = "Phase 2 Doc Sign",
            CircularoDocumentId = "REPLACE_WITH_REAL_DOC_ID", // from Circularo dashboard / Create document API
            DocumentName = "Phase 1 - Offer Letter (demo)",
            IsActive = true
        });
        db.SaveChanges();
        Log.Information("Seeded Phase 1 document configuration");
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
