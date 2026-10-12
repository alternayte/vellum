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
using Vellum.Modules.Modelling.Elements;
using Vellum.Modules.Modelling.Revisions;
using Vellum.Modules.Modelling.Relationships;
using Vellum.Modules.Modelling.Variants;
using Vellum.Modules.Schemas;
using Vellum.Modules.Views;
using Vellum.Modules.Workspaces;

namespace Vellum.Tests.Modules.Modelling;

[Collection("Integration")]
public class RevisionVariantEndpointTests
{
    private readonly IntegrationFixture _fixture;

    public RevisionVariantEndpointTests(IntegrationFixture fixture) => _fixture = fixture;

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

    private static async Task<Guid> AddSystemAsync(HttpClient client, Guid projectId, string name)
    {
        var elementId = Guid.NewGuid();
        var response = await client.PostAsJsonAsync($"/api/projects/{projectId}/elements",
            new { id = elementId, kind = "system", name });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return elementId;
    }

    private sealed record Page<T>(IReadOnlyList<T> Items, string? Cursor);

    [Fact]
    public async Task Create_revision_pins_current_version()
    {
        using var factory = CreateFactory();
        using var client = await CreateAuthenticatedClientAsync(factory);
        var projectId = await SetupProjectAsync(client);

        await AddSystemAsync(client, projectId, "Orders");

        var revisionId = Guid.NewGuid();
        var create = await client.PostAsJsonAsync($"/api/projects/{projectId}/revisions",
            new { id = revisionId, name = "v1", note = "baseline" });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var revision = await create.Content.ReadFromJsonAsync<RevisionDto>();
        Assert.NotNull(revision);
        Assert.Equal(1, revision.Version);

        await AddSystemAsync(client, projectId, "Payments");

        var refetched = await client.GetFromJsonAsync<RevisionDto>($"/api/projects/{projectId}/revisions/{revisionId}");
        Assert.NotNull(refetched);
        Assert.Equal(1, refetched.Version);
        Assert.Equal("baseline", refetched.Note);

        var list = await client.GetFromJsonAsync<List<RevisionDto>>($"/api/projects/{projectId}/revisions");
        Assert.Single(list!);
    }

