using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Vellum.Modules.Settings;
using Vellum.Modules.Settings.Instance;
using Vellum.Modules.Settings.Setup;
using Vellum.Modules.Workspaces;
using Vellum.Shared;

namespace Vellum.Tests.Modules.Settings;

/// <summary>Each test starts on its own empty database: setup happens once per instance.</summary>
[Collection("Integration")]
public class SetupAndBootstrapHostTests
{
    private readonly IntegrationFixture _fixture;

    public SetupAndBootstrapHostTests(IntegrationFixture fixture) => _fixture = fixture;

    private static object Account(string? setupToken = null, string password = TestHost.AdminPassword) => new
    {
        email = $"admin-{Guid.NewGuid():N}@vellum.local",
        password,
        displayName = "Admin",
        workspaceName = "Home",
        setupToken,
    };

    private static async Task<long> CountAsync(string connectionString, string table)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"SELECT count(*) FROM {table}", connection);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    [Fact]
    public async Task Local_setup_creates_the_admin_once_and_then_closes_registration()
    {
        var database = await TestHost.CreateDatabaseAsync(_fixture);
        using var factory = TestHost.Create(database);
        using var client = TestHost.Client(factory);

        var before = await client.GetFromJsonAsync<SetupStatusResponse>("/api/setup/status");
        Assert.Equal(new SetupStatusResponse(NeedsSetup: true, Profile: "local", RequiresSetupToken: false), before);

        // No account exists before setup, and registration cannot create the first one.
        var early = await client.PostAsJsonAsync("/api/auth/register",
            new { email = "early@vellum.local", password = "Test123!", displayName = "Early" });
        Assert.Equal(HttpStatusCode.Forbidden, early.StatusCode);
        Assert.Equal("setup_required", (await early.Content.ReadFromJsonAsync<ErrorResponse>())!.Type);

        // A refused setup leaves nothing behind.
        var weak = await client.PostAsJsonAsync("/api/setup", Account(password: "weak"));
        Assert.Equal(HttpStatusCode.BadRequest, weak.StatusCode);
        Assert.NotEmpty((await weak.Content.ReadFromJsonAsync<ErrorResponse>())!.Errors!);
        var blank = await client.PostAsJsonAsync("/api/setup", new { email = "", password = "", displayName = "", workspaceName = "" });
        Assert.Equal(HttpStatusCode.BadRequest, blank.StatusCode);
        Assert.Equal(4, (await blank.Content.ReadFromJsonAsync<ErrorResponse>())!.Errors!.Count);
        Assert.True((await client.GetFromJsonAsync<SetupStatusResponse>("/api/setup/status"))!.NeedsSetup);
        Assert.Equal(0, await CountAsync(database, "workspaces.workspaces"));

        var response = await client.PostAsJsonAsync("/api/setup", Account());
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
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

        using var stranger = TestHost.Client(factory);
        var second = await stranger.PostAsJsonAsync("/api/setup", Account());
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal("already_set_up", (await second.Content.ReadFromJsonAsync<ErrorResponse>())!.Type);

        var register = await stranger.PostAsJsonAsync("/api/auth/register",
            new { email = "late@vellum.local", password = "Test123!", displayName = "Late" });
        Assert.Equal(HttpStatusCode.Forbidden, register.StatusCode);
        Assert.Equal("registration_closed", (await register.Content.ReadFromJsonAsync<ErrorResponse>())!.Type);
        Assert.Equal(1, await CountAsync(database, "identity.\"AspNetUsers\""));
    }

    [Fact]
    public async Task Hosted_setup_needs_the_setup_token()
    {
        var database = await TestHost.CreateDatabaseAsync(_fixture);
        using var factory = TestHost.Create(database, TestHost.Hosted(TestHost.NewMasterKey()));
        using var client = TestHost.Client(factory, https: true);

        var status = await client.GetFromJsonAsync<SetupStatusResponse>("/api/setup/status");
        Assert.Equal(new SetupStatusResponse(NeedsSetup: true, Profile: "hosted", RequiresSetupToken: true), status);

        foreach (var token in new[] { null, "", "wrong-token-0123456789abcdef", TestHost.SetupToken + "x", TestHost.SetupToken[..^1] })
        {
            var refused = await client.PostAsJsonAsync("/api/setup", Account(token));
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
            var body = await refused.Content.ReadAsStringAsync();
            Assert.Contains("invalid_setup_token", body);
            Assert.DoesNotContain(TestHost.SetupToken, body);
        }

        Assert.True((await client.GetFromJsonAsync<SetupStatusResponse>("/api/setup/status"))!.NeedsSetup);
        Assert.Equal(0, await CountAsync(database, "identity.\"AspNetUsers\""));

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
            new { email = $"user-{Guid.NewGuid():N}@vellum.local", password = "Test123!", displayName = "User" }));
        Assert.False((await user.GetFromJsonAsync<InstanceInfoResponse>("/api/instance"))!.IsInstanceAdmin);
        Assert.Equal(HttpStatusCode.Forbidden, (await user.GetAsync("/api/instance/settings")).StatusCode);
    }

    [Fact]
    public async Task Concurrent_setup_calls_create_one_admin()
    {
        var database = await TestHost.CreateDatabaseAsync(_fixture);
        using var factory = TestHost.Create(database);
        // The host is started before the race, so the calls race on setup and not on start-up.
        using (var warm = TestHost.Client(factory))
            await warm.GetAsync("/api/setup/status");

        var clients = Enumerable.Range(0, 8).Select(_ => TestHost.Client(factory)).ToList();
        var responses = await Task.WhenAll(clients.Select(c => c.PostAsJsonAsync("/api/setup", Account())));

        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Created);
        Assert.Equal(7, responses.Count(r => r.StatusCode == HttpStatusCode.Conflict));
        Assert.Equal(1, await CountAsync(database, "identity.\"AspNetUsers\""));
        Assert.Equal(1, await CountAsync(database, "settings.instance_admins"));
        Assert.Equal(1, await CountAsync(database, "workspaces.workspaces"));
        Assert.Equal(1, await CountAsync(database, "workspaces.memberships"));
        clients.ForEach(c => c.Dispose());
    }

    // ---- Unsafe hosted bootstrap stops the host --------------------------------

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

    [Fact]
    public async Task A_hosted_host_without_a_master_key_does_not_start()
    {
        var database = await TestHost.CreateDatabaseAsync(_fixture);
        var settings = TestHost.Hosted(masterKey: "");
        using var factory = TestHost.Create(database, settings);

        Assert.Contains("Vellum__MasterKey", StartFails(factory).Message);
    }

    [Fact]
    public async Task A_hosted_host_that_accepts_any_host_name_does_not_start()
    {
        var database = await TestHost.CreateDatabaseAsync(_fixture);
        var settings = TestHost.Hosted(TestHost.NewMasterKey());
        settings["AllowedHosts"] = "*";
        using var factory = TestHost.Create(database, settings);

        Assert.Contains("AllowedHosts", StartFails(factory).Message);
    }

    [Fact]
    public async Task A_hosted_host_without_secure_cookies_does_not_start()
    {
        var database = await TestHost.CreateDatabaseAsync(_fixture);
        var settings = TestHost.Hosted(TestHost.NewMasterKey());
        settings["Vellum:SecureCookies"] = "false";
        using var factory = TestHost.Create(database, settings);

        Assert.Contains("Vellum__SecureCookies", StartFails(factory).Message);
    }

    [Fact]
    public async Task A_hosted_host_with_no_user_needs_a_setup_token_and_one_with_a_user_does_not()
    {
        var database = await TestHost.CreateDatabaseAsync(_fixture);
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

    [Fact]
    public async Task A_session_cookie_is_accepted_by_another_instance_with_the_same_master_key_and_refused_with_a_different_one()
    {
        var database = await TestHost.CreateDatabaseAsync(_fixture);
        var key = TestHost.NewMasterKey();

        string cookie;
        using (var first = TestHost.Create(database, new Dictionary<string, string?> { ["Vellum:MasterKey"] = key }))
        {
            using var client = first.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
            var response = await client.PostAsJsonAsync("/api/setup", Account());
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
        Assert.Equal(HttpStatusCode.Unauthorized, await MeAsync(TestHost.NewMasterKey()));
        // The instance with the other key did not damage the keys of the first.
        Assert.Equal(HttpStatusCode.OK, await MeAsync(key));
    }

    [Fact]
    public async Task Health_is_anonymous_and_says_nothing_but_the_status()
    {
        using var factory = TestHost.Create(_fixture);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync());
    }
}
