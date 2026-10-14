using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Vellum.Modules.Settings.Instance;
using Vellum.Modules.Settings.Secrets;
using Vellum.Shared;

namespace Vellum.Tests.Modules.Settings;

public class SecretsTests : IClassFixture<IntegrationFixture>
{
    private const string Github = "https://api.github.com";

    private readonly IntegrationFixture _fixture;

    public SecretsTests(IntegrationFixture fixture) => _fixture = fixture;

    private static string NewValue() => $"sk-test-{Guid.NewGuid():N}{Guid.NewGuid():N}";

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    private static async Task SetAsync(HttpClient client, Guid workspaceId, string name, string value)
    {
        var response = await client.PutAsJsonAsync($"/api/workspaces/{workspaceId}/secrets/{name}", new { value });
        Assert.True(response.StatusCode == HttpStatusCode.NoContent, await response.Content.ReadAsStringAsync());
    }

    private static async Task SaveSettingsAsync(HttpClient client, Guid workspaceId, string overrides)
    {
        var response = await client.PutSettingsAsync($"/api/workspaces/{workspaceId}/settings", Json(overrides));
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    private static string ProviderSettings(Guid providerId, string baseUrl = "https://llm.example.com/v1") => $$"""
        { "ai": { "providers": [ { "id": "{{providerId}}", "kind": "openai-compatible", "name": "P", "model": "m", "baseUrl": "{{baseUrl}}" } ] } }
        """;

    /// <summary>The internal read path that later steps use to call GitHub and AI providers.</summary>
    private static async Task<SecretRead> ReadAsync(WebApplicationFactory<Program> factory, Guid workspaceId, string name, string destination)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<SecretReader>().ReadAsync(workspaceId, name, new Uri(destination));
    }

    private static async Task<string> ValueAsync(WebApplicationFactory<Program> factory, Guid workspaceId, string name, string destination) =>
        Assert.IsType<SecretRead.Found>(await ReadAsync(factory, workspaceId, name, destination)).Value;

    private static async Task<Dictionary<string, JsonNode>> ListAsync(HttpClient client, Guid workspaceId) =>
        (await client.GetFromJsonAsync<JsonArray>($"/api/workspaces/{workspaceId}/secrets"))!
            .ToDictionary(s => (string)s!["name"]!, s => s!);

