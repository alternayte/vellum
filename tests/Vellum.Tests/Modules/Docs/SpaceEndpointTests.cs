// tests/Vellum.Tests/Modules/Docs/SpaceEndpointTests.cs
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vellum.Kernel.EventStore;
using Vellum.Modules.Docs;
using Vellum.Modules.Identity;
using Vellum.Modules.Modelling;
using Vellum.Modules.Views;
using Vellum.Modules.Workspaces;

namespace Vellum.Tests.Modules.Docs;

[Collection("Integration")]
public class SpaceEndpointTests
{
    private readonly IntegrationFixture _fixture;

    public SpaceEndpointTests(IntegrationFixture fixture) => _fixture = fixture;

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
                    services.RemoveAll<DbContextOptions<ViewsDbContext>>();
                    services.RemoveAll<DbContextOptions<DocsDbContext>>();
                    var cs = _fixture.ConnectionString;
                    services.AddDbContext<EventStoreDbContext>(o => o.UseNpgsql(cs).UseSnakeCaseNamingConvention());
                    services.AddDbContext<AppIdentityDbContext>(o => o.UseNpgsql(cs).UseSnakeCaseNamingConvention());
                    services.AddDbContext<WorkspacesDbContext>(o => o.UseNpgsql(cs).UseSnakeCaseNamingConvention());
                    services.AddDbContext<ModellingDbContext>(o => o.UseNpgsql(cs).UseSnakeCaseNamingConvention());
                    services.AddDbContext<ViewsDbContext>(o => o.UseNpgsql(cs).UseSnakeCaseNamingConvention());
                    services.AddDbContext<DocsDbContext>(o => o.UseNpgsql(cs).UseSnakeCaseNamingConvention());
                });
            });
    }

    private static HttpClient CreateClient(WebApplicationFactory<Program> factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true, AllowAutoRedirect = false });

    private static async Task<HttpClient> CreateAuthenticatedClientAsync(WebApplicationFactory<Program> factory)
    {
        var client = CreateClient(factory);
        var email = $"test-{Guid.NewGuid():N}@vellum.local";
        await client.PostAsJsonAsync("/api/auth/register", new { email, password = "Test123!", displayName = "Test User" });
        return client;
    }

    private static async Task<Guid> SetupProjectAsync(HttpClient client)
    {
        var workspaceId = Guid.NewGuid();
        await client.PostAsJsonAsync("/api/workspaces", new { id = workspaceId, name = "Test WS" });
        var projectId = Guid.NewGuid();
        await client.PostAsJsonAsync($"/api/workspaces/{workspaceId}/projects", new { id = projectId, name = "Test Project" });
        return projectId;
    }

    [Fact]
    public async Task Create_space_and_list_returns_it()
    {
        using var factory = CreateFactory();
        using var client = await CreateAuthenticatedClientAsync(factory);
        var projectId = await SetupProjectAsync(client);
        var spaceId = Guid.NewGuid();

        var createResponse = await client.PostAsJsonAsync($"/api/projects/{projectId}/spaces",
            new { id = spaceId, name = "ADRs" });
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);

        var listResponse = await client.GetAsync($"/api/projects/{projectId}/spaces");
        var spaces = await listResponse.Content.ReadFromJsonAsync<List<SpaceDto>>();
        Assert.Single(spaces!);
        Assert.Equal("ADRs", spaces[0].Name);
    }

    [Fact]
    public async Task Update_space_renames_it()
    {
        using var factory = CreateFactory();
        using var client = await CreateAuthenticatedClientAsync(factory);
        var projectId = await SetupProjectAsync(client);
        var spaceId = Guid.NewGuid();

        await client.PostAsJsonAsync($"/api/projects/{projectId}/spaces", new { id = spaceId, name = "Old" });

        var updateResponse = await client.PatchAsJsonAsync($"/api/projects/{projectId}/spaces/{spaceId}",
            new { name = "Renamed" });
        Assert.Equal(HttpStatusCode.OK, updateResponse.StatusCode);

        var listResponse = await client.GetAsync($"/api/projects/{projectId}/spaces");
        var spaces = await listResponse.Content.ReadFromJsonAsync<List<SpaceDto>>();
        Assert.Equal("Renamed", spaces![0].Name);
    }

    [Fact]
    public async Task Delete_space_removes_it()
    {
        using var factory = CreateFactory();
        using var client = await CreateAuthenticatedClientAsync(factory);
        var projectId = await SetupProjectAsync(client);
        var spaceId = Guid.NewGuid();

        await client.PostAsJsonAsync($"/api/projects/{projectId}/spaces", new { id = spaceId, name = "ToDelete" });
        var deleteResponse = await client.DeleteAsync($"/api/projects/{projectId}/spaces/{spaceId}");
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);

        var listResponse = await client.GetAsync($"/api/projects/{projectId}/spaces");
        var spaces = await listResponse.Content.ReadFromJsonAsync<List<SpaceDto>>();
        Assert.Empty(spaces!);
    }

    [Fact]
    public async Task Unauthenticated_request_returns_401()
    {
        using var factory = CreateFactory();
        using var client = CreateClient(factory);
        var response = await client.GetAsync($"/api/projects/{Guid.NewGuid()}/spaces");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}

file static class DocsServiceCollectionExtensions
{
    public static void RemoveAll<T>(this IServiceCollection services)
    {
        var descriptors = services.Where(d => d.ServiceType == typeof(T)).ToList();
        foreach (var d in descriptors) services.Remove(d);
    }
}
