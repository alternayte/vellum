// src/Vellum/Modules/Docs/DocsDbContextFactory.cs
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Vellum.Modules.Docs;

public class DocsDbContextFactory : IDesignTimeDbContextFactory<DocsDbContext>
{
    public DocsDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<DocsDbContext>()
            .UseNpgsql("Host=localhost;Database=vellum;Username=vellum;Password=vellum")
            .UseSnakeCaseNamingConvention()
            .Options;
        return new DocsDbContext(options);
    }
}
