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

[Collection("Integration")]
public class SecretsTests
{
    private readonly IntegrationFixture _fixture;

    public SecretsTests(IntegrationFixture fixture) => _fixture = fixture;

    private static string NewValue() => $"sk-test-{Guid.NewGuid():N}{Guid.NewGuid():N}";

    private static string Fingerprint(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..8];

    private static async Task SetAsync(HttpClient client, Guid workspaceId, string name, string value)
    {
        var response = await client.PutAsJsonAsync($"/api/workspaces/{workspaceId}/secrets/{name}", new { value });
        Assert.True(response.StatusCode == HttpStatusCode.NoContent, await response.Content.ReadAsStringAsync());
    }

    /// <summary>The internal read path that later steps use to call GitHub and AI providers.</summary>
    private static async Task<string?> ReadAsync(WebApplicationFactory<Program> factory, Guid workspaceId, string name)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<SecretReader>().GetAsync(workspaceId, name);
    }

    private static async Task<int> ExecuteAsync(string connectionString, string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value);
        return await command.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task A_secret_round_trips_through_the_reader_and_no_response_holds_its_value()
    {
        using var factory = TestHost.Create(_fixture);
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

        Assert.Null(await ReadAsync(factory, workspaceId, SecretNames.GithubToken));

        await BodyAsync(owner.PutAsJsonAsync($"/api/workspaces/{workspaceId}/secrets/github:token", new { value = token }));
        await BodyAsync(owner.PutAsJsonAsync($"/api/workspaces/{workspaceId}/secrets/ai:{providerId}:apiKey", new { value = apiKey }));

        Assert.Equal(token, await ReadAsync(factory, workspaceId, SecretNames.GithubToken));
        Assert.Equal(apiKey, await ReadAsync(factory, workspaceId, SecretNames.AiApiKey(providerId)));
        Assert.Null(await ReadAsync(factory, workspaceId, SecretNames.WebhookSecret));
        Assert.Null(await ReadAsync(factory, Guid.NewGuid(), SecretNames.GithubToken));

        var list = JsonNode.Parse(await BodyAsync(owner.GetAsync($"/api/workspaces/{workspaceId}/secrets")))!.AsArray();
        var byName = list.ToDictionary(s => (string)s!["name"]!, s => s!);
        Assert.Equal(3, byName.Count);
        Assert.True((bool)byName["github:token"]["isSet"]!);
        Assert.Equal(Fingerprint(token), (string)byName["github:token"]["fingerprint"]!);
        Assert.NotNull(byName["github:token"]["updatedAt"]);
        Assert.False(string.IsNullOrEmpty((string)byName["github:token"]["updatedBy"]!));
        Assert.Equal(Fingerprint(apiKey), (string)byName[$"ai:{providerId}:apiKey"]["fingerprint"]!);
        Assert.False((bool)byName["notifications:webhookSecret"]["isSet"]!);
        Assert.Null(byName["notifications:webhookSecret"]["fingerprint"]);
        // A status has these five fields and no other.
        Assert.All(list, s => Assert.Equal(["fingerprint", "isSet", "name", "updatedAt", "updatedBy"], s!.AsObject().Select(p => p.Key).Order()));

        // A new value replaces the old one and changes the fingerprint.
        var rotated = NewValue();
        await BodyAsync(owner.PutAsJsonAsync($"/api/workspaces/{workspaceId}/secrets/github:token", new { value = rotated }));
        Assert.Equal(rotated, await ReadAsync(factory, workspaceId, SecretNames.GithubToken));
        var after = JsonNode.Parse(await BodyAsync(owner.GetAsync($"/api/workspaces/{workspaceId}/secrets")))!.AsArray();
        Assert.Contains(after, s => (string?)s!["fingerprint"] == Fingerprint(rotated));

        await BodyAsync(owner.GetAsync($"/api/workspaces/{workspaceId}/settings"));
        await BodyAsync(owner.GetAsync($"/api/workspaces/{workspaceId}/audit"));

        // A refused request does not echo the value either.
        var refused = await owner.PutAsJsonAsync($"/api/workspaces/{workspaceId}/secrets/aws:key", new { value = token });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        bodies.Add(await refused.Content.ReadAsStringAsync());

        await BodyAsync(owner.DeleteAsync($"/api/workspaces/{workspaceId}/secrets/github:token"));
        Assert.Null(await ReadAsync(factory, workspaceId, SecretNames.GithubToken));
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
        using var factory = TestHost.Create(_fixture);
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
        using var factory = TestHost.Create(_fixture);
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
        using var factory = TestHost.Create(_fixture);
        using var owner = await TestHost.SignInAsync(factory);
        var (workspaceA, _) = await TestHost.CreateProjectAsync(owner);
        var (workspaceB, _) = await TestHost.CreateProjectAsync(owner);
        var value = NewValue();
        await SetAsync(owner, workspaceA, "github:token", value);

        // An attacker with database access copies workspace A's row into workspace B.
        Assert.Equal(1, await ExecuteAsync(_fixture.ConnectionString, """
            INSERT INTO settings.secrets (workspace_id, name, master_key_id, wrap_nonce, wrapped_key, nonce, ciphertext, fingerprint, updated_by)
            SELECT @b, name, master_key_id, wrap_nonce, wrapped_key, nonce, ciphertext, fingerprint, updated_by
            FROM settings.secrets WHERE workspace_id = @a AND name = 'github:token'
            """, ("a", workspaceA), ("b", workspaceB)));

        var copied = await Assert.ThrowsAsync<SecretUnavailableException>(() => ReadAsync(factory, workspaceB, SecretNames.GithubToken));
        Assert.DoesNotContain(value, copied.ToString());
        Assert.Equal(value, await ReadAsync(factory, workspaceA, SecretNames.GithubToken));

        // The same row under another name in its own workspace.
        Assert.Equal(1, await ExecuteAsync(_fixture.ConnectionString,
            "UPDATE settings.secrets SET name = 'notifications:webhookSecret' WHERE workspace_id = @a AND name = 'github:token'",
            ("a", workspaceA)));

        await Assert.ThrowsAsync<SecretUnavailableException>(() => ReadAsync(factory, workspaceA, SecretNames.WebhookSecret));

        // A changed ciphertext or a changed wrapped key fails the same way.
        await SetAsync(owner, workspaceA, "github:token", value);
        await ExecuteAsync(_fixture.ConnectionString,
            "UPDATE settings.secrets SET ciphertext = set_byte(ciphertext, 0, get_byte(ciphertext, 0) # 1) WHERE workspace_id = @a AND name = 'github:token'",
            ("a", workspaceA));
        await Assert.ThrowsAsync<SecretUnavailableException>(() => ReadAsync(factory, workspaceA, SecretNames.GithubToken));
    }

    [Fact]
    public async Task Rotation_moves_every_key_to_the_new_master_key_and_a_wrong_master_key_fails_closed()
    {
        var database = await TestHost.CreateDatabaseAsync(_fixture);
        var oldKey = TestHost.NewMasterKey();
        var newKey = TestHost.NewMasterKey();
        var value = NewValue();
        Guid workspaceId;
        string email;

        static Dictionary<string, string?> Keys(string current, string? previous = null) =>
            new() { ["Vellum:MasterKey"] = current, ["Vellum:PreviousMasterKey"] = previous };

        async Task<HttpClient> LoginAsync(WebApplicationFactory<Program> host)
        {
            var client = TestHost.Client(host);
            var login = await client.PostAsJsonAsync("/api/auth/login", new { email, password = TestHost.AdminPassword });
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
            return client;
        }

        using (var first = TestHost.Create(database, Keys(oldKey)))
        {
            var (admin, created, adminEmail) = await TestHost.SetUpAsync(first);
            (workspaceId, email) = (created.WorkspaceId, adminEmail);
            await SetAsync(admin, workspaceId, "github:token", value);
            await SetAsync(admin, workspaceId, "notifications:webhookSecret", "hook-" + value);
        }

        // The new key alone cannot read what the old key wrapped, and rotation says why.
        using (var tooEarly = TestHost.Create(database, Keys(newKey)))
        {
            var failure = await Assert.ThrowsAsync<SecretUnavailableException>(() => ReadAsync(tooEarly, workspaceId, SecretNames.GithubToken));
            Assert.DoesNotContain(value, failure.ToString());

            using var admin = await LoginAsync(tooEarly);
            var refused = await admin.PostAsync("/api/instance/keys/rotate", null);
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            var error = (await refused.Content.ReadFromJsonAsync<ErrorResponse>())!;
            Assert.Equal("key_unavailable", error.Type);
            Assert.Contains("Vellum__PreviousMasterKey", error.Detail);
        }

        using (var rotating = TestHost.Create(database, Keys(newKey, previous: oldKey)))
        {
            // The previous key reads; the secret is usable before rotation.
            Assert.Equal(value, await ReadAsync(rotating, workspaceId, SecretNames.GithubToken));

            using var admin = await LoginAsync(rotating);
            var diagnostics = await admin.GetFromJsonAsync<JsonObject>("/api/instance/diagnostics");
            Assert.True((bool)diagnostics!["previousMasterKeyLoaded"]!);

            // Two secrets, and the Data Protection keys written under the old master key.
            var rotated = await admin.PostAsync("/api/instance/keys/rotate", null);
            Assert.Equal(HttpStatusCode.OK, rotated.StatusCode);
            Assert.True((await rotated.Content.ReadFromJsonAsync<RotateKeysResponse>())!.Rewrapped >= 3);

            var again = await admin.PostAsync("/api/instance/keys/rotate", null);
            Assert.Equal(0, (await again.Content.ReadFromJsonAsync<RotateKeysResponse>())!.Rewrapped);

            var audit = await admin.GetStringAsync($"/api/workspaces/{workspaceId}/audit");
            Assert.Contains("keys.rotated", audit);
            Assert.DoesNotContain(oldKey, audit);
            Assert.DoesNotContain(newKey, audit);
        }

        Assert.Equal(0L, await ScalarAsync(database,
            "SELECT count(*) FROM settings.secrets s WHERE s.master_key_id <> (SELECT master_key_id FROM settings.secrets WHERE name = 'github:token' LIMIT 1)"));

        // The previous key is removed: everything still decrypts with the new key alone.
        using (var after = TestHost.Create(database, Keys(newKey)))
        {
            Assert.Equal(value, await ReadAsync(after, workspaceId, SecretNames.GithubToken));
            Assert.Equal("hook-" + value, await ReadAsync(after, workspaceId, SecretNames.WebhookSecret));
            Assert.Equal(0L, await ScalarAsync(database, "SELECT count(*) FROM settings.data_protection_keys WHERE xml NOT LIKE '%vellumEnvelope%'"));
        }

        // The old key no longer reads, and neither does an unrelated key.
        foreach (var wrong in new[] { oldKey, TestHost.NewMasterKey() })
        {
            using var host = TestHost.Create(database, Keys(wrong));
            await Assert.ThrowsAsync<SecretUnavailableException>(() => ReadAsync(host, workspaceId, SecretNames.GithubToken));
        }
    }

    private static async Task<object?> ScalarAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return await command.ExecuteScalarAsync();
    }
}
