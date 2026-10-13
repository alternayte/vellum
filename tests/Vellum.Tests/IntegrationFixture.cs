using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using Vellum.Modules.Identity;
using Vellum.Modules.Modelling;
using Vellum.Modules.Docs;
using Vellum.Modules.Drafts;
using Vellum.Modules.Schemas;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Vellum.Modules.Scoring;
using Vellum.Modules.Settings;
using Vellum.Modules.Trace.Scenarios;
using Vellum.Modules.Views;
using Vellum.Modules.Workspaces;

namespace Vellum.Tests;

public class IntegrationFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17")
        .Build();

    private readonly string _dataDirectory = Path.Combine(Path.GetTempPath(), $"vellum-tests-{Guid.NewGuid():N}");

    public string ConnectionString => _postgres.GetConnectionString();

    /// <summary>The instance admin that first-run setup created on the shared database.</summary>
    public string AdminEmail { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();

        await MigrateAsync<AppIdentityDbContext>();
        await MigrateAsync<WorkspacesDbContext>();
        await MigrateAsync<ModellingDbContext>();
        await MigrateAsync<ViewsDbContext>();
        await MigrateAsync<DocsDbContext>();
        await MigrateAsync<DraftsDbContext>();
        await MigrateAsync<SchemasDbContext>();
        await MigrateAsync<ScoringDbContext>();
        await MigrateAsync<ScenariosDbContext>();
        await MigrateAsync<SettingsDbContext>();
        await MigrateAsync<Vellum.Modules.Ai.Usage.AiUsageDbContext>();
        await MigrateAsync<Vellum.Modules.Ai.Design.AiDesignDbContext>();

        // Every local-profile host of this run keeps its generated master key in one
        // directory outside the source tree, so all of them share one key.
        Environment.SetEnvironmentVariable("Vellum__DataDirectory", _dataDirectory);

        // Tests register accounts freely. An instance allows that only after first-run
        // setup and only when its admin opens registration, so the fixture does both.
        using var factory = TestHost.Create(this);
        var (admin, _, email) = await TestHost.SetUpAsync(factory);
        AdminEmail = email;
        var settings = (await admin.GetFromJsonAsync<JsonObject>("/api/instance/settings"))!;
        settings["policy"]!["allowSelfRegistration"] = true;
        var opened = await admin.PutInstanceSettingsAsync(settings);
        Assert.True(opened.IsSuccessStatusCode, await opened.Content.ReadAsStringAsync());
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

    public async Task DisposeAsync()
    {
        await _postgres.DisposeAsync();
        if (Directory.Exists(_dataDirectory))
            Directory.Delete(_dataDirectory, recursive: true);
    }
}

[CollectionDefinition("Integration")]
public class IntegrationCollection : ICollectionFixture<IntegrationFixture>;
