// tests/Vellum.Tests/Modules/Docs/AdrEndpointTests.cs
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vellum.Kernel.EventStore;
using Vellum.Modules.Docs;
using Vellum.Modules.Identity;
using Vellum.Modules.Drafts;
using Vellum.Modules.Modelling;
using Vellum.Modules.Views;
using Vellum.Modules.Workspaces;

namespace Vellum.Tests.Modules.Docs;

[Collection("Integration")]
public class AdrEndpointTests
{
    private readonly IntegrationFixture _fixture;

    public AdrEndpointTests(IntegrationFixture fixture) => _fixture = fixture;

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
                    services.RemoveAll<DbContextOptions<DraftsDbContext>>();
                    var cs = _fixture.ConnectionString;
                    services.AddDbContext<EventStoreDbContext>(o => o.UseNpgsql(cs).UseSnakeCaseNamingConvention());
                    services.AddDbContext<AppIdentityDbContext>(o => o.UseNpgsql(cs).UseSnakeCaseNamingConvention());
                    services.AddDbContext<WorkspacesDbContext>(o => o.UseNpgsql(cs).UseSnakeCaseNamingConvention());
                    services.AddDbContext<ModellingDbContext>(o => o.UseNpgsql(cs).UseSnakeCaseNamingConvention());
                    services.AddDbContext<ViewsDbContext>(o => o.UseNpgsql(cs).UseSnakeCaseNamingConvention());
                    services.AddDbContext<DocsDbContext>(o => o.UseNpgsql(cs).UseSnakeCaseNamingConvention());
                    services.AddDbContext<DraftsDbContext>(o => o.UseNpgsql(cs).UseSnakeCaseNamingConvention());
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
    public async Task Create_doc_with_adr_status_and_filter()
    {
        using var factory = CreateFactory();
        using var client = await CreateAuthenticatedClientAsync(factory);
        var projectId = await SetupProjectAsync(client);
        var docId = Guid.NewGuid();
        var draftId = Guid.NewGuid();

        // Create an ADR doc with adrStatus = "proposed" and a draftId
        var createResponse = await client.PostAsJsonAsync($"/api/projects/{projectId}/docs",
            new { id = docId, title = "ADR-001", adrStatus = "proposed", draftId });
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);

        var created = await createResponse.Content.ReadFromJsonAsync<DocDto>();
        Assert.Equal("proposed", created!.AdrStatus);
        Assert.Equal(draftId, created.DraftId);

        // List with ?adrStatus=proposed → returns it
        var filtered = await (await client.GetAsync($"/api/projects/{projectId}/docs?adrStatus=proposed"))
            .Content.ReadFromJsonAsync<AdrDocListDto>();
        Assert.Single(filtered!.Items);
        Assert.Equal("ADR-001", filtered.Items[0].Title);

        // List without filter → also returns it
        var all = await (await client.GetAsync($"/api/projects/{projectId}/docs"))
            .Content.ReadFromJsonAsync<AdrDocListDto>();
        Assert.Contains(all!.Items, d => d.Id == docId);

        // Update adrStatus to "accepted"
        var updateResponse = await client.PatchAsJsonAsync($"/api/projects/{projectId}/docs/{docId}",
            new { adrStatus = "accepted", setAdrStatus = true });
        Assert.Equal(HttpStatusCode.OK, updateResponse.StatusCode);
        var updated = await updateResponse.Content.ReadFromJsonAsync<DocDto>();
        Assert.Equal("accepted", updated!.AdrStatus);

        // List with ?adrStatus=proposed → empty (no longer matches)
        var proposedAfterUpdate = await (await client.GetAsync($"/api/projects/{projectId}/docs?adrStatus=proposed"))
            .Content.ReadFromJsonAsync<AdrDocListDto>();
        Assert.Empty(proposedAfterUpdate!.Items);
    }

    [Fact]
    public async Task Create_doc_with_draft_id_and_filter_by_draft()
    {
        using var factory = CreateFactory();
        using var client = await CreateAuthenticatedClientAsync(factory);
        var projectId = await SetupProjectAsync(client);
        var draftId = Guid.NewGuid();
        var adrDocId = Guid.NewGuid();

        // Create one doc linked to a draft
        await client.PostAsJsonAsync($"/api/projects/{projectId}/docs",
            new { id = adrDocId, title = "ADR-linked", draftId });
        // Create another doc without draft
        await client.PostAsJsonAsync($"/api/projects/{projectId}/docs",
            new { id = Guid.NewGuid(), title = "Plain doc" });

        // Filter by draftId → only the linked doc
        var filtered = await (await client.GetAsync($"/api/projects/{projectId}/docs?draftId={draftId}"))
            .Content.ReadFromJsonAsync<AdrDocListDto>();
        Assert.Single(filtered!.Items);
        Assert.Equal("ADR-linked", filtered.Items[0].Title);
        Assert.Equal(draftId, filtered.Items[0].DraftId);
    }

    [Fact]
    public async Task Update_doc_can_clear_adr_status()
    {
        using var factory = CreateFactory();
        using var client = await CreateAuthenticatedClientAsync(factory);
        var projectId = await SetupProjectAsync(client);
        var docId = Guid.NewGuid();

        await client.PostAsJsonAsync($"/api/projects/{projectId}/docs",
            new { id = docId, title = "ADR-002", adrStatus = "proposed" });

        // Clear adrStatus by sending setAdrStatus = true with no adrStatus value
        await client.PatchAsJsonAsync($"/api/projects/{projectId}/docs/{docId}",
            new { setAdrStatus = true });

        var doc = await (await client.GetAsync($"/api/projects/{projectId}/docs/{docId}"))
            .Content.ReadFromJsonAsync<DocDto>();
        Assert.Null(doc!.AdrStatus);
    }

    [Fact]
    public async Task Update_doc_can_clear_draft_id()
    {
        using var factory = CreateFactory();
        using var client = await CreateAuthenticatedClientAsync(factory);
        var projectId = await SetupProjectAsync(client);
        var docId = Guid.NewGuid();
        var draftId = Guid.NewGuid();

        await client.PostAsJsonAsync($"/api/projects/{projectId}/docs",
            new { id = docId, title = "ADR-003", draftId });

        // Clear draftId by sending setDraftId = true with no draftId value
        await client.PatchAsJsonAsync($"/api/projects/{projectId}/docs/{docId}",
            new { setDraftId = true });

        var doc = await (await client.GetAsync($"/api/projects/{projectId}/docs/{docId}"))
            .Content.ReadFromJsonAsync<DocDto>();
        Assert.Null(doc!.DraftId);
    }

    [Fact]
    public async Task Regular_doc_has_null_adr_fields()
    {
        using var factory = CreateFactory();
        using var client = await CreateAuthenticatedClientAsync(factory);
        var projectId = await SetupProjectAsync(client);
        var docId = Guid.NewGuid();

        await client.PostAsJsonAsync($"/api/projects/{projectId}/docs",
            new { id = docId, title = "Plain Doc" });

        var doc = await (await client.GetAsync($"/api/projects/{projectId}/docs/{docId}"))
            .Content.ReadFromJsonAsync<DocDto>();
        Assert.Null(doc!.DraftId);
        Assert.Null(doc.AdrStatus);
    }
}

file sealed record AdrDocListDto(IReadOnlyList<DocDto> Items, string? Cursor);

file static class AdrServiceCollectionExtensions
{
    public static void RemoveAll<T>(this IServiceCollection services)
    {
        var descriptors = services.Where(d => d.ServiceType == typeof(T)).ToList();
        foreach (var d in descriptors) services.Remove(d);
    }
}
