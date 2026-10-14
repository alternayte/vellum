using Microsoft.EntityFrameworkCore;

namespace Vellum.Modules.Trace;

/// <summary>The tables of the module, in the Postgres schema <c>trace</c>.</summary>
public static class TraceTables
{
    public const string Schema = "trace";

    public static void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ScenarioEntity>(b =>
        {
            b.ToTable("scenarios", Schema);
            b.HasKey(s => s.Id);
            b.HasIndex(s => new { s.ProjectId, s.Name });
        });
    }
}

public class ScenarioEntity
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public string Name { get; set; } = null!;
}
