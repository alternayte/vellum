using Microsoft.EntityFrameworkCore;
using Vellum.Shared;

namespace Vellum.Modules.Workspaces;

/// <summary>The tables of the module, in the Postgres schema <c>workspaces</c>.</summary>
public static class WorkspacesTables
{
    public const string Schema = "workspaces";

    public static readonly IReadOnlyList<PostgresEnum> Enums =
    [
        PostgresEnum.Of<WorkspaceRole>("workspace_role", Schema),
        PostgresEnum.Of<InvitationStatus>("invitation_status", Schema),
    ];

    public static void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<WorkspaceEntity>(b =>
        {
            b.ToTable("workspaces", Schema);
            b.HasKey(w => w.Id);
            b.Property(w => w.CreatedAt).HasDefaultValueSql("now()");
            b.Property(w => w.UpdatedAt).HasDefaultValueSql("now()");
        });

        modelBuilder.Entity<ProjectEntity>(b =>
        {
            b.ToTable("projects", Schema);
            b.HasKey(p => p.Id);
            b.HasIndex(p => p.WorkspaceId);
            b.Property(p => p.CreatedAt).HasDefaultValueSql("now()");
            b.Property(p => p.UpdatedAt).HasDefaultValueSql("now()");
        });

        modelBuilder.Entity<MembershipEntity>(b =>
        {
            b.ToTable("memberships", Schema);
            b.HasKey(m => m.Id);
            b.HasIndex(m => new { m.WorkspaceId, m.UserId }).IsUnique();
        });

        modelBuilder.Entity<InvitationEntity>(b =>
        {
            b.ToTable("invitations", Schema);
            b.HasKey(i => i.Id);
            b.Property(i => i.Email).HasMaxLength(InvitationRules.MaxEmailLength);
            b.Property(i => i.NormalizedEmail).HasMaxLength(InvitationRules.MaxEmailLength);
            // One open invitation for an address in a workspace: a second invitation renews it.
            b.HasIndex(i => new { i.WorkspaceId, i.NormalizedEmail }).IsUnique().HasFilter("status = 'pending'");
            // The invitations of the signed-in person are read by address.
            b.HasIndex(i => new { i.NormalizedEmail, i.Status });
        });
    }
}

public class WorkspaceEntity
{
    public Guid Id { get; set; }
    public string Name { get; set; } = null!;
    public string CreatedBy { get; set; } = null!;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public class ProjectEntity
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public string Name { get; set; } = null!;
    public string? Description { get; set; }
    public Guid StreamId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public class MembershipEntity
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public string UserId { get; set; } = null!;
    public WorkspaceRole Role { get; set; }
    /// <summary>
    /// When the membership was made. No writer set this before the member list showed it,
    /// so a row from that time holds the zero date; the list reads that as not recorded.
    /// </summary>
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// An offer of a membership to an email address. The person with an account of that address
/// accepts or declines it; only an accept makes a membership. An invitation names an address
/// and no account, so its creation says nothing about whether an account exists.
/// </summary>
public class InvitationEntity
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    /// <summary>The address as the owner typed it, trimmed.</summary>
    public string Email { get; set; } = null!;
    /// <summary>The address as Identity normalizes it for a lookup (upper case).</summary>
    public string NormalizedEmail { get; set; } = null!;
    public WorkspaceRole Role { get; set; }
    /// <summary>One of <see cref="InvitationStatus"/>. An expired invitation stays pending here: expiry is read from <see cref="ExpiresAt"/>.</summary>
    public InvitationStatus Status { get; set; } = InvitationStatus.Pending;
    public string InvitedBy { get; set; } = null!;
    /// <summary>
    /// The SHA-256 hash of the secret of the invitation link. The secret itself is shown once,
    /// to the owner, and stored nowhere. Null for an invitation made before links had a
    /// secret: it cannot be accepted, and inviting the address again gives it a secret.
    /// </summary>
    public byte[]? SecretHash { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    /// <summary>Who accepted, declined or revoked it, and when.</summary>
    public string? RespondedBy { get; set; }
    public DateTimeOffset? RespondedAt { get; set; }
}

public enum InvitationStatus
{
    Pending,
    Accepted,
    Declined,
    Revoked,
    /// <summary>Not stored: a pending invitation whose expiry has passed.</summary>
    Expired,
}
