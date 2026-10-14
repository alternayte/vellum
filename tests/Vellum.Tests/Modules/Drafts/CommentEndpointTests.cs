// tests/Vellum.Tests/Modules/Drafts/CommentEndpointTests.cs
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Vellum.Modules.Drafts;

namespace Vellum.Tests.Modules.Drafts;

public class CommentEndpointTests : IClassFixture<IntegrationFixture>
{
    private readonly IntegrationFixture _fixture;

    public CommentEndpointTests(IntegrationFixture fixture) => _fixture = fixture;

    private static HttpClient CreateClient(WebApplicationFactory<Program> factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true, AllowAutoRedirect = false });

    private static async Task<(HttpClient Client, string UserId)> CreateAuthenticatedClientAsync(
        WebApplicationFactory<Program> factory)
    {
        var client = CreateClient(factory);
        var email = $"test-{Guid.NewGuid():N}@vellum.local";
        await client.PostAsJsonAsync("/api/auth/register",
            new { email, password = "Test12345!", displayName = "Test User" });
        var me = await client.GetFromJsonAsync<UserInfoResponse>("/api/auth/me");
        return (client, me!.Id);
    }

    private static async Task<(Guid WorkspaceId, Guid ProjectId)> SetupProjectAsync(HttpClient client)
    {
        var workspaceId = Guid.NewGuid();
        await client.PostAsJsonAsync("/api/workspaces", new { id = workspaceId, name = "Test WS" });
        var projectId = Guid.NewGuid();
        await client.PostAsJsonAsync($"/api/workspaces/{workspaceId}/projects", new { id = projectId, name = "Test Project" });
        return (workspaceId, projectId);
    }

    private static async Task<Guid> CreateDraftAsync(HttpClient client, Guid projectId)
    {
        var draftId = Guid.NewGuid();
        await client.PostAsJsonAsync($"/api/projects/{projectId}/drafts",
            new { id = draftId, name = "Test Draft" });
        return draftId;
    }

    [Fact]
    public async Task Create_comment_and_list_returns_it()
    {
        var factory = _fixture.Factory;
        var (client, _) = await CreateAuthenticatedClientAsync(factory);
        var (_, projectId) = await SetupProjectAsync(client);
        var draftId = await CreateDraftAsync(client, projectId);
        var commentId = Guid.NewGuid();

        var createResponse = await client.PostAsJsonAsync(
            $"/api/projects/{projectId}/drafts/{draftId}/comments",
            new { id = commentId, body = "LGTM!" });
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);

        var comment = await createResponse.Content.ReadFromJsonAsync<CommentDto>();
        Assert.Equal(commentId, comment!.Id);
        Assert.Equal("LGTM!", comment.Body);
        Assert.Equal(draftId, comment.DraftId);

        var page = await (await client.GetAsync(
            $"/api/projects/{projectId}/drafts/{draftId}/comments"))
            .Content.ReadFromJsonAsync<CommentPageDto>();
        Assert.Contains(page!.Items, c => c.Id == commentId);
    }

    [Fact]
    public async Task Create_comment_is_idempotent()
    {
        var factory = _fixture.Factory;
        var (client, _) = await CreateAuthenticatedClientAsync(factory);
        var (_, projectId) = await SetupProjectAsync(client);
        var draftId = await CreateDraftAsync(client, projectId);
        var commentId = Guid.NewGuid();

        await client.PostAsJsonAsync(
            $"/api/projects/{projectId}/drafts/{draftId}/comments",
            new { id = commentId, body = "First" });
        var second = await client.PostAsJsonAsync(
            $"/api/projects/{projectId}/drafts/{draftId}/comments",
            new { id = commentId, body = "First" });
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
    }

    [Fact]
    public async Task List_comments_filters_by_entity_id()
    {
        var factory = _fixture.Factory;
        var (client, _) = await CreateAuthenticatedClientAsync(factory);
        var (_, projectId) = await SetupProjectAsync(client);
        var draftId = await CreateDraftAsync(client, projectId);
        var entityId = Guid.NewGuid();

        await client.PostAsJsonAsync(
            $"/api/projects/{projectId}/drafts/{draftId}/comments",
            new { id = Guid.NewGuid(), body = "Entity comment", entityId, entityType = "Element" });
        await client.PostAsJsonAsync(
            $"/api/projects/{projectId}/drafts/{draftId}/comments",
            new { id = Guid.NewGuid(), body = "General comment" });

        var filtered = await (await client.GetAsync(
            $"/api/projects/{projectId}/drafts/{draftId}/comments?entityId={entityId}"))
            .Content.ReadFromJsonAsync<CommentPageDto>();
        Assert.Single(filtered!.Items);
        Assert.Equal("Entity comment", filtered.Items[0].Body);
    }

    [Fact]
    public async Task Update_own_comment_succeeds()
    {
        var factory = _fixture.Factory;
        var (client, _) = await CreateAuthenticatedClientAsync(factory);
        var (_, projectId) = await SetupProjectAsync(client);
        var draftId = await CreateDraftAsync(client, projectId);
        var commentId = Guid.NewGuid();

        await client.PostAsJsonAsync(
            $"/api/projects/{projectId}/drafts/{draftId}/comments",
            new { id = commentId, body = "Original body" });

        var updateResponse = await client.PatchAsJsonAsync(
            $"/api/projects/{projectId}/drafts/{draftId}/comments/{commentId}",
            new { body = "Updated body" });
        Assert.Equal(HttpStatusCode.OK, updateResponse.StatusCode);

        var updated = await updateResponse.Content.ReadFromJsonAsync<CommentDto>();
        Assert.Equal("Updated body", updated!.Body);
    }

    [Fact]
    public async Task Delete_own_comment_succeeds()
    {
        var factory = _fixture.Factory;
        var (client, _) = await CreateAuthenticatedClientAsync(factory);
        var (_, projectId) = await SetupProjectAsync(client);
        var draftId = await CreateDraftAsync(client, projectId);
        var commentId = Guid.NewGuid();

        await client.PostAsJsonAsync(
            $"/api/projects/{projectId}/drafts/{draftId}/comments",
            new { id = commentId, body = "To delete" });

        var deleteResponse = await client.DeleteAsync(
            $"/api/projects/{projectId}/drafts/{draftId}/comments/{commentId}");
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);

        var page = await (await client.GetAsync(
            $"/api/projects/{projectId}/drafts/{draftId}/comments"))
            .Content.ReadFromJsonAsync<CommentPageDto>();
        Assert.DoesNotContain(page!.Items, c => c.Id == commentId);
    }

    [Fact]
    public async Task Update_comment_by_non_author_returns_403()
    {
        var factory = _fixture.Factory;

        // Author sets up workspace + draft + comment
        var (authorClient, _) = await CreateAuthenticatedClientAsync(factory);
        var (workspaceId, projectId) = await SetupProjectAsync(authorClient);
        var draftId = await CreateDraftAsync(authorClient, projectId);
        var commentId = Guid.NewGuid();

        await authorClient.PostAsJsonAsync(
            $"/api/projects/{projectId}/drafts/{draftId}/comments",
            new { id = commentId, body = "Author's comment" });

        // Second user registers and author invites them as Editor
        var (otherClient, _) = await CreateAuthenticatedClientAsync(factory);
        await TestHost.AddMemberAsync(authorClient, otherClient, workspaceId, "Editor");

        // Other user is now an Editor but NOT the comment author
        var updateResponse = await otherClient.PatchAsJsonAsync(
            $"/api/projects/{projectId}/drafts/{draftId}/comments/{commentId}",
            new { body = "Hijacked!" });
        Assert.Equal(HttpStatusCode.Forbidden, updateResponse.StatusCode);
    }

    [Fact]
    public async Task Delete_comment_by_non_author_returns_403()
    {
        var factory = _fixture.Factory;

        var (authorClient, _) = await CreateAuthenticatedClientAsync(factory);
        var (workspaceId, projectId) = await SetupProjectAsync(authorClient);
        var draftId = await CreateDraftAsync(authorClient, projectId);
        var commentId = Guid.NewGuid();

        await authorClient.PostAsJsonAsync(
            $"/api/projects/{projectId}/drafts/{draftId}/comments",
            new { id = commentId, body = "Author's comment" });

        // Second user registers and author invites them as Editor
        var (otherClient, _) = await CreateAuthenticatedClientAsync(factory);
        await TestHost.AddMemberAsync(authorClient, otherClient, workspaceId, "Editor");

        // Other user is Editor but NOT the comment author
        var deleteResponse = await otherClient.DeleteAsync(
            $"/api/projects/{projectId}/drafts/{draftId}/comments/{commentId}");
        Assert.Equal(HttpStatusCode.Forbidden, deleteResponse.StatusCode);
    }

    [Fact]
    public async Task Comments_remain_listed_on_abandoned_draft()
    {
        var factory = _fixture.Factory;
        var (client, _) = await CreateAuthenticatedClientAsync(factory);
        var (_, projectId) = await SetupProjectAsync(client);
        var draftId = await CreateDraftAsync(client, projectId);
        var commentId = Guid.NewGuid();

        await client.PostAsJsonAsync(
            $"/api/projects/{projectId}/drafts/{draftId}/comments",
            new { id = commentId, body = "Persists after abandon" });

        // Abandon the draft — status changes but the draft row is NOT deleted,
        // so comments (which cascade-delete only on row delete) remain accessible.
        var abandonResponse = await client.PostAsync(
            $"/api/projects/{projectId}/drafts/{draftId}/abandon", null);
        Assert.Equal(HttpStatusCode.OK, abandonResponse.StatusCode);

        var page = await (await client.GetAsync(
            $"/api/projects/{projectId}/drafts/{draftId}/comments"))
            .Content.ReadFromJsonAsync<CommentPageDto>();
        Assert.NotNull(page);
        Assert.Contains(page!.Items, c => c.Id == commentId);
    }

    [Fact]
    public async Task A_comment_of_another_project_is_never_returned_or_revealed()
    {
        var factory = _fixture.Factory;
        using var owner = await TestHost.SignInAsync(factory);
        var (_, ownerProject) = await TestHost.CreateProjectAsync(owner);
        var ownerDraft = Guid.NewGuid();
        await TestHost.Created(owner.PostAsJsonAsync($"/api/projects/{ownerProject}/drafts", new { id = ownerDraft, name = "Owner draft" }));
        var commentId = Guid.NewGuid();
        await TestHost.Created(owner.PostAsJsonAsync($"/api/projects/{ownerProject}/drafts/{ownerDraft}/comments",
            new { id = commentId, body = "Secret plan" }));

        // A user of another workspace, with a project and a draft of their own.
        using var stranger = await TestHost.SignInAsync(factory);
        var (_, strangerProject) = await TestHost.CreateProjectAsync(stranger);
        var strangerDraft = Guid.NewGuid();
        await TestHost.Created(stranger.PostAsJsonAsync($"/api/projects/{strangerProject}/drafts", new { id = strangerDraft, name = "Stranger draft" }));

        // Create with the owner's comment ID.
        var create = await stranger.PostAsJsonAsync($"/api/projects/{strangerProject}/drafts/{strangerDraft}/comments",
            new { id = commentId, body = "Mine" });
        Assert.Equal(HttpStatusCode.Conflict, create.StatusCode);
        Assert.DoesNotContain("Secret plan", await create.Content.ReadAsStringAsync());

        // Update and delete through the stranger's own project, naming the owner's draft.
        var update = await stranger.PatchAsJsonAsync($"/api/projects/{strangerProject}/drafts/{ownerDraft}/comments/{commentId}", new { body = "Overwritten" });
        Assert.Equal(HttpStatusCode.NotFound, update.StatusCode);
        var delete = await stranger.DeleteAsync($"/api/projects/{strangerProject}/drafts/{ownerDraft}/comments/{commentId}");
        Assert.Equal(HttpStatusCode.NotFound, delete.StatusCode);

        var page = await owner.GetFromJsonAsync<CommentPageDto>($"/api/projects/{ownerProject}/drafts/{ownerDraft}/comments");
        Assert.Equal("Secret plan", Assert.Single(page!.Items).Body);
    }
}

file sealed record CommentPageDto(IReadOnlyList<CommentDto> Items, string? Cursor);
file sealed record UserInfoResponse(string Id, string Email, string? DisplayName);

