using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Vellum.Modules.Trace.Scenarios;

public class ScenariosDbContextFactory : IDesignTimeDbContextFactory<ScenariosDbContext>
{
    public ScenariosDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<ScenariosDbContext>()
            .UseNpgsql("Host=localhost")
            .UseSnakeCaseNamingConvention()
            .Options;
        return new ScenariosDbContext(options);
    }
}
