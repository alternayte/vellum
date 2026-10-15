using Microsoft.EntityFrameworkCore;
using Vellum.Modules.Import.Extraction;
using Vellum.Modules.Import.Proposals;
using Vellum.Modules.Import.Scans;
using Vellum.Shared;

namespace Vellum.Modules.Import;

/// <summary>
/// Schema <c>import</c>: scan jobs with their checkpoints, the stored blobs, the snapshot of
/// each completed scan, its proposals, and what a person accepted or rejected. Everything
/// read from a repository is stored here as data and is never executed or interpreted.
/// <para>
/// No key holds repository text of unbounded length: a path and a fact key are indexed by
/// their SHA-256, and the text itself sits in a bounded column next to it.
/// </para>
/// </summary>
public static class ImportTables
{
    public const string Schema = "import";

    public static readonly IReadOnlyList<PostgresEnum> Enums =
    [
        PostgresEnum.Of<ScanStatus>("scan_status", Schema),
        PostgresEnum.Of<ScanFileState>("scan_file_state", Schema),
        PostgresEnum.Of<StoredCoverageGap>("coverage_gap", Schema),
        PostgresEnum.Of<FactKind>("fact_kind", Schema),
        PostgresEnum.Of<FactDisposition>("fact_disposition", Schema),
        PostgresEnum.Of<ProposalAction>("proposal_action", Schema),
        PostgresEnum.Of<ProposalStatus>("proposal_status", Schema),
        PostgresEnum.Of<Subject>("subject", Schema),
        PostgresEnum.Of<StoredEntityKind>("entity_kind", Schema),
        PostgresEnum.Of<SourceKind>("source_kind", Schema),
    ];

    public static void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SourceEntity>(b =>
        {
            b.ToTable("sources", Schema);
            b.HasKey(s => s.Id);
            b.Property(s => s.Name).HasMaxLength(SourceEntity.MaxName);
            b.Property(s => s.Host).HasMaxLength(255);
            b.Property(s => s.Locator).HasMaxLength(400);
            b.Property(s => s.Connection).HasColumnType("jsonb");
            b.Ignore(s => s.CitationName);
            // The key of the per-Source rows: one Source per place per project.
            b.HasIndex(s => new { s.ProjectId, s.Host, s.Locator }).IsUnique();
        });

        modelBuilder.Entity<ScanEntity>(b =>
        {
            b.ToTable("scans", Schema);
            b.HasKey(s => s.Id);
            b.HasIndex(s => new { s.SourceId, s.CreatedAt });
            b.HasOne<SourceEntity>().WithMany().HasForeignKey(s => s.SourceId).OnDelete(DeleteBehavior.Cascade);
            b.Property(s => s.Limits).HasColumnType("jsonb");
            b.Property(s => s.Notes).HasColumnType("jsonb");
            b.Property(s => s.Coverage).HasColumnType("jsonb");
            b.Property(s => s.PullRequest).HasColumnType("jsonb");
            b.Property(s => s.ReportedCommitSha).HasMaxLength(64);
            // The fencing token: a run that lost the scan names an old token, and its save fails.
            b.Property(s => s.LeaseToken).IsConcurrencyToken();
            b.HasIndex(s => new { s.ProjectId, s.CreatedAt, s.Id });
            b.HasIndex(s => new { s.WorkspaceId, s.CreatedAt });
            b.HasIndex(s => new { s.WorkspaceId, s.ClaimedAt });
            b.HasIndex(s => new { s.Status, s.CreatedAt });
            // One scan of a repository per project is queued, running or waiting at a time.
            b.HasIndex(s => new { s.ProjectId, s.Host, s.Repository })
                .IsUnique()
                .HasFilter("status IN ('queued', 'running', 'waiting')")
                .HasDatabaseName("ix_scans_one_active_per_repository");
        });

