using Microsoft.EntityFrameworkCore;
using Vellum.Modules.Settings.Entities;

namespace Vellum.Modules.Settings;

public class SettingsDbContext : DbContext
{
    public SettingsDbContext(DbContextOptions<SettingsDbContext> options)
        : base(options) { }

    public DbSet<InstanceSettingsEntity> InstanceSettings => Set<InstanceSettingsEntity>();
    public DbSet<WorkspaceSettingsEntity> WorkspaceSettings => Set<WorkspaceSettingsEntity>();
    public DbSet<SecretEntity> Secrets => Set<SecretEntity>();
    public DbSet<AuditEntryEntity> AuditEntries => Set<AuditEntryEntity>();
    public DbSet<InstanceAdminEntity> InstanceAdmins => Set<InstanceAdminEntity>();
    public DbSet<DataProtectionKeyEntity> DataProtectionKeys => Set<DataProtectionKeyEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("settings");

        modelBuilder.Entity<InstanceSettingsEntity>(b =>
        {
            b.ToTable("instance_settings", t => t.HasCheckConstraint("ck_instance_settings_singleton", "id = 1"));
            b.HasKey(s => s.Id);
            b.Property(s => s.Id).ValueGeneratedNever();
            b.Property(s => s.Policy).HasColumnType("jsonb");
            b.Property(s => s.Defaults).HasColumnType("jsonb");
            b.Property(s => s.Version).HasDefaultValue(1L);
            b.Property(s => s.UpdatedAt).HasDefaultValueSql("now()");
        });

        modelBuilder.Entity<WorkspaceSettingsEntity>(b =>
        {
            b.ToTable("workspace_settings");
            b.HasKey(s => s.WorkspaceId);
            b.Property(s => s.WorkspaceId).ValueGeneratedNever();
            b.Property(s => s.Overrides).HasColumnType("jsonb");
            b.Property(s => s.Version).HasDefaultValue(1L);
            b.Property(s => s.UpdatedAt).HasDefaultValueSql("now()");
        });

        modelBuilder.Entity<SecretEntity>(b =>
        {
            b.ToTable("secrets");
            b.HasKey(s => new { s.WorkspaceId, s.Name });
            b.Property(s => s.Name).HasMaxLength(200);
            b.Property(s => s.BoundOrigin).HasMaxLength(300);
            b.Property(s => s.UpdatedAt).HasDefaultValueSql("now()");
        });

        modelBuilder.Entity<AuditEntryEntity>(b =>
        {
            b.ToTable("audit_entries");
            b.HasKey(a => a.Id);
            b.Property(a => a.Id).UseIdentityAlwaysColumn();
            b.Property(a => a.At).HasDefaultValueSql("now()");
            b.HasIndex(a => new { a.WorkspaceId, a.Id });
        });

        modelBuilder.Entity<InstanceAdminEntity>(b =>
        {
            b.ToTable("instance_admins");
            b.HasKey(a => a.UserId);
            b.Property(a => a.GrantedAt).HasDefaultValueSql("now()");
        });

        modelBuilder.Entity<DataProtectionKeyEntity>(b =>
        {
            b.ToTable("data_protection_keys");
            b.HasKey(k => k.Id);
            b.Property(k => k.Id).UseIdentityAlwaysColumn();
        });
    }
}
