using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Vellum.Modules.Settings;
using Vellum.Modules.Settings.Instance;
using Vellum.Modules.Settings.Setup;
using Vellum.Modules.Workspaces;
using Vellum.Shared;

namespace Vellum.Tests.Modules.Settings;

/// <summary>
/// Each test starts on its own empty database: setup happens once per instance. The tests
/// start several hosts each, so they are split into classes that run in parallel.
/// </summary>
public abstract class SetupAndBootstrapHostTests
{
    protected static object Account(string? setupToken = null, string password = TestHost.AdminPassword) => new
    {
        email = $"admin-{Guid.NewGuid():N}@vellum.local",
        password,
        displayName = "Admin",
        workspaceName = "Home",
        setupToken,
    };

    protected static async Task<long> CountAsync(string connectionString, string table)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"SELECT count(*) FROM {table}", connection);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    protected static BootstrapException StartFails(WebApplicationFactory<Program> factory)
    {
        var thrown = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
        for (var ex = thrown; ex is not null; ex = ex.InnerException)
        {
            if (ex is BootstrapException bootstrap)
                return bootstrap;
        }

        throw new Xunit.Sdk.XunitException($"The host failed for another reason: {thrown}");
    }

}

/// <summary>First-run setup of a local and a hosted instance.</summary>
public class FirstRunSetupTests : SetupAndBootstrapHostTests
{
    [Fact]
    public async Task Local_setup_creates_the_admin_once_and_then_closes_registration()
    {
        var database = await TestHost.CreateDatabaseAsync();
        using var factory = TestHost.Create(database);
        using var client = TestHost.Client(factory);

        var before = await client.GetFromJsonAsync<SetupStatusResponse>("/api/setup/status");
        Assert.Equal(new SetupStatusResponse(NeedsSetup: true, Profile: "local", RequiresSetupToken: true, SetupTokenHint: "log"), before);
        var code = TestHost.SetupCode(factory);

        // Reaching the port is not enough: setup needs the code from the log or the code file.
        foreach (var wrong in new[] { null, "", "AAAAA-BBBBB-CCCCC-DDDDD", code + "X" })
        {
            var refused = await client.PostAsJsonAsync("/api/setup", Account(wrong));
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
            var refusal = await refused.Content.ReadAsStringAsync();
            Assert.Contains("invalid_setup_token", refusal);
            Assert.DoesNotContain(code, refusal);
        }

        Assert.Equal(0, await CountAsync(database, "identity.users"));

        // No account exists before setup, and registration cannot create the first one.
        var early = await client.PostAsJsonAsync("/api/auth/register",
            new { email = "early@vellum.local", password = "Test12345!", displayName = "Early" });
        Assert.Equal(HttpStatusCode.Forbidden, early.StatusCode);
        Assert.Equal("setup_required", (await early.Content.ReadFromJsonAsync<ErrorResponse>())!.Type);

        // A refused setup leaves nothing behind.
        var weak = await client.PostAsJsonAsync("/api/setup", Account(code, password: "weak"));
        Assert.Equal(HttpStatusCode.BadRequest, weak.StatusCode);
        Assert.NotEmpty((await weak.Content.ReadFromJsonAsync<ErrorResponse>())!.Errors!);
        var blank = await client.PostAsJsonAsync("/api/setup", new { email = "", password = "", displayName = "", workspaceName = "", setupToken = code });
        Assert.Equal(HttpStatusCode.BadRequest, blank.StatusCode);
        Assert.Equal(4, (await blank.Content.ReadFromJsonAsync<ErrorResponse>())!.Errors!.Count);
        Assert.True((await client.GetFromJsonAsync<SetupStatusResponse>("/api/setup/status"))!.NeedsSetup);
        Assert.Equal(0, await CountAsync(database, "workspaces.workspaces"));

        // Ten characters is the shortest new password.
        var shortPassword = await client.PostAsJsonAsync("/api/setup", Account(code, password: "Short123!"));
        Assert.Equal(HttpStatusCode.BadRequest, shortPassword.StatusCode);

        var response = await client.PostAsJsonAsync("/api/setup", Account(code));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        // The code worked one time: its file is gone.
        Assert.False(File.Exists(factory.Services.GetRequiredService<SetupAccess>().CodeFilePath));
        var created = (await response.Content.ReadFromJsonAsync<SetupResponse>())!;

        // The same client is signed in, is the instance admin and owns the new workspace.
        var me = await client.GetFromJsonAsync<JsonObject>("/api/auth/me");
        Assert.Equal(created.UserId, (string)me!["id"]!);
        var instance = await client.GetFromJsonAsync<InstanceInfoResponse>("/api/instance");
        Assert.Equal("local", instance!.Profile);
        Assert.True(instance.IsInstanceAdmin);
        Assert.False(instance.AllowSelfRegistration);
        Assert.False(string.IsNullOrEmpty(instance.Version));
        var workspaces = await client.GetFromJsonAsync<WorkspaceDto[]>("/api/workspaces");
        Assert.Equal(created.WorkspaceId, Assert.Single(workspaces!).Id);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/workspaces/{created.WorkspaceId}/secrets")).StatusCode);

        var after = await client.GetFromJsonAsync<SetupStatusResponse>("/api/setup/status");
        Assert.False(after!.NeedsSetup);
        Assert.Null(after.SetupTokenHint);

        using var stranger = TestHost.Client(factory);
        var second = await stranger.PostAsJsonAsync("/api/setup", Account(code));
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal("already_set_up", (await second.Content.ReadFromJsonAsync<ErrorResponse>())!.Type);

        var register = await stranger.PostAsJsonAsync("/api/auth/register",
            new { email = "late@vellum.local", password = "Test12345!", displayName = "Late" });
        Assert.Equal(HttpStatusCode.Forbidden, register.StatusCode);
        Assert.Equal("registration_closed", (await register.Content.ReadFromJsonAsync<ErrorResponse>())!.Type);
        Assert.Equal(1, await CountAsync(database, "identity.users"));
    }

