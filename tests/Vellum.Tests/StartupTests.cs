using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;

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

        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("ConnectionStrings:DefaultConnection", connectionString);
        });
        using var client = factory.CreateClient();

        // An anonymous call reaches the app and is refused by it, not by a start-up failure.
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);

        await using var check = new NpgsqlConnection(connectionString);
        await check.OpenAsync();
        await using var schemas = new NpgsqlCommand(
            "SELECT count(*) FROM information_schema.schemata WHERE schema_name IN ('deedbox','identity','workspaces','modelling','views','docs','drafts','schemas','review','trace','settings','ai')",
            check);
        Assert.Equal(12L, await schemas.ExecuteScalarAsync());
    }
}
