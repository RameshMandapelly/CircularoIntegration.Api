using CircularoIntegration.Api.Models;
using Microsoft.EntityFrameworkCore;
using CircularoIntegration.Api.Data;

namespace CircularoIntegration.Api.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<PhaseDocumentConfig> PhaseDocumentConfigs => Set<PhaseDocumentConfig>();
    public DbSet<SigningTransaction> SigningTransactions => Set<SigningTransaction>();
    public DbSet<KfsEmailLog> KfsEmailLogs => Set<KfsEmailLog>();

    protected override void OnModelCreating(ModelBuilder mb)
    {
        mb.Entity<PhaseDocumentConfig>().ToTable("phase_document_config");
        mb.Entity<PhaseDocumentConfig>().HasIndex(x => x.LeadStageTrigger);

        mb.Entity<SigningTransaction>().ToTable("signing_transaction");
        mb.Entity<SigningTransaction>().HasIndex(x => new { x.LeadId, x.PhaseNo });
        mb.Entity<SigningTransaction>().HasIndex(x => x.CircularoRef);
        mb.Entity<KfsEmailLog>().ToTable("kfs_email_logs");
        mb.Entity<KfsEmailLog>().HasIndex(x => x.LeadId);

    }
}
