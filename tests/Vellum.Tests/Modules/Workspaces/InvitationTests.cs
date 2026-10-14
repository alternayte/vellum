using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Vellum.Modules.Identity;
using Vellum.Modules.Workspaces;
using Vellum.Shared;
using Vellum.Tests.Shared.Outbound;

namespace Vellum.Tests.Modules.Workspaces;

/// <summary>
/// A person joins a workspace by accepting an invitation of their email address, with the
/// secret of the link the owner sent. The invitation tells the owner nothing about accounts,
/// stays in its workspace, and closes for good when it is accepted, declined, revoked or
/// expired. The workspace keeps an owner.
/// </summary>
public class InvitationTests(IntegrationFixture fixture) : IClassFixture<IntegrationFixture>
{
    [Fact]
    public async Task An_invitation_answers_the_same_for_an_address_with_an_account_and_one_without()
    {
        var factory = fixture.Factory;
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
            foreach (var key in new[] { "id", "email", "createdAt", "expiresAt", "secret", "path" })
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
    public async Task The_link_holds_a_secret_of_256_bits_that_the_owner_reads_once()
    {
        var factory = fixture.Factory;
        using var owner = await TestHost.SignInAsync(factory);
        var (workspaceId, _) = await TestHost.CreateProjectAsync(owner);
        using var invitee = await TestHost.SignInAsync(factory);
        var invitation = await InviteAsync(owner, workspaceId, invitee, "Editor");

        // 32 bytes in base64url with no padding, in the fragment of the link.
        Assert.Matches("^[A-Za-z0-9_-]{43}$", invitation.Secret);
        Assert.Equal($"/invitations/{invitation.Id}#secret={invitation.Secret}", invitation.Path);
        Assert.DoesNotContain(invitation.Secret, await owner.GetStringAsync($"/api/workspaces/{workspaceId}/invitations"));
        Assert.DoesNotContain(invitation.Secret, await invitee.GetStringAsync("/api/invitations"));

        // The database holds its hash and not the secret.
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var read = new NpgsqlCommand("SELECT secret_hash FROM workspaces.invitations WHERE id = @id", connection);
        read.Parameters.AddWithValue("id", invitation.Id);
        Assert.Equal(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(invitation.Secret)), (byte[])(await read.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task Accepting_needs_the_secret_and_the_invited_account_and_a_failure_does_not_say_which_was_wrong()
    {
        using var factory = TestHost.Create(fixture.ConnectionString, configure: services => services.AddSingleton(new InvitationLimits(ResponsesPerMinute: 1_000)));
        using var owner = await TestHost.SignInAsync(factory);
        var (workspaceId, _) = await TestHost.CreateProjectAsync(owner);
        using var invitee = await TestHost.SignInAsync(factory);
        using var stranger = await TestHost.SignInAsync(factory);
        var invitation = await InviteAsync(owner, workspaceId, invitee, "Editor");
        var other = await InviteAsync(owner, workspaceId, stranger, "Viewer");

        // The invitee with no secret, an empty one, a wrong one or the secret of another
        // invitation; the stranger and the owner with the right secret; an unknown ID.
        var refusals = new List<HttpResponseMessage>();
        foreach (var action in new[] { "open", "accept", "decline" })
        {
            refusals.Add(await invitee.PostAsync($"/api/invitations/{invitation.Id}/{action}", null));
            refusals.Add(await invitee.PostAsJsonAsync($"/api/invitations/{invitation.Id}/{action}", new { }));
            refusals.Add(await Answer(invitee, invitation.Id, action, ""));
            refusals.Add(await Answer(invitee, invitation.Id, action, invitation.Secret[..^1] + (invitation.Secret[^1] == 'A' ? 'B' : 'A')));
            refusals.Add(await Answer(invitee, invitation.Id, action, other.Secret));
            refusals.Add(await Answer(invitee, invitation.Id, action, new string('x', 5000)));
            refusals.Add(await Answer(stranger, invitation.Id, action, invitation.Secret));
            refusals.Add(await Answer(owner, invitation.Id, action, invitation.Secret));
            refusals.Add(await Answer(invitee, Guid.NewGuid(), action, invitation.Secret));
        }
        var bodies = new HashSet<string>();
        foreach (var refusal in refusals)
        {
            Assert.Equal(HttpStatusCode.NotFound, refusal.StatusCode);
            bodies.Add(await refusal.Content.ReadAsStringAsync());
        }
        Assert.Single(bodies);
        Assert.Equal(HttpStatusCode.Forbidden, (await invitee.GetAsync($"/api/workspaces/{workspaceId}/members")).StatusCode);

        // Without the secret the invitee sees that the invitation exists, and no more.
        var listed = Assert.Single((await invitee.GetFromJsonAsync<MyInvitationDto[]>("/api/invitations"))!);
        Assert.Equal(invitation.Id, listed.Id);

        var opened = await Answer(invitee, invitation.Id, "open", invitation.Secret);
        Assert.Equal(HttpStatusCode.OK, opened.StatusCode);
        Assert.Equal(("Test WS", "Editor", "pending"), await opened.Content.ReadFromJsonAsync<MyInvitationDto>() is { } o ? (o.WorkspaceName, o.Role, o.Status) : default);

        var first = await Answer(invitee, invitation.Id, "accept", invitation.Secret);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var second = await Answer(invitee, invitation.Id, "accept", invitation.Secret);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var accepted = (await first.Content.ReadFromJsonAsync<MembershipDto>())!;
        Assert.Equal(accepted, await second.Content.ReadFromJsonAsync<MembershipDto>());
        Assert.Equal("Editor", accepted.Role);
        Assert.Equal(2, (await owner.GetFromJsonAsync<MembersResponse>($"/api/workspaces/{workspaceId}/members"))!.Members.Count);

        // An accepted invitation is not declined or revoked afterwards.
        Assert.Equal(HttpStatusCode.Conflict, (await Answer(invitee, invitation.Id, "decline", invitation.Secret)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await owner.DeleteAsync($"/api/workspaces/{workspaceId}/invitations/{invitation.Id}")).StatusCode);
    }

    [Fact]
    public async Task An_account_that_registered_with_the_invited_address_but_has_no_link_cannot_accept_or_decline()
    {
        var factory = fixture.Factory;
        using var owner = await TestHost.SignInAsync(factory);
        var (workspaceId, _) = await TestHost.CreateProjectAsync(owner);

        // The owner invites an address with no account; somebody else registers it.
        var email = $"squatted-{Guid.NewGuid():N}@vellum.local";
        var response = await TestHost.Created(owner.PostAsJsonAsync($"/api/workspaces/{workspaceId}/invitations", new { email, role = "Owner" }));
        var invitation = (await response.Content.ReadFromJsonAsync<IssuedInvitationDto>())!;
        using var squatter = TestHost.Client(factory);
        Assert.True((await squatter.PostAsJsonAsync("/api/auth/register", new { email, password = "Test12345!", displayName = "Squatter" })).IsSuccessStatusCode);

        Assert.Equal(HttpStatusCode.NotFound, (await squatter.PostAsync($"/api/invitations/{invitation.Id}/accept", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await squatter.PostAsync($"/api/invitations/{invitation.Id}/decline", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await squatter.GetAsync($"/api/workspaces/{workspaceId}/members")).StatusCode);
        Assert.Equal("pending", Assert.Single((await owner.GetFromJsonAsync<InvitationDto[]>($"/api/workspaces/{workspaceId}/invitations"))!).Status);
    }

    [Fact]
    public async Task A_renewed_invitation_has_a_new_secret_and_the_old_link_stops_working()
    {
        var factory = fixture.Factory;
        using var owner = await TestHost.SignInAsync(factory);
        var (workspaceId, _) = await TestHost.CreateProjectAsync(owner);
        using var invitee = await TestHost.SignInAsync(factory);
        var first = await InviteAsync(owner, workspaceId, invitee, "Editor");

        var email = (await invitee.GetFromJsonAsync<UserInfoResponse>("/api/auth/me"))!.Email;
        var renewed = await owner.PostAsJsonAsync($"/api/workspaces/{workspaceId}/invitations", new { email, role = "Viewer" });
        Assert.Equal(HttpStatusCode.OK, renewed.StatusCode);
        var second = (await renewed.Content.ReadFromJsonAsync<IssuedInvitationDto>())!;
        Assert.Equal(first.Id, second.Id);
        Assert.NotEqual(first.Secret, second.Secret);

        Assert.Equal(HttpStatusCode.NotFound, (await Answer(invitee, first.Id, "accept", first.Secret)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Answer(invitee, first.Id, "decline", first.Secret)).StatusCode);
        var accepted = await Answer(invitee, second.Id, "accept", second.Secret);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal("Viewer", (await accepted.Content.ReadFromJsonAsync<MembershipDto>())!.Role);
    }

    [Fact]
    public async Task The_secret_is_in_no_log_line_and_no_audit_entry()
    {
        var logs = new LogCapture();
        using var factory = TestHost.Create(fixture.ConnectionString,
            configure: services => services.AddLogging(logging => logging.AddProvider(logs).SetMinimumLevel(LogLevel.Trace)));
        using var owner = await TestHost.SignInAsync(factory);
        var (workspaceId, _) = await TestHost.CreateProjectAsync(owner);
        using var invitee = await TestHost.SignInAsync(factory);
        var invitation = await InviteAsync(owner, workspaceId, invitee, "Editor");
        var renewal = await TestHost.Created(owner.PostAsJsonAsync($"/api/workspaces/{workspaceId}/invitations", new { email = "other@vellum.local", role = "Viewer" }));
        var otherSecret = (await renewal.Content.ReadFromJsonAsync<IssuedInvitationDto>())!.Secret;

        Assert.Equal(HttpStatusCode.NotFound, (await Answer(invitee, invitation.Id, "accept", otherSecret)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Answer(invitee, invitation.Id, "open", invitation.Secret)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Answer(invitee, invitation.Id, "accept", invitation.Secret)).StatusCode);

        var lines = logs.Lines;
        Assert.NotEmpty(lines);
        foreach (var secret in new[] { invitation.Secret, otherSecret })
        {
            Assert.DoesNotContain(lines, line => line.Contains(secret, StringComparison.Ordinal));
            Assert.DoesNotContain(secret, await owner.GetStringAsync($"/api/workspaces/{workspaceId}/audit"));
        }
    }

    [Fact]
    public async Task Answers_to_invitations_are_rate_limited_for_each_caller()
    {
        using var factory = TestHost.Create(fixture.ConnectionString, configure: services => services.AddSingleton(new InvitationLimits(ResponsesPerMinute: 3)));
        using var owner = await TestHost.SignInAsync(factory);
        var (workspaceId, _) = await TestHost.CreateProjectAsync(owner);
        using var invitee = await TestHost.SignInAsync(factory);
        var invitation = await InviteAsync(owner, workspaceId, invitee, "Editor");

        for (var i = 0; i < 3; i++)
            Assert.Equal(HttpStatusCode.NotFound, (await Answer(invitee, invitation.Id, "accept", $"guess-{i}")).StatusCode);
        // The right secret, too late: the window is spent.
        var limited = await Answer(invitee, invitation.Id, "accept", invitation.Secret);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Equal("rate_limited", (await limited.Content.ReadFromJsonAsync<ErrorResponse>())!.Type);
        Assert.Equal(HttpStatusCode.Forbidden, (await invitee.GetAsync($"/api/workspaces/{workspaceId}/members")).StatusCode);
    }

    [Fact]
    public async Task Invitations_stay_in_their_workspace()
    {
        var factory = fixture.Factory;
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
        Assert.Equal(HttpStatusCode.OK, (await Answer(invitee, invitation.Id, "accept", invitation.Secret)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await invitee.GetAsync($"/api/workspaces/{workspaceA}/members")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await invitee.GetAsync($"/api/workspaces/{workspaceB}/members")).StatusCode);
    }

    [Fact]
    public async Task A_revoked_expired_or_declined_invitation_is_not_accepted()
    {
        var factory = fixture.Factory;
        using var owner = await TestHost.SignInAsync(factory);
        var (workspaceId, _) = await TestHost.CreateProjectAsync(owner);
        using var invitee = await TestHost.SignInAsync(factory);

        async Task AssertClosedAsync(IssuedInvitationDto invitation, string status)
        {
            var refused = await Answer(invitee, invitation.Id, "accept", invitation.Secret);
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
        await AssertClosedAsync(revoked, "revoked");

        var declined = await InviteAsync(owner, workspaceId, invitee, "Editor");
        Assert.NotEqual(revoked.Id, declined.Id);
        Assert.Equal(HttpStatusCode.NoContent, (await Answer(invitee, declined.Id, "decline", declined.Secret)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Answer(invitee, declined.Id, "decline", declined.Secret)).StatusCode);
        await AssertClosedAsync(declined, "declined");

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
        await AssertClosedAsync(expiring, "expired");

        // A new invitation of the same address renews the expired one, with a new secret.
        var email = (await invitee.GetFromJsonAsync<UserInfoResponse>("/api/auth/me"))!.Email;
        var renewed = await owner.PostAsJsonAsync($"/api/workspaces/{workspaceId}/invitations", new { email, role = "Viewer" });
        Assert.Equal(HttpStatusCode.OK, renewed.StatusCode);
        var renewedDto = (await renewed.Content.ReadFromJsonAsync<IssuedInvitationDto>())!;
        Assert.Equal((expiring.Id, "pending", "Viewer"), (renewedDto.Id, renewedDto.Status, renewedDto.Role));
        Assert.Equal(HttpStatusCode.OK, (await Answer(invitee, renewedDto.Id, "accept", renewedDto.Secret)).StatusCode);
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
    public async Task The_last_owner_is_not_removed_after_a_direct_write_of_the_role()
    {
        var factory = fixture.Factory;
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

    // Accounts register with any address and nobody checks it, so the list of invitations of
    // an address must not hand the inviter's address to whoever registered it.
    [Fact]
    public async Task The_invited_person_reads_the_display_name_of_the_inviter_and_never_the_address()
    {
        var factory = fixture.Factory;
        using var owner = await TestHost.SignInAsync(factory);
        var (workspaceId, _) = await TestHost.CreateProjectAsync(owner);
        var me = (await owner.GetFromJsonAsync<UserInfoResponse>("/api/auth/me"))!;
        using var invitee = await TestHost.SignInAsync(factory);
        var invitation = await InviteAsync(owner, workspaceId, invitee, "Editor");

        Assert.Equal("Test User", Assert.Single((await invitee.GetFromJsonAsync<MyInvitationDto[]>("/api/invitations"))!).InvitedBy);

        await using (var connection = new NpgsqlConnection(fixture.ConnectionString))
        {
            await connection.OpenAsync();
            await using var clear = new NpgsqlCommand("""UPDATE identity.users SET display_name = NULL WHERE id = @id""", connection);
            clear.Parameters.AddWithValue("id", me.Id);
            Assert.Equal(1, await clear.ExecuteNonQueryAsync());
        }
        var listed = await invitee.GetStringAsync("/api/invitations");
        Assert.Null(Assert.Single(JsonSerializer.Deserialize<MyInvitationDto[]>(listed, JsonSerializerOptions.Web)!).InvitedBy);
        Assert.DoesNotContain(me.Email, listed, StringComparison.OrdinalIgnoreCase);
        var opened = await (await Answer(invitee, invitation.Id, "open", invitation.Secret)).Content.ReadAsStringAsync();
        Assert.DoesNotContain(me.Email, opened, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_owner_who_is_demoted_or_removed_takes_the_open_invitations_they_issued_with_them()
    {
        using var factory = TestHost.Create(fixture.ConnectionString, configure: services => services.AddSingleton(new InvitationLimits(PerMinute: 1_000)));
        using var first = await TestHost.SignInAsync(factory);
        var (workspaceId, _) = await TestHost.CreateProjectAsync(first);
        using var demoted = await TestHost.SignInMemberAsync(factory, first, workspaceId, "Owner");
        var demotedId = (await demoted.GetFromJsonAsync<UserInfoResponse>("/api/auth/me"))!.Id;
        using var removed = await TestHost.SignInMemberAsync(factory, first, workspaceId, "Owner");
        var removedId = (await removed.GetFromJsonAsync<UserInfoResponse>("/api/auth/me"))!.Id;
        using var a = await TestHost.SignInAsync(factory);
        using var b = await TestHost.SignInAsync(factory);
        using var c = await TestHost.SignInAsync(factory);
        var fromDemoted = await InviteAsync(demoted, workspaceId, a, "Owner");
        var fromRemoved = await InviteAsync(removed, workspaceId, b, "Owner");
        var fromFirst = await InviteAsync(first, workspaceId, c, "Editor");

        Assert.Equal(HttpStatusCode.OK, (await first.PatchAsJsonAsync($"/api/workspaces/{workspaceId}/members/{demotedId}", new { role = "Editor" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await first.DeleteAsync($"/api/workspaces/{workspaceId}/members/{removedId}")).StatusCode);

        Assert.Equal([fromFirst.Id], (await first.GetFromJsonAsync<InvitationDto[]>($"/api/workspaces/{workspaceId}/invitations"))!.Select(i => i.Id));
        foreach (var (invitee, invitation) in new[] { (a, fromDemoted), (b, fromRemoved) })
        {
            var refused = await Answer(invitee, invitation.Id, "accept", invitation.Secret);
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            Assert.Equal(("invitation_closed", "The invitation is revoked"), await refused.Content.ReadFromJsonAsync<ErrorResponse>() is { } e ? (e.Type, e.Title) : default);
        }
        Assert.Equal(HttpStatusCode.OK, (await Answer(c, fromFirst.Id, "accept", fromFirst.Secret)).StatusCode);

        var audit = JsonNode.Parse(await first.GetStringAsync($"/api/workspaces/{workspaceId}/audit"))!.AsArray();
        var revocations = audit.Where(e => (string)e!["action"]! == "member.invitation-revoked").Select(e => (string)e!["target"]!).ToList();
        Assert.Equal(2, revocations.Count);
        Assert.Contains(revocations, t => t.StartsWith($"invitation:{fromDemoted.Id} ") && t.Contains($"issuer:{demotedId}"));
        Assert.Contains(revocations, t => t.StartsWith($"invitation:{fromRemoved.Id} ") && t.Contains($"issuer:{removedId}"));
    }

    // A membership can change by a path other than the member endpoints (a restore, a manual
    // fix in the database). Accept checks the issuer itself.
    [Fact]
    public async Task An_invitation_whose_issuer_is_no_owner_any_more_is_not_accepted()
    {
        var factory = fixture.Factory;
        using var first = await TestHost.SignInAsync(factory);
        var (workspaceId, _) = await TestHost.CreateProjectAsync(first);
        using var issuer = await TestHost.SignInMemberAsync(factory, first, workspaceId, "Owner");
        var issuerId = (await issuer.GetFromJsonAsync<UserInfoResponse>("/api/auth/me"))!.Id;
        using var invitee = await TestHost.SignInAsync(factory);
        var invitation = await InviteAsync(issuer, workspaceId, invitee, "Owner");

        await using (var connection = new NpgsqlConnection(fixture.ConnectionString))
        {
            await connection.OpenAsync();
            await using var demote = new NpgsqlCommand("UPDATE workspaces.memberships SET role = 'viewer' WHERE workspace_id = @w AND user_id = @u", connection);
            demote.Parameters.AddWithValue("w", workspaceId);
            demote.Parameters.AddWithValue("u", issuerId);
            Assert.Equal(1, await demote.ExecuteNonQueryAsync());
        }

        var refused = await Answer(invitee, invitation.Id, "accept", invitation.Secret);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal(("invitation_closed", "The invitation is revoked"), await refused.Content.ReadFromJsonAsync<ErrorResponse>() is { } e ? (e.Type, e.Title) : default);
        Assert.Equal(HttpStatusCode.Forbidden, (await invitee.GetAsync($"/api/workspaces/{workspaceId}/members")).StatusCode);
    }

    private static Task<HttpResponseMessage> Answer(HttpClient client, Guid invitationId, string action, string secret) =>
        client.PostAsJsonAsync($"/api/invitations/{invitationId}/{action}", new { secret });

    private static async Task<long> OwnersAsync(NpgsqlConnection connection, Guid workspaceId)
    {
        await using var count = new NpgsqlCommand("SELECT count(*) FROM workspaces.memberships WHERE workspace_id = @w AND role = 'owner'", connection);
        count.Parameters.AddWithValue("w", workspaceId);
        return (long)(await count.ExecuteScalarAsync())!;
    }

    private static async Task<IssuedInvitationDto> InviteAsync(HttpClient owner, Guid workspaceId, HttpClient invitee, string role)
    {
        var email = (await invitee.GetFromJsonAsync<UserInfoResponse>("/api/auth/me"))!.Email;
        var response = await TestHost.Created(owner.PostAsJsonAsync($"/api/workspaces/{workspaceId}/invitations", new { email, role }));
        return (await response.Content.ReadFromJsonAsync<IssuedInvitationDto>())!;
    }
}
