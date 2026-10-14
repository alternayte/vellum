using Microsoft.EntityFrameworkCore;

namespace Vellum.Modules.Sharing;

/// <summary>The tables of the module, in the Postgres schema <c>sharing</c>.</summary>
public static class SharingTables
{
    public const string Schema = "sharing";

    public static void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ShareLinkEntity>(b =>
        {
            b.ToTable("share_links", Schema, t =>
            {
                t.HasCheckConstraint("ck_share_links_scope", "scope IN ('project', 'view')");
                t.HasCheckConstraint("ck_share_links_mode", "mode IN ('live', 'pinned')");
                t.HasCheckConstraint("ck_share_links_view", "(scope = 'view') = (view_id IS NOT NULL)");
                t.HasCheckConstraint("ck_share_links_revision", "(mode = 'pinned') = (revision_id IS NOT NULL)");
            });
            b.HasKey(l => l.Id);
            b.Property(l => l.Id).ValueGeneratedNever();
            b.HasIndex(l => l.TokenHash).IsUnique();
            b.HasIndex(l => new { l.ProjectId, l.CreatedAt });
            b.HasIndex(l => l.WorkspaceId);
            b.Property(l => l.Scope).HasMaxLength(16);
            b.Property(l => l.Mode).HasMaxLength(16);
            b.Property(l => l.Label).HasMaxLength(200);
            b.Property(l => l.RevisionName).HasMaxLength(200);
            b.Property(l => l.CreatedAt).HasDefaultValueSql("now()");
            b.Property(l => l.UpdatedAt).HasDefaultValueSql("now()");
        });

        modelBuilder.Entity<ShareSnapshotEntity>(b =>
        {
            b.ToTable("share_snapshots", Schema);
            b.HasKey(s => s.LinkId);
            b.Property(s => s.Content).HasColumnType("jsonb");
            b.HasOne<ShareLinkEntity>()
                .WithOne()
                .HasForeignKey<ShareSnapshotEntity>(s => s.LinkId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}

/// <summary>
/// A read-only link to a project or a view for people without an account. The token is
/// never stored: only its SHA-256. The password is stored as an ASP.NET Identity hash.
/// </summary>
public class ShareLinkEntity
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid ProjectId { get; set; }

    /// <summary><c>project</c> or <c>view</c>.</summary>
    public string Scope { get; set; } = null!;
    public Guid? ViewId { get; set; }

    /// <summary><c>live</c> (the accepted model on main, read on every visit) or <c>pinned</c> (one named revision).</summary>
    public string Mode { get; set; } = null!;
    public Guid? RevisionId { get; set; }

    /// <summary>The name and the date of the revision when the link was made: a pinned page labels them.</summary>
    public string? RevisionName { get; set; }
    public DateTimeOffset? RevisionCreatedAt { get; set; }

    /// <summary>A name the members give the link. Viewers never see it.</summary>
    public string Label { get; set; } = "";

    public bool ShowCosts { get; set; }
    public bool ShowPromises { get; set; }

    public byte[] TokenHash { get; set; } = null!;
    public string? PasswordHash { get; set; }

    /// <summary>Goes up by one on every password change, so an unlock of the old password stops working.</summary>
    public int PasswordVersion { get; set; }

    public DateTimeOffset? ExpiresAt { get; set; }
    public string CreatedBy { get; set; } = null!;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public string? RevokedBy { get; set; }

    /// <summary>The last time a visitor read the link. No visitor is recorded.</summary>
    public DateTimeOffset? LastAccessedAt { get; set; }

    /// <summary>Wrong passwords in the window that starts at <see cref="AttemptWindowStart"/>.</summary>
    public int FailedAttempts { get; set; }
    public DateTimeOffset? AttemptWindowStart { get; set; }
}

/// <summary>
/// The page of a pinned link, as it was made when the link was made: the model of the named
/// revision and the flows, schemas, docs and decisions of that moment. A pinned page never
/// changes after that.
/// </summary>
public class ShareSnapshotEntity
{
    public Guid LinkId { get; set; }
    public string Content { get; set; } = null!;
}
