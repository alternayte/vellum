using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vellum.Modules.Workspaces;

namespace Vellum.Tests.Modules.Workspaces;

public class WorkspaceEndpointTests : IClassFixture<IntegrationFixture>
{
    private readonly IntegrationFixture _fixture;

    public WorkspaceEndpointTests(IntegrationFixture fixture) => _fixture = fixture;

    private static HttpClient CreateClient(WebApplicationFactory<Program> factory)
    {
        return factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = true,
            AllowAutoRedirect = false
        });
    }

    private static async Task<HttpClient> CreateAuthenticatedClientAsync(WebApplicationFactory<Program> factory)
    {
        var client = CreateClient(factory);
        var email = $"test-{Guid.NewGuid():N}@vellum.local";
        await client.PostAsJsonAsync("/api/auth/register",
            new { email, password = "Test12345!", displayName = "Test User" });
        return client;
    }

    [Fact]
    public async Task Create_workspace_and_list_returns_it()
    {
        var factory = _fixture.Factory;
        using var client = await CreateAuthenticatedClientAsync(factory);
        var workspaceId = Guid.NewGuid();

        var createResponse = await client.PostAsJsonAsync("/api/workspaces",
            new { id = workspaceId, name = "Test Workspace" });
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);

        var listResponse = await client.GetAsync("/api/workspaces");
        var workspaces = await listResponse.Content.ReadFromJsonAsync<WorkspaceDto[]>();
        Assert.Contains(workspaces!, w => w.Id == workspaceId);
    }

    [Fact]
    public async Task Create_project_in_workspace()
    {
        var factory = _fixture.Factory;
        using var client = await CreateAuthenticatedClientAsync(factory);
        var workspaceId = Guid.NewGuid();
        var projectId = Guid.NewGuid();

        await client.PostAsJsonAsync("/api/workspaces",
            new { id = workspaceId, name = "Test WS" });

        var createResponse = await client.PostAsJsonAsync(
            $"/api/workspaces/{workspaceId}/projects",
            new { id = projectId, name = "Test Project", description = "A test" });
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);

        var project = await createResponse.Content.ReadFromJsonAsync<ProjectDto>();
        Assert.Equal("Test Project", project!.Name);
        Assert.NotEqual(Guid.Empty, project.StreamId);
    }

    [Fact]
    public async Task A_workspace_or_project_without_an_id_is_refused()
    {
        var factory = _fixture.Factory;
        using var client = await CreateAuthenticatedClientAsync(factory);

        var workspace = await client.PostAsJsonAsync("/api/workspaces", new { name = "No ID" });
        Assert.Equal(HttpStatusCode.BadRequest, workspace.StatusCode);

        var workspaceId = Guid.NewGuid();
        await client.PostAsJsonAsync("/api/workspaces", new { id = workspaceId, name = "Test WS" });
        var project = await client.PostAsJsonAsync($"/api/workspaces/{workspaceId}/projects", new { name = "No ID" });
        Assert.Equal(HttpStatusCode.BadRequest, project.StatusCode);
    }

    [Fact]
    public async Task A_create_with_the_id_of_another_tenants_record_is_409_and_shows_nothing_of_it()
    {
        var factory = _fixture.Factory;
        using var a = await CreateAuthenticatedClientAsync(factory);
        using var b = await CreateAuthenticatedClientAsync(factory);
        var workspaceA = Guid.NewGuid();
        var projectA = Guid.NewGuid();
        await a.PostAsJsonAsync("/api/workspaces", new { id = workspaceA, name = "Secret workspace" });
        await a.PostAsJsonAsync($"/api/workspaces/{workspaceA}/projects", new { id = projectA, name = "Secret project" });
        var workspaceB = Guid.NewGuid();
        await b.PostAsJsonAsync("/api/workspaces", new { id = workspaceB, name = "B" });

        var workspace = await b.PostAsJsonAsync("/api/workspaces", new { id = workspaceA, name = "x" });
        var project = await b.PostAsJsonAsync($"/api/workspaces/{workspaceB}/projects", new { id = projectA, name = "x" });

        foreach (var response in new[] { workspace, project })
        {
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();
            Assert.DoesNotContain("Secret", body);
            Assert.DoesNotContain(workspaceA.ToString(), body);
        }

        // The creator's retry still gets the records back.
        Assert.Equal(HttpStatusCode.OK, (await a.PostAsJsonAsync("/api/workspaces", new { id = workspaceA, name = "Secret workspace" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await a.PostAsJsonAsync($"/api/workspaces/{workspaceA}/projects", new { id = projectA, name = "Secret project" })).StatusCode);
    }

    [Fact]
    public async Task Unauthenticated_workspace_request_returns_401()
    {
        var factory = _fixture.Factory;
        using var client = CreateClient(factory);
        var response = await client.GetAsync("/api/workspaces");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task An_owner_invites_by_email_the_person_accepts_and_the_last_owner_stays_an_owner()
    {
        var factory = _fixture.Factory;
        using var owner = await CreateAuthenticatedClientAsync(factory);
        var workspaceId = Guid.NewGuid();
        await owner.PostAsJsonAsync("/api/workspaces", new { id = workspaceId, name = "Members" });
        var ownerId = (await owner.GetFromJsonAsync<Vellum.Modules.Identity.UserInfoResponse>("/api/auth/me"))!.Id;

        using var other = CreateClient(factory);
        var email = $"member-{Guid.NewGuid():N}@vellum.local";
        await other.PostAsJsonAsync("/api/auth/register", new { email, password = "Test12345!", displayName = "Second Person" });
        var otherId = (await other.GetFromJsonAsync<Vellum.Modules.Identity.UserInfoResponse>("/api/auth/me"))!.Id;

        // The invitation makes no member. The address matches in any case.
        var invited = await owner.PostAsJsonAsync($"/api/workspaces/{workspaceId}/invitations", new { email = email.ToUpperInvariant(), role = "viewer" });
        Assert.Equal(HttpStatusCode.Created, invited.StatusCode);
        var invitation = (await invited.Content.ReadFromJsonAsync<IssuedInvitationDto>())!;
        Assert.Equal(("Viewer", "pending"), (invitation.Role, invitation.Status));
        Assert.Equal(HttpStatusCode.Forbidden, (await other.GetAsync($"/api/workspaces/{workspaceId}/members")).StatusCode);

        var offered = Assert.Single((await other.GetFromJsonAsync<MyInvitationDto[]>("/api/invitations"))!);
        Assert.Equal((invitation.Id, "Members", "Viewer", "Test User"), (offered.Id, offered.WorkspaceName, offered.Role, offered.InvitedBy));
        Assert.Equal(HttpStatusCode.OK, (await other.PostAsJsonAsync($"/api/invitations/{invitation.Id}/accept", new { secret = invitation.Secret })).StatusCode);
        Assert.Empty((await other.GetFromJsonAsync<MyInvitationDto[]>("/api/invitations"))!);
        Assert.Empty((await owner.GetFromJsonAsync<InvitationDto[]>($"/api/workspaces/{workspaceId}/invitations"))!);

        // A member is not invited again.
        Assert.Equal(HttpStatusCode.Conflict, (await owner.PostAsJsonAsync($"/api/workspaces/{workspaceId}/invitations", new { email, role = "Owner" })).StatusCode);

        // Each member reads the list. It names the role of the reader.
        var seen = await other.GetFromJsonAsync<MembersResponse>($"/api/workspaces/{workspaceId}/members");
        Assert.Equal("Viewer", seen!.YourRole);
        Assert.Equal([(ownerId, "Owner"), (otherId, "Viewer")], seen.Members.Select(m => (m.UserId, m.Role)));
        Assert.Equal(email, seen.Members[1].Email);

        // Only an owner changes a role.
        var refused = await other.PatchAsJsonAsync($"/api/workspaces/{workspaceId}/members/{ownerId}", new { role = "Viewer" });
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);

        // The only owner cannot stop being an owner.
        var last = await owner.PatchAsJsonAsync($"/api/workspaces/{workspaceId}/members/{ownerId}", new { role = "Editor" });
        Assert.Equal(HttpStatusCode.Conflict, last.StatusCode);
        Assert.Equal("last_owner", (await last.Content.ReadFromJsonAsync<Vellum.Shared.ErrorResponse>())!.Type);

        // With a second owner, the first one can.
        Assert.Equal(HttpStatusCode.OK, (await owner.PatchAsJsonAsync($"/api/workspaces/{workspaceId}/members/{otherId}", new { role = "Owner" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.PatchAsJsonAsync($"/api/workspaces/{workspaceId}/members/{ownerId}", new { role = "Editor" })).StatusCode);
        var after = await owner.GetFromJsonAsync<MembersResponse>($"/api/workspaces/{workspaceId}/members");
        Assert.Equal("Editor", after!.YourRole);
    }
}

