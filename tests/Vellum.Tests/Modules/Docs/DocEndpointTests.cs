// tests/Vellum.Tests/Modules/Docs/DocEndpointTests.cs
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
public class DocEndpointTests
{
    private readonly IntegrationFixture _fixture;

    public DocEndpointTests(IntegrationFixture fixture) => _fixture = fixture;

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
    public async Task Create_doc_and_get_returns_it()
    {
        using var factory = CreateFactory();
        using var client = await CreateAuthenticatedClientAsync(factory);
        var projectId = await SetupProjectAsync(client);
        var docId = Guid.NewGuid();

        var createResponse = await client.PostAsJsonAsync($"/api/projects/{projectId}/docs",
            new { id = docId, title = "Migration Strategy" });
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);

        var getResponse = await client.GetAsync($"/api/projects/{projectId}/docs/{docId}");
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        var doc = await getResponse.Content.ReadFromJsonAsync<DocDto>();
        Assert.Equal("Migration Strategy", doc!.Title);
        Assert.Equal(string.Empty, doc.Content);
    }

    [Fact]
    public async Task Update_doc_content_and_title()
    {
        using var factory = CreateFactory();
        using var client = await CreateAuthenticatedClientAsync(factory);
        var projectId = await SetupProjectAsync(client);
        var docId = Guid.NewGuid();

        await client.PostAsJsonAsync($"/api/projects/{projectId}/docs", new { id = docId, title = "Draft" });

        var updateResponse = await client.PatchAsJsonAsync($"/api/projects/{projectId}/docs/{docId}",
            new { title = "Final", content = "# Hello\n\nSome MDX content." });
        Assert.Equal(HttpStatusCode.OK, updateResponse.StatusCode);

        var doc = await (await client.GetAsync($"/api/projects/{projectId}/docs/{docId}"))
            .Content.ReadFromJsonAsync<DocDto>();
        Assert.Equal("Final", doc!.Title);
        Assert.Equal("# Hello\n\nSome MDX content.", doc.Content);
    }

    [Fact]
    public async Task List_docs_filtered_by_space()
    {
        using var factory = CreateFactory();
        using var client = await CreateAuthenticatedClientAsync(factory);
        var projectId = await SetupProjectAsync(client);
        var spaceId = Guid.NewGuid();

        await client.PostAsJsonAsync($"/api/projects/{projectId}/spaces", new { id = spaceId, name = "ADRs" });
        await client.PostAsJsonAsync($"/api/projects/{projectId}/docs",
            new { id = Guid.NewGuid(), title = "ADR-001", spaceId });
        await client.PostAsJsonAsync($"/api/projects/{projectId}/docs",
            new { id = Guid.NewGuid(), title = "Loose Doc" });

        var filtered = await (await client.GetAsync($"/api/projects/{projectId}/docs?spaceId={spaceId}"))
            .Content.ReadFromJsonAsync<DocListDto>();
        Assert.Single(filtered!.Items);
        Assert.Equal("ADR-001", filtered.Items[0].Title);
    }

    [Fact]
    public async Task List_docs_filtered_by_element()
    {
        using var factory = CreateFactory();
        using var client = await CreateAuthenticatedClientAsync(factory);
        var projectId = await SetupProjectAsync(client);
        var elementId = Guid.NewGuid();

        await client.PostAsJsonAsync($"/api/projects/{projectId}/docs",
            new { id = Guid.NewGuid(), title = "Element Doc", elementId });
        await client.PostAsJsonAsync($"/api/projects/{projectId}/docs",
            new { id = Guid.NewGuid(), title = "Other Doc" });

        var filtered = await (await client.GetAsync($"/api/projects/{projectId}/docs?elementId={elementId}"))
            .Content.ReadFromJsonAsync<DocListDto>();
        Assert.Single(filtered!.Items);
        Assert.Equal("Element Doc", filtered.Items[0].Title);
    }

    [Fact]
    public async Task Delete_doc_removes_it()
    {
        using var factory = CreateFactory();
        using var client = await CreateAuthenticatedClientAsync(factory);
        var projectId = await SetupProjectAsync(client);
        var docId = Guid.NewGuid();

        await client.PostAsJsonAsync($"/api/projects/{projectId}/docs", new { id = docId, title = "ToDelete" });
        var deleteResponse = await client.DeleteAsync($"/api/projects/{projectId}/docs/{docId}");
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);

        var getResponse = await client.GetAsync($"/api/projects/{projectId}/docs/{docId}");
        Assert.Equal(HttpStatusCode.NotFound, getResponse.StatusCode);
    }

    [Fact]
    public async Task Delete_space_sets_doc_space_to_null()
    {
        using var factory = CreateFactory();
        using var client = await CreateAuthenticatedClientAsync(factory);
        var projectId = await SetupProjectAsync(client);
        var spaceId = Guid.NewGuid();
        var docId = Guid.NewGuid();

        await client.PostAsJsonAsync($"/api/projects/{projectId}/spaces", new { id = spaceId, name = "ADRs" });
        await client.PostAsJsonAsync($"/api/projects/{projectId}/docs",
            new { id = docId, title = "ADR-001", spaceId });

        await client.DeleteAsync($"/api/projects/{projectId}/spaces/{spaceId}");

        var doc = await (await client.GetAsync($"/api/projects/{projectId}/docs/{docId}"))
            .Content.ReadFromJsonAsync<DocDto>();
        Assert.Null(doc!.SpaceId);
    }

    [Fact]
    public async Task Update_doc_can_set_element_id_to_null()
    {
        using var factory = CreateFactory();
        using var client = await CreateAuthenticatedClientAsync(factory);
        var projectId = await SetupProjectAsync(client);
        var docId = Guid.NewGuid();
        var elementId = Guid.NewGuid();

        await client.PostAsJsonAsync($"/api/projects/{projectId}/docs",
            new { id = docId, title = "Attached", elementId });

        await client.PatchAsJsonAsync($"/api/projects/{projectId}/docs/{docId}",
            new { setElementId = true });

        var doc = await (await client.GetAsync($"/api/projects/{projectId}/docs/{docId}"))
            .Content.ReadFromJsonAsync<DocDto>();
        Assert.Null(doc!.ElementId);
    }

    [Fact]
    public async Task Unauthenticated_request_returns_401()
    {
        using var factory = CreateFactory();
        using var client = CreateClient(factory);
        var response = await client.GetAsync($"/api/projects/{Guid.NewGuid()}/docs");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}

file sealed record DocListDto(IReadOnlyList<DocDto> Items, string? Cursor);

file static class DocServiceCollectionExtensions
{
    public static void RemoveAll<T>(this IServiceCollection services)
    {
        var descriptors = services.Where(d => d.ServiceType == typeof(T)).ToList();
        foreach (var d in descriptors) services.Remove(d);
    }
}
