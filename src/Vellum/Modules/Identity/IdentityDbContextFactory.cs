using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Vellum.Modules.Identity;

public class IdentityDbContextFactory : IDesignTimeDbContextFactory<AppIdentityDbContext>
{
    public AppIdentityDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<AppIdentityDbContext>()
            .UseNpgsql("Host=localhost;Database=vellum;Username=vellum;Password=vellum")
            .UseSnakeCaseNamingConvention()
            .Options;

        return new AppIdentityDbContext(options);
    }
}
