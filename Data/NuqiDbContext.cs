using CircularoIntegration.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace CircularoIntegration.Api.Data;

/// <summary>
/// Separate EF Core context for the 3 NUQI tables (v3 lean):
///   nuqi_approval_event, nuqi_package_document, integration_audit.
/// Same MySQL connection string as AppDbContext, but kept apart so:
///  * AppDbContext / EnsureCreated() / the 4 live Murabaha flows are not modified;
///  * the tables are created only by Database/NUQI_v3_Lean_Database.sql
///    (never by EnsureCreated – same "manual SQL" convention as the rest of mwfcirclerodb).
/// </summary>
public class NuqiDbContext : DbContext
{
          public NuqiDbContext(DbContextOptions<NuqiDbContext> options) : base(options) { }

          public DbSet<NuqiApprovalEvent> ApprovalEvents => Set<NuqiApprovalEvent>();
          public DbSet<NuqiPackageDocument> PackageDocuments => Set<NuqiPackageDocument>();
          public DbSet<IntegrationAudit> Audits => Set<IntegrationAudit>();

          protected override void OnModelCreating(ModelBuilder mb)
          {
                    mb.Entity<NuqiApprovalEvent>(e =>
                    {
                              e.ToTable("nuqi_approval_event");
                              e.HasIndex(x => x.IdempotencyKey).IsUnique();
                              e.Property(x => x.ApprovedAmount).HasPrecision(18, 2);
                              e.Property(x => x.CashWithdrawalLimit).HasPrecision(18, 2);
                              e.Ignore(x => x.PackageReference);
                              e.Ignore(x => x.GateOpened);
                    });

                    mb.Entity<NuqiPackageDocument>(e =>
                    {
                              e.ToTable("nuqi_package_document");
                              e.HasIndex(x => x.CircularoDocumentId);
                    });

                    mb.Entity<IntegrationAudit>().ToTable("nuqi_integration_audit");
          }
}
