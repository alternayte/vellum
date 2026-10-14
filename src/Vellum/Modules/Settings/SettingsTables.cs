using Microsoft.EntityFrameworkCore;

namespace Vellum.Modules.Settings;

/// <summary>The tables of the module, in the Postgres schema <c>settings</c>.</summary>
public static class SettingsTables
{
    public const string Schema = "settings";

    public static void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<InstanceSettingsEntity>(b =>
        {
            b.ToTable("instance_settings", Schema, t => t.HasCheckConstraint("ck_instance_settings_singleton", "id = 1"));
            b.HasKey(s => s.Id);
            b.Property(s => s.Id).ValueGeneratedNever();
            b.Property(s => s.Policy).HasColumnType("jsonb");
            b.Property(s => s.Defaults).HasColumnType("jsonb");
            b.Property(s => s.Version).HasDefaultValue(1L);
            b.Property(s => s.UpdatedAt).HasDefaultValueSql("now()");
        });

        modelBuilder.Entity<WorkspaceSettingsEntity>(b =>
        {
            b.ToTable("workspace_settings", Schema);
            b.HasKey(s => s.WorkspaceId);
            b.Property(s => s.WorkspaceId).ValueGeneratedNever();
            b.Property(s => s.Overrides).HasColumnType("jsonb");
            b.Property(s => s.Version).HasDefaultValue(1L);
            b.Property(s => s.UpdatedAt).HasDefaultValueSql("now()");
        });

        modelBuilder.Entity<SecretEntity>(b =>
        {
            b.ToTable("secrets", Schema);
            b.HasKey(s => new { s.WorkspaceId, s.Name });
            b.Property(s => s.Name).HasMaxLength(200);
            b.Property(s => s.BoundOrigin).HasMaxLength(300);
            b.Property(s => s.UpdatedAt).HasDefaultValueSql("now()");
        });

        modelBuilder.Entity<AuditEntryEntity>(b =>
        {
            b.ToTable("audit_entries", Schema);
            b.HasKey(a => a.Id);
            b.Property(a => a.Id).UseIdentityAlwaysColumn();
            b.Property(a => a.At).HasDefaultValueSql("now()");
            b.HasIndex(a => new { a.WorkspaceId, a.Id });
        });

        modelBuilder.Entity<InstanceAdminEntity>(b =>
        {
            b.ToTable("instance_admins", Schema);
            b.HasKey(a => a.UserId);
            b.Property(a => a.GrantedAt).HasDefaultValueSql("now()");
        });

        modelBuilder.Entity<DataProtectionKeyEntity>(b =>
        {
            b.ToTable("data_protection_keys", Schema);
            b.HasKey(k => k.Id);
            b.Property(k => k.Id).UseIdentityAlwaysColumn();
        });
    }
}

/// <summary>One row (<see cref="SingletonId"/>). A null column means "use the profile default".</summary>
public class InstanceSettingsEntity
{
    public const int SingletonId = 1;

    public int Id { get; set; }
    public string? Policy { get; set; }
    public string? Defaults { get; set; }
    /// <summary>Goes up by one on every save. A writer states the version it read.</summary>
    public long Version { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public string UpdatedBy { get; set; } = null!;
}

public class WorkspaceSettingsEntity
{
    public Guid WorkspaceId { get; set; }
    public string Overrides { get; set; } = null!;
    /// <summary>Goes up by one on every save. A writer states the version it read.</summary>
    public long Version { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public string UpdatedBy { get; set; } = null!;
}

/// <summary>An envelope-encrypted credential. No column holds the value.</summary>
public class SecretEntity
{
    public Guid WorkspaceId { get; set; }
    public string Name { get; set; } = null!;
    public string MasterKeyId { get; set; } = null!;
    public byte[] WrapNonce { get; set; } = null!;
    public byte[] WrappedKey { get; set; } = null!;
    public byte[] Nonce { get; set; } = null!;
    public byte[] Ciphertext { get; set; } = null!;
    public string Fingerprint { get; set; } = null!;
    /// <summary>
    /// The origin (scheme, host, port) that the credential was saved for. It is part of the
    /// encrypted row's associated data. Null on a row from before origins were recorded;
    /// such a row is never sent anywhere.
    /// </summary>
    public string? BoundOrigin { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public string UpdatedBy { get; set; } = null!;
}

/// <summary>Append-only: a database trigger refuses UPDATE, DELETE and TRUNCATE.</summary>
public class AuditEntryEntity
{
    public long Id { get; set; }
    public Guid? WorkspaceId { get; set; }
    public DateTimeOffset At { get; set; }
    public string ActorId { get; set; } = null!;
    public string Action { get; set; } = null!;
    public string Target { get; set; } = null!;
}

public class InstanceAdminEntity
{
    public string UserId { get; set; } = null!;
    public DateTimeOffset GrantedAt { get; set; }
}

public class DataProtectionKeyEntity
{
    public int Id { get; set; }
    public string? FriendlyName { get; set; }
    public string Xml { get; set; } = null!;
}