    private static async Task<int> ExecuteAsync(string connectionString, string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value);
        return await command.ExecuteNonQueryAsync();
    }

    private static async Task<object?> ScalarAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return await command.ExecuteScalarAsync();
    }

    [Fact]
    public async Task A_secret_round_trips_through_the_reader_and_no_response_holds_its_value()
    {
        var factory = _fixture.Factory;
        using var owner = await TestHost.SignInAsync(factory);
        var (workspaceId, _) = await TestHost.CreateProjectAsync(owner);
        var providerId = Guid.NewGuid();
        var token = NewValue();
        var apiKey = NewValue();
        var bodies = new List<string>();

        async Task<string> BodyAsync(Task<HttpResponseMessage> request)
        {
            var response = await request;
            var body = await response.Content.ReadAsStringAsync();
            bodies.Add(body + string.Join("\n", response.Headers.Select(h => $"{h.Key}: {string.Join(",", h.Value)}")));
            Assert.True(response.IsSuccessStatusCode, body);
            return body;
        }

        Assert.IsType<SecretRead.NotSet>(await ReadAsync(factory, workspaceId, SecretNames.GithubToken, Github));

        await SaveSettingsAsync(owner, workspaceId, ProviderSettings(providerId));
        await BodyAsync(owner.PutAsJsonAsync($"/api/workspaces/{workspaceId}/secrets/github:token", new { value = token }));
        await BodyAsync(owner.PutAsJsonAsync($"/api/workspaces/{workspaceId}/secrets/ai:{providerId}:apiKey", new { value = apiKey }));

        // The path of the destination does not matter; its origin does.
        Assert.Equal(token, await ValueAsync(factory, workspaceId, SecretNames.GithubToken, Github + "/repos/a/b"));
        Assert.Equal(apiKey, await ValueAsync(factory, workspaceId, SecretNames.AiApiKey(providerId), "https://llm.example.com/v1/chat"));
        Assert.IsType<SecretRead.NotSet>(await ReadAsync(factory, workspaceId, SecretNames.WebhookSecret, "https://hooks.example.com"));
        Assert.IsType<SecretRead.NotSet>(await ReadAsync(factory, Guid.NewGuid(), SecretNames.GithubToken, Github));

        var list = JsonNode.Parse(await BodyAsync(owner.GetAsync($"/api/workspaces/{workspaceId}/secrets")))!.AsArray();
        var byName = list.ToDictionary(s => (string)s!["name"]!, s => s!);
        Assert.Equal(3, byName.Count);
        Assert.True((bool)byName["github:token"]["isSet"]!);
        Assert.Matches("^[0-9a-f]{8}$", (string)byName["github:token"]["fingerprint"]!);
        Assert.Equal(Github, (string)byName["github:token"]["boundOrigin"]!);
        Assert.True((bool)byName["github:token"]["matchesCurrentUrl"]!);
        Assert.NotNull(byName["github:token"]["updatedAt"]);
        Assert.False(string.IsNullOrEmpty((string)byName["github:token"]["updatedBy"]!));
        Assert.Equal("https://llm.example.com", (string)byName[$"ai:{providerId}:apiKey"]["boundOrigin"]!);
        Assert.False((bool)byName["notifications:webhookSecret"]["isSet"]!);
        Assert.Null(byName["notifications:webhookSecret"]["fingerprint"]);
        Assert.Null(byName["notifications:webhookSecret"]["boundOrigin"]);
        Assert.False((bool)byName["notifications:webhookSecret"]["matchesCurrentUrl"]!);
        // A status has these seven fields and no other.
        Assert.All(list, s => Assert.Equal(
            ["boundOrigin", "fingerprint", "isSet", "matchesCurrentUrl", "name", "updatedAt", "updatedBy"],
            s!.AsObject().Select(p => p.Key).Order()));

        // A new value replaces the old one and changes the fingerprint.
        var before = (string)byName["github:token"]["fingerprint"]!;
        var rotated = NewValue();
        await BodyAsync(owner.PutAsJsonAsync($"/api/workspaces/{workspaceId}/secrets/github:token", new { value = rotated }));
        Assert.Equal(rotated, await ValueAsync(factory, workspaceId, SecretNames.GithubToken, Github));
        var after = JsonNode.Parse(await BodyAsync(owner.GetAsync($"/api/workspaces/{workspaceId}/secrets")))!.AsArray();
        Assert.NotEqual(before, (string)after.Single(s => (string)s!["name"]! == "github:token")!["fingerprint"]!);

        await BodyAsync(owner.GetAsync($"/api/workspaces/{workspaceId}/settings"));
        await BodyAsync(owner.GetAsync($"/api/workspaces/{workspaceId}/audit"));

        // A refused request does not echo the value either.
        var refused = await owner.PutAsJsonAsync($"/api/workspaces/{workspaceId}/secrets/aws:key", new { value = token });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        bodies.Add(await refused.Content.ReadAsStringAsync());

        await BodyAsync(owner.DeleteAsync($"/api/workspaces/{workspaceId}/secrets/github:token"));
        Assert.IsType<SecretRead.NotSet>(await ReadAsync(factory, workspaceId, SecretNames.GithubToken, Github));
        var deleted = JsonNode.Parse(await BodyAsync(owner.GetAsync($"/api/workspaces/{workspaceId}/secrets")))!.AsArray();
        Assert.False((bool)deleted.Single(s => (string)s!["name"]! == "github:token")!["isSet"]!);
        // Deleting again is not an error.
        await BodyAsync(owner.DeleteAsync($"/api/workspaces/{workspaceId}/secrets/github:token"));

        Assert.All(bodies, body =>
        {
            Assert.DoesNotContain(token, body);
            Assert.DoesNotContain(apiKey, body);
            Assert.DoesNotContain(rotated, body);
        });

        // The database holds no value either, in any text form.
        await using var connection = new NpgsqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT s::text FROM settings.secrets s WHERE workspace_id = @w UNION ALL SELECT a::text FROM settings.audit_entries a WHERE workspace_id = @w", connection);
        command.Parameters.AddWithValue("w", workspaceId);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var row = reader.GetString(0);
            Assert.DoesNotContain(apiKey, row);
            Assert.DoesNotContain(Convert.ToHexStringLower(Encoding.UTF8.GetBytes(apiKey)), row);
        }
    }

    [Fact]
    public async Task A_fingerprint_differs_between_workspaces_and_is_not_a_plain_hash_of_the_value()
    {
        var factory = _fixture.Factory;
        using var owner = await TestHost.SignInAsync(factory);
        var (workspaceA, _) = await TestHost.CreateProjectAsync(owner);
        var (workspaceB, _) = await TestHost.CreateProjectAsync(owner);
        var value = NewValue();
        await SetAsync(owner, workspaceA, "github:token", value);
        await SetAsync(owner, workspaceB, "github:token", value);

        var a = (string)(await ListAsync(owner, workspaceA))["github:token"]["fingerprint"]!;
        var b = (string)(await ListAsync(owner, workspaceB))["github:token"]["fingerprint"]!;

        // The same value in two workspaces: a reader of both rows cannot tell that they are equal.
        Assert.NotEqual(a, b);
        // A reader of the database cannot test a guess of the value against the fingerprint.
        var plain = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..8];
        Assert.NotEqual(plain, a);
        Assert.NotEqual(plain, b);
        // The same value saved again in one workspace keeps its fingerprint, so a user can compare.
        await SetAsync(owner, workspaceA, "github:token", value);
        Assert.Equal(a, (string)(await ListAsync(owner, workspaceA))["github:token"]["fingerprint"]!);
    }

    [Fact]
    public async Task A_credential_does_not_follow_a_changed_url()
    {
        var factory = _fixture.Factory;
        using var owner = await TestHost.SignInAsync(factory);
        var (workspaceId, _) = await TestHost.CreateProjectAsync(owner);
        var providerId = Guid.NewGuid();
        var token = NewValue();
        var apiKey = NewValue();
        await SaveSettingsAsync(owner, workspaceId, ProviderSettings(providerId));
        await SetAsync(owner, workspaceId, "github:token", token);
        await SetAsync(owner, workspaceId, $"ai:{providerId}:apiKey", apiKey);

        // An owner points both URLs at another host. The secrets stay, bound to the old hosts.
        await SaveSettingsAsync(owner, workspaceId, $$"""
            { "github": { "apiBaseUrl": "https://evil.example.net/api/v3" },
              "ai": { "providers": [ { "id": "{{providerId}}", "kind": "openai-compatible", "name": "P", "model": "m", "baseUrl": "https://evil.example.net/v1" } ] } }
            """);

        var github = Assert.IsType<SecretRead.SavedForAnotherHost>(
            await ReadAsync(factory, workspaceId, SecretNames.GithubToken, "https://evil.example.net/api/v3/user"));
        Assert.Equal(Github, github.BoundOrigin);
        Assert.Equal("https://evil.example.net", github.RequestedOrigin);
        Assert.IsType<SecretRead.SavedForAnotherHost>(
            await ReadAsync(factory, workspaceId, SecretNames.AiApiKey(providerId), "https://evil.example.net/v1/chat"));
        // Scheme and port are part of the origin.
        Assert.IsType<SecretRead.SavedForAnotherHost>(await ReadAsync(factory, workspaceId, SecretNames.GithubToken, "http://api.github.com"));
        Assert.IsType<SecretRead.SavedForAnotherHost>(await ReadAsync(factory, workspaceId, SecretNames.GithubToken, "https://api.github.com:8443"));

        var list = await ListAsync(owner, workspaceId);
        Assert.True((bool)list["github:token"]["isSet"]!);
        Assert.Equal(Github, (string)list["github:token"]["boundOrigin"]!);
        Assert.False((bool)list["github:token"]["matchesCurrentUrl"]!);
        Assert.False((bool)list[$"ai:{providerId}:apiKey"]["matchesCurrentUrl"]!);

        // Entering the value again binds it to the URL that the settings name now.
        await SetAsync(owner, workspaceId, "github:token", token);
        Assert.Equal(token, await ValueAsync(factory, workspaceId, SecretNames.GithubToken, "https://evil.example.net/api/v3/user"));
        Assert.True((bool)(await ListAsync(owner, workspaceId))["github:token"]["matchesCurrentUrl"]!);

        // The audit entry of the settings change names the fields and the new origins, and no path or value.
        var audit = await owner.GetStringAsync($"/api/workspaces/{workspaceId}/audit");
        Assert.Contains("github.apiBaseUrl=https://evil.example.net", audit);
        Assert.Contains("ai.providers[0].baseUrl=https://evil.example.net", audit);
        Assert.DoesNotContain("/api/v3", audit);
    }

    [Fact]
    public async Task A_database_writer_cannot_redirect_a_credential_or_forge_its_fingerprint()
    {
        var factory = _fixture.Factory;
        using var owner = await TestHost.SignInAsync(factory);
        var (workspaceId, _) = await TestHost.CreateProjectAsync(owner);
        var value = NewValue();
        await SetAsync(owner, workspaceId, "github:token", value);

        Assert.Equal(1, await ExecuteAsync(_fixture.ConnectionString,
            "UPDATE settings.secrets SET bound_origin = 'https://evil.example.net' WHERE workspace_id = @w", ("w", workspaceId)));

        // The stored origin is part of what the master key authenticates.
        await Assert.ThrowsAsync<SecretUnavailableException>(() => ReadAsync(factory, workspaceId, SecretNames.GithubToken, "https://evil.example.net"));
        Assert.IsType<SecretRead.SavedForAnotherHost>(await ReadAsync(factory, workspaceId, SecretNames.GithubToken, Github));
        var redirected = (await ListAsync(owner, workspaceId))["github:token"];
        Assert.Null(redirected["boundOrigin"]);
        Assert.Null(redirected["fingerprint"]);
        Assert.False((bool)redirected["matchesCurrentUrl"]!);

        await SetAsync(owner, workspaceId, "github:token", value);
        Assert.Equal(1, await ExecuteAsync(_fixture.ConnectionString,
            "UPDATE settings.secrets SET fingerprint = 'deadbeef' WHERE workspace_id = @w", ("w", workspaceId)));

        var forged = (await ListAsync(owner, workspaceId))["github:token"];
        Assert.Null(forged["fingerprint"]);
        Assert.False((bool)forged["matchesCurrentUrl"]!);
        await Assert.ThrowsAsync<SecretUnavailableException>(() => ReadAsync(factory, workspaceId, SecretNames.GithubToken, Github));
    }

    [Fact]
    public async Task An_api_key_needs_its_provider_and_leaves_with_it()
    {
        var factory = _fixture.Factory;
        using var owner = await TestHost.SignInAsync(factory);
        var (workspaceId, _) = await TestHost.CreateProjectAsync(owner);
        var kept = Guid.NewGuid();
        var removed = Guid.NewGuid();

        var early = await owner.PutAsJsonAsync($"/api/workspaces/{workspaceId}/secrets/ai:{kept}:apiKey", new { value = "v" });
        Assert.Equal(HttpStatusCode.BadRequest, early.StatusCode);
        Assert.Contains((await early.Content.ReadFromJsonAsync<ErrorResponse>())!.Errors!, e => e.Field == "name");

        // A webhook secret needs the webhook URL that it is bound to.
        var noUrl = await owner.PutAsJsonAsync($"/api/workspaces/{workspaceId}/secrets/notifications:webhookSecret", new { value = "v" });
        Assert.Equal(HttpStatusCode.BadRequest, noUrl.StatusCode);

        await SaveSettingsAsync(owner, workspaceId, $$"""
            { "ai": { "providers": [
                { "id": "{{kept}}", "kind": "anthropic", "name": "A", "model": "m" },
                { "id": "{{removed}}", "kind": "openai", "name": "O", "model": "m" } ] },
              "notifications": { "webhookUrl": "https://hooks.example.com/vellum" } }
            """);
        await SetAsync(owner, workspaceId, $"ai:{kept}:apiKey", "kept-key-value");
        await SetAsync(owner, workspaceId, $"ai:{removed}:apiKey", "removed-key-value");
        await SetAsync(owner, workspaceId, "notifications:webhookSecret", "hook-secret-value");

        // A provider with no base URL of its own is bound to the address of its kind.
        var list = await ListAsync(owner, workspaceId);
        Assert.Equal("https://api.anthropic.com", (string)list[$"ai:{kept}:apiKey"]["boundOrigin"]!);
        Assert.Equal("https://api.openai.com", (string)list[$"ai:{removed}:apiKey"]["boundOrigin"]!);
        Assert.Equal("https://hooks.example.com", (string)list["notifications:webhookSecret"]["boundOrigin"]!);

        await SaveSettingsAsync(owner, workspaceId, $$"""
            { "ai": { "providers": [ { "id": "{{kept}}", "kind": "anthropic", "name": "A", "model": "m" } ] } }
            """);

        list = await ListAsync(owner, workspaceId);
        Assert.True((bool)list[$"ai:{kept}:apiKey"]["isSet"]!);
        Assert.DoesNotContain($"ai:{removed}:apiKey", list.Keys);
        // Removing a URL does not delete its secret.
        Assert.True((bool)list["notifications:webhookSecret"]["isSet"]!);
        Assert.False((bool)list["notifications:webhookSecret"]["matchesCurrentUrl"]!);

        var audit = (await owner.GetFromJsonAsync<JsonArray>($"/api/workspaces/{workspaceId}/audit"))!;
        Assert.Contains(audit, e => (string)e!["action"]! == "secret.deleted" && (string)e["target"]! == $"ai:{removed}:apiKey");
    }

    [Theory]
    [InlineData("aws:key")]
    [InlineData("github:Token")]
    [InlineData("github")]
    [InlineData("ai:not-a-guid:apiKey")]
    [InlineData("ai:00000000-0000-0000-0000-000000000000:apiKey")]
    [InlineData("ai:11111111-1111-1111-1111-111111111111:apikey")]
    [InlineData("ai:11111111-1111-1111-1111-111111111111:apiKey:extra")]
    [InlineData("notifications:webhookSecret2")]
    public async Task A_secret_name_that_is_not_allowed_is_refused(string name)
    {
        var factory = _fixture.Factory;
        using var owner = await TestHost.SignInAsync(factory);
        var (workspaceId, _) = await TestHost.CreateProjectAsync(owner);
        var url = $"/api/workspaces/{workspaceId}/secrets/{name}";

        var set = await owner.PutAsJsonAsync(url, new { value = "value" });
        var delete = await owner.DeleteAsync(url);

        Assert.Equal(HttpStatusCode.BadRequest, set.StatusCode);
        Assert.Contains((await set.Content.ReadFromJsonAsync<ErrorResponse>())!.Errors!, e => e.Field == "name");
        Assert.Equal(HttpStatusCode.BadRequest, delete.StatusCode);
    }

    [Fact]
    public async Task A_secret_value_has_a_bounded_length()
    {
        var factory = _fixture.Factory;
        using var owner = await TestHost.SignInAsync(factory);
        var (workspaceId, _) = await TestHost.CreateProjectAsync(owner);
        var url = $"/api/workspaces/{workspaceId}/secrets/github:token";

        foreach (var body in new object[] { new { value = "" }, new { value = new string('x', 4097) }, new { value = (string?)null }, new { } })
        {
            var response = await owner.PutAsJsonAsync(url, body);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains((await response.Content.ReadFromJsonAsync<ErrorResponse>())!.Errors!, e => e.Field == "value");
        }

        Assert.Equal(HttpStatusCode.NoContent, (await owner.PutAsJsonAsync(url, new { value = new string('x', 4096) })).StatusCode);
    }

    [Fact]
    public async Task A_row_moved_to_another_workspace_or_another_name_does_not_decrypt()
    {
        var factory = _fixture.Factory;
        using var owner = await TestHost.SignInAsync(factory);
        var (workspaceA, _) = await TestHost.CreateProjectAsync(owner);
        var (workspaceB, _) = await TestHost.CreateProjectAsync(owner);
        var value = NewValue();
        await SetAsync(owner, workspaceA, "github:token", value);

        // An attacker with database access copies workspace A's row into workspace B.
        Assert.Equal(1, await ExecuteAsync(_fixture.ConnectionString, """
            INSERT INTO settings.secrets (workspace_id, name, master_key_id, wrap_nonce, wrapped_key, nonce, ciphertext, fingerprint, bound_origin, updated_by)
            SELECT @b, name, master_key_id, wrap_nonce, wrapped_key, nonce, ciphertext, fingerprint, bound_origin, updated_by
            FROM settings.secrets WHERE workspace_id = @a AND name = 'github:token'
            """, ("a", workspaceA), ("b", workspaceB)));

        var copied = await Assert.ThrowsAsync<SecretUnavailableException>(() => ReadAsync(factory, workspaceB, SecretNames.GithubToken, Github));
        Assert.DoesNotContain(value, copied.ToString());
        Assert.Equal(value, await ValueAsync(factory, workspaceA, SecretNames.GithubToken, Github));

        // The same row under another name in its own workspace.
        Assert.Equal(1, await ExecuteAsync(_fixture.ConnectionString,
            "UPDATE settings.secrets SET name = 'notifications:webhookSecret' WHERE workspace_id = @a AND name = 'github:token'",
            ("a", workspaceA)));

        await Assert.ThrowsAsync<SecretUnavailableException>(() => ReadAsync(factory, workspaceA, SecretNames.WebhookSecret, Github));

        // A changed ciphertext or a changed wrapped key fails the same way.
        await SetAsync(owner, workspaceA, "github:token", value);
        await ExecuteAsync(_fixture.ConnectionString,
            "UPDATE settings.secrets SET ciphertext = set_byte(ciphertext, 0, get_byte(ciphertext, 0) # 1) WHERE workspace_id = @a AND name = 'github:token'",
            ("a", workspaceA));
        await Assert.ThrowsAsync<SecretUnavailableException>(() => ReadAsync(factory, workspaceA, SecretNames.GithubToken, Github));
    }

    private static Dictionary<string, string?> Keys(string current, string? previous = null) =>
        new() { ["Vellum:MasterKey"] = current, ["Vellum:PreviousMasterKey"] = previous };

    private static async Task<HttpClient> LoginAsync(WebApplicationFactory<Program> host, string email)
    {
        var client = TestHost.Client(host);
        var login = await client.PostAsJsonAsync("/api/auth/login", new { email, password = TestHost.AdminPassword });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        return client;
    }

    private static async Task<RotateKeysResponse> RotateAsync(HttpClient admin)
    {
        var response = await admin.PostAsync("/api/instance/keys/rotate", null);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<RotateKeysResponse>())!;
    }

    [Fact]
    public async Task Rotation_moves_every_key_to_the_new_master_key_and_reports_what_remains()
    {
        var database = await TestHost.CreateDatabaseAsync();
        var oldKey = TestHost.NewMasterKey();
        var newKey = TestHost.NewMasterKey();
        var value = NewValue();
        Guid workspaceId;
        string email;
        string fingerprintBefore;

        using (var first = TestHost.Create(database, Keys(oldKey)))
        {
            var (admin, created, adminEmail) = await TestHost.SetUpAsync(first);
            (workspaceId, email) = (created.WorkspaceId, adminEmail);
            await SaveSettingsAsync(admin, workspaceId, """{ "notifications": { "webhookUrl": "https://hooks.example.com/v" } }""");
            await SetAsync(admin, workspaceId, "github:token", value);
            await SetAsync(admin, workspaceId, "notifications:webhookSecret", "hook-" + value);
            fingerprintBefore = (string)(await ListAsync(admin, workspaceId))["github:token"]["fingerprint"]!;
        }

        using (var rotating = TestHost.Create(database, Keys(newKey, previous: oldKey)))
        {
            // The previous key reads; the secret is usable before rotation.
            Assert.Equal(value, await ValueAsync(rotating, workspaceId, SecretNames.GithubToken, Github));

            using var admin = await LoginAsync(rotating, email);
            var diagnostics = await admin.GetFromJsonAsync<JsonObject>("/api/instance/diagnostics");
            Assert.True((bool)diagnostics!["previousMasterKeyLoaded"]!);
            // Before rotation every row is under the old key, and diagnostics say so.
            var inUse = Assert.Single(diagnostics["masterKeysInUse"]!.AsArray())!;
            Assert.Equal(2, (long)inUse["secrets"]!);
            Assert.True((long)inUse["dataProtectionKeys"]! >= 1);
            Assert.False((bool)inUse["isCurrent"]!);
            Assert.True((bool)inUse["isLoaded"]!);

            // Two secrets, and the Data Protection keys written under the old master key.
            var rotated = await RotateAsync(admin);
            Assert.True(rotated.Rewrapped >= 3);
            Assert.Equal(0, rotated.Remaining);
            Assert.Contains("remove the previous master key", rotated.Message);

            var again = await RotateAsync(admin);
            Assert.Equal(0, again.Rewrapped);
            Assert.Equal(0, again.Remaining);

            var after = await admin.GetFromJsonAsync<JsonObject>("/api/instance/diagnostics");
            var only = Assert.Single(after!["masterKeysInUse"]!.AsArray())!;
            Assert.True((bool)only["isCurrent"]!);
            Assert.Equal((string)after["masterKeyId"]!, (string)only["masterKeyId"]!);

            // The fingerprint is keyed by the master key, so rotation computes it again.
            var fingerprintAfter = (string)(await ListAsync(admin, workspaceId))["github:token"]["fingerprint"]!;
            Assert.Matches("^[0-9a-f]{8}$", fingerprintAfter);
            Assert.NotEqual(fingerprintBefore, fingerprintAfter);
            Assert.True((bool)(await ListAsync(admin, workspaceId))["github:token"]["matchesCurrentUrl"]!);

            // A workspace owner sees that a rotation happened, and not who ran it or with which key.
            var audit = (await admin.GetFromJsonAsync<JsonArray>($"/api/workspaces/{workspaceId}/audit"))!;
            var entry = audit.First(e => (string)e!["action"]! == "keys.rotated")!;
            Assert.Equal("instance-admin", (string)entry["actorId"]!);
            Assert.Equal("master-key", (string)entry["target"]!);
            var masterKeyId = (string)after["masterKeyId"]!;
            Assert.DoesNotContain(masterKeyId, audit.ToJsonString());
            // The instance admin sees the detail in the instance log.
            var instanceAudit = await admin.GetStringAsync("/api/instance/audit");
            Assert.Contains($"master-key:{masterKeyId} rewrapped:{rotated.Rewrapped} remaining:0", instanceAudit);
            Assert.DoesNotContain(oldKey, instanceAudit);
            Assert.DoesNotContain(newKey, instanceAudit);
        }

        // The previous key is removed: everything still decrypts with the new key alone.
        using (var after = TestHost.Create(database, Keys(newKey)))
        {
            Assert.Equal(value, await ValueAsync(after, workspaceId, SecretNames.GithubToken, Github));
            Assert.Equal("hook-" + value, await ValueAsync(after, workspaceId, SecretNames.WebhookSecret, "https://hooks.example.com"));
            Assert.Equal(0L, await ScalarAsync(database, "SELECT count(*) FROM settings.data_protection_keys WHERE xml NOT LIKE '%vellumEnvelope%'"));
            // The session that the old key signed is still valid: its signing key was moved too.
            using var admin = await LoginAsync(after, email);
            Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/instance/settings")).StatusCode);
        }
    }

    [Fact]
    public async Task A_secret_saved_while_rotation_runs_is_not_corrupted()
    {
        var database = await TestHost.CreateDatabaseAsync();
        var oldKey = TestHost.NewMasterKey();
        var newKey = TestHost.NewMasterKey();
        Guid workspaceId;
        string email;

        // A rolling restart: this instance still runs with the old key as its current key.
        using var oldInstance = TestHost.Create(database, Keys(oldKey));
        var (oldAdmin, created, adminEmail) = await TestHost.SetUpAsync(oldInstance);
        (workspaceId, email) = (created.WorkspaceId, adminEmail);

        // The row that a save from the old instance will write: capture it, then put an
        // older value back, so the test can commit that save at an exact moment.
        await SetAsync(oldAdmin, workspaceId, "github:token", "value-saved-during-rotation");
        await using var writer = new NpgsqlConnection(database);
        await writer.OpenAsync();
        var saved = new object[5];
        await using (var read = new NpgsqlCommand(
            "SELECT master_key_id, wrap_nonce, wrapped_key, nonce, ciphertext, fingerprint FROM settings.secrets WHERE workspace_id = @w", writer))
        {
            read.Parameters.AddWithValue("w", workspaceId);
            await using var row = await read.ExecuteReaderAsync();
            Assert.True(await row.ReadAsync());
            saved = [row.GetString(0), row.GetFieldValue<byte[]>(1), row.GetFieldValue<byte[]>(2), row.GetFieldValue<byte[]>(3), row.GetFieldValue<byte[]>(4), row.GetString(5)];
        }
        await SetAsync(oldAdmin, workspaceId, "github:token", "value-before-rotation");

        using var newInstance = TestHost.Create(database, Keys(newKey, previous: oldKey));
        using var admin = await LoginAsync(newInstance, email);

        // The save holds the row. Rotation starts and has to wait for it.
        await using var transaction = await writer.BeginTransactionAsync();
        await using (var hold = new NpgsqlCommand("SELECT 1 FROM settings.secrets WHERE workspace_id = @w FOR UPDATE", writer, transaction))
        {
            hold.Parameters.AddWithValue("w", workspaceId);
            await hold.ExecuteScalarAsync();
        }

        var rotation = RotateAsync(admin);
        for (var attempt = 0; ; attempt++)
        {
            var waiting = (long)(await ScalarAsync(database,
                "SELECT count(*) FROM pg_stat_activity WHERE datname = current_database() AND wait_event_type = 'Lock'"))!;
            if (waiting > 0)
                break;
            Assert.False(rotation.IsCompleted, "Rotation ended without waiting for the row that a save holds.");
            Assert.True(attempt < 200, "Rotation never reached the held row.");
            await Task.Delay(50);
        }

        // The save commits its new ciphertext and its own wrapped key while rotation waits.
        await using (var write = new NpgsqlCommand("""
            UPDATE settings.secrets
            SET master_key_id = @k, wrap_nonce = @wn, wrapped_key = @wk, nonce = @n, ciphertext = @c, fingerprint = @f
            WHERE workspace_id = @w
            """, writer, transaction))
        {
            write.Parameters.AddWithValue("k", saved[0]);
            write.Parameters.AddWithValue("wn", saved[1]);
            write.Parameters.AddWithValue("wk", saved[2]);
            write.Parameters.AddWithValue("n", saved[3]);
            write.Parameters.AddWithValue("c", saved[4]);
            write.Parameters.AddWithValue("f", saved[5]);
            write.Parameters.AddWithValue("w", workspaceId);
            Assert.Equal(1, await write.ExecuteNonQueryAsync());
        }
        await transaction.CommitAsync();

        var result = await rotation;
        Assert.Equal(0, result.Remaining);
        // Rotation moved the row that the save wrote, with that row's own data key.
        Assert.Equal("value-saved-during-rotation", await ValueAsync(newInstance, workspaceId, SecretNames.GithubToken, Github));

        // The old instance saves once more after the rotation. The count that remains says so,
        // and one more rotation clears it.
        await SetAsync(oldAdmin, workspaceId, "github:token", "value-after-rotation");
        var diagnostics = await admin.GetFromJsonAsync<JsonObject>("/api/instance/diagnostics");
        Assert.Contains(diagnostics!["masterKeysInUse"]!.AsArray(), k => !(bool)k!["isCurrent"]! && (long)k["secrets"]! == 1);
        var second = await RotateAsync(admin);
        Assert.True(second.Rewrapped >= 1);
        Assert.Equal(0, second.Remaining);

        using var afterRotation = TestHost.Create(database, Keys(newKey));
        Assert.Equal("value-after-rotation", await ValueAsync(afterRotation, workspaceId, SecretNames.GithubToken, Github));
    }
}
