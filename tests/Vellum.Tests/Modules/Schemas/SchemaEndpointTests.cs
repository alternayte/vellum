using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vellum.Kernel.EventStore;
using Vellum.Modules.Docs;
using Vellum.Modules.Drafts;
using Vellum.Modules.Identity;
using Vellum.Modules.Modelling;
using Vellum.Modules.Schemas;
using Vellum.Modules.Views;
using Vellum.Modules.Workspaces;

namespace Vellum.Tests.Modules.Schemas;

[Collection("Integration")]
public class SchemaEndpointTests
{
    private readonly IntegrationFixture _fixture;

    public SchemaEndpointTests(IntegrationFixture fixture) => _fixture = fixture;

    private WebApplicationFactory<Program> CreateFactory()
    {
        return new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Testing");
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<DbContextOptions<EventStoreDbContext>>();
                    services.RemoveAll<DbContextOptions<AppIdentityDbContext>>();
                    services.RemoveAll<DbContextOptions<WorkspacesDbContext>>();
                    services.RemoveAll<DbContextOptions<ModellingDbContext>>();
                    services.RemoveAll<DbContextOptions<DraftsDbContext>>();
                    services.RemoveAll<DbContextOptions<ViewsDbContext>>();
                    services.RemoveAll<DbContextOptions<DocsDbContext>>();
                    services.RemoveAll<DbContextOptions<SchemasDbContext>>();
                    services.AddDbContext<EventStoreDbContext>(o =>
                        o.UseNpgsql(_fixture.ConnectionString).UseSnakeCaseNamingConvention());
                    services.AddDbContext<AppIdentityDbContext>(o =>
                        o.UseNpgsql(_fixture.ConnectionString).UseSnakeCaseNamingConvention());
                    services.AddDbContext<WorkspacesDbContext>(o =>
                        o.UseNpgsql(_fixture.ConnectionString).UseSnakeCaseNamingConvention());
                    services.AddDbContext<ModellingDbContext>(o =>
                        o.UseNpgsql(_fixture.ConnectionString).UseSnakeCaseNamingConvention());
                    services.AddDbContext<DraftsDbContext>(o =>
                        o.UseNpgsql(_fixture.ConnectionString).UseSnakeCaseNamingConvention());
                    services.AddDbContext<ViewsDbContext>(o =>
                        o.UseNpgsql(_fixture.ConnectionString).UseSnakeCaseNamingConvention());
                    services.AddDbContext<DocsDbContext>(o =>
                        o.UseNpgsql(_fixture.ConnectionString).UseSnakeCaseNamingConvention());
                    services.AddDbContext<SchemasDbContext>(o =>
                        o.UseNpgsql(_fixture.ConnectionString).UseSnakeCaseNamingConvention());
                });
            });
    }

    private static HttpClient CreateClient(WebApplicationFactory<Program> factory)
    {
        return factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = true,
            AllowAutoRedirect = false
        });
    }

    private static async Task<HttpClient> CreateAuthenticatedClientAsync(WebApplicationFactory<Program> factory)
    {
        var client = CreateClient(factory);
        var email = $"test-{Guid.NewGuid():N}@vellum.local";
        await client.PostAsJsonAsync("/api/auth/register",
            new { email, password = "Test123!", displayName = "Test User" });
        return client;
    }

    private static async Task<Guid> SetupProjectAsync(HttpClient client)
    {
        var workspaceId = Guid.NewGuid();
        await client.PostAsJsonAsync("/api/workspaces", new { id = workspaceId, name = "Test WS" });
        var projectId = Guid.NewGuid();
        await client.PostAsJsonAsync($"/api/workspaces/{workspaceId}/projects",
            new { id = projectId, name = "Test Project" });
        return projectId;
    }

    [Fact]
    public async Task Create_and_get_schema()
    {
        using var factory = CreateFactory();
        using var client = await CreateAuthenticatedClientAsync(factory);
        var projectId = await SetupProjectAsync(client);

        var schemaId = Guid.NewGuid();
        var response = await client.PostAsJsonAsync($"/api/projects/{projectId}/schemas", new
        {
            id = schemaId,
            name = "OrderEvent",
            description = "An order event schema",
            content = """{"type":"object","properties":{"orderId":{"type":"string"}}}"""
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var get = await client.GetAsync($"/api/projects/{projectId}/schemas/{schemaId}");
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        var dto = await get.Content.ReadFromJsonAsync<SchemaDto>();
        Assert.Equal("OrderEvent", dto!.Name);
        Assert.Equal(1, dto.Version);
    }

    [Fact]
    public async Task List_schemas()
    {
        using var factory = CreateFactory();
        using var client = await CreateAuthenticatedClientAsync(factory);
        var projectId = await SetupProjectAsync(client);

        await client.PostAsJsonAsync($"/api/projects/{projectId}/schemas", new
        {
            id = Guid.NewGuid(),
            name = "EventA",
            content = """{"type":"object"}"""
        });
        await client.PostAsJsonAsync($"/api/projects/{projectId}/schemas", new
        {
            id = Guid.NewGuid(),
            name = "EventB",
            content = """{"type":"object"}"""
        });

        var response = await client.GetAsync($"/api/projects/{projectId}/schemas");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Update_schema()
    {
        using var factory = CreateFactory();
        using var client = await CreateAuthenticatedClientAsync(factory);
        var projectId = await SetupProjectAsync(client);

        var schemaId = Guid.NewGuid();
        await client.PostAsJsonAsync($"/api/projects/{projectId}/schemas", new
        {
            id = schemaId,
            name = "OldName",
            content = """{"type":"object"}"""
        });

        var patch = await client.PatchAsJsonAsync($"/api/projects/{projectId}/schemas/{schemaId}", new
        {
            name = "NewName"
        });
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);

        var get = await client.GetAsync($"/api/projects/{projectId}/schemas/{schemaId}");
        var dto = await get.Content.ReadFromJsonAsync<SchemaDto>();
        Assert.Equal("NewName", dto!.Name);
    }

    [Fact]
    public async Task Delete_schema_returns_204_and_get_returns_404()
    {
        using var factory = CreateFactory();
        using var client = await CreateAuthenticatedClientAsync(factory);
        var projectId = await SetupProjectAsync(client);

        var schemaId = Guid.NewGuid();
        await client.PostAsJsonAsync($"/api/projects/{projectId}/schemas", new
        {
            id = schemaId,
            name = "ToDelete",
            content = """{"type":"object"}"""
        });

        var del = await client.DeleteAsync($"/api/projects/{projectId}/schemas/{schemaId}");
        Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);

        var get = await client.GetAsync($"/api/projects/{projectId}/schemas/{schemaId}");
        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
    }

    [Fact]
    public async Task Unauthenticated_request_returns_401()
    {
        using var factory = CreateFactory();
        using var client = CreateClient(factory);
        var response = await client.GetAsync($"/api/projects/{Guid.NewGuid()}/schemas");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}

file static class SchemaTestServiceCollectionExtensions
{
    public static void RemoveAll<T>(this IServiceCollection services)
    {
        var descriptors = services.Where(d => d.ServiceType == typeof(T)).ToList();
        foreach (var d in descriptors) services.Remove(d);
    }
}
