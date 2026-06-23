using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Vellum.Modules.Schemas;

public class SchemasDbContextFactory : IDesignTimeDbContextFactory<SchemasDbContext>
{
    public SchemasDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<SchemasDbContext>()
            .UseNpgsql("Host=localhost")
            .UseSnakeCaseNamingConvention()
            .Options;
        return new SchemasDbContext(options);
    }
}
