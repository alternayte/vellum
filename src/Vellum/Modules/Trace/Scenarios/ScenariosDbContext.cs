using Microsoft.EntityFrameworkCore;
using Vellum.Modules.Trace.Scenarios.Entities;

namespace Vellum.Modules.Trace.Scenarios;

public class ScenariosDbContext : DbContext
{
    public ScenariosDbContext(DbContextOptions<ScenariosDbContext> options) : base(options) { }

    public DbSet<ScenarioEntity> Scenarios => Set<ScenarioEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("trace");
        modelBuilder.Entity<ScenarioEntity>(b =>
        {
            b.HasKey(s => s.Id);
            b.HasIndex(s => new { s.ProjectId, s.Name });
        });
    }
}
