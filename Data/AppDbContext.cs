using CircularoIntegration.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace CircularoIntegration.Api.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<PhaseDocumentConfig> PhaseDocumentConfigs => Set<PhaseDocumentConfig>();
    public DbSet<SigningTransaction> SigningTransactions => Set<SigningTransaction>();
    public DbSet<KfsEmailLog> KfsEmailLogs => Set<KfsEmailLog>();
    public DbSet<PhaseDocumentItem> PhaseDocumentItems => Set<PhaseDocumentItem>();

    protected override void OnModelCreating(ModelBuilder mb)
    {
        mb.Entity<PhaseDocumentConfig>().ToTable("phase_document_config");
        mb.Entity<PhaseDocumentConfig>().HasIndex(x => x.LeadStageTrigger);

        mb.Entity<PhaseDocumentItem>().ToTable("phase_document_item");
        mb.Entity<PhaseDocumentItem>().HasIndex(x => x.PhaseDocumentConfigId);
        mb.Entity<PhaseDocumentItem>().HasIndex(x => new { x.PhaseDocumentConfigId, x.DocIndex }).IsUnique();

        mb.Entity<SigningTransaction>().ToTable("signing_transaction");
        // FIX: NOT unique. The retry path inserts a NEW row after a Failed attempt
        // (the duplicate check excludes Failed), so a unique index would make every
        // retry throw DbUpdateException. Plain index keeps lookups fast.
        mb.Entity<SigningTransaction>().HasIndex(x => new { x.LeadId, x.PhaseNo, x.DocIndex });
        mb.Entity<SigningTransaction>().HasIndex(x => x.CircularoRef);

        mb.Entity<KfsEmailLog>().ToTable("kfs_email_logs");
        mb.Entity<KfsEmailLog>().HasIndex(x => x.LeadId);
    }
}
