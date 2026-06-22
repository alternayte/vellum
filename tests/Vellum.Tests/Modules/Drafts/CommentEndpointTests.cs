// tests/Vellum.Tests/Modules/Drafts/CommentEndpointTests.cs
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
public class CommentEndpointTests
{
    private readonly IntegrationFixture _fixture;

    public CommentEndpointTests(IntegrationFixture fixture) => _fixture = fixture;

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

    private static async Task<(HttpClient Client, string UserId)> CreateAuthenticatedClientAsync(
        WebApplicationFactory<Program> factory)
    {
        var client = CreateClient(factory);
        var email = $"test-{Guid.NewGuid():N}@vellum.local";
        await client.PostAsJsonAsync("/api/auth/register",
            new { email, password = "Test123!", displayName = "Test User" });
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
        using var factory = CreateFactory();
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
        using var factory = CreateFactory();
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
        using var factory = CreateFactory();
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
        using var factory = CreateFactory();
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
        using var factory = CreateFactory();
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
        using var factory = CreateFactory();

        // Author sets up workspace + draft + comment
        var (authorClient, _) = await CreateAuthenticatedClientAsync(factory);
        var (workspaceId, projectId) = await SetupProjectAsync(authorClient);
        var draftId = await CreateDraftAsync(authorClient, projectId);
        var commentId = Guid.NewGuid();

        await authorClient.PostAsJsonAsync(
            $"/api/projects/{projectId}/drafts/{draftId}/comments",
            new { id = commentId, body = "Author's comment" });

        // Second user registers and author invites them as Editor
        var (otherClient, otherUserId) = await CreateAuthenticatedClientAsync(factory);
        await authorClient.PostAsJsonAsync(
            $"/api/workspaces/{workspaceId}/members",
            new { userId = otherUserId, role = "Editor" });

        // Other user is now an Editor but NOT the comment author
        var updateResponse = await otherClient.PatchAsJsonAsync(
            $"/api/projects/{projectId}/drafts/{draftId}/comments/{commentId}",
            new { body = "Hijacked!" });
        Assert.Equal(HttpStatusCode.Forbidden, updateResponse.StatusCode);
    }

    [Fact]
    public async Task Delete_comment_by_non_author_returns_403()
    {
        using var factory = CreateFactory();

        var (authorClient, _) = await CreateAuthenticatedClientAsync(factory);
        var (workspaceId, projectId) = await SetupProjectAsync(authorClient);
        var draftId = await CreateDraftAsync(authorClient, projectId);
        var commentId = Guid.NewGuid();

        await authorClient.PostAsJsonAsync(
            $"/api/projects/{projectId}/drafts/{draftId}/comments",
            new { id = commentId, body = "Author's comment" });

        // Second user registers and author invites them as Editor
        var (otherClient, otherUserId) = await CreateAuthenticatedClientAsync(factory);
        await authorClient.PostAsJsonAsync(
            $"/api/workspaces/{workspaceId}/members",
            new { userId = otherUserId, role = "Editor" });

        // Other user is Editor but NOT the comment author
        var deleteResponse = await otherClient.DeleteAsync(
            $"/api/projects/{projectId}/drafts/{draftId}/comments/{commentId}");
        Assert.Equal(HttpStatusCode.Forbidden, deleteResponse.StatusCode);
    }

    [Fact]
    public async Task Comments_remain_listed_on_abandoned_draft()
    {
        using var factory = CreateFactory();
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
}

file sealed record CommentPageDto(IReadOnlyList<CommentDto> Items, string? Cursor);
file sealed record UserInfoResponse(string Id, string Email, string? DisplayName);

file static class CommentServiceCollectionExtensions
{
    public static void RemoveAll<T>(this IServiceCollection services)
    {
        var descriptors = services.Where(d => d.ServiceType == typeof(T)).ToList();
        foreach (var d in descriptors) services.Remove(d);
    }
}
