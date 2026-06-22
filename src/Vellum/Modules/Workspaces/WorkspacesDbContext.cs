using Microsoft.EntityFrameworkCore;
using Vellum.Modules.Workspaces.Entities;

namespace Vellum.Modules.Workspaces;

public class WorkspacesDbContext : DbContext
{
    public WorkspacesDbContext(DbContextOptions<WorkspacesDbContext> options)
        : base(options) { }

    public DbSet<WorkspaceEntity> Workspaces => Set<WorkspaceEntity>();
    public DbSet<ProjectEntity> Projects => Set<ProjectEntity>();
    public DbSet<MembershipEntity> Memberships => Set<MembershipEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("workspaces");

        modelBuilder.Entity<WorkspaceEntity>(b =>
        {
            b.HasKey(w => w.Id);
            b.Property(w => w.CreatedAt).HasDefaultValueSql("now()");
            b.Property(w => w.UpdatedAt).HasDefaultValueSql("now()");
        });

        modelBuilder.Entity<ProjectEntity>(b =>
        {
            b.HasKey(p => p.Id);
            b.HasIndex(p => p.WorkspaceId);
            b.Property(p => p.CreatedAt).HasDefaultValueSql("now()");
            b.Property(p => p.UpdatedAt).HasDefaultValueSql("now()");
        });

        modelBuilder.Entity<MembershipEntity>(b =>
        {
            b.HasKey(m => m.Id);
            b.HasIndex(m => new { m.WorkspaceId, m.UserId }).IsUnique();
        });
    }
}
