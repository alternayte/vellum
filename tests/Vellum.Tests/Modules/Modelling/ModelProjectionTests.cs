using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vellum.Kernel.EventStore;
using Vellum.Modules.Drafts;
using Vellum.Modules.Identity;
using Vellum.Modules.Modelling;
using Vellum.Modules.Modelling.Elements;
using Vellum.Modules.Modelling.Relationships;
using Vellum.Modules.Workspaces;

namespace Vellum.Tests.Modules.Modelling;

file static class ProjectionServiceCollectionExtensions
{
    public static void RemoveAll<T>(this IServiceCollection services)
    {
        var descriptors = services.Where(d => d.ServiceType == typeof(T)).ToList();
        foreach (var d in descriptors) services.Remove(d);
    }
}

[Collection("Integration")]
public class ModelProjectionTests
{
    private readonly IntegrationFixture _fixture;

    public ModelProjectionTests(IntegrationFixture fixture) => _fixture = fixture;

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
    public async Task Element_add_projects_a_row()
    {
        using var factory = CreateFactory();
        using var client = await CreateAuthenticatedClientAsync(factory);
        var projectId = await SetupProjectAsync(client);
        var elementId = Guid.NewGuid();

        var response = await client.PostAsJsonAsync($"/api/projects/{projectId}/elements",
            new { id = elementId, kind = "system", name = "Payments", description = "Payment system", tags = new[] { "core" } });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var get = await client.GetAsync($"/api/projects/{projectId}/elements/{elementId}");
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        var dto = await get.Content.ReadFromJsonAsync<ElementDto>();
        Assert.Equal(elementId, dto!.Id);
        Assert.Equal("Payments", dto.Name);
        Assert.Equal("system", dto.Kind);
        Assert.Equal("Payment system", dto.Description);
    }

    [Fact]
    public async Task Element_update_projects_changes()
    {
        using var factory = CreateFactory();
        using var client = await CreateAuthenticatedClientAsync(factory);
        var projectId = await SetupProjectAsync(client);
        var elementId = Guid.NewGuid();

        var addResponse = await client.PostAsJsonAsync($"/api/projects/{projectId}/elements",
            new { id = elementId, kind = "system", name = "API Gateway", technology = "nginx" });
        Assert.Equal(HttpStatusCode.Created, addResponse.StatusCode);

        var patch = await client.PatchAsJsonAsync($"/api/projects/{projectId}/elements/{elementId}",
            new { name = "API Gateway v2", technology = "envoy" });
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);

