using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Deedbox;
using Microsoft.AspNetCore.Authorization;
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
        await Refused(reader.Client.PostAsJsonAsync($"/api/projects/{projectA}/build/brief", new { }), AgentBoundary.AgentNotAllowedError);

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
        await Refused(wide.Client.GetAsync($"/api/projects/{projectA}/views"), AgentBoundary.AgentNotAllowedError);
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

    /// <summary>The endpoints a caller with no credential may call, by design. Each gives nothing of a workspace.</summary>
    private static readonly string[] Anonymous =
    [
        "* /health",
        "GET /api/setup/status",
        "POST /api/setup/",
        "POST /api/auth/register",
        "POST /api/auth/login",
        "GET /api/auth/external/{provider}",
        "GET /api/auth/external/callback",
        "GET /llms.txt",
        "GET /llms-full.txt",
        "GET /skills/vellum/SKILL.md",
        // The app shell, for a path that is no file and no endpoint.
        "GET {*path:nonfile}",
        "HEAD {*path:nonfile}",
    ];

    /// <summary>Only a person signed in to Vellum.</summary>
    private static readonly string[] ForAPerson =
    [
        "POST /api/projects/{projectId}/promises/{promiseId:guid}/approve",
        "PUT /api/projects/{projectId}/promises/prefix",
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
        "PUT /api/projects/{projectId}/review/dispositions",
        "POST /api/projects/{projectId}/review/snapshots",
        "DELETE /api/projects/{projectId}/review/snapshots/{snapshotId:guid}",
        "POST /api/projects/{projectId}/review/critiques/",
        "DELETE /api/projects/{projectId}/review/critiques/{critiqueId:guid}",
        "POST /api/projects/{projectId}/review/critiques/{critiqueId:guid}/proposed-decisions/{index:int}/keep",
        "POST /api/projects/{projectId}/review/critiques/{critiqueId:guid}/questions/{index:int}/keep",
        "POST /api/projects/{projectId}/decisions/{decisionId:guid}/accept",
        "POST /api/projects/{projectId}/decisions/{decisionId:guid}/reject",
        "POST /api/projects/{projectId}/questions/keep",
        "POST /api/projects/{projectId}/questions/{questionId:guid}/answer",
        "POST /api/projects/{projectId}/questions/{questionId:guid}/drop",
        "DELETE /api/projects/{projectId}/questions/{questionId:guid}",
        "POST /api/projects/{projectId}/load-tests/targets/{targetId:guid}/authorise",
        "DELETE /api/projects/{projectId}/load-tests/targets/{targetId:guid}/authorisation",
        "PUT /api/projects/{projectId}/load-tests/targets/{targetId:guid}/override",
    ];

    /// <summary>
    /// Open to an agent token, with the scopes of which the token needs one. No scope: the
    /// endpoint takes a token of any scope, and checks each call itself or gives nothing of a
    /// workspace.
    /// </summary>
    private static readonly Dictionary<string, string> ForAnAgent = new()
    {
        ["GET /api/workspaces/{workspaceId}/projects/"] = "read",
        ["GET /api/projects/{projectId}/elements/"] = "read",
        ["GET /api/projects/{projectId}/elements/{elementId}"] = "read",
        ["GET /api/projects/{projectId}/relationships/"] = "read",
        ["GET /api/projects/{projectId}/relationships/{relationshipId}"] = "read",
        ["GET /api/projects/{projectId}/messages/"] = "read",
        ["GET /api/projects/{projectId}/messages/{messageId}"] = "read",
        ["GET /api/projects/{projectId}/operations/"] = "read",
        ["GET /api/projects/{projectId}/channels/"] = "read",
        ["GET /api/projects/{projectId}/subscriptions/"] = "read",
        ["GET /api/projects/{projectId}/bindings/"] = "read",
        ["GET /api/projects/{projectId}/schemas/"] = "read",
        ["GET /api/projects/{projectId}/schemas/{schemaId}"] = "read",
        ["GET /api/projects/{projectId}/revisions/"] = "read",
        ["GET /api/projects/{projectId}/revisions/{revisionId}"] = "read",
        ["GET /api/projects/{projectId}/scenarios/"] = "read",
        ["GET /api/projects/{projectId}/scenarios/{scenarioId}"] = "read",
        ["POST /api/projects/{projectId}/scenarios/{scenarioId}/run"] = "read",
        ["GET /api/projects/{projectId}/promises/"] = "read",
        ["GET /api/projects/{projectId}/promises/prefix"] = "read",
        ["GET /api/projects/{projectId}/promises/{promiseId:guid}"] = "read",
        ["GET /api/projects/{projectId}/promises/{promiseId:guid}/runs"] = "read",
        ["POST /api/projects/{projectId}/promises/"] = "propose",
        ["PUT /api/projects/{projectId}/promises/{promiseId:guid}"] = "propose",
        ["POST /api/projects/{projectId}/promises/{promiseId:guid}/revise"] = "propose",
        ["DELETE /api/projects/{projectId}/promises/{promiseId:guid}"] = "propose",
        ["POST /api/projects/{projectId}/promises/{promiseId:guid}/obligations/"] = "propose",
        ["POST /api/projects/{projectId}/promises/{promiseId:guid}/obligations/generate"] = "propose",
        ["PUT /api/projects/{projectId}/promises/{promiseId:guid}/obligations/{obligationKey}"] = "propose",
        ["DELETE /api/projects/{projectId}/promises/{promiseId:guid}/obligations/{obligationKey}"] = "propose",
        ["POST /api/projects/{projectId}/promises/{promiseId:guid}/obligations/{obligationKey}/run"] = "propose",
        ["POST /api/projects/{projectId}/promises/{promiseId:guid}/obligations/{obligationKey}/evidence"] = "propose",
        ["GET /api/projects/{projectId}/build/manifest"] = "read or export",
        ["GET /api/projects/{projectId}/review/rules"] = "read",
        ["GET /api/projects/{projectId}/review/findings"] = "read",
        ["GET /api/projects/{projectId}/decisions/"] = "read",
        ["GET /api/projects/{projectId}/decisions/export"] = "read",
        ["GET /api/projects/{projectId}/decisions/{decisionId:guid}"] = "read",
        ["GET /api/projects/{projectId}/decisions/{decisionId:guid}/export"] = "read",
        ["POST /api/projects/{projectId}/decisions/"] = "propose",
        ["GET /api/projects/{projectId}/questions/"] = "read",
        ["POST /api/projects/{projectId}/questions/"] = "propose",
        ["POST /api/projects/{projectId}/build/export"] = "export",
        ["POST /api/projects/{projectId}/build/brief"] = "export",
        ["POST /api/projects/{projectId}/build/verify"] = "export",
        ["GET /api/projects/{projectId}/load-tests/targets"] = "read or export",
        ["POST /api/projects/{projectId}/load-tests/scripts"] = "export",
        ["GET /api/projects/{projectId}/load-tests/scripts"] = "read or export",
        ["GET /api/projects/{projectId}/load-tests/scripts/{scriptId:guid}"] = "read or export",
        ["POST /api/projects/{projectId}/load-tests/results"] = "propose",
        ["GET /api/projects/{projectId}/load-tests/results"] = "read",
        ["GET /api/projects/{projectId}/load-tests/results/{resultId:guid}"] = "read",
        ["GET /api/projects/{projectId}/load-tests/compare/estimate"] = "read",
        ["GET /api/projects/{projectId}/load-tests/compare/results"] = "read",
        ["GET /api/agent/capabilities"] = "",
        ["POST /mcp/"] = "",
        ["GET /llms.txt"] = "",
        ["GET /llms-full.txt"] = "",
        ["GET /skills/vellum/SKILL.md"] = "",
    };

    /// <summary>
    /// Every mapped endpoint is of exactly one class: open with no credential (listed), for a
    /// person only (listed), open to an agent token with named scopes (listed), or for a
    /// signed-in session, which is what an endpoint is when nobody decided otherwise. The
    /// class comes from the endpoint data source, so an endpoint that is added later is
    /// classed here too: it fails this test when it is open with no credential or open to
    /// agents and no list names it. Then a real token with every scope calls every endpoint
    /// that is not open to agents and gets 403, and a token that lacks the scope of an
    /// endpoint gets 403 there.
    /// </summary>
    [Fact]
    public async Task Every_endpoint_is_of_one_class_and_an_agent_token_gets_403_wherever_it_is_not_let_in()
    {
        var (factory, owner, workspaceId, projectId) = await StartAsync();
        using var _ = factory;
        var endpoints = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .SelectMany(e => (e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["*"])
                .Select(method => (Name: $"{method} {e.RoutePattern.RawText}", Method: method, Endpoint: e)))
            .ToList();
        Assert.True(endpoints.Count > 150);
        Assert.Equal(endpoints.Count, endpoints.Select(e => e.Name).Distinct().Count());

        var anonymous = new List<string>();
        var forAPerson = new List<string>();
        var forAnAgent = new Dictionary<string, string>();
        var forASession = new List<string>();
        foreach (var (name, _, endpoint) in endpoints)
        {
            var needsNoCredential = endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null
                || endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Count == 0;
            var humanOnly = endpoint.Metadata.GetMetadata<HumanOnly>() is not null;
            var agent = endpoint.Metadata.GetMetadata<AgentAllowed>();

            // An endpoint for a person is never open to an agent, and never open with no credential.
            Assert.False(humanOnly && (agent is not null || needsNoCredential), name);
            // A scope is checked against the identity of a token, so an endpoint with a scope needs a credential.
            Assert.False(needsNoCredential && agent is { Scopes.Count: > 0 }, name);

            if (needsNoCredential)
                anonymous.Add(name);
            if (agent is not null)
                forAnAgent[name] = string.Join(" or ", agent.Scopes.Select(AgentScopes.Name));
            if (humanOnly)
                forAPerson.Add(name);
            else if (!needsNoCredential && agent is null)
                forASession.Add(name);
        }

        // A list that differs is shown whole: the names that are only on one side.
        static void Same(string what, IEnumerable<string> expected, IEnumerable<string> actual)
        {
            var (want, have) = (expected.ToHashSet(StringComparer.Ordinal), actual.ToHashSet(StringComparer.Ordinal));
            Assert.True(want.SetEquals(have),
                $"{what}\nnot in the list: {string.Join(" | ", have.Except(want).Order(StringComparer.Ordinal))}\nonly in the list: {string.Join(" | ", want.Except(have).Order(StringComparer.Ordinal))}");
        }
        Same("open with no credential", Anonymous, anonymous);
        Same("for a person only", ForAPerson, forAPerson);
        Same("open to agents", ForAnAgent.Select(e => $"{e.Key} = {e.Value}"), forAnAgent.Select(e => $"{e.Key} = {e.Value}"));

        // Everything else needs a signed-in session: the model, the views, the docs, the
        // drafts, the imports, the AI, the settings that are read, and the workspaces.
        Assert.True(forASession.Count > 80, $"{forASession.Count} of {endpoints.Count}");
        Assert.Equal(endpoints.Count, anonymous.Count + forAPerson.Count + forASession.Count + forAnAgent.Keys.Except(anonymous).Count());

        // The calls. A route value is the workspace or the project of the token, or an ID that names nothing.
        string Url(RouteEndpoint endpoint) => "/" + string.Join('/', endpoint.RoutePattern.RawText!.Trim('/').Split('/').Select(part =>
            !part.StartsWith('{') ? part
            : part.StartsWith("{*", StringComparison.Ordinal) ? "some/page"
            : part.Trim('{', '}').Split(':')[0] switch
            {
                "workspaceId" => workspaceId.ToString(),
                "projectId" => projectId.ToString(),
                "provider" or "name" or "alias" or "obligationKey" => "x",
                "index" => "0",
                _ => Guid.NewGuid().ToString(),
            }));
        async Task<HttpResponseMessage> SendAsync(HttpClient client, string method, RouteEndpoint endpoint)
        {
            var request = new HttpRequestMessage(new HttpMethod(method == "*" ? "GET" : method), Url(endpoint));
            if (request.Method != HttpMethod.Get)
                request.Content = JsonContent.Create(new { });
            return await client.SendAsync(request);
        }

        // A token with every scope, whose user is an Owner: 403 on every endpoint that is not open to agents.
        var every = await TestHost.CreateAgentAsync(factory, owner, workspaceId);
        foreach (var (name, method, endpoint) in endpoints.Where(e => !ForAnAgent.ContainsKey(e.Name)))
        {
            using var response = await SendAsync(every.Client, method, endpoint);
            Assert.True(response.StatusCode == HttpStatusCode.Forbidden, $"{name}: {(int)response.StatusCode}");
            if (method == "HEAD")
                continue;
            var refusal = (await response.Content.ReadFromJsonAsync<JsonObject>())!["type"]!.GetValue<string>();
            Assert.Equal(ForAPerson.Contains(name) ? AgentBoundary.HumanOnlyError : AgentBoundary.AgentNotAllowedError, refusal);
        }

        // A token that lacks the scope of an endpoint: 403 there, before the endpoint runs.
        var lacking = new Dictionary<string, HttpClient>();
        foreach (var scope in new[] { "read", "propose", "export" })
            lacking[scope] = (await TestHost.CreateAgentAsync(factory, owner, workspaceId, [scope])).Client;
        foreach (var (name, method, endpoint) in endpoints.Where(e => ForAnAgent.GetValueOrDefault(e.Name) is { Length: > 0 }))
        {
            var needed = ForAnAgent[name].Split(" or ");
            using var response = await SendAsync(lacking.First(token => !needed.Contains(token.Key)).Value, method, endpoint);
            Assert.True(response.StatusCode == HttpStatusCode.Forbidden, $"{name}: {(int)response.StatusCode}");
            Assert.Equal(AgentBoundary.AgentNotAllowedError, (await response.Content.ReadFromJsonAsync<JsonObject>())!["type"]!.GetValue<string>());
        }

        // And the token is let in where a list says so: no endpoint that is open to it answers 403 or 401.
        foreach (var (name, method, endpoint) in endpoints.Where(e => ForAnAgent.ContainsKey(e.Name) && e.Method == "GET"))
        {
            using var response = await SendAsync(every.Client, method, endpoint);
            Assert.True(response.StatusCode != HttpStatusCode.Forbidden && response.StatusCode != HttpStatusCode.Unauthorized, $"{name}: {(int)response.StatusCode}");
        }
    }
}
