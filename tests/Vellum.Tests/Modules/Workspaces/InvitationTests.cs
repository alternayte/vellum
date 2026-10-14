using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Vellum.Modules.Identity;
using Vellum.Modules.Workspaces;
using Vellum.Shared;

namespace Vellum.Tests.Modules.Workspaces;

/// <summary>
/// A person joins a workspace by accepting an invitation of their email address. The
/// invitation tells the owner nothing about accounts, stays in its workspace, and closes for
/// good when it is accepted, declined, revoked or expired. The workspace keeps an owner.
/// </summary>
[Collection("Integration")]
public class InvitationTests(IntegrationFixture fixture)
{
    [Fact]
    public async Task An_invitation_answers_the_same_for_an_address_with_an_account_and_one_without()
    {
        using var factory = TestHost.Create(fixture);
        using var owner = await TestHost.SignInAsync(factory);
        var (workspaceId, _) = await TestHost.CreateProjectAsync(owner);
        using var known = await TestHost.SignInAsync(factory);
        var knownEmail = (await known.GetFromJsonAsync<UserInfoResponse>("/api/auth/me"))!.Email;
        var unknownEmail = $"nobody-{Guid.NewGuid():N}@vellum.local";

        async Task<(HttpStatusCode Status, string Shape)> InviteByAddressAsync(string email)
        {
            var response = await owner.PostAsJsonAsync($"/api/workspaces/{workspaceId}/invitations", new { email, role = "Editor" });
            var body = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
            // Every field, with the values that differ for any two invitations blanked.
            foreach (var key in new[] { "id", "email", "createdAt", "expiresAt" })
            {
                Assert.True(body.ContainsKey(key), key);
                body[key] = "*";
            }
            return (response.StatusCode, body.ToJsonString());
        }

        var forKnown = await InviteByAddressAsync(knownEmail);
        var forUnknown = await InviteByAddressAsync(unknownEmail);
        Assert.Equal(HttpStatusCode.Created, forKnown.Status);
        Assert.Equal(forKnown, forUnknown);

        // Both are open, and both are in the audit log without the address.
        var open = await owner.GetFromJsonAsync<InvitationDto[]>($"/api/workspaces/{workspaceId}/invitations");
        Assert.Equal([knownEmail, unknownEmail], open!.Select(i => i.Email));
        var audit = await owner.GetStringAsync($"/api/workspaces/{workspaceId}/audit");
        Assert.Equal(2, JsonNode.Parse(audit)!.AsArray().Count(e => (string)e!["action"]! == "member.invited"));
        Assert.DoesNotContain(knownEmail, audit);
        Assert.DoesNotContain(unknownEmail, audit);
    }

