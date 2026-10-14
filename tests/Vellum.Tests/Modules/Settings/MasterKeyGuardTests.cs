using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Vellum.Modules.Settings;
using Vellum.Modules.Settings.Crypto;

namespace Vellum.Tests.Modules.Settings;

/// <summary>An instance never runs with a master key that cannot read what the database holds.</summary>
public class MasterKeyGuardTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"vellum-keyguard-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private static BootstrapException StartFails(WebApplicationFactory<Program> factory)
    {
        var thrown = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
        for (var ex = thrown; ex is not null; ex = ex.InnerException)
        {
            if (ex is BootstrapException bootstrap)
                return bootstrap;
        }

        throw new Xunit.Sdk.XunitException($"The host failed for another reason: {thrown}");
    }

    private static async Task<long> CountAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    /// <summary>An instance that ran with <paramref name="settings"/>: one user, one stored credential.</summary>
    private static async Task<string> SeedAsync(string database, Dictionary<string, string?> settings)
    {
        using var first = TestHost.Create(database, settings);
        var https = settings.GetValueOrDefault("Vellum:Profile") == "hosted";
        var (admin, created, _) = await TestHost.SetUpAsync(first, settings.GetValueOrDefault("Vellum:SetupToken"), https);
        var saved = await admin.PutAsJsonAsync($"/api/workspaces/{created.WorkspaceId}/secrets/github:token", new { value = "a-stored-token" });
        Assert.Equal(HttpStatusCode.NoContent, saved.StatusCode);
        var diagnostics = await admin.GetFromJsonAsync<JsonObject>("/api/instance/diagnostics");
        return (string)diagnostics!["masterKeyId"]!;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_start_with_another_master_key_is_refused_and_changes_nothing(bool hosted)
    {
        var database = await TestHost.CreateDatabaseAsync();
        Dictionary<string, string?> Settings(string key) =>
            hosted ? TestHost.Hosted(key) : new() { ["Vellum:MasterKey"] = key };
        var originalKey = TestHost.NewMasterKey();
        var keyId = await SeedAsync(database, Settings(originalKey));
        var sessionKeys = await CountAsync(database, "SELECT count(*) FROM settings.data_protection_keys");

        using (var wrong = TestHost.Create(database, Settings(TestHost.NewMasterKey())))
        {
            var message = StartFails(wrong).Message;
            // The message names the missing key, the rows under it and both ways out.
            Assert.Contains($"key {keyId} (1 stored credential, {sessionKeys} session key", message);
            Assert.Contains("Vellum__PreviousMasterKey", message);
            Assert.Contains("keys forget --yes", message);
            Assert.DoesNotContain(originalKey, message);
        }

        // The refused instance issued no session key and wrote no row.
        Assert.Equal(sessionKeys, await CountAsync(database, "SELECT count(*) FROM settings.data_protection_keys"));
        Assert.Equal(1, await CountAsync(database, "SELECT count(*) FROM settings.secrets"));

        // The missing key as the previous key is the fix.
        var withPrevious = Settings(TestHost.NewMasterKey());
        withPrevious["Vellum:PreviousMasterKey"] = originalKey;
        using var rotating = TestHost.Create(database, withPrevious);
        using var client = TestHost.Client(rotating, hosted);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/setup/status")).StatusCode);
    }

    [Fact]
    public async Task A_local_instance_that_lost_its_key_file_does_not_create_a_new_key()
    {
        var database = await TestHost.CreateDatabaseAsync();
        var first = Path.Combine(_directory, "first");
        var second = Path.Combine(_directory, "second");
        var keyId = await SeedAsync(database, new() { ["Vellum:DataDirectory"] = first });
        Assert.True(File.Exists(Path.Combine(first, Bootstrap.MasterKeyFileName)));

        // The data volume is gone: the same database, an empty data directory.
        using (var lost = TestHost.Create(database, new Dictionary<string, string?> { ["Vellum:DataDirectory"] = second }))
        {
            var message = StartFails(lost).Message;
            Assert.Contains(Path.Combine(second, Bootstrap.MasterKeyFileName), message);
            Assert.Contains($"key {keyId} (1 stored credential", message);
            Assert.Contains("keys forget --yes", message);
        }

        Assert.False(File.Exists(Path.Combine(second, Bootstrap.MasterKeyFileName)));

        // The key file back in place is the fix.
        using var restored = TestHost.Create(database, new Dictionary<string, string?> { ["Vellum:DataDirectory"] = first });
        using var client = TestHost.Client(restored);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/setup/status")).StatusCode);
    }

    [Fact]
    public async Task A_key_file_on_disk_never_takes_over_from_the_environment_key_without_notice()
    {
        var database = await TestHost.CreateDatabaseAsync();
        // The instance ran with a generated key file first.
        var fileKeyId = await SeedAsync(database, new() { ["Vellum:DataDirectory"] = _directory });
        var fileKey = File.ReadAllText(Path.Combine(_directory, Bootstrap.MasterKeyFileName)).Trim();

        // The self-hosted rotation: a new key from the environment, the file's key as the previous key.
        var newKey = TestHost.NewMasterKey();
        var rotatingSettings = new Dictionary<string, string?>
        {
            ["Vellum:DataDirectory"] = _directory, ["Vellum:MasterKey"] = newKey, ["Vellum:PreviousMasterKey"] = fileKey,
        };
        using (var rotating = TestHost.Create(database, rotatingSettings))
        {
            var bootstrap = rotating.Services.GetRequiredService<Bootstrap>();
            // The environment key is used and the file is reported as not used.
            Assert.NotEqual(fileKeyId, bootstrap.Keys.Current.Id);
            Assert.Equal(Path.Combine(_directory, Bootstrap.MasterKeyFileName), bootstrap.UnusedKeyFile);
            Assert.True(bootstrap.Keys.IsLoaded(fileKeyId));
        }

        // The operator removes the environment key by mistake. The old file is still on disk.
        // Rows written under the environment key would be unreadable, so the start is refused
        // when such rows exist; here the rows are still under the file's key, so it starts.
        using (var back = TestHost.Create(database, new Dictionary<string, string?> { ["Vellum:DataDirectory"] = _directory }))
        {
            using var client = TestHost.Client(back);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/setup/status")).StatusCode);
        }

        // After rows exist under the environment key, the file's key alone is refused.
        await using (var connection = new NpgsqlConnection(database))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand("UPDATE settings.secrets SET master_key_id = @id", connection);
            command.Parameters.AddWithValue("id", new MasterKey(Convert.FromBase64String(newKey)).Id);
            await command.ExecuteNonQueryAsync();
        }

        using var fileOnly = TestHost.Create(database, new Dictionary<string, string?> { ["Vellum:DataDirectory"] = _directory });
        Assert.Contains("is not loaded", StartFails(fileOnly).Message);
    }

    [Fact]
    public async Task Keys_forget_deletes_the_rows_of_a_lost_key_only_when_it_is_confirmed()
    {
        var database = await TestHost.CreateDatabaseAsync();
        var lostKeyId = await SeedAsync(database, new() { ["Vellum:MasterKey"] = TestHost.NewMasterKey() });
        var newKey = TestHost.NewMasterKey();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] = database,
            ["Vellum:MasterKey"] = newKey,
            ["Vellum:DataDirectory"] = _directory,
        }).Build();

        var output = new StringWriter();
        Assert.Equal(2, await KeysCommand.RunAsync(["keys", "drop"], configuration, output));
        Assert.Equal(1, await KeysCommand.RunAsync(["keys", "forget"], configuration, output));
        Assert.Contains($"key {lostKeyId} (1 stored credential", output.ToString());
        Assert.Contains("--yes", output.ToString());
        Assert.Equal(1, await CountAsync(database, "SELECT count(*) FROM settings.secrets"));

        using (var refused = TestHost.Create(database, new Dictionary<string, string?> { ["Vellum:MasterKey"] = newKey }))
            StartFails(refused);

        Assert.Equal(0, await KeysCommand.RunAsync(["keys", "forget", "--yes"], configuration, output));
        Assert.Equal(0, await CountAsync(database, "SELECT count(*) FROM settings.secrets"));
        Assert.Equal(0, await CountAsync(database, "SELECT count(*) FROM settings.data_protection_keys"));
        // The loss is on record: once for the instance, once for the workspace that lost a credential.
        Assert.Equal(1, await CountAsync(database,
            $"SELECT count(*) FROM settings.audit_entries WHERE workspace_id IS NULL AND action = 'keys.forgotten' AND target LIKE 'master-key:{lostKeyId} secrets:1 %'"));
        Assert.Equal(1, await CountAsync(database,
            "SELECT count(*) FROM settings.audit_entries WHERE actor_id = 'operator' AND action = 'secret.deleted' AND target = 'github:token'"));
        // Nothing is left to forget.
        Assert.Equal(0, await KeysCommand.RunAsync(["keys", "forget", "--yes"], configuration, output));

        // The instance starts with the new key. Users and workspaces are still there.
        using var started = TestHost.Create(database, new Dictionary<string, string?> { ["Vellum:MasterKey"] = newKey });
        using var client = TestHost.Client(started);
        var status = await client.GetFromJsonAsync<JsonObject>("/api/setup/status");
        Assert.False((bool)status!["needsSetup"]!);
        Assert.Equal(0, await CountAsync(database, "SELECT count(*) FROM settings.secrets"));
    }
}
