using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Vellum.Modules.Modelling;

public class ModellingDbContextFactory : IDesignTimeDbContextFactory<ModellingDbContext>
{
    public ModellingDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<ModellingDbContext>()
            .UseNpgsql("Host=localhost;Database=vellum;Username=vellum;Password=vellum")
            .UseSnakeCaseNamingConvention()
            .Options;
        return new ModellingDbContext(options);
    }
}
