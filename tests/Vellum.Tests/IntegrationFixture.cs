using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using Vellum.Kernel.EventStore;
using Vellum.Modules.Identity;
using Vellum.Modules.Modelling;
using Vellum.Modules.Docs;
using Vellum.Modules.Views;
using Vellum.Modules.Workspaces;

namespace Vellum.Tests;

public class IntegrationFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17")
        .Build();

    public string ConnectionString => _postgres.GetConnectionString();

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();

        await MigrateAsync<EventStoreDbContext>();
        await MigrateAsync<AppIdentityDbContext>();
        await MigrateAsync<WorkspacesDbContext>();
        await MigrateAsync<ModellingDbContext>();
        await MigrateAsync<ViewsDbContext>();
        await MigrateAsync<DocsDbContext>();
    }

    private async Task MigrateAsync<TContext>() where TContext : DbContext
    {
        var options = new DbContextOptionsBuilder<TContext>()
            .UseNpgsql(ConnectionString)
            .UseSnakeCaseNamingConvention()
            .Options;
        await using var db = (TContext)Activator.CreateInstance(typeof(TContext), options)!;
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync() => await _postgres.DisposeAsync();
}

[CollectionDefinition("Integration")]
public class IntegrationCollection : ICollectionFixture<IntegrationFixture>;