        var dto = await patch.Content.ReadFromJsonAsync<ElementDto>();
        Assert.Equal("API Gateway v2", dto!.Name);
        Assert.Equal("envoy", dto.Technology);
    }

    [Fact]
    public async Task Element_update_with_SetTechnology_clears_technology()
    {
        using var factory = CreateFactory();
        using var client = await CreateAuthenticatedClientAsync(factory);
        var projectId = await SetupProjectAsync(client);
        var elementId = Guid.NewGuid();

        var addResponse = await client.PostAsJsonAsync($"/api/projects/{projectId}/elements",
            new { id = elementId, kind = "system", name = "Cache", technology = "Redis" });
        Assert.Equal(HttpStatusCode.Created, addResponse.StatusCode);

        var patch = await client.PatchAsJsonAsync($"/api/projects/{projectId}/elements/{elementId}",
            new { setTechnology = true });
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);

        var dto = await patch.Content.ReadFromJsonAsync<ElementDto>();
        Assert.Null(dto!.Technology);
    }

    [Fact]
    public async Task Element_remove_deletes_the_row()
    {
        using var factory = CreateFactory();
        using var client = await CreateAuthenticatedClientAsync(factory);
        var projectId = await SetupProjectAsync(client);
        var elementId = Guid.NewGuid();

        await client.PostAsJsonAsync($"/api/projects/{projectId}/elements",
            new { id = elementId, kind = "system", name = "Ephemeral" });

        var delete = await client.DeleteAsync($"/api/projects/{projectId}/elements/{elementId}");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);

        var get = await client.GetAsync($"/api/projects/{projectId}/elements/{elementId}");
        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
    }

    [Fact]
    public async Task Relationship_add_projects_a_row()
    {
        using var factory = CreateFactory();
        using var client = await CreateAuthenticatedClientAsync(factory);
        var projectId = await SetupProjectAsync(client);
        var sysA = Guid.NewGuid();
        var sysB = Guid.NewGuid();
        var relId = Guid.NewGuid();

        await client.PostAsJsonAsync($"/api/projects/{projectId}/elements",
            new { id = sysA, kind = "system", name = "Frontend" });
        await client.PostAsJsonAsync($"/api/projects/{projectId}/elements",
            new { id = sysB, kind = "system", name = "Backend" });

        var response = await client.PostAsJsonAsync($"/api/projects/{projectId}/relationships",
            new { id = relId, fromId = sysA, toId = sysB, label = "calls" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var get = await client.GetAsync($"/api/projects/{projectId}/relationships/{relId}");
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        var dto = await get.Content.ReadFromJsonAsync<RelationshipDto>();
        Assert.Equal(relId, dto!.Id);
        Assert.Equal(sysA, dto.FromId);
        Assert.Equal(sysB, dto.ToId);
        Assert.Equal("calls", dto.Label);
    }

    [Fact]
    public async Task Relationship_remove_deletes_the_row()
    {
        using var factory = CreateFactory();
        using var client = await CreateAuthenticatedClientAsync(factory);
        var projectId = await SetupProjectAsync(client);
        var sysA = Guid.NewGuid();
        var sysB = Guid.NewGuid();
        var relId = Guid.NewGuid();

        await client.PostAsJsonAsync($"/api/projects/{projectId}/elements",
            new { id = sysA, kind = "system", name = "Source" });
        await client.PostAsJsonAsync($"/api/projects/{projectId}/elements",
            new { id = sysB, kind = "system", name = "Target" });
        await client.PostAsJsonAsync($"/api/projects/{projectId}/relationships",
            new { id = relId, fromId = sysA, toId = sysB, label = "sends to" });

        var delete = await client.DeleteAsync($"/api/projects/{projectId}/relationships/{relId}");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);

        var get = await client.GetAsync($"/api/projects/{projectId}/relationships/{relId}");
        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
    }

    [Fact]
    public async Task Cascade_remove_cleans_up_children_and_relationships()
    {
        using var factory = CreateFactory();
        using var client = await CreateAuthenticatedClientAsync(factory);
        var projectId = await SetupProjectAsync(client);
        var parent = Guid.NewGuid();
        var child = Guid.NewGuid();
        var peer = Guid.NewGuid();
        var relParentPeer = Guid.NewGuid();
        var relChildPeer = Guid.NewGuid();

        await client.PostAsJsonAsync($"/api/projects/{projectId}/elements",
            new { id = parent, kind = "system", name = "Parent" });
        await client.PostAsJsonAsync($"/api/projects/{projectId}/elements",
            new { id = child, kind = "app", name = "Child", parentId = parent });
        await client.PostAsJsonAsync($"/api/projects/{projectId}/elements",
            new { id = peer, kind = "system", name = "Peer" });
        await client.PostAsJsonAsync($"/api/projects/{projectId}/relationships",
            new { id = relParentPeer, fromId = parent, toId = peer, label = "uses" });
        await client.PostAsJsonAsync($"/api/projects/{projectId}/relationships",
            new { id = relChildPeer, fromId = child, toId = peer, label = "depends on" });

        var delete = await client.DeleteAsync($"/api/projects/{projectId}/elements/{parent}");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);

        var childGet = await client.GetAsync($"/api/projects/{projectId}/elements/{child}");
        Assert.Equal(HttpStatusCode.NotFound, childGet.StatusCode);

        var relParentPeerGet = await client.GetAsync($"/api/projects/{projectId}/relationships/{relParentPeer}");
        Assert.Equal(HttpStatusCode.NotFound, relParentPeerGet.StatusCode);

        var relChildPeerGet = await client.GetAsync($"/api/projects/{projectId}/relationships/{relChildPeer}");
        Assert.Equal(HttpStatusCode.NotFound, relChildPeerGet.StatusCode);
    }
}
