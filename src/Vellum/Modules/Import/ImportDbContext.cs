using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Vellum.Modules.Import.Proposals;
using Vellum.Modules.Import.Scans;

namespace Vellum.Modules.Import;

/// <summary>
/// Schema <c>import</c>: scan jobs with their checkpoints, the stored blobs, the snapshot of
/// each completed scan, its proposals, and what a person accepted or rejected. Everything
/// read from a repository is stored here as data and is never executed or interpreted.
/// </summary>
public class ImportDbContext(DbContextOptions<ImportDbContext> options) : DbContext(options)
{
    public DbSet<ScanEntity> Scans => Set<ScanEntity>();
    public DbSet<ScanFileEntity> ScanFiles => Set<ScanFileEntity>();
    public DbSet<BlobEntity> Blobs => Set<BlobEntity>();
    public DbSet<SnapshotEntity> Snapshots => Set<SnapshotEntity>();
    public DbSet<SnapshotFactEntity> SnapshotFacts => Set<SnapshotFactEntity>();
    public DbSet<ProposalEntity> Proposals => Set<ProposalEntity>();
    public DbSet<FactLinkEntity> FactLinks => Set<FactLinkEntity>();
    public DbSet<RejectionEntity> Rejections => Set<RejectionEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("import");

        modelBuilder.Entity<ScanEntity>(b =>
        {
            b.ToTable("scans");
            b.HasKey(s => s.Id);
            b.Property(s => s.Limits).HasColumnType("jsonb");
            b.Property(s => s.Notes).HasColumnType("jsonb");
            // A worker that lost its lease must not write: its save finds another owner and fails.
            b.Property(s => s.LeaseOwner).IsConcurrencyToken();
            b.HasIndex(s => new { s.ProjectId, s.CreatedAt, s.Id });
            b.HasIndex(s => new { s.WorkspaceId, s.CreatedAt });
            b.HasIndex(s => new { s.Status, s.CreatedAt });
            // One scan of a repository per project is queued, running or waiting at a time.
            b.HasIndex(s => new { s.ProjectId, s.Host, s.Repository })
                .IsUnique()
                .HasFilter("status IN ('queued', 'running', 'waiting')")
                .HasDatabaseName("ix_scans_one_active_per_repository");
        });

        modelBuilder.Entity<ScanFileEntity>(b =>
        {
            b.ToTable("scan_files");
            b.HasKey(f => new { f.ScanId, f.Path });
            b.HasIndex(f => new { f.ScanId, f.State });
            b.HasIndex(f => f.BlobSha);
            b.HasOne<ScanEntity>().WithMany().HasForeignKey(f => f.ScanId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<BlobEntity>(b =>
        {
            b.ToTable("blobs");
            // Per workspace: a blob read with one workspace's token is never served to another.
            b.HasKey(blob => new { blob.WorkspaceId, blob.BlobSha });
        });

        modelBuilder.Entity<SnapshotEntity>(b =>
        {
            b.ToTable("snapshots");
            b.HasKey(s => s.ScanId);
            b.Property(s => s.RuleVersions).HasColumnType("jsonb");
            b.Property(s => s.Limits).HasColumnType("jsonb");
            b.HasIndex(s => new { s.ProjectId, s.Host, s.Repository, s.CreatedAt });
            b.HasOne<ScanEntity>().WithOne().HasForeignKey<SnapshotEntity>(s => s.ScanId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<SnapshotFactEntity>(b =>
        {
            b.ToTable("snapshot_facts");
            b.HasKey(f => new { f.ScanId, f.Seq });
            b.Property(f => f.Attributes).HasColumnType("jsonb");
            b.Property(f => f.Evidence).HasColumnType("jsonb");
            b.HasIndex(f => new { f.ScanId, f.Disposition, f.Seq });
            b.HasOne<SnapshotEntity>().WithMany().HasForeignKey(f => f.ScanId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ProposalEntity>(b =>
        {
            b.ToTable("proposals");
            b.HasKey(p => p.Id);
            b.Property(p => p.Proposed).HasColumnType("jsonb");
            b.Property(p => p.Current).HasColumnType("jsonb");
            b.Property(p => p.Changes).HasColumnType("jsonb");
            b.Property(p => p.Evidence).HasColumnType("jsonb");
            b.Property(p => p.Attributes).HasColumnType("jsonb");
            b.HasIndex(p => new { p.ScanId, p.Seq }).IsUnique();
            b.HasIndex(p => new { p.ScanId, p.Status });
            b.HasOne<ScanEntity>().WithMany().HasForeignKey(p => p.ScanId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<FactLinkEntity>(b =>
        {
            b.ToTable("fact_links");
            b.HasKey(l => new { l.ProjectId, l.Host, l.Repository, l.FactKey });
            b.Property(l => l.Attributes).HasColumnType("jsonb");
            b.Property(l => l.Accepted).HasColumnType("jsonb");
            b.Property(l => l.Evidence).HasColumnType("jsonb");
            b.HasIndex(l => new { l.ProjectId, l.EntityId });
        });

        modelBuilder.Entity<RejectionEntity>(b =>
        {
            b.ToTable("rejections");
            b.HasKey(r => new { r.ProjectId, r.Host, r.Repository, r.FactKey, r.Fingerprint });
        });
    }
}

public class ImportDbContextFactory : IDesignTimeDbContextFactory<ImportDbContext>
{
    public ImportDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<ImportDbContext>()
            .UseNpgsql("Host=localhost")
            .UseSnakeCaseNamingConvention()
            .Options;
        return new ImportDbContext(options);
    }
}
