using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Vellum.Modules.Scoring;

public class ScoringDbContextFactory : IDesignTimeDbContextFactory<ScoringDbContext>
{
    public ScoringDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<ScoringDbContext>()
            .UseNpgsql("Host=localhost;Database=vellum;Username=vellum;Password=vellum")
            .UseSnakeCaseNamingConvention()
            .Options;
        return new ScoringDbContext(options);
    }
}
