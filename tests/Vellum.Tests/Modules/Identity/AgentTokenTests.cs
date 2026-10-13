using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Deedbox;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Vellum.Modules.Identity;
using Vellum.Modules.Identity.AgentTokens;
using Vellum.Tests.Modules.Import;

namespace Vellum.Tests.Modules.Identity;

/// <summary>
/// Agent tokens and the boundary between a person and an agent, through the real app with
/// real tokens: no test here builds a principal.
/// </summary>
[Collection("Integration")]
public class AgentTokenTests(IntegrationFixture fixture)
{
    private static string Tokens(Guid workspaceId, string path = "") => $"/api/workspaces/{workspaceId}/agent-tokens{path}";
    private static string Promises(Guid projectId, string path = "") => $"/api/projects/{projectId}/promises{path}";

    private static JsonObject Promise(string? origin = null) => new()
    {
        ["title"] = "A promise",
        ["claim"] = "Something must always hold.",
        ["preconditions"] = "The system runs.",
        ["expectedOutcome"] = "It holds.",
        ["forbiddenOutcome"] = "It does not hold.",
        ["evidenceCriteria"] = "A check shows that it holds.",
        ["origin"] = origin,
    };

    private static async Task<JsonObject> Json(Task<HttpResponseMessage> request, HttpStatusCode expected)
    {
        var response = await request;
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {text}");
        return JsonNode.Parse(text)!.AsObject();
    }

    private static async Task Refused(Task<HttpResponseMessage> request, string type) =>
        Assert.Equal(type, (await Json(request, HttpStatusCode.Forbidden))["type"]!.GetValue<string>());

    private async Task<(WebApplicationFactory<Program> Factory, HttpClient Owner, Guid WorkspaceId, Guid ProjectId)> StartAsync(
        Action<IServiceCollection>? configure = null)
    {
        var factory = TestHost.Create(fixture.ConnectionString, configure: configure);
        var owner = await TestHost.SignInAsync(factory);
        var (workspaceId, projectId) = await TestHost.CreateProjectAsync(owner);
        await Json(owner.PutAsJsonAsync(Promises(projectId, "/prefix"), new { prefix = "ORD" }), HttpStatusCode.OK);
        return (factory, owner, workspaceId, projectId);
    }

    private async Task<List<string>> AuditAsync(HttpClient owner, Guid workspaceId) =>
        (await owner.GetFromJsonAsync<JsonArray>($"/api/workspaces/{workspaceId}/audit?limit=200"))!
            .Select(e => $"{e!["actorId"]!.GetValue<string>()} {e["action"]!.GetValue<string>()} {e["target"]!.GetValue<string>()}")
            .ToList();

