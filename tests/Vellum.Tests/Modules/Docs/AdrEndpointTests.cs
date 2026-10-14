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

        // Create an ADR doc with type = "adr" and a draftId
        var createResponse = await client.PostAsJsonAsync($"/api/projects/{projectId}/docs",
            new { id = docId, title = "ADR-001", type = "adr", draftId });
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);

        var created = await createResponse.Content.ReadFromJsonAsync<DocDto>();
        Assert.Equal("adr", created!.Type);
        Assert.Equal(draftId, created.DraftId);

        // List with ?type=adr → returns it
        var filtered = await (await client.GetAsync($"/api/projects/{projectId}/docs?type=adr"))
            .Content.ReadFromJsonAsync<AdrDocListDto>();
        Assert.Single(filtered!.Items);
        Assert.Equal("ADR-001", filtered.Items[0].Title);

        // List without filter → also returns it
        var all = await (await client.GetAsync($"/api/projects/{projectId}/docs"))
            .Content.ReadFromJsonAsync<AdrDocListDto>();
        Assert.Contains(all!.Items, d => d.Id == docId);

        // Update type to "prd"
        var updateResponse = await client.PatchAsJsonAsync($"/api/projects/{projectId}/docs/{docId}",
            new { type = "prd", setType = true });
        Assert.Equal(HttpStatusCode.OK, updateResponse.StatusCode);
        var updated = await updateResponse.Content.ReadFromJsonAsync<DocDto>();
        Assert.Equal("prd", updated!.Type);

        // List with ?type=adr → empty (no longer matches)
        var adrAfterUpdate = await (await client.GetAsync($"/api/projects/{projectId}/docs?type=adr"))
            .Content.ReadFromJsonAsync<AdrDocListDto>();
        Assert.Empty(adrAfterUpdate!.Items);
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
    public async Task Update_doc_can_clear_type()
    {
        var factory = _fixture.Factory;
        using var client = await CreateAuthenticatedClientAsync(factory);
        var projectId = await SetupProjectAsync(client);
        var docId = Guid.NewGuid();

        await client.PostAsJsonAsync($"/api/projects/{projectId}/docs",
            new { id = docId, title = "ADR-002", type = "adr" });

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
            new { id = docId, title = "ADR-003", draftId });

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

