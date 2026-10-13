// tests/Vellum.Tests/Modules/Drafts/MergeEndpointTests.cs
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vellum.Modules.Docs;
using Vellum.Modules.Drafts;
using Vellum.Modules.Identity;
using Vellum.Modules.Modelling;
using Vellum.Modules.Modelling.Elements;
using Vellum.Modules.Modelling.Messages;
using Vellum.Modules.Modelling.Relationships;
using Vellum.Modules.Schemas;
using Vellum.Modules.Views;
using Vellum.Modules.Workspaces;

namespace Vellum.Tests.Modules.Drafts;

[Collection("Integration")]
public class MergeEndpointTests
{
    private readonly IntegrationFixture _fixture;

    public MergeEndpointTests(IntegrationFixture fixture) => _fixture = fixture;

    private WebApplicationFactory<Program> CreateFactory()
    {
        return new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Testing");
                builder.UseSetting("ConnectionStrings:DefaultConnection", _fixture.ConnectionString);
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<DbContextOptions<AppIdentityDbContext>>();
                    services.RemoveAll<DbContextOptions<WorkspacesDbContext>>();
                    services.RemoveAll<DbContextOptions<ModellingDbContext>>();
                    services.RemoveAll<DbContextOptions<ViewsDbContext>>();
                    services.RemoveAll<DbContextOptions<DocsDbContext>>();
                    services.RemoveAll<DbContextOptions<DraftsDbContext>>();
                    services.RemoveAll<DbContextOptions<SchemasDbContext>>();
                    var cs = _fixture.ConnectionString;
                    services.AddDbContext<AppIdentityDbContext>(o => o.UseNpgsql(cs).UseSnakeCaseNamingConvention());
                    services.AddDbContext<WorkspacesDbContext>(o => o.UseNpgsql(cs).UseSnakeCaseNamingConvention());
                    services.AddDbContext<ModellingDbContext>(o => o.UseNpgsql(cs).UseSnakeCaseNamingConvention());
                    services.AddDbContext<ViewsDbContext>(o => o.UseNpgsql(cs).UseSnakeCaseNamingConvention());
                    services.AddDbContext<DocsDbContext>(o => o.UseNpgsql(cs).UseSnakeCaseNamingConvention());
                    services.AddDbContext<DraftsDbContext>(o => o.UseNpgsql(cs).UseSnakeCaseNamingConvention());
                    services.AddDbContext<SchemasDbContext>(o => o.UseNpgsql(cs).UseSnakeCaseNamingConvention());
                });
            });
    }

    private static HttpClient CreateClient(WebApplicationFactory<Program> factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true, AllowAutoRedirect = false });

    private static async Task<HttpClient> CreateAuthenticatedClientAsync(WebApplicationFactory<Program> factory)
    {
        var client = CreateClient(factory);
        var email = $"test-{Guid.NewGuid():N}@vellum.local";
        await client.PostAsJsonAsync("/api/auth/register", new { email, password = "Test12345!", displayName = "Test User" });
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
    public async Task Full_fork_edit_preview_merge_flow()
    {
        using var factory = CreateFactory();
        using var client = await CreateAuthenticatedClientAsync(factory);
        var projectId = await SetupProjectAsync(client);

        // Add element to main
        var elId = Guid.NewGuid();
        await client.PostAsJsonAsync($"/api/projects/{projectId}/elements",
            new { id = elId, kind = "system", name = "API" });

        // Create draft
        var draftId = Guid.NewGuid();
        var draftResp = await client.PostAsJsonAsync($"/api/projects/{projectId}/drafts",
            new { id = draftId, name = "Add auth" });
        var draft = await draftResp.Content.ReadFromJsonAsync<DraftDto>();

        // Add element on draft
        var newElId = Guid.NewGuid();
        await client.PostAsJsonAsync(
            $"/api/projects/{projectId}/elements?branchId={draft!.StreamId}",
            new { id = newElId, kind = "system", name = "Auth Service" });

        // Preview
        var previewResp = await client.PostAsync(
            $"/api/projects/{projectId}/drafts/{draftId}/merge/preview", null);
        Assert.Equal(HttpStatusCode.OK, previewResp.StatusCode);
        var preview = await previewResp.Content.ReadFromJsonAsync<MergePreviewResponse>();
        Assert.Single(preview!.AutoResolved);
        Assert.Empty(preview.Conflicts);

        // Execute
        var execResp = await client.PostAsJsonAsync(
            $"/api/projects/{projectId}/drafts/{draftId}/merge/execute",
            new { resolutions = Array.Empty<object>(), expectedMainVersion = preview.MainVersion });
        Assert.Equal(HttpStatusCode.OK, execResp.StatusCode);

        // Verify element now on main
        var mainElements = await client.GetFromJsonAsync<Page<ElementDto>>(
            $"/api/projects/{projectId}/elements");
        Assert.Contains(mainElements!.Items, e => e.Id == newElId);

        // Verify draft is merged
        var mergedDraft = await client.GetFromJsonAsync<DraftDto>(
            $"/api/projects/{projectId}/drafts/{draftId}");
        Assert.Equal("merged", mergedDraft!.Status);
    }

    [Fact]
    public async Task Preview_returns_409_when_draft_not_open()
    {
        using var factory = CreateFactory();
        using var client = await CreateAuthenticatedClientAsync(factory);
        var projectId = await SetupProjectAsync(client);

        var draftId = Guid.NewGuid();
        await client.PostAsJsonAsync($"/api/projects/{projectId}/drafts",
            new { id = draftId, name = "Draft to abandon" });
        await client.PostAsync($"/api/projects/{projectId}/drafts/{draftId}/abandon", null);

        var previewResp = await client.PostAsync(
            $"/api/projects/{projectId}/drafts/{draftId}/merge/preview", null);
        Assert.Equal(HttpStatusCode.Conflict, previewResp.StatusCode);
    }

    [Fact]
    public async Task Execute_returns_409_when_main_advanced()
    {
        using var factory = CreateFactory();
        using var client = await CreateAuthenticatedClientAsync(factory);
        var projectId = await SetupProjectAsync(client);

        // Create draft at version 0 (empty main)
        var draftId = Guid.NewGuid();
        var draftResp = await client.PostAsJsonAsync($"/api/projects/{projectId}/drafts",
            new { id = draftId, name = "Stale draft" });
        var draft = await draftResp.Content.ReadFromJsonAsync<DraftDto>();

        // Add element on draft
        var newElId = Guid.NewGuid();
        await client.PostAsJsonAsync(
            $"/api/projects/{projectId}/elements?branchId={draft!.StreamId}",
            new { id = newElId, kind = "system", name = "New Service" });

        // Preview to get current main version
        var previewResp = await client.PostAsync(
            $"/api/projects/{projectId}/drafts/{draftId}/merge/preview", null);
        var preview = await previewResp.Content.ReadFromJsonAsync<MergePreviewResponse>();

        // Advance main by adding an element directly
        var mainElId = Guid.NewGuid();
        await client.PostAsJsonAsync($"/api/projects/{projectId}/elements",
            new { id = mainElId, kind = "system", name = "Main Element" });

        // Execute with stale version should return 409
        var execResp = await client.PostAsJsonAsync(
            $"/api/projects/{projectId}/drafts/{draftId}/merge/execute",
            new { resolutions = Array.Empty<object>(), expectedMainVersion = preview!.MainVersion });
        Assert.Equal(HttpStatusCode.Conflict, execResp.StatusCode);
    }

    [Fact]
    public async Task Execute_returns_400_when_conflicts_unresolved()
    {
        using var factory = CreateFactory();
        using var client = await CreateAuthenticatedClientAsync(factory);
        var projectId = await SetupProjectAsync(client);

        // Add an element to main
        var elId = Guid.NewGuid();
        await client.PostAsJsonAsync($"/api/projects/{projectId}/elements",
            new { id = elId, kind = "system", name = "Shared Element" });

        // Create draft
        var draftId = Guid.NewGuid();
        var draftResp = await client.PostAsJsonAsync($"/api/projects/{projectId}/drafts",
            new { id = draftId, name = "Conflicting draft" });
        var draft = await draftResp.Content.ReadFromJsonAsync<DraftDto>();

        // Rename element on draft
        await client.PatchAsJsonAsync(
            $"/api/projects/{projectId}/elements/{elId}?branchId={draft!.StreamId}",
            new { name = "Renamed on Draft" });

        // Now rename element on main too (to create a conflict)
        await client.PatchAsJsonAsync(
            $"/api/projects/{projectId}/elements/{elId}",
            new { name = "Renamed on Main" });

        // Preview to get updated version
        var previewResp2 = await client.PostAsync(
            $"/api/projects/{projectId}/drafts/{draftId}/merge/preview", null);
        var preview2 = await previewResp2.Content.ReadFromJsonAsync<MergePreviewResponse>();

        Assert.NotEmpty(preview2!.Conflicts);

        // Execute without resolutions — should 400
        var execResp = await client.PostAsJsonAsync(
            $"/api/projects/{projectId}/drafts/{draftId}/merge/execute",
            new { resolutions = Array.Empty<object>(), expectedMainVersion = preview2.MainVersion });
        Assert.Equal(HttpStatusCode.BadRequest, execResp.StatusCode);
    }

    [Fact]
    public async Task Merge_preserves_message_fact_status_and_provenance()
    {
        using var factory = CreateFactory();
        using var client = await CreateAuthenticatedClientAsync(factory);
        var projectId = await SetupProjectAsync(client);

        var producerId = Guid.NewGuid();
        var producer = await client.PostAsJsonAsync($"/api/projects/{projectId}/elements",
            new { id = producerId, kind = "system", name = "Orders" });
        Assert.Equal(HttpStatusCode.Created, producer.StatusCode);

        var draftId = Guid.NewGuid();
        var draftResp = await client.PostAsJsonAsync($"/api/projects/{projectId}/drafts",
            new { id = draftId, name = "Message draft" });
        var draft = await draftResp.Content.ReadFromJsonAsync<DraftDto>();

        var messageId = Guid.NewGuid();
        var addMessage = await client.PostAsJsonAsync(
            $"/api/projects/{projectId}/messages?branchId={draft!.StreamId}",
            new
            {
                id = messageId,
                name = "OrderPlaced",
                producerId,
                consumerIds = Array.Empty<Guid>(),
                tags = Array.Empty<string>(),
            });
        Assert.Equal(HttpStatusCode.Created, addMessage.StatusCode);

        var patch = await client.PatchAsJsonAsync(
            $"/api/projects/{projectId}/messages/{messageId}?branchId={draft.StreamId}",
            new
            {
                factStatus = "observed-in-source",
                provenance = new { source = "github.com/acme/orders@abc:src/Events.cs:3-9", revision = "abc" },
                setProvenance = true,
            });
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);

        var previewResp = await client.PostAsync(
            $"/api/projects/{projectId}/drafts/{draftId}/merge/preview", null);
        var preview = await previewResp.Content.ReadFromJsonAsync<MergePreviewResponse>();
        Assert.NotNull(preview);

        var exec = await client.PostAsJsonAsync(
            $"/api/projects/{projectId}/drafts/{draftId}/merge/execute",
            new { resolutions = Array.Empty<object>(), expectedMainVersion = preview.MainVersion });
        Assert.Equal(HttpStatusCode.OK, exec.StatusCode);

        var messages = await client.GetFromJsonAsync<Page<MessageDto>>(
            $"/api/projects/{projectId}/messages");
        var merged = Assert.Single(messages!.Items, m => m.Id == messageId);
        Assert.Equal("observed-in-source", merged.FactStatus);
        Assert.Equal("github.com/acme/orders@abc:src/Events.cs:3-9", merged.Provenance!.Source);
        Assert.Equal("abc", merged.Provenance.Revision);
    }

    [Fact]
    public async Task Execute_rejects_duplicate_resolutions()
    {
        using var factory = CreateFactory();
        using var client = await CreateAuthenticatedClientAsync(factory);
        var projectId = await SetupProjectAsync(client);

        var elId = Guid.NewGuid();
        await client.PostAsJsonAsync($"/api/projects/{projectId}/elements",
            new { id = elId, kind = "system", name = "Orders" });

        var draftId = Guid.NewGuid();
        var draftResp = await client.PostAsJsonAsync($"/api/projects/{projectId}/drafts",
            new { id = draftId, name = "Conflicting draft" });
        var draft = await draftResp.Content.ReadFromJsonAsync<DraftDto>();

        await client.PatchAsJsonAsync(
            $"/api/projects/{projectId}/elements/{elId}?branchId={draft!.StreamId}",
            new { name = "Renamed on Draft" });
        await client.PatchAsJsonAsync(
            $"/api/projects/{projectId}/elements/{elId}",
            new { name = "Renamed on Main" });

        var previewResp = await client.PostAsync(
            $"/api/projects/{projectId}/drafts/{draftId}/merge/preview", null);
        var preview = await previewResp.Content.ReadFromJsonAsync<MergePreviewResponse>();
        Assert.NotNull(preview);
        var conflict = Assert.Single(preview.Conflicts);

        var exec = await client.PostAsJsonAsync(
            $"/api/projects/{projectId}/drafts/{draftId}/merge/execute",
            new
            {
                resolutions = new[]
                {
                    new { entityId = conflict.EntityId, resolution = "take_theirs" },
                    new { entityId = conflict.EntityId, resolution = "take_ours" },
                },
                expectedMainVersion = preview.MainVersion,
            });
        Assert.Equal(HttpStatusCode.BadRequest, exec.StatusCode);
    }

    [Fact]
    public async Task Merge_projects_icon_and_line_shape_changes()
    {
        using var factory = CreateFactory();
        using var client = await CreateAuthenticatedClientAsync(factory);
        var projectId = await SetupProjectAsync(client);

        var ordersId = Guid.NewGuid();
        await client.PostAsJsonAsync($"/api/projects/{projectId}/elements",
            new { id = ordersId, kind = "system", name = "Orders" });
        var paymentsId = Guid.NewGuid();
        await client.PostAsJsonAsync($"/api/projects/{projectId}/elements",
            new { id = paymentsId, kind = "system", name = "Payments" });

        var relationshipId = Guid.NewGuid();
        await client.PostAsJsonAsync($"/api/projects/{projectId}/relationships",
            new { id = relationshipId, fromId = ordersId, toId = paymentsId, label = "calls" });

        var draftId = Guid.NewGuid();
        var draftResp = await client.PostAsJsonAsync($"/api/projects/{projectId}/drafts",
            new { id = draftId, name = "Style draft" });
        var draft = await draftResp.Content.ReadFromJsonAsync<DraftDto>();

        await client.PatchAsJsonAsync(
            $"/api/projects/{projectId}/elements/{ordersId}?branchId={draft!.StreamId}",
            new { icon = "database", setIcon = true });
        await client.PatchAsJsonAsync(
            $"/api/projects/{projectId}/relationships/{relationshipId}?branchId={draft.StreamId}",
            new { lineShape = "smoothstep", setLineShape = true });

        var previewResp = await client.PostAsync(
            $"/api/projects/{projectId}/drafts/{draftId}/merge/preview", null);
        var preview = await previewResp.Content.ReadFromJsonAsync<MergePreviewResponse>();
        Assert.NotNull(preview);

        var exec = await client.PostAsJsonAsync(
            $"/api/projects/{projectId}/drafts/{draftId}/merge/execute",
            new { resolutions = Array.Empty<object>(), expectedMainVersion = preview.MainVersion });
        Assert.Equal(HttpStatusCode.OK, exec.StatusCode);

        var elements = await client.GetFromJsonAsync<Page<ElementDto>>($"/api/projects/{projectId}/elements");
        Assert.Equal("database", Assert.Single(elements!.Items, e => e.Id == ordersId).Icon);

        var relationships = await client.GetFromJsonAsync<Page<RelationshipDto>>(
            $"/api/projects/{projectId}/relationships");
        Assert.Equal("smoothstep", Assert.Single(relationships!.Items, r => r.Id == relationshipId).LineShape);
    }

    private sealed record Page<T>(IReadOnlyList<T> Items, string? Cursor);
}

file static class MergeServiceCollectionExtensions
{
    public static void RemoveAll<T>(this IServiceCollection services)
    {
        var descriptors = services.Where(d => d.ServiceType == typeof(T)).ToList();
        foreach (var d in descriptors) services.Remove(d);
    }
}
