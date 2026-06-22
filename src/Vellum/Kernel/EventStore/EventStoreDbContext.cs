using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Vellum.Kernel.Outbox;
using Vellum.Kernel.Projections;

namespace Vellum.Kernel.EventStore;

public class EventStoreDbContext : DbContext
{
    public EventStoreDbContext(DbContextOptions<EventStoreDbContext> options)
        : base(options) { }

    public DbSet<StreamEntity> Streams => Set<StreamEntity>();
    public DbSet<EventEntity> Events => Set<EventEntity>();
    public DbSet<OutboxMessageEntity> OutboxMessages => Set<OutboxMessageEntity>();
    public DbSet<DeadLetterEntity> DeadLetters => Set<DeadLetterEntity>();
    public DbSet<CheckpointEntity> Checkpoints => Set<CheckpointEntity>();

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

        modelBuilder.Entity<OutboxMessageEntity>(b =>
        {
            b.ToTable("outbox_messages");
            b.HasKey(o => o.Id);
            b.Property(o => o.Id).UseIdentityAlwaysColumn();
            b.Property(o => o.Payload).HasColumnType("jsonb");
            b.Property(o => o.CreatedAt).HasDefaultValueSql("now()");
            b.HasIndex(o => new { o.ProcessedAt, o.NextRetryAt });
        });

        modelBuilder.Entity<DeadLetterEntity>(b =>
        {
            b.ToTable("dead_letters");
            b.HasKey(d => d.Id);
            b.Property(d => d.Id).UseIdentityAlwaysColumn();
            b.Property(d => d.Payload).HasColumnType("jsonb");
            b.Property(d => d.FailedAt).HasDefaultValueSql("now()");
        });

        modelBuilder.Entity<CheckpointEntity>(b =>
        {
            b.ToTable("checkpoints");
            b.HasKey(c => c.ProjectionName);
            b.Property(c => c.UpdatedAt).HasDefaultValueSql("now()");
        });
    }
}
