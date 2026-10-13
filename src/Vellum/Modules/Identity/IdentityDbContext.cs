using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Vellum.Modules.Identity.AgentTokens;

namespace Vellum.Modules.Identity;

public class AppIdentityDbContext : IdentityDbContext<ApplicationUser>
{
    public AppIdentityDbContext(DbContextOptions<AppIdentityDbContext> options)
        : base(options) { }

    public DbSet<AgentTokenEntity> AgentTokens => Set<AgentTokenEntity>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.HasDefaultSchema("identity");

        builder.Entity<AgentTokenEntity>(e =>
        {
            e.ToTable("agent_tokens");
            e.HasKey(t => t.Id);
            e.Property(t => t.Name).HasMaxLength(AgentTokenRules.MaxNameLength);
            e.Property(t => t.MasterKeyId).HasMaxLength(64);
            e.Property(t => t.CreatedBy).HasMaxLength(450);
            e.Property(t => t.RevokedBy).HasMaxLength(450);
            e.HasIndex(t => t.WorkspaceId);
            e.HasIndex(t => t.ProjectId);
        });
    }
}
