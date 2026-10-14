using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging;
using Npgsql;
using Vellum.Tests.Shared.Outbound;

namespace Vellum.Tests;

[Collection("Integration")]
public class StartupTests
{
    private readonly IntegrationFixture _fixture;

    public StartupTests(IntegrationFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task A_production_start_on_an_empty_database_creates_every_schema_and_serves_requests()
    {
        var database = $"empty_{Guid.NewGuid():N}";
        await using (var admin = new NpgsqlConnection(_fixture.ConnectionString))
        {
            await admin.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE {database}", admin);
            await create.ExecuteNonQueryAsync();
        }
        var connectionString = new NpgsqlConnectionStringBuilder(_fixture.ConnectionString) { Database = database }.ToString();

        var logs = new LogCapture();
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("ConnectionStrings:DefaultConnection", connectionString);
            builder.ConfigureLogging(logging => logging.AddProvider(logs));
        });
        using var client = factory.CreateClient();

        // An anonymous call reaches the app and is refused by it, not by a start-up failure.
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);

        // The log of a clean install holds no error, and no line for each SQL command.
        Assert.Contains(logs.Lines, l => l.Contains("Applying migration", StringComparison.Ordinal));
        Assert.DoesNotContain(logs.Lines, l => l.StartsWith("Error", StringComparison.Ordinal) || l.StartsWith("Critical", StringComparison.Ordinal));
        Assert.DoesNotContain(logs.Lines, l => l.Contains("Executed DbCommand", StringComparison.Ordinal));

        await using var check = new NpgsqlConnection(connectionString);
        await check.OpenAsync();
        await using var schemas = new NpgsqlCommand(
            "SELECT count(*) FROM information_schema.schemata WHERE schema_name IN ('deedbox','identity','workspaces','modelling','views','docs','drafts','schemas','review','trace','settings','ai')",
            check);
        Assert.Equal(12L, await schemas.ExecuteScalarAsync());
    }
}
