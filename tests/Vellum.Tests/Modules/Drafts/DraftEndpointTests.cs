// tests/Vellum.Tests/Modules/Drafts/DraftEndpointTests.cs
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Vellum.Modules.Drafts;

namespace Vellum.Tests.Modules.Drafts;

public class DraftEndpointTests : IClassFixture<IntegrationFixture>
{
    private readonly IntegrationFixture _fixture;

    public DraftEndpointTests(IntegrationFixture fixture) => _fixture = fixture;

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
    public async Task Create_draft_and_get_returns_it()
    {
        var factory = _fixture.Factory;
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
        var factory = _fixture.Factory;
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
    public async Task A_draft_id_from_another_workspace_is_refused_and_reveals_nothing()
    {
        var factory = _fixture.Factory;
        using var owner = await CreateAuthenticatedClientAsync(factory);
        var ownerProject = await SetupProjectAsync(owner);
        var draftId = Guid.NewGuid();
        await owner.PostAsJsonAsync($"/api/projects/{ownerProject}/drafts", new { id = draftId, name = "Secret plan" });

        using var other = await CreateAuthenticatedClientAsync(factory);
        var otherProject = await SetupProjectAsync(other);
        var response = await other.PostAsJsonAsync($"/api/projects/{otherProject}/drafts", new { id = draftId, name = "Mine" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.DoesNotContain("Secret plan", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_schema_id_that_is_already_a_model_stream_gives_a_conflict()
    {
        var factory = _fixture.Factory;
        using var client = await CreateAuthenticatedClientAsync(factory);
        var projectId = await SetupProjectAsync(client);
        var draftId = Guid.NewGuid();
        var draft = await (await client.PostAsJsonAsync($"/api/projects/{projectId}/drafts", new { id = draftId, name = "D" }))
            .Content.ReadFromJsonAsync<DraftDto>();

        var response = await client.PostAsJsonAsync($"/api/projects/{projectId}/schemas",
            new { id = draft!.StreamId, name = "Order", content = "{}" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task List_drafts_filters_by_status()
    {
        var factory = _fixture.Factory;
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
        var factory = _fixture.Factory;
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
        var factory = _fixture.Factory;
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

