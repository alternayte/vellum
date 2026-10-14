using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Vellum.Modules.Identity;
using Vellum.Modules.Settings.Instance;

namespace Vellum.Tests.Modules.Settings;

[Collection("Integration")]
public class IsolationAndAuditTests
{
    private readonly IntegrationFixture _fixture;

    public IsolationAndAuditTests(IntegrationFixture fixture) => _fixture = fixture;

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    /// <summary>Every settings, secrets and audit call on one workspace.</summary>
    private static Dictionary<string, Func<HttpClient, Task<HttpResponseMessage>>> Calls(Guid workspaceId)
    {
        var root = $"/api/workspaces/{workspaceId}";
        return new()
        {
            ["GET settings"] = c => c.GetAsync($"{root}/settings"),
            ["PUT settings"] = c => c.PutSettingsAsync($"{root}/settings", Json("""{ "import": { "maxFiles": 3 } }""")),
            ["GET secrets"] = c => c.GetAsync($"{root}/secrets"),
            ["PUT secret"] = c => c.PutAsJsonAsync($"{root}/secrets/github:token", new { value = "intruder-value" }),
            ["DELETE secret"] = c => c.DeleteAsync($"{root}/secrets/github:token"),
            ["GET audit"] = c => c.GetAsync($"{root}/audit"),
        };
    }

    private static async Task AssertAllAsync(HttpClient client, Guid workspaceId, HttpStatusCode expected, params string[] except)
    {
        foreach (var (name, call) in Calls(workspaceId).Where(c => !except.Contains(c.Key)))
        {
            var response = await call(client);
            Assert.True(response.StatusCode == expected, $"{name}: {(int)response.StatusCode}");
        }
    }

    private async Task<HttpClient> SignInAsInstanceAdminAsync(WebApplicationFactory<Program> factory)
    {
        var client = TestHost.Client(factory);
        var login = await client.PostAsJsonAsync("/api/auth/login", new { email = _fixture.AdminEmail, password = TestHost.AdminPassword });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.True((await client.GetFromJsonAsync<InstanceInfoResponse>("/api/instance"))!.IsInstanceAdmin);
        return client;
    }

    [Fact]
    public async Task A_member_of_another_workspace_reaches_nothing_and_changes_nothing()
    {
        using var factory = TestHost.Create(_fixture);
        using var ownerA = await TestHost.SignInAsync(factory);
        var (workspaceA, _) = await TestHost.CreateProjectAsync(ownerA);
        using var ownerB = await TestHost.SignInAsync(factory);
        await TestHost.CreateProjectAsync(ownerB);
        using var anonymous = TestHost.Client(factory);

        var saved = await ownerA.PutAsJsonAsync($"/api/workspaces/{workspaceA}/secrets/github:token", new { value = "owner-a-token-value" });
        Assert.Equal(HttpStatusCode.NoContent, saved.StatusCode);
        var before = await ownerA.GetStringAsync($"/api/workspaces/{workspaceA}/secrets");

        await AssertAllAsync(ownerB, workspaceA, HttpStatusCode.Forbidden);
        await AssertAllAsync(anonymous, workspaceA, HttpStatusCode.Unauthorized);
        // A workspace that does not exist answers like one the caller is not in.
        await AssertAllAsync(ownerB, Guid.NewGuid(), HttpStatusCode.Forbidden);

        Assert.Equal(before, await ownerA.GetStringAsync($"/api/workspaces/{workspaceA}/secrets"));
        var settings = await ownerA.GetFromJsonAsync<JsonObject>($"/api/workspaces/{workspaceA}/settings");
        Assert.Empty(settings!["overrides"]!.AsObject());
    }

    [Theory]
    [InlineData("Viewer")]
    [InlineData("Editor")]
    public async Task A_plain_member_reads_settings_and_can_do_nothing_else(string role)
    {
        using var factory = TestHost.Create(_fixture);
        using var owner = await TestHost.SignInAsync(factory);
        var (workspaceId, _) = await TestHost.CreateProjectAsync(owner);
        using var member = await TestHost.SignInMemberAsync(factory, owner, workspaceId, role);

        Assert.Equal(HttpStatusCode.OK, (await member.GetAsync($"/api/workspaces/{workspaceId}/settings")).StatusCode);
        await AssertAllAsync(member, workspaceId, HttpStatusCode.Forbidden, "GET settings");

        var settings = await owner.GetFromJsonAsync<JsonObject>($"/api/workspaces/{workspaceId}/settings");
        Assert.Empty(settings!["overrides"]!.AsObject());
    }

