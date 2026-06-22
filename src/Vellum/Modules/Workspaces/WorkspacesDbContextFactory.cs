using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Vellum.Modules.Workspaces;

public class WorkspacesDbContextFactory : IDesignTimeDbContextFactory<WorkspacesDbContext>
{
    public WorkspacesDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<WorkspacesDbContext>()
            .UseNpgsql("Host=localhost;Database=vellum;Username=vellum;Password=vellum")
            .UseSnakeCaseNamingConvention()
            .Options;

        return new WorkspacesDbContext(options);
    }
}
