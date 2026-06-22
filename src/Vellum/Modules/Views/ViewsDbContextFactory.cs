using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Vellum.Modules.Views;

public class ViewsDbContextFactory : IDesignTimeDbContextFactory<ViewsDbContext>
{
    public ViewsDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<ViewsDbContext>()
            .UseNpgsql("Host=localhost;Database=vellum;Username=vellum;Password=vellum")
            .UseSnakeCaseNamingConvention()
            .Options;
        return new ViewsDbContext(options);
    }
}
