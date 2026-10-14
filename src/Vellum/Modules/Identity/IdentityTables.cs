using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Vellum.Modules.Identity.AgentTokens;
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Net.Http.Headers;
using Vellum.Modules.Settings.Crypto;

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

/// <summary>
/// One agent token. No column holds the token: <see cref="TokenHash"/> is an HMAC-SHA-256
/// under a key that is derived from the master key <see cref="MasterKeyId"/> names.
/// </summary>
public class AgentTokenEntity
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    /// <summary>The one project the token works on, or null for every project of the workspace.</summary>
    public Guid? ProjectId { get; set; }
    public string Name { get; set; } = null!;
    public string[] Scopes { get; set; } = [];
    public byte[] TokenHash { get; set; } = null!;
    public string MasterKeyId { get; set; } = null!;
    /// <summary>The user the token acts for. The token has no more access than this user's role gives.</summary>
    public string CreatedBy { get; set; } = null!;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }
    /// <summary>When a refused use was last written to the audit log.</summary>
    public DateTimeOffset? LastRefusedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public string? RevokedBy { get; set; }
}

public class ApplicationUser : IdentityUser
{
    public string? DisplayName { get; set; }
}
