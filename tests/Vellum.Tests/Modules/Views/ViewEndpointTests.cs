using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vellum.Kernel.EventStore;
using Vellum.Modules.Identity;
using Vellum.Modules.Modelling;
using Vellum.Modules.Views;
using Vellum.Modules.Workspaces;

namespace Vellum.Tests.Modules.Views;

[Collection("Integration")]
public class ViewEndpointTests
{
    private readonly IntegrationFixture _fixture;

    public ViewEndpointTests(IntegrationFixture fixture) => _fixture = fixture;

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
                    services.AddDbContext<EventStoreDbContext>(o =>
                        o.UseNpgsql(_fixture.ConnectionString).UseSnakeCaseNamingConvention());
                    services.AddDbContext<AppIdentityDbContext>(o =>
                        o.UseNpgsql(_fixture.ConnectionString).UseSnakeCaseNamingConvention());
                    services.AddDbContext<WorkspacesDbContext>(o =>
                        o.UseNpgsql(_fixture.ConnectionString).UseSnakeCaseNamingConvention());
                    services.AddDbContext<ModellingDbContext>(o =>
                        o.UseNpgsql(_fixture.ConnectionString).UseSnakeCaseNamingConvention());
                    services.AddDbContext<ViewsDbContext>(o =>
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
    public async Task Create_view_and_get_returns_it_with_empty_layout()
    {
        using var factory = CreateFactory();
        using var client = await CreateAuthenticatedClientAsync(factory);
        var projectId = await SetupProjectAsync(client);
        var viewId = Guid.NewGuid();

        var createResponse = await client.PostAsJsonAsync($"/api/projects/{projectId}/views",
            new { id = viewId, name = "Context View" });
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);

        var getResponse = await client.GetAsync($"/api/projects/{projectId}/views/{viewId}");
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        var detail = await getResponse.Content.ReadFromJsonAsync<ViewDetailDto>();
        Assert.Equal("Context View", detail!.Name);
        Assert.Empty(detail.Positions);
    }

    [Fact]
    public async Task Save_layout_and_get_returns_positions()
    {
        using var factory = CreateFactory();
        using var client = await CreateAuthenticatedClientAsync(factory);
        var projectId = await SetupProjectAsync(client);
        var viewId = Guid.NewGuid();
        var elementId = Guid.NewGuid();

        await client.PostAsJsonAsync($"/api/projects/{projectId}/views",
            new { id = viewId, name = "Layout Test" });

        var layoutResponse = await client.PutAsJsonAsync($"/api/projects/{projectId}/views/{viewId}/layout",
            new { positions = new[] { new { elementId, x = 100.0, y = 200.0 } } });
        Assert.Equal(HttpStatusCode.OK, layoutResponse.StatusCode);

        var getResponse = await client.GetAsync($"/api/projects/{projectId}/views/{viewId}");
        var detail = await getResponse.Content.ReadFromJsonAsync<ViewDetailDto>();
        Assert.Single(detail!.Positions);
        Assert.Equal(100.0, detail.Positions[0].X);
    }

    [Fact]
    public async Task Update_view_with_stale_updated_at_returns_409()
    {
        using var factory = CreateFactory();
        using var client = await CreateAuthenticatedClientAsync(factory);
        var projectId = await SetupProjectAsync(client);
        var viewId = Guid.NewGuid();

        await client.PostAsJsonAsync($"/api/projects/{projectId}/views",
            new { id = viewId, name = "Concurrency Test" });

        // First update succeeds
        await client.PatchAsJsonAsync($"/api/projects/{projectId}/views/{viewId}",
            new { name = "Updated" });

        // Second update with stale timestamp returns 409
        var staleResponse = await client.PatchAsJsonAsync($"/api/projects/{projectId}/views/{viewId}",
            new { name = "Stale", updatedAt = DateTimeOffset.MinValue });
        Assert.Equal(HttpStatusCode.Conflict, staleResponse.StatusCode);
    }

    [Fact]
    public async Task List_views_returns_views_for_project()
    {
        using var factory = CreateFactory();
        using var client = await CreateAuthenticatedClientAsync(factory);
        var projectId = await SetupProjectAsync(client);

        await client.PostAsJsonAsync($"/api/projects/{projectId}/views",
            new { id = Guid.NewGuid(), name = "View A" });
        await client.PostAsJsonAsync($"/api/projects/{projectId}/views",
            new { id = Guid.NewGuid(), name = "View B" });

        var listResponse = await client.GetAsync($"/api/projects/{projectId}/views");
        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        var views = await listResponse.Content.ReadFromJsonAsync<List<ViewDto>>();
        Assert.Equal(2, views!.Count);
    }

    [Fact]
    public async Task Delete_view_removes_it()
    {
        using var factory = CreateFactory();
        using var client = await CreateAuthenticatedClientAsync(factory);
        var projectId = await SetupProjectAsync(client);
        var viewId = Guid.NewGuid();

        await client.PostAsJsonAsync($"/api/projects/{projectId}/views",
            new { id = viewId, name = "To Delete" });

        var deleteResponse = await client.DeleteAsync($"/api/projects/{projectId}/views/{viewId}");
        Assert.Equal(HttpStatusCode.OK, deleteResponse.StatusCode);

        var getResponse = await client.GetAsync($"/api/projects/{projectId}/views/{viewId}");
        Assert.Equal(HttpStatusCode.NotFound, getResponse.StatusCode);
    }

    [Fact]
    public async Task Unauthenticated_request_returns_401()
    {
        using var factory = CreateFactory();
        using var client = CreateClient(factory);
        var response = await client.GetAsync($"/api/projects/{Guid.NewGuid()}/views");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Delete_view_cascades_to_layout_positions_and_edges()
    {
        using var factory = CreateFactory();
        using var client = await CreateAuthenticatedClientAsync(factory);
        var projectId = await SetupProjectAsync(client);
        var viewId = Guid.NewGuid();
        var elementId = Guid.NewGuid();
        var relationshipId = Guid.NewGuid();

        await client.PostAsJsonAsync($"/api/projects/{projectId}/views",
            new { id = viewId, name = "Cascade Test" });

        await client.PutAsJsonAsync($"/api/projects/{projectId}/views/{viewId}/layout",
            new
            {
                positions = new[] { new { elementId, x = 10.0, y = 20.0 } },
                edges = new[] { new { relationshipId, routePoints = (object?)null } }
            });

        var deleteResponse = await client.DeleteAsync($"/api/projects/{projectId}/views/{viewId}");
        Assert.Equal(HttpStatusCode.OK, deleteResponse.StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ViewsDbContext>();
        var orphanedPositions = await db.LayoutPositions.Where(p => p.ViewId == viewId).CountAsync();
        var orphanedEdges = await db.LayoutEdges.Where(e => e.ViewId == viewId).CountAsync();
        Assert.Equal(0, orphanedPositions);
        Assert.Equal(0, orphanedEdges);
    }
}

file static class ViewsServiceCollectionExtensions
{
    public static void RemoveAll<T>(this IServiceCollection services)
    {
        var descriptors = services.Where(d => d.ServiceType == typeof(T)).ToList();
        foreach (var d in descriptors) services.Remove(d);
    }
}
