using Microsoft.EntityFrameworkCore;
using Vellum.Modules.Schemas.Entities;

namespace Vellum.Modules.Schemas;

public class SchemasDbContext : DbContext
{
    public SchemasDbContext(DbContextOptions<SchemasDbContext> options) : base(options) { }
    public DbSet<SchemaEntity> Schemas => Set<SchemaEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("schemas");
        modelBuilder.Entity<SchemaEntity>(b =>
        {
            b.HasKey(s => s.Id);
            b.HasIndex(s => s.ProjectId);
        });
    }
}
