using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace Vellum.Kernel.EventStore;

public class EventStoreDbContext : DbContext
{
    public EventStoreDbContext(DbContextOptions<EventStoreDbContext> options)
        : base(options) { }

    public DbSet<StreamEntity> Streams => Set<StreamEntity>();
    public DbSet<EventEntity> Events => Set<EventEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("es");

        modelBuilder.Entity<StreamEntity>(b =>
        {
            b.ToTable("streams");
            b.HasKey(s => s.StreamId);
            b.Property(s => s.Version).IsConcurrencyToken();
            b.Property(s => s.State).HasColumnType("jsonb");
            b.Property(s => s.CreatedAt).HasDefaultValueSql("now()");
            b.Property(s => s.UpdatedAt).HasDefaultValueSql("now()");
        });

        modelBuilder.Entity<EventEntity>(b =>
        {
            b.ToTable("events");
            b.HasKey(e => new { e.StreamId, e.Version });
            b.Property(e => e.GlobalPosition).UseIdentityAlwaysColumn();
            b.Property(e => e.Payload).HasColumnType("jsonb");
            b.Property(e => e.Metadata).HasColumnType("jsonb");
            b.Property(e => e.OccurredAt).HasDefaultValueSql("now()");
            b.HasIndex(e => e.GlobalPosition).IsUnique();
        });
    }
}
