// src/Vellum/Modules/Drafts/DraftsDbContextFactory.cs
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Vellum.Modules.Drafts;

public class DraftsDbContextFactory : IDesignTimeDbContextFactory<DraftsDbContext>
{
    public DraftsDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<DraftsDbContext>()
            .UseNpgsql("Host=localhost;Database=vellum;Username=vellum;Password=vellum")
            .UseSnakeCaseNamingConvention()
            .Options;
        return new DraftsDbContext(options);
    }
}