    [Fact]
    public async Task Only_the_person_with_the_invited_address_accepts_and_a_second_accept_changes_nothing()
    {
        using var factory = TestHost.Create(fixture);
        using var owner = await TestHost.SignInAsync(factory);
        var (workspaceId, _) = await TestHost.CreateProjectAsync(owner);
        using var invitee = await TestHost.SignInAsync(factory);
        using var stranger = await TestHost.SignInAsync(factory);
        var invitation = await InviteAsync(owner, workspaceId, invitee, "Editor");

        // Another account does not see it, cannot accept and cannot decline it.
        Assert.Empty((await stranger.GetFromJsonAsync<MyInvitationDto[]>("/api/invitations"))!);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.PostAsync($"/api/invitations/{invitation.Id}/accept", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.PostAsync($"/api/invitations/{invitation.Id}/decline", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await stranger.GetAsync($"/api/workspaces/{workspaceId}/members")).StatusCode);
        // Nor does the owner who sent it.
        Assert.Equal(HttpStatusCode.NotFound, (await owner.PostAsync($"/api/invitations/{invitation.Id}/accept", null)).StatusCode);

        var first = await invitee.PostAsync($"/api/invitations/{invitation.Id}/accept", null);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var second = await invitee.PostAsync($"/api/invitations/{invitation.Id}/accept", null);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var accepted = (await first.Content.ReadFromJsonAsync<MembershipDto>())!;
        Assert.Equal(accepted, await second.Content.ReadFromJsonAsync<MembershipDto>());
        Assert.Equal("Editor", accepted.Role);
        Assert.Equal(2, (await owner.GetFromJsonAsync<MembersResponse>($"/api/workspaces/{workspaceId}/members"))!.Members.Count);

        // An accepted invitation is not declined or revoked afterwards.
        Assert.Equal(HttpStatusCode.Conflict, (await invitee.PostAsync($"/api/invitations/{invitation.Id}/decline", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await owner.DeleteAsync($"/api/workspaces/{workspaceId}/invitations/{invitation.Id}")).StatusCode);
    }

    [Fact]
    public async Task Invitations_stay_in_their_workspace()
    {
        using var factory = TestHost.Create(fixture);
        using var ownerA = await TestHost.SignInAsync(factory);
        var (workspaceA, _) = await TestHost.CreateProjectAsync(ownerA);
        using var ownerB = await TestHost.SignInAsync(factory);
        var (workspaceB, _) = await TestHost.CreateProjectAsync(ownerB);
        using var invitee = await TestHost.SignInAsync(factory);
        var invitation = await InviteAsync(ownerA, workspaceA, invitee, "Viewer");

        // The owner of B reads, makes and revokes nothing in A.
        Assert.Equal(HttpStatusCode.Forbidden, (await ownerB.GetAsync($"/api/workspaces/{workspaceA}/invitations")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ownerB.PostAsJsonAsync($"/api/workspaces/{workspaceA}/invitations", new { email = "x@vellum.local", role = "Owner" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ownerB.DeleteAsync($"/api/workspaces/{workspaceA}/invitations/{invitation.Id}")).StatusCode);
        // An invitation of A is not found through B.
        Assert.Equal(HttpStatusCode.NotFound, (await ownerB.DeleteAsync($"/api/workspaces/{workspaceB}/invitations/{invitation.Id}")).StatusCode);
        Assert.Empty((await ownerB.GetFromJsonAsync<InvitationDto[]>($"/api/workspaces/{workspaceB}/invitations"))!);

        // A member who is no owner of A reads no addresses and invites nobody.
        using var editor = await TestHost.SignInMemberAsync(factory, ownerA, workspaceA, "Editor");
        Assert.Equal(HttpStatusCode.Forbidden, (await editor.GetAsync($"/api/workspaces/{workspaceA}/invitations")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await editor.PostAsJsonAsync($"/api/workspaces/{workspaceA}/invitations", new { email = "x@vellum.local", role = "Owner" })).StatusCode);

        // The invitation is still open, and accepting it opens A and not B.
        Assert.Equal(HttpStatusCode.OK, (await invitee.PostAsync($"/api/invitations/{invitation.Id}/accept", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await invitee.GetAsync($"/api/workspaces/{workspaceA}/members")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await invitee.GetAsync($"/api/workspaces/{workspaceB}/members")).StatusCode);
    }

    [Fact]
    public async Task A_revoked_expired_or_declined_invitation_is_not_accepted()
    {
        using var factory = TestHost.Create(fixture);
        using var owner = await TestHost.SignInAsync(factory);
        var (workspaceId, _) = await TestHost.CreateProjectAsync(owner);
        using var invitee = await TestHost.SignInAsync(factory);

        async Task AssertClosedAsync(Guid id, string status)
        {
            var refused = await invitee.PostAsync($"/api/invitations/{id}/accept", null);
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            var error = (await refused.Content.ReadFromJsonAsync<ErrorResponse>())!;
            Assert.Equal(("invitation_closed", $"The invitation is {status}"), (error.Type, error.Title));
            Assert.Empty((await invitee.GetFromJsonAsync<MyInvitationDto[]>("/api/invitations"))!);
            Assert.Equal(HttpStatusCode.Forbidden, (await invitee.GetAsync($"/api/workspaces/{workspaceId}/members")).StatusCode);
        }

        var revoked = await InviteAsync(owner, workspaceId, invitee, "Editor");
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/api/workspaces/{workspaceId}/invitations/{revoked.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/api/workspaces/{workspaceId}/invitations/{revoked.Id}")).StatusCode);
        Assert.Empty((await owner.GetFromJsonAsync<InvitationDto[]>($"/api/workspaces/{workspaceId}/invitations"))!);
        await AssertClosedAsync(revoked.Id, "revoked");

        var declined = await InviteAsync(owner, workspaceId, invitee, "Editor");
        Assert.NotEqual(revoked.Id, declined.Id);
        Assert.Equal(HttpStatusCode.NoContent, (await invitee.PostAsync($"/api/invitations/{declined.Id}/decline", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await invitee.PostAsync($"/api/invitations/{declined.Id}/decline", null)).StatusCode);
        await AssertClosedAsync(declined.Id, "declined");

        // An invitation lives 14 days.
        var expiring = await InviteAsync(owner, workspaceId, invitee, "Editor");
        Assert.Equal(InvitationRules.Lifetime, expiring.ExpiresAt - expiring.CreatedAt);
        await using (var connection = new NpgsqlConnection(fixture.ConnectionString))
        {
            await connection.OpenAsync();
            await using var expire = new NpgsqlCommand("UPDATE workspaces.invitations SET expires_at = now() - interval '1 second' WHERE id = @id", connection);
            expire.Parameters.AddWithValue("id", expiring.Id);
            Assert.Equal(1, await expire.ExecuteNonQueryAsync());
        }
        Assert.Empty((await owner.GetFromJsonAsync<InvitationDto[]>($"/api/workspaces/{workspaceId}/invitations"))!);
        await AssertClosedAsync(expiring.Id, "expired");

        // A new invitation of the same address renews the expired one, which can then be accepted.
        var email = (await invitee.GetFromJsonAsync<UserInfoResponse>("/api/auth/me"))!.Email;
        var renewed = await owner.PostAsJsonAsync($"/api/workspaces/{workspaceId}/invitations", new { email, role = "Viewer" });
        Assert.Equal(HttpStatusCode.OK, renewed.StatusCode);
        var renewedDto = (await renewed.Content.ReadFromJsonAsync<InvitationDto>())!;
        Assert.Equal((expiring.Id, "pending", "Viewer"), (renewedDto.Id, renewedDto.Status, renewedDto.Role));
        Assert.Equal(HttpStatusCode.OK, (await invitee.PostAsync($"/api/invitations/{expiring.Id}/accept", null)).StatusCode);
    }

    [Fact]
    public async Task Invitations_are_rate_limited_for_each_caller()
    {
        using var factory = TestHost.Create(fixture.ConnectionString, configure: services => services.AddSingleton(new InvitationLimits(PerMinute: 3)));
        using var owner = await TestHost.SignInAsync(factory);
        var (workspaceId, _) = await TestHost.CreateProjectAsync(owner);

        for (var i = 0; i < 3; i++)
            await TestHost.Created(owner.PostAsJsonAsync($"/api/workspaces/{workspaceId}/invitations", new { email = $"r{i}-{Guid.NewGuid():N}@vellum.local", role = "Viewer" }));
        var limited = await owner.PostAsJsonAsync($"/api/workspaces/{workspaceId}/invitations", new { email = $"r-{Guid.NewGuid():N}@vellum.local", role = "Viewer" });
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Equal("rate_limited", (await limited.Content.ReadFromJsonAsync<ErrorResponse>())!.Type);
        Assert.Equal(3, (await owner.GetFromJsonAsync<InvitationDto[]>($"/api/workspaces/{workspaceId}/invitations"))!.Length);
    }

    [Fact]
    public async Task The_last_owner_is_not_removed_whatever_the_case_of_the_stored_role()
    {
        using var factory = TestHost.Create(fixture);
        using var owner = await TestHost.SignInAsync(factory);
        var (workspaceId, _) = await TestHost.CreateProjectAsync(owner);
        var ownerId = (await owner.GetFromJsonAsync<UserInfoResponse>("/api/auth/me"))!.Id;
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using (var lower = new NpgsqlCommand("UPDATE workspaces.memberships SET role = 'owner' WHERE workspace_id = @w", connection))
        {
            lower.Parameters.AddWithValue("w", workspaceId);
            Assert.Equal(1, await lower.ExecuteNonQueryAsync());
        }

        var removed = await owner.DeleteAsync($"/api/workspaces/{workspaceId}/members/{ownerId}");
        Assert.Equal(HttpStatusCode.Conflict, removed.StatusCode);
        Assert.Equal("last_owner", (await removed.Content.ReadFromJsonAsync<ErrorResponse>())!.Type);
        Assert.Equal(1, await OwnersAsync(connection, workspaceId));
    }

    [Fact]
    public async Task A_removal_and_a_role_change_at_the_same_time_leave_an_owner()
    {
        using var factory = TestHost.Create(fixture.ConnectionString, configure: services => services.AddSingleton(new InvitationLimits(PerMinute: 1_000)));
        using var first = await TestHost.SignInAsync(factory);
        var firstId = (await first.GetFromJsonAsync<UserInfoResponse>("/api/auth/me"))!.Id;
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();

        for (var round = 0; round < 8; round++)
        {
            var (workspaceId, _) = await TestHost.CreateProjectAsync(first);
            using var second = await TestHost.SignInMemberAsync(factory, first, workspaceId, "Owner");
            var secondId = (await second.GetFromJsonAsync<UserInfoResponse>("/api/auth/me"))!.Id;

            // Each call alone leaves one owner; both together would leave none. The one that
            // waits is refused: as the last owner (409), or as a caller who is no owner (403).
            var results = await Task.WhenAll(
                first.DeleteAsync($"/api/workspaces/{workspaceId}/members/{secondId}"),
                second.PatchAsJsonAsync($"/api/workspaces/{workspaceId}/members/{firstId}", new { role = "Editor" }));
            Assert.Single(results, r => r.IsSuccessStatusCode);
            Assert.Equal(1, await OwnersAsync(connection, workspaceId));
        }
    }

    private static async Task<long> OwnersAsync(NpgsqlConnection connection, Guid workspaceId)
    {
        await using var count = new NpgsqlCommand("SELECT count(*) FROM workspaces.memberships WHERE workspace_id = @w AND lower(role) = 'owner'", connection);
        count.Parameters.AddWithValue("w", workspaceId);
        return (long)(await count.ExecuteScalarAsync())!;
    }

    private static async Task<InvitationDto> InviteAsync(HttpClient owner, Guid workspaceId, HttpClient invitee, string role)
    {
        var email = (await invitee.GetFromJsonAsync<UserInfoResponse>("/api/auth/me"))!.Email;
        var response = await TestHost.Created(owner.PostAsJsonAsync($"/api/workspaces/{workspaceId}/invitations", new { email, role }));
        return (await response.Content.ReadFromJsonAsync<InvitationDto>())!;
    }
}
