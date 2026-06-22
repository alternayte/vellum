using Testcontainers.PostgreSql;

namespace Vellum.Tests;

public class IntegrationFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17")
        .Build();

    public string ConnectionString => _postgres.GetConnectionString();

    public async Task InitializeAsync() => await _postgres.StartAsync();

    public async Task DisposeAsync() => await _postgres.DisposeAsync();
}

[CollectionDefinition("Integration")]
public class IntegrationCollection : ICollectionFixture<IntegrationFixture>;
