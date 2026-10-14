using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Vellum.Shared;

/// <summary>
/// The context for <c>dotnet ef</c> (<c>just migration</c>). Creating a migration needs no
/// database; the connection string is never opened.
/// </summary>
public class AppDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args) => new(
        new DbContextOptionsBuilder<AppDbContext>()
            .UseAppDatabase("Host=localhost")
            .Options);
}
