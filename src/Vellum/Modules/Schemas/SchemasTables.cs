using Microsoft.EntityFrameworkCore;
using Vellum.Modules.Schemas.Entities;

namespace Vellum.Modules.Schemas;

/// <summary>The tables of the module, in the Postgres schema <c>schemas</c>.</summary>
public static class SchemasTables
{
    public const string Schema = "schemas";

    public static void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SchemaEntity>(b =>
        {
            b.ToTable("schemas", Schema);
            b.HasKey(s => s.Id);
            b.HasIndex(s => s.ProjectId);
        });
    }
}
