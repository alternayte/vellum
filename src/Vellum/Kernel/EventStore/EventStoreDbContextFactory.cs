using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Vellum.Kernel.EventStore;

/// <summary>
/// Design-time factory used by EF Core tools (migrations, scaffolding).
/// Not used at runtime.
/// </summary>
public class EventStoreDbContextFactory : IDesignTimeDbContextFactory<EventStoreDbContext>
{
    public EventStoreDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<EventStoreDbContext>()
            .UseNpgsql("Host=localhost;Database=vellum;Username=vellum;Password=vellum")
            .UseSnakeCaseNamingConvention()
            .Options;

        return new EventStoreDbContext(options);
    }
}
