using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Vellum.Modules.Identity.AgentTokens;

namespace Vellum.Modules.Identity;

/// <summary>
/// The tables of the module, in the Postgres schema <c>identity</c>: the ASP.NET Identity
/// tables, which <see cref="Vellum.Shared.AppDbContext"/> gets from its base class, and the
/// agent tokens.
/// </summary>
public static class IdentityTables
{
    public const string Schema = "identity";

    /// <summary>Runs after the Identity base class has configured its own entity types.</summary>
    public static void Configure(ModelBuilder builder)
    {
        // Identity names its tables and indexes AspNetUsers, EmailIndex and so on. Here they
        // get snake_case names like every other table.
        builder.Entity<ApplicationUser>(b =>
        {
            b.ToTable("users", Schema);
            b.HasIndex(u => u.NormalizedEmail).HasDatabaseName("ix_users_normalized_email");
            b.HasIndex(u => u.NormalizedUserName).HasDatabaseName("ix_users_normalized_user_name");
        });
        builder.Entity<IdentityRole>(b =>
        {
            b.ToTable("roles", Schema);
            b.HasIndex(r => r.NormalizedName).HasDatabaseName("ix_roles_normalized_name");
        });
        builder.Entity<IdentityUserRole<string>>().ToTable("user_roles", Schema);
        builder.Entity<IdentityUserClaim<string>>().ToTable("user_claims", Schema);
        builder.Entity<IdentityUserLogin<string>>().ToTable("user_logins", Schema);
        builder.Entity<IdentityUserToken<string>>().ToTable("user_tokens", Schema);
        builder.Entity<IdentityRoleClaim<string>>().ToTable("role_claims", Schema);

        builder.Entity<AgentTokenEntity>(e =>
        {
            e.ToTable("agent_tokens", Schema);
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
