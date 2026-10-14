// tests/Vellum.Tests/Modules/Docs/AdrEndpointTests.cs
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Vellum.Modules.Docs;

namespace Vellum.Tests.Modules.Docs;

public class AdrEndpointTests : IClassFixture<IntegrationFixture>
{
    private readonly IntegrationFixture _fixture;

    public AdrEndpointTests(IntegrationFixture fixture) => _fixture = fixture;

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
    public async Task Create_doc_with_type_and_filter()
    {
        var factory = _fixture.Factory;
        using var client = await CreateAuthenticatedClientAsync(factory);
        var projectId = await SetupProjectAsync(client);
        var docId = Guid.NewGuid();
        var draftId = Guid.NewGuid();

        // Create a PRD doc with type = "prd" and a draftId
        var createResponse = await client.PostAsJsonAsync($"/api/projects/{projectId}/docs",
            new { id = docId, title = "PRD-001", type = "prd", draftId });
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);

        var created = await createResponse.Content.ReadFromJsonAsync<DocDto>();
        Assert.Equal("prd", created!.Type);
        Assert.Equal(draftId, created.DraftId);

        // List with ?type=prd → returns it
        var filtered = await (await client.GetAsync($"/api/projects/{projectId}/docs?type=prd"))
            .Content.ReadFromJsonAsync<AdrDocListDto>();
        Assert.Single(filtered!.Items);
        Assert.Equal("PRD-001", filtered.Items[0].Title);

        // List without filter → also returns it
        var all = await (await client.GetAsync($"/api/projects/{projectId}/docs"))
            .Content.ReadFromJsonAsync<AdrDocListDto>();
        Assert.Contains(all!.Items, d => d.Id == docId);

        // Update type to "sdd"
        var updateResponse = await client.PatchAsJsonAsync($"/api/projects/{projectId}/docs/{docId}",
            new { type = "sdd", setType = true });
        Assert.Equal(HttpStatusCode.OK, updateResponse.StatusCode);
        var updated = await updateResponse.Content.ReadFromJsonAsync<DocDto>();
        Assert.Equal("sdd", updated!.Type);

        // List with ?type=prd → empty (no longer matches)
        var prdAfterUpdate = await (await client.GetAsync($"/api/projects/{projectId}/docs?type=prd"))
            .Content.ReadFromJsonAsync<AdrDocListDto>();
        Assert.Empty(prdAfterUpdate!.Items);
    }

    [Fact]
    public async Task A_doc_cannot_be_an_adr_because_an_adr_is_a_review_decision()
    {
        var factory = _fixture.Factory;
        using var client = await CreateAuthenticatedClientAsync(factory);
        var projectId = await SetupProjectAsync(client);

        foreach (var type in new[] { "adr", "ADR", " Adr " })
        {
            var created = await client.PostAsJsonAsync($"/api/projects/{projectId}/docs",
                new { id = Guid.NewGuid(), title = "ADR-001", type });
            Assert.Equal(HttpStatusCode.BadRequest, created.StatusCode);
            var body = await created.Content.ReadAsStringAsync();
            Assert.Contains("decision", body);
            Assert.Contains("\"type\"", body);
        }

        var docId = Guid.NewGuid();
        await TestHost.Created(client.PostAsJsonAsync($"/api/projects/{projectId}/docs", new { id = docId, title = "Notes", type = "note" }));
        var changed = await client.PatchAsJsonAsync($"/api/projects/{projectId}/docs/{docId}", new { type = "adr", setType = true });
        Assert.Equal(HttpStatusCode.BadRequest, changed.StatusCode);
        Assert.Contains("decision", await changed.Content.ReadAsStringAsync());

        var list = await (await client.GetAsync($"/api/projects/{projectId}/docs"))
            .Content.ReadFromJsonAsync<AdrDocListDto>();
        Assert.Equal("note", Assert.Single(list!.Items).Type);
    }

    [Fact]
    public async Task Create_doc_with_draft_id_and_filter_by_draft()
    {
        var factory = _fixture.Factory;
        using var client = await CreateAuthenticatedClientAsync(factory);
        var projectId = await SetupProjectAsync(client);
        var draftId = Guid.NewGuid();
        var adrDocId = Guid.NewGuid();

        // Create one doc linked to a draft
        await client.PostAsJsonAsync($"/api/projects/{projectId}/docs",
            new { id = adrDocId, title = "Draft-linked", draftId });
        // Create another doc without draft
        await client.PostAsJsonAsync($"/api/projects/{projectId}/docs",
            new { id = Guid.NewGuid(), title = "Plain doc" });

        // Filter by draftId → only the linked doc
        var filtered = await (await client.GetAsync($"/api/projects/{projectId}/docs?draftId={draftId}"))
            .Content.ReadFromJsonAsync<AdrDocListDto>();
        Assert.Single(filtered!.Items);
        Assert.Equal("Draft-linked", filtered.Items[0].Title);
        Assert.Equal(draftId, filtered.Items[0].DraftId);
    }

    [Fact]
    public async Task Update_doc_can_clear_type()
    {
        var factory = _fixture.Factory;
        using var client = await CreateAuthenticatedClientAsync(factory);
        var projectId = await SetupProjectAsync(client);
        var docId = Guid.NewGuid();

        await client.PostAsJsonAsync($"/api/projects/{projectId}/docs",
            new { id = docId, title = "PRD-002", type = "prd" });

        // Clear type by sending setType = true with no type value
        await client.PatchAsJsonAsync($"/api/projects/{projectId}/docs/{docId}",
            new { setType = true });

        var doc = await (await client.GetAsync($"/api/projects/{projectId}/docs/{docId}"))
            .Content.ReadFromJsonAsync<DocDto>();
        Assert.Null(doc!.Type);
    }

    [Fact]
    public async Task Update_doc_can_clear_draft_id()
    {
        var factory = _fixture.Factory;
        using var client = await CreateAuthenticatedClientAsync(factory);
        var projectId = await SetupProjectAsync(client);
        var docId = Guid.NewGuid();
        var draftId = Guid.NewGuid();

        await client.PostAsJsonAsync($"/api/projects/{projectId}/docs",
            new { id = docId, title = "PRD-003", draftId });

        // Clear draftId by sending setDraftId = true with no draftId value
        await client.PatchAsJsonAsync($"/api/projects/{projectId}/docs/{docId}",
            new { setDraftId = true });

        var doc = await (await client.GetAsync($"/api/projects/{projectId}/docs/{docId}"))
            .Content.ReadFromJsonAsync<DocDto>();
        Assert.Null(doc!.DraftId);
    }

    [Fact]
    public async Task Regular_doc_has_null_type_and_draft_fields()
    {
        var factory = _fixture.Factory;
        using var client = await CreateAuthenticatedClientAsync(factory);
        var projectId = await SetupProjectAsync(client);
        var docId = Guid.NewGuid();

        await client.PostAsJsonAsync($"/api/projects/{projectId}/docs",
            new { id = docId, title = "Plain Doc" });

        var doc = await (await client.GetAsync($"/api/projects/{projectId}/docs/{docId}"))
            .Content.ReadFromJsonAsync<DocDto>();
        Assert.Null(doc!.DraftId);
        Assert.Null(doc.Type);
    }
}

file sealed record AdrDocListDto(IReadOnlyList<DocDto> Items, string? Cursor);

