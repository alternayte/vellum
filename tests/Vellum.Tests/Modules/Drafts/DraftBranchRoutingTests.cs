// tests/Vellum.Tests/Modules/Drafts/DraftBranchRoutingTests.cs
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Vellum.Modules.Drafts;
using Vellum.Modules.Modelling;

namespace Vellum.Tests.Modules.Drafts;

public class DraftBranchRoutingTests : IClassFixture<IntegrationFixture>
{
    private readonly IntegrationFixture _fixture;

    public DraftBranchRoutingTests(IntegrationFixture fixture) => _fixture = fixture;

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
    public async Task Element_added_to_draft_stream_not_visible_on_main()
    {
        var factory = _fixture.Factory;
        using var client = await CreateAuthenticatedClientAsync(factory);
        var projectId = await SetupProjectAsync(client);

        var draftId = Guid.NewGuid();
        var createDraftResp = await client.PostAsJsonAsync(
            $"/api/projects/{projectId}/drafts",
            new { id = draftId, name = "Feature branch" });
        var draft = await createDraftResp.Content.ReadFromJsonAsync<DraftDto>();

        var elementId = Guid.NewGuid();
        var addResp = await client.PostAsJsonAsync(
            $"/api/projects/{projectId}/elements?branchId={draft!.StreamId}",
            new { id = elementId, kind = "system", name = "Auth Service" });
        Assert.Equal(HttpStatusCode.Created, addResp.StatusCode);

        var mainElements = await client.GetFromJsonAsync<Page<ElementDto>>(
            $"/api/projects/{projectId}/elements");
        Assert.DoesNotContain(mainElements!.Items, e => e.Id == elementId);

        var draftElements = await client.GetFromJsonAsync<Page<ElementDto>>(
            $"/api/projects/{projectId}/elements?branchId={draft.StreamId}");
        Assert.Contains(draftElements!.Items, e => e.Id == elementId);
    }

    [Fact]
    public async Task Draft_branch_reads_the_full_model()
    {
        var factory = _fixture.Factory;
        using var client = await CreateAuthenticatedClientAsync(factory);
        var projectId = await SetupProjectAsync(client);

        var elementId = Guid.NewGuid();
        await client.PostAsJsonAsync($"/api/projects/{projectId}/elements",
            new { id = elementId, kind = "system", name = "Orders" });

        var draftId = Guid.NewGuid();
        var createDraftResp = await client.PostAsJsonAsync(
            $"/api/projects/{projectId}/drafts",
            new { id = draftId, name = "Feature branch" });
        var draft = await createDraftResp.Content.ReadFromJsonAsync<DraftDto>();

        var draftElements = await client.GetFromJsonAsync<Page<ElementDto>>(
            $"/api/projects/{projectId}/elements?branchId={draft!.StreamId}");
        Assert.Contains(draftElements!.Items, e => e.Id == elementId);
    }

    private sealed record Page<T>(IReadOnlyList<T> Items, string? Cursor);
}