    [Fact]
    public async Task An_agent_token_never_approves_and_never_claims_a_human_origin()
    {
        var (factory, owner, workspaceId, projectId) = await StartAsync();
        using var _ = factory;
        var agent = await TestHost.CreateAgentAsync(factory, owner, workspaceId);

        // The body says a person wrote it. The origin is what the caller is.
        var created = await Json(agent.Client.PostAsJsonAsync(Promises(projectId), Promise("human")), HttpStatusCode.Created);
        var id = created["id"]!.GetValue<Guid>();
        Assert.Equal(("agent-suggested", "draft"), (created["origin"]!.GetValue<string>(), created["state"]!.GetValue<string>()));
        var unsaid = await Json(agent.Client.PostAsJsonAsync(Promises(projectId), Promise()), HttpStatusCode.Created);
        Assert.Equal("agent-suggested", unsaid["origin"]!.GetValue<string>());

        // The token has every scope and its user is an Owner. It still does not approve.
        await Refused(agent.Client.PostAsJsonAsync(Promises(projectId, $"/{id}/approve"), new { revision = 1 }), AgentBoundary.HumanOnlyError);
        await Refused(agent.Client.PutAsJsonAsync(Promises(projectId, "/prefix"), new { prefix = "ZZZ" }), AgentBoundary.HumanOnlyError);
        var read = await Json(agent.Client.GetAsync(Promises(projectId, $"/{id}")), HttpStatusCode.OK);
        Assert.Equal("draft", read["state"]!.GetValue<string>());

        // The event names the token, not the user it acts for.
        using (var scope = factory.Services.CreateScope())
        {
            var events = await scope.ServiceProvider.GetRequiredService<IEventStore>().ReadStream(StreamId.From(id), afterVersion: 0, limit: 10);
            Assert.NotEmpty(events);
            Assert.All(events, e => Assert.Equal($"agent-token:{agent.TokenId}", e.Metadata.Actor));
        }

        // A person: human by default, ai-suggested when the body says so, never agent-suggested.
        var byPerson = await Json(owner.PostAsJsonAsync(Promises(projectId), Promise()), HttpStatusCode.Created);
        Assert.Equal("human", byPerson["origin"]!.GetValue<string>());
        var byAi = await Json(owner.PostAsJsonAsync(Promises(projectId), Promise("ai-suggested")), HttpStatusCode.Created);
        Assert.Equal("ai-suggested", byAi["origin"]!.GetValue<string>());
        var claimed = await Json(owner.PostAsJsonAsync(Promises(projectId), Promise("agent-suggested")), HttpStatusCode.BadRequest);
        Assert.Equal("origin", claimed["errors"]![0]!["field"]!.GetValue<string>());

        // The same person approves the agent's draft in the session.
        var asRead = await Json(owner.GetAsync(Promises(projectId, $"/{id}")), HttpStatusCode.OK);
        var approved = await Json(owner.PostAsJsonAsync(Promises(projectId, $"/{id}/approve"),
            new { revision = 1, contentHash = asRead["current"]!["contentHash"]!.GetValue<string>() }), HttpStatusCode.OK);
        Assert.Equal("approved", approved["state"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_bearer_request_is_an_agent_request_whatever_cookie_comes_with_it()
    {
        var (factory, owner, workspaceId, projectId) = await StartAsync();
        using var _ = factory;
        var agent = await TestHost.CreateAgentAsync(factory, owner, workspaceId);
        var id = (await Json(owner.PostAsJsonAsync(Promises(projectId), Promise()), HttpStatusCode.Created))["id"]!.GetValue<Guid>();

        // The owner's session cookie and a token on one request: the cookie upgrades nothing.
        async Task<HttpResponseMessage> Both(string token)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, Promises(projectId, $"/{id}/approve")) { Content = JsonContent.Create(new { revision = 1 }) };
            request.Headers.Authorization = new("Bearer", token);
            return await owner.SendAsync(request);
        }

        await Refused(Both(agent.Token), AgentBoundary.HumanOnlyError);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Both("not-a-token")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Both(agent.Token[..^1] + (agent.Token[^1] == 'A' ? 'B' : 'A'))).StatusCode);
        Assert.Equal("draft", (await Json(owner.GetAsync(Promises(projectId, $"/{id}")), HttpStatusCode.OK))["state"]!.GetValue<string>());

