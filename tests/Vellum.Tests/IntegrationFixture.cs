using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;
using Vellum.Modules.Settings;
using Vellum.Shared;

namespace Vellum.Tests;

/// <summary>
/// The class fixture of the integration tests: a database of its own, copied from the
/// template of the run, and one app over it that the tests of the class share. Each class
/// has its own instance, so classes run in parallel.
/// </summary>
public class IntegrationFixture : IAsyncLifetime
{
    private readonly Lazy<WebApplicationFactory<Program>> _factory;

    public IntegrationFixture() => _factory = new(() => TestHost.Create(ConnectionString));

    /// <summary>The database of this class.</summary>
    public string ConnectionString { get; private set; } = null!;

    /// <summary>The instance admin that first-run setup created on the template database.</summary>
    public string AdminEmail => TestDatabase.AdminEmail;

    /// <summary>The app over the database of this class, started when a test first asks for it.</summary>
    public WebApplicationFactory<Program> Factory => _factory.Value;

    public async Task InitializeAsync() => ConnectionString = await TestDatabase.CreateFromTemplateAsync();

    public async Task DisposeAsync()
    {
        if (_factory.IsValueCreated)
            await _factory.Value.DisposeAsync();
        await TestDatabase.DropAsync(ConnectionString);
    }
}

/// <summary>
/// One Postgres server for the whole run, and two template databases that tests copy: one
/// with every schema and no row, and one where first-run setup is done and self-registration
/// is open.
/// </summary>
internal static class TestDatabase
{
    private const string SchemaName = "vellum_schema";
    private const string TemplateName = "vellum_template";

    private static readonly string Root = Path.Combine(Path.GetTempPath(), $"vellum-tests-{Guid.NewGuid():N}");

    private static readonly PostgreSqlContainer Postgres = new PostgreSqlBuilder("postgres:17")
        // The data of a test run is thrown away, so it never needs to reach a disk.
        .WithTmpfsMount("/var/lib/postgresql/data")
        .WithCommand("-c", "fsync=off", "-c", "synchronous_commit=off", "-c", "full_page_writes=off", "-c", "max_connections=1000")
        .Build();

    private static readonly Lazy<Task> Templates = new(CreateTemplatesAsync);

    private static string? _adminEmail;

    static TestDatabase()
    {
        // A host that no test gives a data directory keeps its files here, not in the source tree.
        Environment.SetEnvironmentVariable("Vellum__DataDirectory", Path.Combine(Root, "default"));
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
            }
        };
    }

    public static string AdminEmail => _adminEmail ?? throw new InvalidOperationException("The template database is not ready.");

    /// <summary>A copy of the template where first-run setup is done and self-registration is open.</summary>
    public static async Task<string> CreateFromTemplateAsync()
    {
        await Templates.Value;
        return await CreateDatabaseAsync($"TEMPLATE {TemplateName}");
    }

    /// <summary>
    /// A database with every schema and no row: an instance that has never started. The app
    /// finds its migrations applied when it starts.
    /// </summary>
    public static async Task<string> CreateUnsetAsync()
    {
        await Templates.Value;
        return await CreateDatabaseAsync($"TEMPLATE {SchemaName}");
    }

    /// <summary>A database with no schema at all.</summary>
    public static async Task<string> CreateEmptyAsync()
    {
        await Templates.Value;
        return await CreateDatabaseAsync("");
    }

    /// <summary>A server connection string for <paramref name="database"/>.</summary>
    private static string On(string database) =>
        new NpgsqlConnectionStringBuilder(Postgres.GetConnectionString()) { Database = database }.ToString();

    private static async Task<string> CreateDatabaseAsync(string options)
    {
        var database = $"t_{Guid.NewGuid():N}";
        await ExecuteAsync($"CREATE DATABASE {database} {options}");
        return On(database);
    }

    private static async Task ExecuteAsync(string sql)
    {
        await using var admin = new NpgsqlConnection(On("postgres"));
        await admin.OpenAsync();
        await using var command = new NpgsqlCommand(sql, admin);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Drops a database that no test uses any more. The server keeps its data in memory, so a
    /// run that kept every copy would run the container out of memory.
    /// </summary>
    public static async Task DropAsync(string? connectionString)
    {
        if (connectionString is null)
            return;
        NpgsqlConnection.ClearPool(new NpgsqlConnection(connectionString));
        var database = new NpgsqlConnectionStringBuilder(connectionString).Database;
        await ExecuteAsync($"DROP DATABASE IF EXISTS {database} WITH (FORCE)");
    }

    /// <summary>
    /// The data directory of the instance over <paramref name="connectionString"/>. Hosts over
    /// one database share it, as the instances of one deployment do; hosts over different
    /// databases never share a setup code file. Each starts with the master key of the run, as
    /// all hosts shared one before tests ran in parallel, so every copy of the template can
    /// read what the template holds.
    /// </summary>
    public static string DataDirectory(string connectionString)
    {
        var database = new NpgsqlConnectionStringBuilder(connectionString).Database ?? "default";
        var directory = Path.Combine(Root, database);
        if (database == TemplateName)
            return directory;

        Directory.CreateDirectory(directory);
        var key = Path.Combine(Root, TemplateName, Bootstrap.MasterKeyFileName);
        var copy = Path.Combine(directory, Bootstrap.MasterKeyFileName);
        if (File.Exists(key) && !File.Exists(copy))
        {
            try
            {
                File.Copy(key, copy);
            }
            catch (IOException) when (File.Exists(copy))
            {
                // Another host over the same database copied it first.
            }
        }

        return directory;
    }

    private static async Task CreateTemplatesAsync()
    {
        await Postgres.StartAsync();

        await ExecuteAsync($"CREATE DATABASE {SchemaName}");
        var schema = On(SchemaName);
        await MigrateAsync(schema);
        await SealAsync(SchemaName);

        // Tests register accounts freely. An instance allows that only after first-run
        // setup and only when its admin opens registration, so the template has both.
        await ExecuteAsync($"CREATE DATABASE {TemplateName} TEMPLATE {SchemaName}");
        await using (var factory = TestHost.Create(On(TemplateName)))
        {
            var (admin, _, email) = await TestHost.SetUpAsync(factory);
            var settings = (await admin.GetFromJsonAsync<JsonObject>("/api/instance/settings"))!;
            settings["policy"]!["allowSelfRegistration"] = true;
            var opened = await admin.PutInstanceSettingsAsync(settings);
            Assert.True(opened.IsSuccessStatusCode, await opened.Content.ReadAsStringAsync());
            _adminEmail = email;
        }
        await SealAsync(TemplateName);
    }

    /// <summary>A database is copied only while nobody is connected to it.</summary>
    private static async Task SealAsync(string database)
    {
        NpgsqlConnection.ClearAllPools();
        await ExecuteAsync($"ALTER DATABASE {database} WITH IS_TEMPLATE true ALLOW_CONNECTIONS false");
        await ExecuteAsync($"SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = '{database}'");
    }

    private static async Task MigrateAsync(string connectionString)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseAppDatabase(connectionString)
            .Options;
        await using var db = new AppDbContext(options);
        await db.Database.MigrateAsync();
        await DatabaseObjects.EnsureAsync(connectionString);
    }
}
