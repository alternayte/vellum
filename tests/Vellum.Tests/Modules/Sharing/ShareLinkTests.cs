using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vellum.Modules.Identity;
using Vellum.Modules.Sharing;
using Vellum.Shared;

namespace Vellum.Tests.Modules.Sharing;

/// <summary>
/// Share links: what a visitor with no account can read, and when a link answers 404. Each
/// test reads the page as a client with no cookie, as a browser that is signed out does.
/// </summary>
public class ShareLinkTests(IntegrationFixture fixture) : IClassFixture<IntegrationFixture>
{
    private HttpClient Anonymous() => fixture.Factory.CreateClient(new() { HandleCookies = false, AllowAutoRedirect = false });

    private static string Links(Guid projectId) => $"/api/projects/{projectId}/share-links";

    private static async Task<JsonObject> Json(HttpResponseMessage response, HttpStatusCode expected)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{response.StatusCode}: {body}");
        return JsonNode.Parse(body)!.AsObject();
    }

    private static async Task<(JsonObject Link, string Token)> CreateLinkAsync(HttpClient client, Guid projectId, object request)
    {
        var created = await Json(await client.PostAsJsonAsync(Links(projectId), request), HttpStatusCode.Created);
        return (created["link"]!.AsObject(), created["token"]!.GetValue<string>());
    }

    private static object ProjectLink(string? password = null, bool costs = false, bool promises = false) =>
        new { scope = "project", mode = "live", showCosts = costs, showPromises = promises, password };

    private static async Task<Guid> AddElementAsync(HttpClient client, Guid projectId, string name, string kind = "system")
    {
        var id = Guid.NewGuid();
        await TestHost.Created(client.PostAsJsonAsync($"/api/projects/{projectId}/elements", new { id, kind, name }));
        return id;
    }

    private static async Task SaveSharingAsync(HttpClient owner, Guid workspaceId, object sharing)
    {
        var response = await owner.PutSettingsAsync($"/api/workspaces/{workspaceId}/settings",
            new StringContent(new JsonObject { ["sharing"] = JsonSerializerNode(sharing) }.ToJsonString(), Encoding.UTF8, "application/json"));
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
    }

    private static JsonNode JsonSerializerNode(object value) => System.Text.Json.JsonSerializer.SerializeToNode(value, System.Text.Json.JsonSerializerOptions.Web)!;

    [Fact]
    public async Task The_page_of_a_link_holds_no_source_text_comment_member_setting_draft_or_unconfirmed_inference()
    {
        var owner = await TestHost.SignInAsync(fixture.Factory);
        var (workspaceId, projectId) = await TestHost.CreateProjectAsync(owner);
        var me = (await owner.GetFromJsonAsync<UserInfoResponse>("/api/auth/me"))!;

        // A second member, whose name and address must not reach a visitor.
        var member = await TestHost.SignInMemberAsync(fixture.Factory, owner, workspaceId, "Editor");
        var memberInfo = (await member.GetFromJsonAsync<UserInfoResponse>("/api/auth/me"))!;

        var api = await AddElementAsync(owner, projectId, "Orders API");
        var observed = await Json(await owner.PatchAsJsonAsync($"/api/projects/{projectId}/elements/{api}", new
        {
            factStatus = "observed-in-source",
            provenance = new { source = "github.com/acme-secret/orders@deadbeefcafe:src/Api/Orders.cs:41-57", revision = "deadbeefcafe", confidence = (double?)null },
            setProvenance = true,
        }), HttpStatusCode.OK);
        Assert.Equal("observed-in-source", observed["factStatus"]!.GetValue<string>());

        var guess = await AddElementAsync(owner, projectId, "Guessed Cache");
        await Json(await owner.PatchAsJsonAsync($"/api/projects/{projectId}/elements/{guess}", new
        {
            factStatus = "inferred",
            provenance = new { source = "ai:openai/model-x#flow-proposal:1", revision = (string?)null, confidence = 0.4 },
            setProvenance = true,
        }), HttpStatusCode.OK);
        var guessChild = Guid.NewGuid();
        await TestHost.Created(owner.PostAsJsonAsync($"/api/projects/{projectId}/elements",
            new { id = guessChild, kind = "app", name = "Child of a guess", parentId = guess }));

        await TestHost.Created(owner.PostAsJsonAsync($"/api/projects/{projectId}/docs",
            new { id = Guid.NewGuid(), title = "Main runbook", content = "Shared doc body" }));
        var draft = Guid.NewGuid();
        await TestHost.Created(owner.PostAsJsonAsync($"/api/projects/{projectId}/drafts", new { id = draft, name = "Unmerged idea" }));
        await TestHost.Created(owner.PostAsJsonAsync($"/api/projects/{projectId}/docs",
            new { id = Guid.NewGuid(), title = "Draft-only page", draftId = draft, content = "draft secret" }));
        await TestHost.Created(owner.PostAsJsonAsync($"/api/projects/{projectId}/threads",
            new { target = new { kind = "project", id = projectId }, body = "internal comment text" }));
        await TestHost.Created(owner.PostAsJsonAsync($"/api/projects/{projectId}/threads",
            new { target = new { kind = "project", id = projectId }, body = "draft comment text", draftId = draft }));

        var settings = await owner.PutSettingsAsync($"/api/workspaces/{workspaceId}/settings",
            new StringContent("""{"github":{"defaultRef":"settings-marker"}}""", Encoding.UTF8, "application/json"));
        Assert.True(settings.IsSuccessStatusCode, await settings.Content.ReadAsStringAsync());

        var (_, token) = await CreateLinkAsync(owner, projectId, ProjectLink(costs: true, promises: true));
        using var visitor = Anonymous();
        var response = await visitor.GetAsync($"/api/share/{token}");
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // What a visitor sees: the confirmed model, its labels and a citation as path:lines.
        Assert.Contains("Orders API", body);
        Assert.Contains("\"citation\":\"src/Api/Orders.cs:41-57\"", body);
        Assert.Contains("observed-in-source", body);
        Assert.Contains("Shared doc body", body);

        // What a visitor never sees, anywhere in the answer.
        foreach (var hidden in new[]
                 {
                     "acme-secret", "deadbeefcafe", "github.com", "ai:openai", "flow-proposal",
                     "Guessed Cache", "Child of a guess", guess.ToString(), guessChild.ToString(),
                     "internal comment text", "draft comment text", "Draft-only page", "draft secret", "Unmerged idea",
                     "settings-marker", me.Email, me.Id, memberInfo.Email, memberInfo.Id, memberInfo.DisplayName!,
                     workspaceId.ToString(),
                 })
        {
            Assert.DoesNotContain(hidden, body, StringComparison.OrdinalIgnoreCase);
        }

        // The top level of the page is exactly the parts the share shows.
        var page = JsonNode.Parse(body)!.AsObject();
        Assert.Equal(
            ["asOf", "costs", "decisions", "docs", "flows", "mode", "model", "projectName", "promises", "revision", "schemas", "scope", "views"],
            page.Select(p => p.Key).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(
            ["citation", "description", "icon", "id", "kind", "name", "parentId", "stale", "status", "tags", "technology", "truthLabel"],
            page["model"]!["elements"]![0]!.AsObject().Select(p => p.Key).Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task A_revoked_expired_or_policy_blocked_link_answers_404()
    {
        var owner = await TestHost.SignInAsync(fixture.Factory);
        var (workspaceId, projectId) = await TestHost.CreateProjectAsync(owner);
        await AddElementAsync(owner, projectId, "Web");
        using var visitor = Anonymous();

        var (revoked, revokedToken) = await CreateLinkAsync(owner, projectId, ProjectLink());
        Assert.Equal(HttpStatusCode.OK, (await visitor.GetAsync($"/api/share/{revokedToken}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.PostAsync($"{Links(projectId)}/{revoked["id"]}/revoke", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync($"/api/share/{revokedToken}")).StatusCode);

        var (expired, expiredToken) = await CreateLinkAsync(owner, projectId,
            new { scope = "project", mode = "live", expiresAt = DateTimeOffset.UtcNow.AddHours(1) });
        Assert.Equal(HttpStatusCode.OK, (await visitor.GetAsync($"/api/share/{expiredToken}")).StatusCode);
        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var id = expired["id"]!.GetValue<Guid>();
            await scope.ServiceProvider.GetRequiredService<AppDbContext>().ShareLinks.Where(l => l.Id == id)
                .ExecuteUpdateAsync(s => s.SetProperty(l => l.ExpiresAt, DateTimeOffset.UtcNow.AddSeconds(-1)));
        }
        Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync($"/api/share/{expiredToken}")).StatusCode);

        // A public link under a policy of password-only answers 404; it does not ask for a password it never had.
        var (_, publicToken) = await CreateLinkAsync(owner, projectId, ProjectLink());
        var (_, protectedToken) = await CreateLinkAsync(owner, projectId, ProjectLink(password: "correct horse"));
        await SaveSharingAsync(owner, workspaceId, new { linkPolicy = "password-only" });
        Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync($"/api/share/{publicToken}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await visitor.GetAsync($"/api/share/{protectedToken}")).StatusCode);
        var refused = await owner.PostAsJsonAsync(Links(projectId), ProjectLink());
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);

        await SaveSharingAsync(owner, workspaceId, new { linkPolicy = "off" });
        Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync($"/api/share/{protectedToken}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await visitor.PostAsJsonAsync($"/api/share/{protectedToken}/unlock", new { password = "correct horse" })).StatusCode);

        // The policy applies to links that exist: allowing public links again opens the link again.
        await SaveSharingAsync(owner, workspaceId, new { linkPolicy = "public" });
        Assert.Equal(HttpStatusCode.OK, (await visitor.GetAsync($"/api/share/{publicToken}")).StatusCode);

        // Each 404 is the same answer as a token that never existed.
        Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync($"/api/share/{ShareToken.New()}")).StatusCode);

        var list = (await owner.GetFromJsonAsync<JsonArray>(Links(projectId)))!;
        Assert.Contains(list, l => l!["status"]!.GetValue<string>() == "revoked");
        Assert.Contains(list, l => l!["status"]!.GetValue<string>() == "expired");
        var audit = (await owner.GetFromJsonAsync<JsonArray>($"/api/workspaces/{workspaceId}/audit"))!;
        Assert.Contains(audit, a => a!["action"]!.GetValue<string>() == ShareAudit.Created);
        Assert.Contains(audit, a => a!["action"]!.GetValue<string>() == ShareAudit.Revoked);
        Assert.DoesNotContain(audit, a => a!["target"]!.GetValue<string>().Contains(revokedToken, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_wrong_password_is_limited_for_each_link_and_a_grant_opens_the_link()
    {
        var owner = await TestHost.SignInAsync(fixture.Factory);
        var (_, projectId) = await TestHost.CreateProjectAsync(owner);
        await AddElementAsync(owner, projectId, "Web");
        var (link, token) = await CreateLinkAsync(owner, projectId, ProjectLink(password: "correct horse"));
        using var visitor = Anonymous();

        var prompt = await Json(await visitor.GetAsync($"/api/share/{token}"), HttpStatusCode.Unauthorized);
        Assert.Equal("password_required", prompt["type"]!.GetValue<string>());
        Assert.DoesNotContain("Web", prompt.ToJsonString());

        var unlocked = await Json(await visitor.PostAsJsonAsync($"/api/share/{token}/unlock", new { password = "correct horse" }), HttpStatusCode.OK);
        var grant = unlocked["grant"]!.GetValue<string>();
        using (var read = new HttpRequestMessage(HttpMethod.Get, $"/api/share/{token}"))
        {
            read.Headers.Add(ShareGrants.Header, grant);
            Assert.Equal(HttpStatusCode.OK, (await visitor.SendAsync(read)).StatusCode);
        }

        for (var i = 0; i < SharePasswordLimit.MaxFailures; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await visitor.PostAsJsonAsync($"/api/share/{token}/unlock", new { password = $"guess {i}" })).StatusCode);

        // The window is full: even the right password waits, so guessing cannot go on.
        var limited = await visitor.PostAsJsonAsync($"/api/share/{token}/unlock", new { password = "correct horse" });
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.NotNull(limited.Headers.RetryAfter);

        // A new password ends every grant of the old one.
        await Json(await owner.PatchAsJsonAsync($"{Links(projectId)}/{link["id"]}", new { password = "battery staple" }), HttpStatusCode.OK);
        using (var stale = new HttpRequestMessage(HttpMethod.Get, $"/api/share/{token}"))
        {
            stale.Headers.Add(ShareGrants.Header, grant);
            Assert.Equal(HttpStatusCode.Unauthorized, (await visitor.SendAsync(stale)).StatusCode);
        }
    }

    [Fact]
    public async Task A_pinned_page_stays_as_it_was_while_main_changes_and_a_live_page_follows_main()
    {
        var owner = await TestHost.SignInAsync(fixture.Factory);
        var (_, projectId) = await TestHost.CreateProjectAsync(owner);
        var web = await AddElementAsync(owner, projectId, "Web");
        var revision = Guid.NewGuid();
        await TestHost.Created(owner.PostAsJsonAsync($"/api/projects/{projectId}/revisions", new { id = revision, name = "v1" }));

        var (pinnedLink, pinned) = await CreateLinkAsync(owner, projectId,
            new { scope = "project", mode = "pinned", revisionId = revision, showCosts = false, showPromises = false });
        Assert.Equal("v1", pinnedLink["revisionName"]!.GetValue<string>());
        var (_, live) = await CreateLinkAsync(owner, projectId, ProjectLink());
        using var visitor = Anonymous();
        var before = await visitor.GetStringAsync($"/api/share/{pinned}");

        await Json(await owner.PatchAsJsonAsync($"/api/projects/{projectId}/elements/{web}", new { name = "Web Shop" }), HttpStatusCode.OK);
        await AddElementAsync(owner, projectId, "Payments");
        await TestHost.Created(owner.PostAsJsonAsync($"/api/projects/{projectId}/docs", new { id = Guid.NewGuid(), title = "Later page" }));
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/api/projects/{projectId}/revisions/{revision}")).StatusCode);

        Assert.Equal(before, await visitor.GetStringAsync($"/api/share/{pinned}"));
        var page = JsonNode.Parse(before)!;
        Assert.Equal("pinned", page["mode"]!.GetValue<string>());
        Assert.Equal("v1", page["revision"]!["name"]!.GetValue<string>());

        var now = await visitor.GetStringAsync($"/api/share/{live}");
        Assert.Contains("Web Shop", now);
        Assert.Contains("Payments", now);
        Assert.Contains("Later page", now);
        Assert.DoesNotContain("Web Shop", before);
    }

    [Fact]
    public async Task Only_the_embed_page_of_a_view_link_relaxes_frame_ancestors_and_only_to_the_allow_list()
    {
        var owner = await TestHost.SignInAsync(fixture.Factory);
        var (workspaceId, projectId) = await TestHost.CreateProjectAsync(owner);
        var view = Guid.NewGuid();
        await TestHost.Created(owner.PostAsJsonAsync($"/api/projects/{projectId}/views", new { id = view, name = "Context" }));
        var (_, viewToken) = await CreateLinkAsync(owner, projectId, new { scope = "view", viewId = view, mode = "live" });
        var (_, projectToken) = await CreateLinkAsync(owner, projectId, ProjectLink());
        using var visitor = Anonymous();

        static async Task<string> FrameAncestors(HttpClient client, string path)
        {
            var response = await client.GetAsync(path);
            var csp = response.Headers.TryGetValues("Content-Security-Policy", out var values) ? string.Join(";", values) : "";
            var directive = csp.Split(';', StringSplitOptions.TrimEntries).Single(d => d.StartsWith("frame-ancestors", StringComparison.Ordinal));
            if (directive != "frame-ancestors 'none'")
                Assert.False(response.Headers.Contains("X-Frame-Options"));
            else
                Assert.Equal("DENY", string.Join(",", response.Headers.GetValues("X-Frame-Options")));
            return directive;
        }

        // The allow-list starts empty: no site may frame anything.
        Assert.Equal("frame-ancestors 'none'", await FrameAncestors(visitor, $"/embed/{viewToken}"));

        var invalid = await owner.PutSettingsAsync($"/api/workspaces/{workspaceId}/settings", new StringContent(
            """{"sharing":{"embedHosts":["https://wiki.example.com; script-src *"]}}""", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        await SaveSharingAsync(owner, workspaceId, new { embedHosts = new[] { "https://wiki.example.com", "https://*.atlassian.net" } });

        Assert.Equal("frame-ancestors https://wiki.example.com https://*.atlassian.net", await FrameAncestors(visitor, $"/embed/{viewToken}"));
        Assert.Equal("frame-ancestors 'none'", await FrameAncestors(visitor, $"/embed/{projectToken}"));
        Assert.Equal("frame-ancestors 'none'", await FrameAncestors(visitor, $"/embed/{ShareToken.New()}"));
        Assert.Equal("frame-ancestors 'none'", await FrameAncestors(visitor, $"/embed/{viewToken}/x"));
        Assert.Equal("frame-ancestors 'none'", await FrameAncestors(visitor, $"/s/{viewToken}"));
        Assert.Equal("frame-ancestors 'none'", await FrameAncestors(visitor, $"/api/share/{viewToken}"));
        Assert.Equal("frame-ancestors 'none'", await FrameAncestors(visitor, "/"));
        Assert.Equal("frame-ancestors 'none'", await FrameAncestors(owner, $"/api/projects/{projectId}/share-links"));

        // The view page shows the one view and no project parts.
        var page = JsonNode.Parse(await visitor.GetStringAsync($"/api/share/{viewToken}"))!;
        Assert.Equal("view", page["scope"]!.GetValue<string>());
        Assert.Equal(view, Assert.Single(page["views"]!.AsArray())!["id"]!.GetValue<Guid>());
    }

    [Fact]
    public async Task A_view_link_shows_only_what_the_view_places_and_no_promise()
    {
        var owner = await TestHost.SignInAsync(fixture.Factory);
        var (_, projectId) = await TestHost.CreateProjectAsync(owner);
        var shop = await AddElementAsync(owner, projectId, "Shop");
        var ledger = await AddElementAsync(owner, projectId, "Secret Ledger");
        var view = Guid.NewGuid();
        await TestHost.Created(owner.PostAsJsonAsync($"/api/projects/{projectId}/views", new { id = view, name = "Shop only" }));
        var layout = await owner.PutAsJsonAsync($"/api/projects/{projectId}/views/{view}/layout",
            new { positions = new[] { new { elementId = shop, x = 0.0, y = 0.0 } } });
        Assert.True(layout.IsSuccessStatusCode, await layout.Content.ReadAsStringAsync());

        foreach (var mode in new[] { "live", "pinned" })
        {
            Guid? revision = null;
            if (mode == "pinned")
            {
                revision = Guid.NewGuid();
                await TestHost.Created(owner.PostAsJsonAsync($"/api/projects/{projectId}/revisions", new { id = revision, name = "r1" }));
            }
            var (_, token) = await CreateLinkAsync(owner, projectId,
                new { scope = "view", viewId = view, mode, revisionId = revision, showCosts = true, showPromises = true });
            using var visitor = Anonymous();
            var body = await visitor.GetStringAsync($"/api/share/{token}");
            Assert.Contains("Shop", body);
            Assert.DoesNotContain("Secret Ledger", body);
            Assert.DoesNotContain(ledger.ToString(), body);
            Assert.Null(JsonNode.Parse(body)!["promises"]);
        }
    }

    [Fact]
    public void A_flow_that_names_a_hidden_fact_is_not_shared_and_a_shared_flow_carries_no_payload()
    {
        var (caller, operation) = (Guid.NewGuid(), Guid.NewGuid());
        var payload = System.Text.Json.JsonDocument.Parse("""{"Authorization":"Bearer sample-secret"}""").RootElement;
        var scenario = new Vellum.Modules.Trace.Engine.Scenario(
            new Vellum.Modules.Modelling.ScenarioId(Guid.NewGuid()), "Checkout",
            [new Vellum.Modules.Trace.Engine.CallStep("s1", new Vellum.Modules.Modelling.ElementId(caller),
                new Vellum.Modules.Modelling.OperationId(operation), payload, payload, [], [])],
            [], []);

        Assert.Null(SharedFlow.From(scenario, new HashSet<Guid> { caller }));

        var shared = SharedFlow.From(scenario, new HashSet<Guid> { caller, operation });
        Assert.NotNull(shared);
        var json = System.Text.Json.JsonSerializer.Serialize(shared, System.Text.Json.JsonSerializerOptions.Web);
        Assert.DoesNotContain("sample-secret", json);
        Assert.Contains(operation.ToString(), json);
    }

    [Fact]
    public async Task A_hosted_workspace_refuses_a_public_link_by_default()
    {
        var database = await TestHost.CreateDatabaseAsync();
        await using var factory = TestHost.Create(database, TestHost.Hosted(TestHost.NewMasterKey()));
        var (owner, created, _) = await TestHost.SetUpAsync(factory, TestHost.SetupToken, https: true);
        var projectId = Guid.NewGuid();
        await TestHost.Created(owner.PostAsJsonAsync($"/api/workspaces/{created.WorkspaceId}/projects", new { id = projectId, name = "Hosted" }));

        var settings = (await owner.GetFromJsonAsync<JsonObject>($"/api/workspaces/{created.WorkspaceId}/settings"))!;
        Assert.Equal("password-only", settings["effective"]!["sharing"]!["linkPolicy"]!.GetValue<string>());

        var refused = await Json(await owner.PostAsJsonAsync(Links(projectId), ProjectLink()), HttpStatusCode.BadRequest);
        Assert.Equal("password", refused["errors"]![0]!["field"]!.GetValue<string>());
        var (link, _) = await CreateLinkAsync(owner, projectId, ProjectLink(password: "correct horse"));
        var removed = await owner.PatchAsJsonAsync($"{Links(projectId)}/{link["id"]}", new { removePassword = true });
        Assert.Equal(HttpStatusCode.BadRequest, removed.StatusCode);

        // A hosted allow-list takes https origins only.
        var http = await owner.PutSettingsAsync($"/api/workspaces/{created.WorkspaceId}/settings", new StringContent(
            """{"sharing":{"embedHosts":["http://wiki.example.com"]}}""", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, http.StatusCode);
    }

    [Fact]
    public async Task Links_stay_inside_their_workspace_and_follow_the_roles()
    {
        var owner = await TestHost.SignInAsync(fixture.Factory);
        var (workspaceId, projectId) = await TestHost.CreateProjectAsync(owner);
        var stranger = await TestHost.SignInAsync(fixture.Factory);
        var (_, strangerProject) = await TestHost.CreateProjectAsync(stranger);
        var strangerView = Guid.NewGuid();
        await TestHost.Created(stranger.PostAsJsonAsync($"/api/projects/{strangerProject}/views", new { id = strangerView, name = "Theirs" }));

        var (link, _) = await CreateLinkAsync(owner, projectId, ProjectLink());
        var linkId = link["id"]!.GetValue<Guid>();

        // Another tenant can neither list, make, change nor revoke links of this project.
        Assert.Equal(HttpStatusCode.Forbidden, (await stranger.GetAsync(Links(projectId))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await stranger.PostAsJsonAsync(Links(projectId), ProjectLink())).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await stranger.PostAsync($"{Links(projectId)}/{linkId}/revoke", null)).StatusCode);
        // Through their own project the link is not found either.
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.PostAsync($"{Links(strangerProject)}/{linkId}/revoke", null)).StatusCode);
        // A view of another project cannot be shared through this one.
        Assert.Equal(HttpStatusCode.BadRequest,
            (await owner.PostAsJsonAsync(Links(projectId), new { scope = "view", viewId = strangerView, mode = "live" })).StatusCode);

        // A Viewer cannot make a link; an Editor makes one and revokes only their own; an Owner revokes any.
        var viewer = await TestHost.SignInMemberAsync(fixture.Factory, owner, workspaceId, "Viewer");
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsJsonAsync(Links(projectId), ProjectLink())).StatusCode);
        var editor = await TestHost.SignInMemberAsync(fixture.Factory, owner, workspaceId, "Editor");
        var (editorLink, _) = await CreateLinkAsync(editor, projectId, ProjectLink());
        Assert.Equal(HttpStatusCode.Forbidden, (await editor.PostAsync($"{Links(projectId)}/{linkId}/revoke", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.PostAsync($"{Links(projectId)}/{editorLink["id"]}/revoke", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.PostAsync($"{Links(projectId)}/{linkId}/revoke", null)).StatusCode);
    }
}