    [Fact]
    public async Task Hosted_setup_needs_the_setup_token()
    {
        var database = await TestHost.CreateDatabaseAsync();
        using var factory = TestHost.Create(database, TestHost.Hosted(TestHost.NewMasterKey()));
        using var client = TestHost.Client(factory, https: true);

        var status = await client.GetFromJsonAsync<SetupStatusResponse>("/api/setup/status");
        Assert.Equal(new SetupStatusResponse(NeedsSetup: true, Profile: "hosted", RequiresSetupToken: true, SetupTokenHint: "operator"), status);

        foreach (var token in new[] { null, "", "wrong-token-0123456789abcdef", TestHost.SetupToken + "x", TestHost.SetupToken[..^1] })
        {
            var refused = await client.PostAsJsonAsync("/api/setup", Account(token));
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
            var body = await refused.Content.ReadAsStringAsync();
            Assert.Contains("invalid_setup_token", body);
            Assert.DoesNotContain(TestHost.SetupToken, body);
        }

        Assert.True((await client.GetFromJsonAsync<SetupStatusResponse>("/api/setup/status"))!.NeedsSetup);
        Assert.Equal(0, await CountAsync(database, "identity.users"));

        var response = await client.PostAsJsonAsync("/api/setup", Account(TestHost.SetupToken));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        // The hosted cookie is marked Secure.
        Assert.Contains(response.Headers.GetValues("Set-Cookie"), c => c.Contains("; secure", StringComparison.OrdinalIgnoreCase));

        var instance = await client.GetFromJsonAsync<InstanceInfoResponse>("/api/instance");
        Assert.Equal("hosted", instance!.Profile);
        Assert.True(instance.AllowSelfRegistration);

        // A hosted instance starts closed to private addresses and to forwarding private code.
        var policy = (await client.GetFromJsonAsync<JsonObject>("/api/instance/settings"))!["policy"]!;
        Assert.False((bool)policy["allowPrivateNetworkUrls"]!);
        Assert.False((bool)policy["allowSendingPrivateCodeToAi"]!);

        // Hosted opens registration by default. A registered user is not an instance admin.
        using var user = TestHost.Client(factory, https: true);
        await TestHost.Created(user.PostAsJsonAsync("/api/auth/register",
            new { email = $"user-{Guid.NewGuid():N}@vellum.local", password = "Test12345!", displayName = "User" }));
        Assert.False((await user.GetFromJsonAsync<InstanceInfoResponse>("/api/instance"))!.IsInstanceAdmin);
        Assert.Equal(HttpStatusCode.Forbidden, (await user.GetAsync("/api/instance/settings")).StatusCode);
    }

    [Fact]
    public async Task Concurrent_setup_calls_create_one_admin()
    {
        var database = await TestHost.CreateDatabaseAsync();
        using var factory = TestHost.Create(database);
        // The host is started before the race, so the calls race on setup and not on start-up.
        using (var warm = TestHost.Client(factory))
            await warm.GetAsync("/api/setup/status");

        var code = TestHost.SetupCode(factory);
        var clients = Enumerable.Range(0, 8).Select(_ => TestHost.Client(factory)).ToList();
        var responses = await Task.WhenAll(clients.Select(c => c.PostAsJsonAsync("/api/setup", Account(code))));

        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Created);
        Assert.Equal(7, responses.Count(r => r.StatusCode == HttpStatusCode.Conflict));
        Assert.Equal(1, await CountAsync(database, "identity.users"));
        Assert.Equal(1, await CountAsync(database, "settings.instance_admins"));
        Assert.Equal(1, await CountAsync(database, "workspaces.workspaces"));
        Assert.Equal(1, await CountAsync(database, "workspaces.memberships"));
        clients.ForEach(c => c.Dispose());
    }

}

