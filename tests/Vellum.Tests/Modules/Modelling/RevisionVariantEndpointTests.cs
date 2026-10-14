using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Vellum.Modules.Modelling.Elements;
using Vellum.Modules.Modelling.Revisions;
using Vellum.Modules.Modelling.Relationships;
using Vellum.Modules.Modelling.Variants;

namespace Vellum.Tests.Modules.Modelling;

public class RevisionVariantEndpointTests : IClassFixture<IntegrationFixture>
{
    private readonly IntegrationFixture _fixture;

    public RevisionVariantEndpointTests(IntegrationFixture fixture) => _fixture = fixture;

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
        var factory = _fixture.Factory;
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
        var factory = _fixture.Factory;
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
        var factory = _fixture.Factory;
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
        var factory = _fixture.Factory;
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
    public async Task A_revision_that_a_record_names_is_not_deleted_and_the_refusal_names_the_record()
    {
        using var client = await CreateAuthenticatedClientAsync(_fixture.Factory);
        var projectId = await SetupProjectAsync(client);
        var url = (string path) => $"/api/projects/{projectId}/{path}";
        await AddSystemAsync(client, projectId, "Orders");

        var pinned = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync(url("revisions"), new { id = pinned, name = "v1" })).StatusCode);
        var free = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync(url("revisions"), new { id = free, name = "v2" })).StatusCode);

        // A rename keeps the stored model and refuses a name in use.
        Assert.Equal(HttpStatusCode.Conflict, (await client.PatchAsJsonAsync(url($"revisions/{pinned}"), new { name = "v2" })).StatusCode);
        var renamed = await client.PatchAsJsonAsync(url($"revisions/{pinned}"), new { name = "Baseline" });
        var revision = (await renamed.Content.ReadFromJsonAsync<RevisionDto>())!;
        Assert.Equal(("Baseline", 1), (revision.Name, revision.Version));

        var variant = await (await client.PostAsJsonAsync(url("variants"), new { id = Guid.NewGuid(), name = "from-baseline", baseRevisionId = pinned }))
            .Content.ReadFromJsonAsync<VariantDto>();
        var snapshot = await client.PostAsJsonAsync(url("review/snapshots"), new { revisionId = pinned, note = "Baseline review" });
        Assert.Equal(HttpStatusCode.Created, snapshot.StatusCode);
        var snapshotId = (await snapshot.Content.ReadFromJsonAsync<Vellum.Modules.Review.SnapshotSummary>())!.Id;

        var refused = await client.DeleteAsync(url($"revisions/{pinned}"));
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        var error = (await refused.Content.ReadFromJsonAsync<Vellum.Shared.ErrorResponse>())!;
        Assert.Equal("revision_in_use", error.Type);
        Assert.Equal(
            ["The review snapshot 'Baseline review'", "The variant 'from-baseline', which was forked from it"],
            error.Errors!.Select(e => e.Message));

        // With the records gone, the revision goes. A revision that nothing names goes at once.
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync(url($"review/snapshots/{snapshotId}"))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync(url($"variants/{variant!.Id}"))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync(url($"revisions/{pinned}"))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync(url($"revisions/{free}"))).StatusCode);
        Assert.Empty((await client.GetFromJsonAsync<List<RevisionDto>>(url("revisions")))!);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync(url($"revisions/{free}"))).StatusCode);
    }

    [Fact]
    public async Task A_deleted_variant_leaves_the_lists_and_takes_no_write()
    {
        using var client = await CreateAuthenticatedClientAsync(_fixture.Factory);
        var projectId = await SetupProjectAsync(client);
        var url = (string path) => $"/api/projects/{projectId}/{path}";
        await AddSystemAsync(client, projectId, "Orders");

        var variant = (await (await client.PostAsJsonAsync(url("variants"), new { id = Guid.NewGuid(), name = "event-driven" }))
            .Content.ReadFromJsonAsync<VariantDto>())!;
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync(url($"variants/{variant.Id}"))).StatusCode);

        Assert.Empty((await client.GetFromJsonAsync<List<VariantDto>>(url("variants")))!);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PostAsJsonAsync(url($"elements?branchId={variant.StreamId}"), new { id = Guid.NewGuid(), kind = "system", name = "Late" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync(url($"variants/{variant.Id}"))).StatusCode);

        // The name is free again, and main kept its model.
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync(url("variants"), new { id = Guid.NewGuid(), name = "event-driven" })).StatusCode);
        Assert.Single((await client.GetFromJsonAsync<Page<ElementDto>>(url("elements")))!.Items);
    }

    [Fact]
    public async Task Reused_revision_and_variant_ids_are_idempotent()
    {
        var factory = _fixture.Factory;
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
    public async Task A_refused_comparison_names_the_side_it_refuses()
    {
        var factory = _fixture.Factory;
        using var client = await CreateAuthenticatedClientAsync(factory);
        var projectId = await SetupProjectAsync(client);

        async Task<string?> RefusedField(string query)
        {
            var response = await client.GetAsync($"/api/projects/{projectId}/model/compare?{query}");
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var error = await response.Content.ReadFromJsonAsync<Vellum.Shared.ErrorResponse>();
            return Assert.Single(error!.Errors!).Field;
        }

        Assert.Equal("left", await RefusedField($"left=revision:{Guid.NewGuid()}&right=main"));
        Assert.Equal("right", await RefusedField($"left=main&right=revision:{Guid.NewGuid()}"));
        Assert.Equal("left", await RefusedField($"left={Guid.NewGuid()}&right=main"));
        Assert.Equal("right", await RefusedField($"left=main&right={Guid.NewGuid()}"));
        Assert.Equal("right", await RefusedField("left=main&right=nonsense"));
    }

    [Fact]
    public async Task Revision_and_variant_endpoints_require_authentication()
    {
        var factory = _fixture.Factory;
        using var client = CreateClient(factory);

        var revisions = await client.GetAsync($"/api/projects/{Guid.NewGuid()}/revisions");
        Assert.Equal(HttpStatusCode.Unauthorized, revisions.StatusCode);

        var variants = await client.GetAsync($"/api/projects/{Guid.NewGuid()}/variants");
        Assert.Equal(HttpStatusCode.Unauthorized, variants.StatusCode);
    }
}