        // A bearer credential that does not authenticate is refused on an open endpoint too.
        var open = new HttpRequestMessage(HttpMethod.Get, "/api/setup/status");
        open.Headers.Authorization = new("Bearer", "not-a-token");
        Assert.Equal(HttpStatusCode.Unauthorized, (await owner.SendAsync(open)).StatusCode);
    }

    [Fact]
    public async Task A_token_stays_inside_its_scopes_its_project_and_its_workspace()
    {
        var (factory, owner, workspaceId, projectA) = await StartAsync();
        using var _ = factory;
        var projectB = Guid.NewGuid();
        await TestHost.Created(owner.PostAsJsonAsync($"/api/workspaces/{workspaceId}/projects", new { id = projectB, name = "Second Project" }));
        await Json(owner.PutAsJsonAsync(Promises(projectB, "/prefix"), new { prefix = "SEC" }), HttpStatusCode.OK);
        var stranger = await TestHost.SignInAsync(factory);
        var (otherWorkspace, otherProject) = await TestHost.CreateProjectAsync(stranger);

        // Without propose: reads, and writes nothing.
        var reader = await TestHost.CreateAgentAsync(factory, owner, workspaceId, ["read"]);
        await Json(reader.Client.GetAsync(Promises(projectA)), HttpStatusCode.OK);
        await Refused(reader.Client.PostAsJsonAsync(Promises(projectA), Promise()), AgentBoundary.AgentNotAllowedError);
        await Refused(reader.Client.GetAsync($"/api/projects/{projectA}/build/manifest"), AgentBoundary.AgentNotAllowedError);

        // Without read: writes a draft, and reads nothing.
        var writer = await TestHost.CreateAgentAsync(factory, owner, workspaceId, ["propose"]);
        await Json(writer.Client.PostAsJsonAsync(Promises(projectA), Promise()), HttpStatusCode.Created);
        await Refused(writer.Client.GetAsync(Promises(projectA)), AgentBoundary.AgentNotAllowedError);

        // Restricted to project A: nothing on project B of the same workspace.
        var narrow = await TestHost.CreateAgentAsync(factory, owner, workspaceId, projectId: projectA);
        await Json(narrow.Client.GetAsync(Promises(projectA)), HttpStatusCode.OK);
        await Refused(narrow.Client.GetAsync(Promises(projectB)), AgentBoundary.AgentNotAllowedError);
        await Refused(narrow.Client.PostAsJsonAsync(Promises(projectB), Promise()), AgentBoundary.AgentNotAllowedError);

        // A token of this workspace: nothing in another workspace, and a project that does
        // not exist gets the same answer.
        var wide = await TestHost.CreateAgentAsync(factory, owner, workspaceId);
        await Json(wide.Client.GetAsync(Promises(projectB)), HttpStatusCode.OK);
        await Refused(wide.Client.GetAsync(Promises(otherProject)), AgentBoundary.AgentNotAllowedError);
        await Refused(wide.Client.GetAsync($"/api/projects/{otherProject}/build/manifest"), AgentBoundary.AgentNotAllowedError);
        await Refused(wide.Client.GetAsync(Promises(Guid.NewGuid())), AgentBoundary.AgentNotAllowedError);

        // An endpoint that did not opt in, with every scope.
        await Refused(wide.Client.GetAsync("/api/workspaces"), AgentBoundary.AgentNotAllowedError);
        await Refused(wide.Client.GetAsync($"/api/projects/{projectA}/elements"), AgentBoundary.AgentNotAllowedError);
        await Refused(wide.Client.GetAsync("/api/auth/me"), AgentBoundary.AgentNotAllowedError);
        await Refused(wide.Client.GetAsync(Tokens(otherWorkspace)), AgentBoundary.HumanOnlyError);

        // The export scope opens the build endpoints.
        await Json(wide.Client.GetAsync($"/api/projects/{projectA}/build/manifest"), HttpStatusCode.OK);

        // A refused use is in the audit log of the token's workspace, once in the interval.
        var refusals = (await AuditAsync(owner, workspaceId)).Where(e => e.Contains(" agent-token.refused ")).ToList();
        Assert.Equal(4, refusals.Count);
        Assert.Contains(refusals, e => e.StartsWith($"agent-token:{reader.TokenId} ") && e.Contains("reason:scope at:POST /api/projects/{projectId}/promises/"));
        Assert.Contains(refusals, e => e.StartsWith($"agent-token:{narrow.TokenId} ") && e.Contains("reason:project "));
        Assert.Contains(refusals, e => e.StartsWith($"agent-token:{wide.TokenId} ") && e.Contains("reason:workspace "));
        Assert.DoesNotContain(await AuditAsync(stranger, otherWorkspace), e => e.Contains("agent-token"));
    }

    [Theory]
    [InlineData("PUT", "/api/workspaces/{workspaceId}/settings")]
    [InlineData("PUT", "/api/workspaces/{workspaceId}/secrets/github:token")]
    [InlineData("PUT", "/api/instance/settings")]
    [InlineData("POST", "/api/instance/keys/rotate")]
    [InlineData("POST", "/api/workspaces/{workspaceId}/members")]
    [InlineData("POST", "/api/projects/{projectId}/imports/{id}/proposals/decide")]
    [InlineData("POST", "/api/projects/{projectId}/ai/flow-proposals/{id}/accept")]
    [InlineData("POST", "/api/projects/{projectId}/ai/architecture-proposals/{id}/decide")]
    [InlineData("POST", "/api/workspaces/{workspaceId}/agent-tokens")]
    public async Task A_human_only_endpoint_refuses_an_agent_token(string method, string route)
    {
        var (factory, owner, workspaceId, projectId) = await StartAsync();
        using var _ = factory;
        var agent = await TestHost.CreateAgentAsync(factory, owner, workspaceId);
        var url = route
            .Replace("{workspaceId}", workspaceId.ToString())
            .Replace("{projectId}", projectId.ToString())
            .Replace("{id}", Guid.NewGuid().ToString());

        var refused = await Json(
            agent.Client.SendAsync(new HttpRequestMessage(new HttpMethod(method), url) { Content = JsonContent.Create(new { }) }),
            HttpStatusCode.Forbidden);
        Assert.Equal(AgentBoundary.HumanOnlyError, refused["type"]!.GetValue<string>());
        Assert.Equal("Only a person signed in to Vellum does this", refused["title"]!.GetValue<string>());

        // No credential is 401, not 403.
        using var anonymous = await factory.CreateClient().SendAsync(new HttpRequestMessage(new HttpMethod(method), url) { Content = JsonContent.Create(new { }) });
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }

    [Fact]
    public async Task The_database_holds_a_keyed_hash_and_the_token_is_shown_once()
    {
        var (factory, owner, workspaceId, projectId) = await StartAsync();
        using var _ = factory;
        var ownerId = (await owner.GetFromJsonAsync<UserInfoResponse>("/api/auth/me"))!.Id;

        var before = DateTimeOffset.UtcNow;
        var response = await TestHost.Created(owner.PostAsJsonAsync(Tokens(workspaceId), new { name = "  CI agent  ", scopes = new[] { "export", "read", "read" }, projectId }));
        var created = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
        var token = created["token"]!.GetValue<string>();
        var id = created["id"]!.GetValue<Guid>();

        // The format: the prefix, the token ID, and 256 random bits.
        Assert.StartsWith($"vlm_agt_{id:N}_", token);
        Assert.Equal(32, Convert.FromBase64String(token[41..].Replace('-', '+').Replace('_', '/') + "=").Length);
        Assert.Equal(("CI agent", projectId), (created["name"]!.GetValue<string>(), created["projectId"]!.GetValue<Guid>()));
        Assert.Equal(["read", "export"], created["scopes"]!.AsArray().Select(s => s!.GetValue<string>()));
        var expiresAt = created["expiresAt"]!.GetValue<DateTimeOffset>();
        Assert.InRange(expiresAt, before + AgentTokenRules.DefaultLifetime, DateTimeOffset.UtcNow + AgentTokenRules.DefaultLifetime);

        // The row, every column as text and as bytes: no token, no secret part, no bare SHA-256.
        var secret = token[41..];
        var sha = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        var shaOfSecret = SHA256.HashData(Encoding.UTF8.GetBytes(secret));
        await using (var connection = new NpgsqlConnection(fixture.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(
                "SELECT row_to_json(t)::text, token_hash, master_key_id FROM identity.agent_tokens t WHERE id = @id", connection);
            command.Parameters.AddWithValue("id", id);
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            var (row, hash) = (reader.GetString(0), (byte[])reader[1]);
            Assert.DoesNotContain(token, row);
            Assert.DoesNotContain(secret, row);
            foreach (var digest in (byte[][])[sha, shaOfSecret])
            {
                Assert.DoesNotContain(Convert.ToHexStringLower(digest), row);
                Assert.DoesNotContain(Convert.ToBase64String(digest), row);
                Assert.NotEqual(digest, hash);
            }
            Assert.Equal(32, hash.Length);
            Assert.NotEqual(Encoding.UTF8.GetBytes(token), hash);
            Assert.False(await reader.ReadAsync());
        }

        // The token works, and the list shows that it was used and never shows it.
        var agent = TestHost.BearerClient(factory, token);
        await Json(agent.GetAsync(Promises(projectId)), HttpStatusCode.OK);
        var listText = await owner.GetStringAsync(Tokens(workspaceId));
        Assert.DoesNotContain(secret, listText);
        Assert.DoesNotContain("token\"", listText);
        Assert.DoesNotContain("hash", listText, StringComparison.OrdinalIgnoreCase);
        var listed = Assert.Single(JsonNode.Parse(listText)!.AsArray())!;
        Assert.Equal(
            ["createdAt", "createdBy", "expiresAt", "id", "lastUsedAt", "name", "projectId", "revokedAt", "scopes"],
            listed.AsObject().Select(p => p.Key).Order(StringComparer.Ordinal));
        Assert.Equal((id, "CI agent", ownerId), (listed["id"]!.GetValue<Guid>(), listed["name"]!.GetValue<string>(), listed["createdBy"]!.GetValue<string>()));
        Assert.NotNull(listed["lastUsedAt"]);
        Assert.Null(listed["revokedAt"]);

        // The audit log names the token by its ID and holds neither the token nor its name.
        var audit = await AuditAsync(owner, workspaceId);
        Assert.Contains(audit, e => e.StartsWith($"{ownerId} agent-token.created agent-token:{id} scopes:read,export project:{projectId} expires:"));
        Assert.DoesNotContain(audit, e => e.Contains(secret) || e.Contains("CI agent"));
    }

    [Fact]
    public async Task Only_an_owner_creates_lists_and_revokes_and_a_request_is_bounded()
    {
        var (factory, owner, workspaceId, projectId) = await StartAsync();
        using var _ = factory;
        var editor = await TestHost.SignInMemberAsync(factory, owner, workspaceId, "Editor");
        var stranger = await TestHost.SignInAsync(factory);
        var (otherWorkspace, otherProject) = await TestHost.CreateProjectAsync(stranger);
        var agent = await TestHost.CreateAgentAsync(factory, owner, workspaceId);
        var body = new { name = "Agent", scopes = new[] { "read" } };

        foreach (var client in (HttpClient[])[editor, stranger])
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync(Tokens(workspaceId), body)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(Tokens(workspaceId))).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.DeleteAsync(Tokens(workspaceId, $"/{agent.TokenId}"))).StatusCode);
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync(Tokens(workspaceId))).StatusCode);

        // An owner of another workspace does not revoke this token through their own workspace.
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.DeleteAsync(Tokens(otherWorkspace, $"/{agent.TokenId}"))).StatusCode);
        await Json(agent.Client.GetAsync(Promises(projectId)), HttpStatusCode.OK);

        async Task Invalid(object request, string field) => Assert.Equal(field,
            (await Json(owner.PostAsJsonAsync(Tokens(workspaceId), request), HttpStatusCode.BadRequest))["errors"]![0]!["field"]!.GetValue<string>());

        await Invalid(new { name = " ", scopes = new[] { "read" } }, "name");
        await Invalid(new { name = new string('n', AgentTokenRules.MaxNameLength + 1), scopes = new[] { "read" } }, "name");
        await Invalid(new { name = "Agent", scopes = Array.Empty<string>() }, "scopes");
        await Invalid(new { name = "Agent", scopes = new[] { "read", "approve" } }, "scopes");
        await Invalid(new { name = "Agent", scopes = new[] { "read" }, projectId = otherProject }, "projectId");
        await Invalid(new { name = "Agent", scopes = new[] { "read" }, expiresAt = DateTimeOffset.UtcNow.AddMinutes(-1) }, "expiresAt");
        await Invalid(new { name = "Agent", scopes = new[] { "read" }, expiresAt = DateTimeOffset.UtcNow + AgentTokenRules.MaxLifetime + TimeSpan.FromHours(1) }, "expiresAt");
        await TestHost.Created(owner.PostAsJsonAsync(Tokens(workspaceId),
            new { name = "Agent", scopes = new[] { "read" }, expiresAt = DateTimeOffset.UtcNow + AgentTokenRules.MaxLifetime - TimeSpan.FromHours(1) }));

        // The bound on live tokens. A revoked token makes room.
        for (var live = 2; live < AgentTokenRules.MaxLivePerWorkspace; live++)
            await TestHost.Created(owner.PostAsJsonAsync(Tokens(workspaceId), body));
        var full = await Json(owner.PostAsJsonAsync(Tokens(workspaceId), body), HttpStatusCode.Conflict);
        Assert.Equal("conflict", full["type"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync(Tokens(workspaceId, $"/{agent.TokenId}"))).StatusCode);
        await TestHost.Created(owner.PostAsJsonAsync(Tokens(workspaceId), body));
        Assert.Equal(AgentTokenRules.MaxLivePerWorkspace + 1, (await owner.GetFromJsonAsync<JsonArray>(Tokens(workspaceId)))!.Count);
    }

    [Fact]
    public async Task A_revoked_token_an_expired_token_and_a_token_of_a_removed_user_stop()
    {
        var clock = new ManualTime();
        var (factory, owner, workspaceId, projectId) = await StartAsync(services => services.AddSingleton<TimeProvider>(clock));
        using var _ = factory;
        var ownerId = (await owner.GetFromJsonAsync<UserInfoResponse>("/api/auth/me"))!.Id;

        // Revoked: never again, and revoking twice is the same answer.
        var revoked = await TestHost.CreateAgentAsync(factory, owner, workspaceId);
        await Json(revoked.Client.GetAsync(Promises(projectId)), HttpStatusCode.OK);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync(Tokens(workspaceId, $"/{revoked.TokenId}"))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync(Tokens(workspaceId, $"/{revoked.TokenId}"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.DeleteAsync(Tokens(workspaceId, $"/{Guid.NewGuid()}"))).StatusCode);
        var afterRevoke = await revoked.Client.GetAsync(Promises(projectId));
        Assert.Equal(HttpStatusCode.Unauthorized, afterRevoke.StatusCode);
        Assert.Equal("Bearer", afterRevoke.Headers.WwwAuthenticate.ToString());
        Assert.Equal(HttpStatusCode.Unauthorized, (await revoked.Client.GetAsync("/api/setup/status")).StatusCode);
        var listed = (await owner.GetFromJsonAsync<JsonArray>(Tokens(workspaceId)))!.Single(t => t!["id"]!.GetValue<Guid>() == revoked.TokenId)!;
        Assert.NotNull(listed["revokedAt"]);

        // Expired: the app's clock passes the expiry.
        var expiring = await TestHost.CreateAgentAsync(factory, owner, workspaceId, expiresAt: clock.GetUtcNow().AddHours(1));
        clock.Advance(TimeSpan.FromMinutes(59));
        await Json(expiring.Client.GetAsync(Promises(projectId)), HttpStatusCode.OK);
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(HttpStatusCode.Unauthorized, (await expiring.Client.GetAsync(Promises(projectId))).StatusCode);

        // The last-used time moves at most once in the interval.
        var used = await TestHost.CreateAgentAsync(factory, owner, workspaceId);
        async Task<DateTimeOffset?> LastUsedAsync() =>
            (await owner.GetFromJsonAsync<JsonArray>(Tokens(workspaceId)))!.Single(t => t!["id"]!.GetValue<Guid>() == used.TokenId)!["lastUsedAt"]?.GetValue<DateTimeOffset>();
        Assert.Null(await LastUsedAsync());
        await Json(used.Client.GetAsync(Promises(projectId)), HttpStatusCode.OK);
        var first = await LastUsedAsync();
        Assert.NotNull(first);
        clock.Advance(AgentTokenRules.LastUsedInterval - TimeSpan.FromSeconds(1));
        await Json(used.Client.GetAsync(Promises(projectId)), HttpStatusCode.OK);
        Assert.Equal(first, await LastUsedAsync());
        clock.Advance(TimeSpan.FromSeconds(1));
        await Json(used.Client.GetAsync(Promises(projectId)), HttpStatusCode.OK);
        Assert.Equal(first + AgentTokenRules.LastUsedInterval, await LastUsedAsync());

        // The user the token acts for is removed from the workspace: the token stops. With a
        // lower role the token has that lower role.
        var second = await TestHost.SignInMemberAsync(factory, owner, workspaceId, "Owner");
        var secondId = (await second.GetFromJsonAsync<UserInfoResponse>("/api/auth/me"))!.Id;
        var orphan = await TestHost.CreateAgentAsync(factory, second, workspaceId);
        await Json(orphan.Client.PostAsJsonAsync(Promises(projectId), Promise()), HttpStatusCode.Created);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/api/workspaces/{workspaceId}/members/{secondId}")).StatusCode);
        await Refused(orphan.Client.GetAsync(Promises(projectId)), AgentBoundary.AgentNotAllowedError);
        await TestHost.Created(owner.PostAsJsonAsync($"/api/workspaces/{workspaceId}/members", new { userId = secondId, role = "Viewer" }));
        await Json(orphan.Client.GetAsync(Promises(projectId)), HttpStatusCode.OK);
        Assert.Equal(HttpStatusCode.Forbidden, (await orphan.Client.PostAsJsonAsync(Promises(projectId), Promise())).StatusCode);

        var audit = await AuditAsync(owner, workspaceId);
        Assert.Single(audit, e => e == $"{ownerId} agent-token.revoked agent-token:{revoked.TokenId}");
        Assert.Single(audit, e => e.StartsWith($"agent-token:{revoked.TokenId} agent-token.refused agent-token:{revoked.TokenId} reason:revoked "));
        Assert.Single(audit, e => e.StartsWith($"agent-token:{expiring.TokenId} agent-token.refused agent-token:{expiring.TokenId} reason:expired "));
        Assert.Single(audit, e => e.StartsWith($"agent-token:{orphan.TokenId} agent-token.refused agent-token:{orphan.TokenId} reason:membership "));
    }

    [Fact]
    public async Task Deleting_a_project_deletes_the_tokens_that_are_restricted_to_it()
    {
        var (factory, owner, workspaceId, projectA) = await StartAsync();
        using var _ = factory;
        var projectB = Guid.NewGuid();
        await TestHost.Created(owner.PostAsJsonAsync($"/api/workspaces/{workspaceId}/projects", new { id = projectB, name = "Second Project" }));
        var narrow = await TestHost.CreateAgentAsync(factory, owner, workspaceId, projectId: projectA);
        var wide = await TestHost.CreateAgentAsync(factory, owner, workspaceId);

        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/api/projects/{projectA}")).StatusCode);

        // The restricted token is gone. It did not become a token for the whole workspace.
        Assert.Equal(HttpStatusCode.Unauthorized, (await narrow.Client.GetAsync(Promises(projectB))).StatusCode);
        var left = (await owner.GetFromJsonAsync<JsonArray>(Tokens(workspaceId)))!;
        Assert.Equal(wide.TokenId, Assert.Single(left)!["id"]!.GetValue<Guid>());
        await Json(wide.Client.GetAsync(Promises(projectB)), HttpStatusCode.OK);
        Assert.Contains(await AuditAsync(owner, workspaceId), e => e.Contains($"agent-token.deleted agent-token:{narrow.TokenId} project-deleted:{projectA}"));
    }

    [Fact]
    public async Task A_token_moves_to_the_new_master_key_when_it_is_used_and_stops_under_a_key_that_is_gone()
    {
        var database = await TestHost.CreateDatabaseAsync(fixture);
        var (oldKey, newKey) = (TestHost.NewMasterKey(), TestHost.NewMasterKey());
        static Dictionary<string, string?> Keys(string current, string? previous = null) =>
            new() { ["Vellum:MasterKey"] = current, ["Vellum:PreviousMasterKey"] = previous };

        string used, unused, email;
        Guid projectId;
        using (var first = TestHost.Create(database, Keys(oldKey)))
        {
            var (admin, created, adminEmail) = await TestHost.SetUpAsync(first);
            email = adminEmail;
            projectId = Guid.NewGuid();
            await TestHost.Created(admin.PostAsJsonAsync($"/api/workspaces/{created.WorkspaceId}/projects", new { id = projectId, name = "P" }));
            used = (await TestHost.CreateAgentAsync(first, admin, created.WorkspaceId)).Token;
            unused = (await TestHost.CreateAgentAsync(first, admin, created.WorkspaceId)).Token;
        }

        // The old key is the previous key: the token still works, and its digest moves.
        using (var rotating = TestHost.Create(database, Keys(newKey, previous: oldKey)))
        {
            await Json(TestHost.BearerClient(rotating, used).GetAsync(Promises(projectId)), HttpStatusCode.OK);

            // The operator rotates, so the instance starts without the old key afterwards.
            var admin = TestHost.Client(rotating);
            Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync("/api/auth/login", new { email, password = TestHost.AdminPassword })).StatusCode);
            var rotated = await Json(admin.PostAsync("/api/instance/keys/rotate", null), HttpStatusCode.OK);
            Assert.Equal(0, rotated["remaining"]!.GetValue<int>());
        }

        // The old key is gone. The token that was used in between works; the other one does not.
        using var after = TestHost.Create(database, Keys(newKey));
        await Json(TestHost.BearerClient(after, used).GetAsync(Promises(projectId)), HttpStatusCode.OK);
        Assert.Equal(HttpStatusCode.Unauthorized, (await TestHost.BearerClient(after, unused).GetAsync(Promises(projectId))).StatusCode);
    }

    /// <summary>
    /// The two lists are the contract. An endpoint that opens itself to agents, or a
    /// human-only endpoint that loses its mark, fails here and not in production.
    /// </summary>
    [Fact]
    public void The_endpoints_open_to_agents_and_the_human_only_endpoints_are_exactly_the_agreed_lists()
    {
        using var factory = TestHost.Create(fixture);
        var endpoints = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .SelectMany(e => (e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["*"])
                .Select(method => (Name: $"{method} {e.RoutePattern.RawText}", Endpoint: e)))
            .ToList();
        Assert.True(endpoints.Count > 100);

        const string promises = "/api/projects/{projectId}/promises";
        const string obligations = promises + "/{promiseId:guid}/obligations";
        const string build = "/api/projects/{projectId}/build";
        var expected = new Dictionary<string, AgentScope>
        {
            [$"GET {promises}/"] = AgentScope.Read,
            [$"GET {promises}/prefix"] = AgentScope.Read,
            [$"GET {promises}/{{promiseId:guid}}"] = AgentScope.Read,
            [$"GET {promises}/{{promiseId:guid}}/runs"] = AgentScope.Read,
            [$"POST {promises}/"] = AgentScope.Propose,
            [$"PUT {promises}/{{promiseId:guid}}"] = AgentScope.Propose,
            [$"POST {promises}/{{promiseId:guid}}/revise"] = AgentScope.Propose,
            [$"DELETE {promises}/{{promiseId:guid}}"] = AgentScope.Propose,
            [$"POST {obligations}/"] = AgentScope.Propose,
            [$"POST {obligations}/generate"] = AgentScope.Propose,
            [$"PUT {obligations}/{{obligationKey}}"] = AgentScope.Propose,
            [$"DELETE {obligations}/{{obligationKey}}"] = AgentScope.Propose,
            [$"POST {obligations}/{{obligationKey}}/run"] = AgentScope.Propose,
            [$"POST {obligations}/{{obligationKey}}/evidence"] = AgentScope.Propose,
            [$"POST {build}/export"] = AgentScope.Export,
            [$"GET {build}/manifest"] = AgentScope.Export,
            [$"POST {build}/brief"] = AgentScope.Export,
            [$"POST {build}/verify"] = AgentScope.Export,
        };

        var open = endpoints.Where(e => e.Endpoint.Metadata.GetMetadata<AgentAllowed>() is not null).ToList();
        var api = open.Where(e => e.Endpoint.RoutePattern.RawText!.StartsWith("/api", StringComparison.Ordinal))
            .ToDictionary(e => e.Name, e => e.Endpoint.Metadata.GetMetadata<AgentAllowed>()!.Scope);
        Assert.Equal(
            expected.OrderBy(e => e.Key, StringComparer.Ordinal).Select(e => $"{e.Key} = {e.Value}"),
            api.OrderBy(e => e.Key, StringComparer.Ordinal).Select(e => $"{e.Key} = {e.Value}"));

        // Everything else that is open to agents is the MCP endpoint, which checks each call
        // itself and takes no session cookie. Every route of it carries the mark.
        var mcp = endpoints.Where(e => !e.Endpoint.RoutePattern.RawText!.StartsWith("/api", StringComparison.Ordinal)
            && e.Endpoint.RoutePattern.RawText.Contains("mcp", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(mcp);
        Assert.Equal(mcp.Select(e => e.Name).Order(), open.Where(e => !api.ContainsKey(e.Name)).Select(e => e.Name).Order());
        Assert.All(mcp, e => Assert.Null(e.Endpoint.Metadata.GetMetadata<AgentAllowed>()!.Scope));

        var humanOnly = endpoints.Where(e => e.Endpoint.Metadata.GetMetadata<HumanOnly>() is not null).Select(e => e.Name).Order(StringComparer.Ordinal);
        Assert.Equal(new[]
        {
            $"POST {promises}/{{promiseId:guid}}/approve",
            $"PUT {promises}/prefix",
            "PUT /api/instance/settings",
            "POST /api/instance/keys/rotate",
            "PUT /api/workspaces/{workspaceId}/settings",
            "PUT /api/workspaces/{workspaceId}/secrets/{name}",
            "DELETE /api/workspaces/{workspaceId}/secrets/{name}",
            "POST /api/workspaces/{workspaceId}/members/",
            "DELETE /api/workspaces/{workspaceId}/members/{memberUserId}",
            "POST /api/workspaces/{workspaceId}/agent-tokens/",
            "GET /api/workspaces/{workspaceId}/agent-tokens/",
            "DELETE /api/workspaces/{workspaceId}/agent-tokens/{tokenId}",
            "POST /api/projects/{projectId}/imports/{importId}/proposals/decide",
            "POST /api/projects/{projectId}/ai/flow-proposals/",
            "POST /api/projects/{projectId}/ai/flow-proposals/{proposalId}/accept",
            "POST /api/projects/{projectId}/ai/flow-proposals/{proposalId}/missing/{alias}/create",
            "POST /api/projects/{projectId}/ai/architecture-proposals/",
            "POST /api/projects/{projectId}/ai/architecture-proposals/{proposalId}/decide",
        }.Order(StringComparer.Ordinal), humanOnly);

        // No endpoint is both.
        Assert.DoesNotContain(open, e => e.Endpoint.Metadata.GetMetadata<HumanOnly>() is not null);
    }
}
