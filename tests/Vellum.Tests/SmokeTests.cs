using Npgsql;

namespace Vellum.Tests;

[Collection("Integration")]
public class SmokeTests
{
    private readonly IntegrationFixture _fixture;

    public SmokeTests(IntegrationFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Postgres_connects_and_reports_version_17()
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();

        Assert.Equal(System.Data.ConnectionState.Open, conn.State);

        await using var cmd = new NpgsqlCommand("SHOW server_version", conn);
        var version = (string)(await cmd.ExecuteScalarAsync())!;
        Assert.StartsWith("17", version);
    }
}
