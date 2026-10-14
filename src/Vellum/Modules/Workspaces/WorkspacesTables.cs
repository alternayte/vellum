using Microsoft.EntityFrameworkCore;
using Vellum.Modules.Workspaces.Entities;

namespace Vellum.Modules.Workspaces;

/// <summary>The tables of the module, in the Postgres schema <c>workspaces</c>.</summary>
public static class WorkspacesTables
{
    public const string Schema = "workspaces";

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
            b.Property(i => i.Role).HasMaxLength(16);
            b.Property(i => i.Status).HasMaxLength(16);
            // One open invitation for an address in a workspace: a second invitation renews it.
            b.HasIndex(i => new { i.WorkspaceId, i.NormalizedEmail }).IsUnique().HasFilter("status = 'pending'");
            // The invitations of the signed-in person are read by address.
            b.HasIndex(i => new { i.NormalizedEmail, i.Status });
        });
    }
}