    [Fact]
    public async Task Create_revision_with_duplicate_name_conflicts()
    {
        using var factory = CreateFactory();
        using var client = await CreateAuthenticatedClientAsync(factory);
        var projectId = await SetupProjectAsync(client);

        var first = await client.PostAsJsonAsync($"/api/projects/{projectId}/revisions",
            new { id = Guid.NewGuid(), name = "v1" });
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        var second = await client.PostAsJsonAsync($"/api/projects/{projectId}/revisions",
            new { id = Guid.NewGuid(), name = "v1" });
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task Variant_forks_full_model_and_isolates_edits()
    {
        using var factory = CreateFactory();
        using var client = await CreateAuthenticatedClientAsync(factory);
        var projectId = await SetupProjectAsync(client);

        var ordersId = await AddSystemAsync(client, projectId, "Orders");
        var paymentsId = await AddSystemAsync(client, projectId, "Payments");
        var relationshipResponse = await client.PostAsJsonAsync($"/api/projects/{projectId}/relationships",
            new { id = Guid.NewGuid(), fromId = ordersId, toId = paymentsId, label = "calls", kind = "behavioural" });
        Assert.Equal(HttpStatusCode.Created, relationshipResponse.StatusCode);

        var variantId = Guid.NewGuid();
        var create = await client.PostAsJsonAsync($"/api/projects/{projectId}/variants",
            new { id = variantId, name = "event-driven" });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var variant = await create.Content.ReadFromJsonAsync<VariantDto>();
        Assert.NotNull(variant);

        var variantElements = await client.GetFromJsonAsync<Page<ElementDto>>(
            $"/api/projects/{projectId}/elements?branchId={variant.StreamId}");
        Assert.Equal(2, variantElements!.Items.Count);

        var variantRelationships = await client.GetFromJsonAsync<Page<RelationshipDto>>(
            $"/api/projects/{projectId}/relationships?branchId={variant.StreamId}");
        Assert.Equal("behavioural", Assert.Single(variantRelationships!.Items).Kind);

        // Edits on the variant stay off the main branch.
        await AddSystemAsync(client, projectId, "Saga");
        var variantAfterEdit = await client.GetFromJsonAsync<Page<ElementDto>>(
            $"/api/projects/{projectId}/elements?branchId={variant.StreamId}");
        Assert.Equal(2, variantAfterEdit!.Items.Count);

        var mainElements = await client.GetFromJsonAsync<Page<ElementDto>>($"/api/projects/{projectId}/elements");
        Assert.Equal(3, mainElements!.Items.Count);
    }

    [Fact]
    public async Task Variant_from_revision_uses_pinned_state()
    {
        using var factory = CreateFactory();
        using var client = await CreateAuthenticatedClientAsync(factory);
        var projectId = await SetupProjectAsync(client);

        await AddSystemAsync(client, projectId, "Orders");

        var revisionId = Guid.NewGuid();
        await client.PostAsJsonAsync($"/api/projects/{projectId}/revisions",
            new { id = revisionId, name = "v1" });

        await AddSystemAsync(client, projectId, "Payments");

        var create = await client.PostAsJsonAsync($"/api/projects/{projectId}/variants",
            new { id = Guid.NewGuid(), name = "from-v1", baseRevisionId = revisionId });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var variant = await create.Content.ReadFromJsonAsync<VariantDto>();
        Assert.NotNull(variant);
        Assert.Equal(revisionId, variant.BaseRevisionId);

        var variantElements = await client.GetFromJsonAsync<Page<ElementDto>>(
            $"/api/projects/{projectId}/elements?branchId={variant.StreamId}");
        Assert.Single(variantElements!.Items);
        Assert.Equal("Orders", variantElements.Items[0].Name);
    }

    [Fact]
    public async Task Reused_revision_and_variant_ids_are_idempotent()
    {
        using var factory = CreateFactory();
        using var client = await CreateAuthenticatedClientAsync(factory);
        var projectId = await SetupProjectAsync(client);

        var revisionId = Guid.NewGuid();
        var first = await client.PostAsJsonAsync($"/api/projects/{projectId}/revisions",
            new { id = revisionId, name = "v1" });
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        var second = await client.PostAsJsonAsync($"/api/projects/{projectId}/revisions",
            new { id = revisionId, name = "v1-again" });
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        var revisions = await client.GetFromJsonAsync<List<RevisionDto>>($"/api/projects/{projectId}/revisions");
        Assert.Single(revisions!);

        var variantId = Guid.NewGuid();
        var firstVariant = await client.PostAsJsonAsync($"/api/projects/{projectId}/variants",
            new { id = variantId, name = "alt" });
        Assert.Equal(HttpStatusCode.Created, firstVariant.StatusCode);

        var secondVariant = await client.PostAsJsonAsync($"/api/projects/{projectId}/variants",
            new { id = variantId, name = "alt-again" });
        Assert.Equal(HttpStatusCode.OK, secondVariant.StatusCode);

        var variants = await client.GetFromJsonAsync<List<VariantDto>>($"/api/projects/{projectId}/variants");
        Assert.Single(variants!);

        var otherProjectId = await SetupProjectAsync(client);
        var crossProject = await client.PostAsJsonAsync($"/api/projects/{otherProjectId}/revisions",
            new { id = revisionId, name = "v1" });
        Assert.Equal(HttpStatusCode.Conflict, crossProject.StatusCode);
    }

    [Fact]
    public async Task Revision_and_variant_endpoints_require_authentication()
    {
        using var factory = CreateFactory();
        using var client = CreateClient(factory);

        var revisions = await client.GetAsync($"/api/projects/{Guid.NewGuid()}/revisions");
        Assert.Equal(HttpStatusCode.Unauthorized, revisions.StatusCode);

        var variants = await client.GetAsync($"/api/projects/{Guid.NewGuid()}/variants");
        Assert.Equal(HttpStatusCode.Unauthorized, variants.StatusCode);
    }
}

file static class RevisionVariantServiceCollectionExtensions
{
    public static void RemoveAll<T>(this IServiceCollection services)
    {
        var descriptors = services.Where(d => d.ServiceType == typeof(T)).ToList();
        foreach (var d in descriptors) services.Remove(d);
    }
}
