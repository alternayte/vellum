// tests/Vellum.Tests/Modules/Drafts/DraftEndpointTests.cs
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
using Vellum.Modules.Views;
using Vellum.Modules.Workspaces;

namespace Vellum.Tests.Modules.Drafts;

[Collection("Integration")]
public class DraftEndpointTests
{
    private readonly IntegrationFixture _fixture;

    public DraftEndpointTests(IntegrationFixture fixture) => _fixture = fixture;

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
    public async Task Create_draft_and_get_returns_it()
    {
        using var factory = CreateFactory();
        using var client = await CreateAuthenticatedClientAsync(factory);
        var projectId = await SetupProjectAsync(client);
        var draftId = Guid.NewGuid();

        var createResponse = await client.PostAsJsonAsync(
            $"/api/projects/{projectId}/drafts",
            new { id = draftId, name = "Add auth service" });
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);

        var getResponse = await client.GetAsync($"/api/projects/{projectId}/drafts/{draftId}");
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        var draft = await getResponse.Content.ReadFromJsonAsync<DraftDto>();
        Assert.Equal("Add auth service", draft!.Name);
        Assert.Equal("open", draft.Status);
    }

    [Fact]
    public async Task Create_draft_is_idempotent()
    {
        using var factory = CreateFactory();
        using var client = await CreateAuthenticatedClientAsync(factory);
        var projectId = await SetupProjectAsync(client);
        var draftId = Guid.NewGuid();

        await client.PostAsJsonAsync($"/api/projects/{projectId}/drafts",
            new { id = draftId, name = "Draft 1" });
        var second = await client.PostAsJsonAsync($"/api/projects/{projectId}/drafts",
            new { id = draftId, name = "Draft 1" });
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
    }

    [Fact]
    public async Task List_drafts_filters_by_status()
    {
        using var factory = CreateFactory();
        using var client = await CreateAuthenticatedClientAsync(factory);
        var projectId = await SetupProjectAsync(client);

        await client.PostAsJsonAsync($"/api/projects/{projectId}/drafts",
            new { id = Guid.NewGuid(), name = "Open Draft" });

        var abandonId = Guid.NewGuid();
        await client.PostAsJsonAsync($"/api/projects/{projectId}/drafts",
            new { id = abandonId, name = "Abandoned Draft" });
        await client.PostAsync($"/api/projects/{projectId}/drafts/{abandonId}/abandon", null);

        var openOnly = await client.GetFromJsonAsync<Page<DraftDto>>(
            $"/api/projects/{projectId}/drafts?status=open");
        Assert.Single(openOnly!.Items);
        Assert.Equal("Open Draft", openOnly.Items[0].Name);
    }

    [Fact]
    public async Task Abandon_draft_sets_status_and_timestamp()
    {
        using var factory = CreateFactory();
        using var client = await CreateAuthenticatedClientAsync(factory);
        var projectId = await SetupProjectAsync(client);
        var draftId = Guid.NewGuid();

        await client.PostAsJsonAsync($"/api/projects/{projectId}/drafts",
            new { id = draftId, name = "To Abandon" });

        var response = await client.PostAsync(
            $"/api/projects/{projectId}/drafts/{draftId}/abandon", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var draft = await client.GetFromJsonAsync<DraftDto>(
            $"/api/projects/{projectId}/drafts/{draftId}");
        Assert.Equal("abandoned", draft!.Status);
        Assert.NotNull(draft.AbandonedAt);
    }

    [Fact]
    public async Task Abandon_already_abandoned_returns_409()
    {
        using var factory = CreateFactory();
        using var client = await CreateAuthenticatedClientAsync(factory);
        var projectId = await SetupProjectAsync(client);
        var draftId = Guid.NewGuid();

        await client.PostAsJsonAsync($"/api/projects/{projectId}/drafts",
            new { id = draftId, name = "Draft" });
        await client.PostAsync($"/api/projects/{projectId}/drafts/{draftId}/abandon", null);

        var second = await client.PostAsync(
            $"/api/projects/{projectId}/drafts/{draftId}/abandon", null);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    private sealed record Page<T>(IReadOnlyList<T> Items, string? Cursor);
}

file static class DraftServiceCollectionExtensions
{
    public static void RemoveAll<T>(this IServiceCollection services)
    {
        var descriptors = services.Where(d => d.ServiceType == typeof(T)).ToList();
        foreach (var d in descriptors) services.Remove(d);
    }
}
