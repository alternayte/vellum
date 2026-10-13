using MassTransit;
using Microsoft.EntityFrameworkCore;

namespace Orders.Api;

public sealed class OrdersDbContext(DbContextOptions<OrdersDbContext> options) : DbContext(options)
{
    public DbSet<Order> Orders => Set<Order>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Order>(order =>
        {
            order.ToTable("orders");
            order.HasKey(o => o.Id);
            order.OwnsMany(o => o.Lines, lines => lines.ToJson());
        });

        // Outbox tables of MassTransit: outbox_message, outbox_state, inbox_state.
        modelBuilder.AddOutboxMessageEntity();
        modelBuilder.AddOutboxStateEntity();
        modelBuilder.AddInboxStateEntity();
    }
}