/// <summary>An unsafe hosted bootstrap stops the host.</summary>
public class UnsafeHostedBootstrapTests : SetupAndBootstrapHostTests
{
    [Fact]
    public async Task A_hosted_host_without_a_master_key_does_not_start()
    {
        var database = await TestHost.CreateDatabaseAsync();
        var settings = TestHost.Hosted(masterKey: "");
        using var factory = TestHost.Create(database, settings);

        Assert.Contains("Vellum__MasterKey", StartFails(factory).Message);
    }

    [Fact]
    public async Task A_hosted_host_that_accepts_any_host_name_does_not_start()
    {
        var database = await TestHost.CreateDatabaseAsync();
        var settings = TestHost.Hosted(TestHost.NewMasterKey());
        settings["AllowedHosts"] = "*";
        using var factory = TestHost.Create(database, settings);

        Assert.Contains("AllowedHosts", StartFails(factory).Message);
    }

    [Fact]
    public async Task A_hosted_host_without_secure_cookies_does_not_start()
    {
        var database = await TestHost.CreateDatabaseAsync();
        var settings = TestHost.Hosted(TestHost.NewMasterKey());
        settings["Vellum:SecureCookies"] = "false";
        using var factory = TestHost.Create(database, settings);

        Assert.Contains("Vellum__SecureCookies", StartFails(factory).Message);
    }

    [Fact]
    public async Task A_hosted_host_with_no_user_needs_a_setup_token_and_one_with_a_user_does_not()
    {
        var database = await TestHost.CreateDatabaseAsync();
        var key = TestHost.NewMasterKey();

        using (var open = TestHost.Create(database, TestHost.Hosted(key, setupToken: null)))
            Assert.Contains("Vellum__SetupToken", StartFails(open).Message);

        using (var first = TestHost.Create(database, TestHost.Hosted(key)))
            await TestHost.SetUpAsync(first, TestHost.SetupToken, https: true);

        // After setup the operator removes the token; the instance still starts.
        using var later = TestHost.Create(database, TestHost.Hosted(key, setupToken: null));
        using var client = TestHost.Client(later, https: true);
        var status = await client.GetFromJsonAsync<SetupStatusResponse>("/api/setup/status");
        Assert.False(status!.NeedsSetup);
    }

    // ---- Data Protection keys: in Postgres, sealed with the master key --------------

}

/// <summary>Instances that share a master key share sessions.</summary>
public class SharedMasterKeySessionTests : SetupAndBootstrapHostTests
{
    [Fact]
    public async Task A_session_cookie_is_accepted_by_another_instance_with_the_same_master_key_and_an_instance_with_a_different_one_does_not_start()
    {
        var database = await TestHost.CreateDatabaseAsync();
        var key = TestHost.NewMasterKey();

        string cookie;
        using (var first = TestHost.Create(database, new Dictionary<string, string?> { ["Vellum:MasterKey"] = key }))
        {
            using var client = first.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
            var response = await client.PostAsJsonAsync("/api/setup", Account(TestHost.SetupCode(first)));
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            cookie = string.Join("; ", response.Headers.GetValues("Set-Cookie").Select(c => c.Split(';')[0]));
        }

        // The keys are in the database, and no stored key is readable without the master key.
        await using (var connection = new NpgsqlConnection(database))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand("SELECT xml FROM settings.data_protection_keys", connection);
            await using var reader = await command.ExecuteReaderAsync();
            var rows = 0;
            while (await reader.ReadAsync())
            {
                rows++;
                var xml = reader.GetString(0);
                Assert.Contains("<vellumEnvelope", xml);
                Assert.Contains("<encryptedSecret", xml);
                Assert.DoesNotContain("<masterKey", xml);
            }

            Assert.True(rows >= 1);
        }

        async Task<HttpStatusCode> MeAsync(string masterKey)
        {
            using var host = TestHost.Create(database, new Dictionary<string, string?> { ["Vellum:MasterKey"] = masterKey });
            using var client = host.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
            using var request = new HttpRequestMessage(HttpMethod.Get, "/api/auth/me");
            request.Headers.Add("Cookie", cookie);
            return (await client.SendAsync(request)).StatusCode;
        }

        Assert.Equal(HttpStatusCode.OK, await MeAsync(key));
        // An instance with another key does not start: it could read no stored key, and it
        // would sign sessions with keys that the first instance cannot read.
        using (var other = TestHost.Create(database, new Dictionary<string, string?> { ["Vellum:MasterKey"] = TestHost.NewMasterKey() }))
            Assert.Contains("keys forget", StartFails(other).Message);
        // The refused start changed nothing.
        Assert.Equal(HttpStatusCode.OK, await MeAsync(key));
    }

}

/// <summary>The health endpoint.</summary>
public class HealthTests(IntegrationFixture fixture) : IClassFixture<IntegrationFixture>
{
    [Fact]
    public async Task Health_is_anonymous_and_says_nothing_but_the_status()
    {
        var factory = fixture.Factory;
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync());
    }
}