        modelBuilder.Entity<ScanFileEntity>(b =>
        {
            b.ToTable("scan_files", Schema);
            b.HasKey(f => new { f.ScanId, f.PathHash });
            b.Property(f => f.Path).HasMaxLength(ScanFileEntity.MaxPath);
            b.Property(f => f.PathHash).HasMaxLength(64);
            b.HasIndex(f => new { f.ScanId, f.State });
            b.HasIndex(f => f.BlobSha);
            b.HasOne<ScanEntity>().WithMany().HasForeignKey(f => f.ScanId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<BlobEntity>(b =>
        {
            b.ToTable("blobs", Schema);
            // Per workspace: a blob read with one workspace's token is never served to another.
            b.HasKey(blob => new { blob.WorkspaceId, blob.BlobSha });
        });

        modelBuilder.Entity<ScanChargeEntity>(b =>
        {
            b.ToTable("scan_charges", Schema);
            b.HasKey(c => c.Id);
            b.HasIndex(c => new { c.WorkspaceId, c.ChargedAt });
        });

        modelBuilder.Entity<TrackedRefEntity>(b =>
        {
            b.ToTable("tracked_refs", Schema);
            b.HasKey(t => new { t.ProjectId, t.Host, t.Repository });
            b.Property(t => t.RefName).HasMaxLength(255);
        });

        modelBuilder.Entity<SnapshotEntity>(b =>
        {
            b.ToTable("snapshots", Schema);
            // Named here: review.snapshots has a key of the same name in its own schema.
            b.HasKey(s => s.ScanId).HasName("pk_snapshots");
            b.Property(s => s.RuleVersions).HasColumnType("jsonb");
            b.Property(s => s.Limits).HasColumnType("jsonb");
            b.Property(s => s.SnapshotId).HasMaxLength(64);
            b.HasIndex(s => new { s.ProjectId, s.Host, s.Repository, s.CreatedAt });
            b.HasIndex(s => new { s.SourceId, s.CreatedAt });
            b.HasOne<ScanEntity>().WithOne().HasForeignKey<SnapshotEntity>(s => s.ScanId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<SnapshotFactEntity>(b =>
        {
            b.ToTable("snapshot_facts", Schema);
            b.HasKey(f => new { f.ScanId, f.Seq });
            b.Property(f => f.FactKey).HasMaxLength(FactMapping.MaxFactKey);
            b.Property(f => f.Attributes).HasColumnType("jsonb");
            b.Property(f => f.Evidence).HasColumnType("jsonb");
            b.HasIndex(f => new { f.ScanId, f.Disposition, f.Seq });
            b.HasOne<SnapshotEntity>().WithMany().HasForeignKey(f => f.ScanId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ProposalEntity>(b =>
        {
            b.ToTable("proposals", Schema);
            b.HasKey(p => p.Id);
            b.Property(p => p.FactKey).HasMaxLength(FactMapping.MaxFactKey);
            b.Property(p => p.Subject).HasDefaultValue(Subject.Fact);
            b.Property(p => p.Proposed).HasColumnType("jsonb");
            b.Property(p => p.Current).HasColumnType("jsonb");
            b.Property(p => p.Changes).HasColumnType("jsonb");
            b.Property(p => p.Evidence).HasColumnType("jsonb");
            b.Property(p => p.Attributes).HasColumnType("jsonb");
            b.Property(p => p.ConsumerKeys).HasColumnType("jsonb").HasDefaultValueSql("'[]'::jsonb");
            b.Property(p => p.OtherSources).HasColumnType("jsonb").HasDefaultValueSql("'[]'::jsonb");
            b.HasIndex(p => new { p.ScanId, p.Seq }).IsUnique();
            b.HasIndex(p => new { p.ScanId, p.Status });
            b.HasOne<ScanEntity>().WithMany().HasForeignKey(p => p.ScanId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<FactLinkEntity>(b =>
        {
            b.ToTable("fact_links", Schema);
            b.HasKey(l => l.Id);
            b.Property(l => l.FactKey).HasMaxLength(FactMapping.MaxFactKey);
            b.Property(l => l.FactKeyHash).HasMaxLength(64);
            b.Property(l => l.Subject).HasDefaultValue(Subject.Fact);
            b.Property(l => l.Attributes).HasColumnType("jsonb");
            b.Property(l => l.ConsumerKeys).HasColumnType("jsonb").HasDefaultValueSql("'[]'::jsonb");
            b.Property(l => l.Accepted).HasColumnType("jsonb");
            b.Property(l => l.Evidence).HasColumnType("jsonb");
            b.HasIndex(l => new { l.ProjectId, l.Host, l.Repository, l.Subject, l.FactKeyHash }).IsUnique();
            b.HasIndex(l => new { l.ProjectId, l.EntityId });
        });

        modelBuilder.Entity<RejectionEntity>(b =>
        {
            b.ToTable("rejections", Schema);
            b.HasKey(r => r.Id);
            b.Property(r => r.FactKey).HasMaxLength(FactMapping.MaxFactKey);
            b.Property(r => r.FactKeyHash).HasMaxLength(64);
            b.Property(r => r.Subject).HasDefaultValue(Subject.Fact);
            b.HasIndex(r => new { r.ProjectId, r.Host, r.Repository, r.Subject, r.FactKeyHash, r.Fingerprint }).IsUnique();
        });
    }
}

