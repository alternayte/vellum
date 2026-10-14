using Microsoft.EntityFrameworkCore;
using Vellum.Modules.Trace.Scenarios.Entities;

namespace Vellum.Modules.Trace.Scenarios;

/// <summary>The tables of the module, in the Postgres schema <c>trace</c>.</summary>
public static class ScenariosTables
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