    [Fact]
    public async Task The_instance_admin_has_no_access_to_a_workspace_without_a_membership()
    {
        using var factory = TestHost.Create(_fixture);
        using var owner = await TestHost.SignInAsync(factory);
        var (workspaceId, projectId) = await TestHost.CreateProjectAsync(owner);
        using var admin = await SignInAsInstanceAdminAsync(factory);

        await AssertAllAsync(admin, workspaceId, HttpStatusCode.Forbidden);
        // Model content is workspace data too.
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.GetAsync($"/api/projects/{projectId}/elements")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.GetAsync($"/api/workspaces/{workspaceId}/projects")).StatusCode);

        // A membership, and only a membership, opens the workspace.
        await TestHost.AddMemberAsync(owner, admin, workspaceId, "Owner");
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"/api/workspaces/{workspaceId}/secrets")).StatusCode);
    }

    [Fact]
    public async Task Sensitive_changes_are_audited_newest_first_without_values()
    {
        using var factory = TestHost.Create(_fixture);
        using var owner = await TestHost.SignInAsync(factory);
        var (workspaceId, _) = await TestHost.CreateProjectAsync(owner);
        var ownerId = (await owner.GetFromJsonAsync<UserInfoResponse>("/api/auth/me"))!.Id;
        const string secret = "audit-secret-value-9f8e7d6c5b4a";
        const string webhook = "https://hooks.example.com/audited-path-31337";

        Assert.Equal(HttpStatusCode.OK, (await owner.PutSettingsAsync($"/api/workspaces/{workspaceId}/settings", Json($$"""{ "notifications": { "webhookUrl": "{{webhook}}" } }"""))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.PutAsJsonAsync($"/api/workspaces/{workspaceId}/secrets/github:token", new { value = secret })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/api/workspaces/{workspaceId}/secrets/github:token")).StatusCode);
        using var member = await TestHost.SignInMemberAsync(factory, owner, workspaceId, "Editor");
        var memberInfo = (await member.GetFromJsonAsync<UserInfoResponse>("/api/auth/me"))!;
        var memberId = memberInfo.Id;
        Assert.True((await owner.DeleteAsync($"/api/workspaces/{workspaceId}/members/{memberId}")).IsSuccessStatusCode);
        // Refused calls record nothing.
        Assert.Equal(HttpStatusCode.Forbidden, (await member.PutAsJsonAsync($"/api/workspaces/{workspaceId}/secrets/github:token", new { value = secret })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PutSettingsAsync($"/api/workspaces/{workspaceId}/settings", Json("""{ "import": { "maxFiles": 0 } }"""))).StatusCode);

        var body = await owner.GetStringAsync($"/api/workspaces/{workspaceId}/audit");
        var entries = JsonNode.Parse(body)!.AsArray();

        Assert.Equal(
            ["member.removed", "member.added", "member.invited", "secret.deleted", "secret.set", "settings.updated", "member.added"],
            entries.Select(e => (string)e!["action"]!).Where(a => a != "keys.rotated"));
        var workspaceEntries = entries.Where(e => (string)e!["action"]! != "keys.rotated").ToList();
        // The invitation is named by its ID; the email address is in no entry.
        var invitation = ((string)workspaceEntries[2]!["target"]!).Split(' ')[0];
        Assert.StartsWith("invitation:", invitation);
        Assert.Equal(
            [$"user:{memberId} role:editor", $"user:{memberId} role:editor {invitation}", $"{invitation} role:editor", "github:token", "github:token", "workspace-settings fields:notifications.webhookUrl=https://hooks.example.com", $"user:{ownerId} role:owner"],
            workspaceEntries.Select(e => (string)e!["target"]!));
        // The person who accepts is the actor of the membership; the owner of everything else.
        Assert.Equal(
            [ownerId, memberId, ownerId, ownerId, ownerId, ownerId, ownerId],
            workspaceEntries.Select(e => (string)e!["actorId"]!));
        Assert.All(workspaceEntries, e => Assert.Equal(["action", "actorId", "at", "target"], e.AsObject().Select(p => p.Key).Order()));
        Assert.DoesNotContain(memberInfo.Email, body);
        var times = entries.Select(e => (DateTimeOffset)e!["at"]!).ToList();
        Assert.Equal(times.OrderByDescending(t => t), times);
        Assert.DoesNotContain(secret, body);
        Assert.DoesNotContain(webhook, body);

        var limited = await owner.GetFromJsonAsync<JsonArray>($"/api/workspaces/{workspaceId}/audit?limit=2");
        Assert.Equal(2, limited!.Count);
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync($"/api/workspaces/{workspaceId}/audit?limit=100000")).StatusCode);

        // The log is append-only in the database.
        await using var connection = new NpgsqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        foreach (var sql in new[]
                 {
                     "UPDATE settings.audit_entries SET action = 'x' WHERE workspace_id = @w",
                     "DELETE FROM settings.audit_entries WHERE workspace_id = @w",
                     "TRUNCATE settings.audit_entries",
                     "TRUNCATE settings.audit_entries RESTART IDENTITY CASCADE",
                 })
        {
            await using var command = new NpgsqlCommand(sql, connection);
            if (sql.Contains("@w"))
                command.Parameters.AddWithValue("w", workspaceId);
            var refused = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
            Assert.Contains("append-only", refused.MessageText);
        }
    }
}
