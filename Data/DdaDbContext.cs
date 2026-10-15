using CircularoIntegration.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace CircularoIntegration.Api.Data;

/// <summary>
/// EF Core context for the DDA demo module. Maps nuqi_dda_request and the existing integration_audit table.
/// Tables are created only by Database/DDA_v1_Database.sql (never by EnsureCreated).
/// </summary>
public class DdaDbContext : DbContext
{
          public DdaDbContext(DbContextOptions<DdaDbContext> options) : base(options) { }

          public DbSet<DdaRequest> Requests => Set<DdaRequest>();
          public DbSet<IntegrationAudit> Audits => Set<IntegrationAudit>();

          protected override void OnModelCreating(ModelBuilder mb)
          {
                    mb.Entity<DdaRequest>(e =>
                    {
                              e.ToTable("nuqi_dda_request");
                              e.HasIndex(x => x.IdempotencyKey).IsUnique();
                              e.HasIndex(x => x.LeadId);
                              e.HasIndex(x => x.DdaId);
                    });
                    mb.Entity<IntegrationAudit>().ToTable("nuqi_integration_audit");
          }
}
