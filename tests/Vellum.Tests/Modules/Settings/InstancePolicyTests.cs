using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Vellum.Modules.Settings.Document;
using Vellum.Modules.Settings.Instance;
using Vellum.Shared;

namespace Vellum.Tests.Modules.Settings;

/// <summary>
/// Instance policy and defaults change the whole instance, so this class has its own
/// database. Each test sets the policy it needs.
/// </summary>
[Collection("Integration")]
public class InstancePolicyTests : IAsyncLifetime
{
    private const string Provider = "33333333-3333-3333-3333-333333333333";

    private readonly IntegrationFixture _fixture;
    private readonly string _masterKey = TestHost.NewMasterKey();
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _admin = null!;
    private Guid _workspaceId;
    private JsonObject _initial = null!;

    public InstancePolicyTests(IntegrationFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        var database = await TestHost.CreateDatabaseAsync(_fixture);
        _factory = TestHost.Create(database,
            new Dictionary<string, string?> { ["Vellum:MasterKey"] = _masterKey },
            services => TestHost.UseResolver(services, new Dictionary<string, string> { ["internal.example"] = "10.0.0.5" }));
        var (admin, created, _) = await TestHost.SetUpAsync(_factory);
        _admin = admin;
        _workspaceId = created.WorkspaceId;
        _initial = (await _admin.GetFromJsonAsync<JsonObject>("/api/instance/settings"))!;
    }

    public Task DisposeAsync()
    {
        _admin.Dispose();
        _factory.Dispose();
        return Task.CompletedTask;
    }

    private string SettingsUrl => $"/api/workspaces/{_workspaceId}/settings";

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    private static async Task<JsonObject> OkAsync(Task<HttpResponseMessage> request)
    {
        var response = await request;
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, body);
        return JsonNode.Parse(body)!.AsObject();
    }

    private static async Task RefusedAsync(Task<HttpResponseMessage> request, string field)
    {
        var response = await request;
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.BadRequest, body);
        Assert.Contains((await response.Content.ReadFromJsonAsync<ErrorResponse>())!.Errors!, e => e.Field == field);
    }

    /// <summary>Saves the initial instance settings with <paramref name="change"/> applied.</summary>
    private Task<HttpResponseMessage> PutInstanceAsync(Action<JsonObject, JsonObject> change)
    {
        var settings = _initial.DeepClone().AsObject();
        change(settings["policy"]!.AsObject(), settings["defaults"]!.AsObject());
        return _admin.PutInstanceSettingsAsync(settings);
    }

    private static string ProviderOverride(string kind, string baseUrl = "") => $$"""
        { "ai": { "providers": [ { "id": "{{Provider}}", "kind": "{{kind}}", "name": "P", "model": "m", "baseUrl": "{{baseUrl}}" } ],
                  "taskRouting": { "critique": "{{Provider}}" } } }
        """;

    [Fact]
    public void A_local_instance_starts_with_the_local_policy_and_the_built_in_defaults()
    {
        var policy = _initial["policy"]!;
        Assert.False((bool)policy["allowSelfRegistration"]!);
        Assert.True((bool)policy["allowPrivateNetworkUrls"]!);
        Assert.True((bool)policy["allowSendingPrivateCodeToAi"]!);
        Assert.Equal(["openai", "anthropic", "gemini", "openai-compatible"],
            policy["allowedAiProviderKinds"]!.AsArray().Select(k => (string)k!));
        Assert.Equal(2000, (int)_initial["defaults"]!["import"]!["maxFiles"]!);
        Assert.Equal("https://api.github.com", (string)_initial["defaults"]!["github"]!["apiBaseUrl"]!);
    }

    [Fact]
    public async Task The_policy_limits_the_provider_kinds_a_workspace_can_use()
    {
        await OkAsync(PutInstanceAsync((_, _) => { }));
        await OkAsync(_admin.PutSettingsAsync(SettingsUrl, Json(ProviderOverride("anthropic"))));

        await OkAsync(PutInstanceAsync((policy, _) => policy["allowedAiProviderKinds"] = new JsonArray("openai")));

        // The stored override stays as written; what applies obeys the policy at once.
        var settings = await OkAsync(_admin.GetAsync(SettingsUrl));
        Assert.Equal("anthropic", (string)settings["overrides"]!["ai"]!["providers"]![0]!["kind"]!);
        Assert.False((bool)settings["effective"]!["ai"]!["providers"]![0]!["enabled"]!);
        Assert.Null(settings["effective"]!["ai"]!["taskRouting"]!["critique"]);

        await RefusedAsync(_admin.PutSettingsAsync(SettingsUrl, Json(ProviderOverride("anthropic"))), "ai.providers[0].kind");
        var allowed = await OkAsync(_admin.PutSettingsAsync(SettingsUrl, Json(ProviderOverride("openai"))));
        Assert.Equal(Provider, (string)allowed["effective"]!["ai"]!["taskRouting"]!["critique"]!);

        // The instance defaults obey the policy that is saved with them.
        await RefusedAsync(PutInstanceAsync((policy, defaults) =>
        {
            policy["allowedAiProviderKinds"] = new JsonArray("openai");
            defaults["ai"]!["providers"] = new JsonArray(JsonNode.Parse(
                $$"""{ "id": "{{Provider}}", "kind": "gemini", "name": "G", "model": "m" }"""));
        }), "defaults.ai.providers[0].kind");
    }

    [Fact]
    public async Task The_policy_decides_whether_private_code_can_go_to_ai()
    {
        const string forward = """{ "privacy": { "sendPrivateCodeToAi": true } }""";

        await OkAsync(PutInstanceAsync((policy, _) => policy["allowSendingPrivateCodeToAi"] = false));
        await RefusedAsync(_admin.PutSettingsAsync(SettingsUrl, Json(forward)), "privacy.sendPrivateCodeToAi");
        await RefusedAsync(PutInstanceAsync((policy, defaults) =>
        {
            policy["allowSendingPrivateCodeToAi"] = false;
            defaults["privacy"]!["sendPrivateCodeToAi"] = true;
        }), "defaults.privacy.sendPrivateCodeToAi");

        await OkAsync(PutInstanceAsync((policy, _) => policy["allowSendingPrivateCodeToAi"] = true));
        var saved = await OkAsync(_admin.PutSettingsAsync(SettingsUrl, Json(forward)));
        Assert.True((bool)saved["effective"]!["privacy"]!["sendPrivateCodeToAi"]!);

        // The admin withdraws the permission: the workspace's stored choice no longer applies.
        await OkAsync(PutInstanceAsync((policy, _) => policy["allowSendingPrivateCodeToAi"] = false));
        var after = await OkAsync(_admin.GetAsync(SettingsUrl));
        Assert.False((bool)after["effective"]!["privacy"]!["sendPrivateCodeToAi"]!);
        Assert.True((bool)after["overrides"]!["privacy"]!["sendPrivateCodeToAi"]!);
    }

    [Theory]
    [InlineData("""{ "github": { "apiBaseUrl": "https://internal.example/api/v3" } }""", "github.apiBaseUrl")]
    [InlineData("""{ "github": { "apiBaseUrl": "http://127.0.0.1:3000" } }""", "github.apiBaseUrl")]
    [InlineData("""{ "github": { "apiBaseUrl": "http://2130706433" } }""", "github.apiBaseUrl")]
    [InlineData("""{ "github": { "apiBaseUrl": "http://[::ffff:10.0.0.1]" } }""", "github.apiBaseUrl")]
    [InlineData("""{ "notifications": { "webhookUrl": "http://169.254.169.254/latest/meta-data/" } }""", "notifications.webhookUrl")]
    [InlineData("""{ "notifications": { "webhookUrl": "http://localhost:9000/hook" } }""", "notifications.webhookUrl")]
    [InlineData("""{ "ai": { "providers": [ { "id": "33333333-3333-3333-3333-333333333333", "kind": "openai-compatible", "name": "Ollama", "model": "m", "baseUrl": "http://localhost:11434/v1" } ] } }""", "ai.providers[0].baseUrl")]
    [InlineData("""{ "ai": { "providers": [ { "id": "33333333-3333-3333-3333-333333333333", "kind": "openai", "name": "Proxy", "model": "m", "baseUrl": "https://internal.example/v1" } ] } }""", "ai.providers[0].baseUrl")]
    public async Task With_private_networks_closed_a_url_to_a_private_address_is_refused(string overrides, string field)
    {
        await OkAsync(PutInstanceAsync((policy, _) => policy["allowPrivateNetworkUrls"] = false));

        await RefusedAsync(_admin.PutSettingsAsync(SettingsUrl, Json(overrides)), field);
    }

    [Fact]
    public async Task With_private_networks_closed_a_public_url_is_accepted()
    {
        await OkAsync(PutInstanceAsync((policy, _) => policy["allowPrivateNetworkUrls"] = false));

        var saved = await OkAsync(_admin.PutSettingsAsync(SettingsUrl, Json("""
            { "github": { "apiBaseUrl": "https://github.public.example/api/v3" },
              "notifications": { "webhookUrl": "https://hooks.public.example/vellum" } }
            """)));

        Assert.Equal("https://github.public.example/api/v3", (string)saved["effective"]!["github"]!["apiBaseUrl"]!);

        // The instance defaults go through the same check.
        await RefusedAsync(PutInstanceAsync((policy, defaults) =>
        {
            policy["allowPrivateNetworkUrls"] = false;
            defaults["github"]!["apiBaseUrl"] = "https://internal.example";
        }), "defaults.github.apiBaseUrl");
    }

    [Fact]
    public async Task Instance_defaults_reach_every_workspace_that_has_no_override()
    {
        await OkAsync(_admin.PutSettingsAsync(SettingsUrl, Json("""{ "simulation": { "seed": 7 } }""")));

        var saved = await OkAsync(PutInstanceAsync((_, defaults) =>
        {
            defaults["usage"]!["maxScansPerDay"] = 5;
            defaults["simulation"]!["seed"] = 99;
            // A section the admin leaves out takes the built-in default.
            defaults.Remove("cost");
        }));
        Assert.Equal(5, (int)saved["defaults"]!["usage"]!["maxScansPerDay"]!);
        Assert.Equal("USD", (string)saved["defaults"]!["cost"]!["currency"]!);

        var workspace = await OkAsync(_admin.GetAsync(SettingsUrl));
        Assert.Equal(5, (int)workspace["effective"]!["usage"]!["maxScansPerDay"]!);
        Assert.Equal(5, (int)workspace["defaults"]!["usage"]!["maxScansPerDay"]!);
        Assert.Equal(7, (int)workspace["effective"]!["simulation"]!["seed"]!);
        Assert.Equal(99, (int)workspace["defaults"]!["simulation"]!["seed"]!);

        var read = await OkAsync(_admin.GetAsync("/api/instance/settings"));
        Assert.True(JsonNode.DeepEquals(saved, read));

        await RefusedAsync(PutInstanceAsync((_, defaults) => defaults["import"]!["maxFiles"] = 0), "defaults.import.maxFiles");
        await RefusedAsync(PutInstanceAsync((_, defaults) => defaults["import"]!["maxFiles"] = "many"), "defaults.import.maxFiles");
        await RefusedAsync(PutInstanceAsync((policy, _) => policy["allowedAiProviderKinds"] = new JsonArray("openai", "mistral")),
            "policy.allowedAiProviderKinds");
        await RefusedAsync(PutInstanceAsync((policy, _) => policy["allowedAiProviderKinds"] = new JsonArray("openai", "openai")),
            "policy.allowedAiProviderKinds");
        await RefusedAsync(_admin.PutSettingsAsync("/api/instance/settings", Json("""{ "defaults": {} }""")), "policy");
        await RefusedAsync(_admin.PutSettingsAsync("/api/instance/settings", Json($$"""{ "policy": {{_initial["policy"]!.ToJsonString()}}, "defaults": [] }""")), "defaults");
    }

    [Fact]
    public async Task Registration_follows_the_policy_and_only_the_admin_manages_the_instance()
    {
        object NewAccount() => new { email = $"u-{Guid.NewGuid():N}@vellum.local", password = "Test12345!", displayName = "User" };

        await OkAsync(PutInstanceAsync((policy, _) => policy["allowSelfRegistration"] = true));
        Assert.True((await _admin.GetFromJsonAsync<InstanceInfoResponse>("/api/instance"))!.AllowSelfRegistration);
        using var user = TestHost.Client(_factory);
        await TestHost.Created(user.PostAsJsonAsync("/api/auth/register", NewAccount()));

        await OkAsync(PutInstanceAsync((policy, _) => policy["allowSelfRegistration"] = false));
        using var late = TestHost.Client(_factory);
        var closed = await late.PostAsJsonAsync("/api/auth/register", NewAccount());
        Assert.Equal(HttpStatusCode.Forbidden, closed.StatusCode);
        Assert.Equal("registration_closed", (await closed.Content.ReadFromJsonAsync<ErrorResponse>())!.Type);

        // A signed-in user who is not the admin, and a caller who is not signed in.
        var info = await user.GetFromJsonAsync<InstanceInfoResponse>("/api/instance");
        Assert.False(info!.IsInstanceAdmin);
        Assert.False(info.AllowSelfRegistration);
        foreach (var (client, expected) in new[] { (user, HttpStatusCode.Forbidden), (late, HttpStatusCode.Unauthorized) })
        {
            Assert.Equal(expected, (await client.GetAsync("/api/instance/settings")).StatusCode);
            Assert.Equal(expected, (await client.PutInstanceSettingsAsync(_initial)).StatusCode);
            Assert.Equal(expected, (await client.PostAsync("/api/instance/keys/rotate", null)).StatusCode);
            Assert.Equal(expected, (await client.GetAsync("/api/instance/diagnostics")).StatusCode);
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await late.GetAsync("/api/instance")).StatusCode);
        // The refused PUT changed nothing.
        Assert.False((bool)(await OkAsync(_admin.GetAsync("/api/instance/settings")))["policy"]!["allowSelfRegistration"]!);
    }

    [Fact]
    public async Task A_stricter_policy_blocks_a_private_url_that_a_workspace_saved_before()
    {
        var overrides = $$"""
            { "github": { "apiBaseUrl": "http://192.168.1.50:3000/api/v3" },
              "notifications": { "webhookUrl": "http://localhost:9000/hook" },
              "ai": { "providers": [ { "id": "{{Provider}}", "kind": "openai-compatible", "name": "Ollama", "model": "m", "baseUrl": "http://127.0.0.1:11434/v1" } ],
                      "taskRouting": { "critique": "{{Provider}}" } } }
            """;
        var saved = await OkAsync(_admin.PutSettingsAsync(SettingsUrl, Json(overrides)));
        Assert.Empty(saved["policyViolations"]!.AsArray());
        Assert.Equal(HttpStatusCode.NoContent,
            (await _admin.PutAsJsonAsync($"/api/workspaces/{_workspaceId}/secrets/github:token", new { value = "token-for-the-private-host" })).StatusCode);

        // The admin closes private networks after the workspace saved its URLs.
        var tightened = await OkAsync(PutInstanceAsync((policy, _) => policy["allowPrivateNetworkUrls"] = false));
        Assert.Equal(1, (int)tightened["workspacesWithPolicyViolations"]!);
        Assert.Equal(1, (int)(await OkAsync(_admin.GetAsync("/api/instance/settings")))["workspacesWithPolicyViolations"]!);

        var settings = await OkAsync(_admin.GetAsync(SettingsUrl));
        Assert.Equal(
            ["ai.providers[0].baseUrl", "github.apiBaseUrl", "notifications.webhookUrl"],
            settings["policyViolations"]!.AsArray().Select(v => (string)v!["field"]!));
        Assert.All(settings["policyViolations"]!.AsArray(), v => Assert.False(string.IsNullOrEmpty((string)v!["message"]!)));
        // What applies holds none of the refused URLs; what the workspace stored is unchanged.
        var effective = settings["effective"]!;
        Assert.Equal("", (string)effective["github"]!["apiBaseUrl"]!);
        Assert.Null(effective["notifications"]!["webhookUrl"]);
        Assert.False((bool)effective["ai"]!["providers"]![0]!["enabled"]!);
        Assert.Null(effective["ai"]!["taskRouting"]!["critique"]);
        Assert.Equal("http://192.168.1.50:3000/api/v3", (string)settings["overrides"]!["github"]!["apiBaseUrl"]!);

        // A caller that makes requests gets a typed answer, not the URL.
        using (var scope = _factory.Services.CreateScope())
        {
            var workspace = await scope.ServiceProvider.GetRequiredService<SettingsResolver>().GetWorkspaceAsync(_workspaceId, default);
            Assert.Equal("github.apiBaseUrl", Assert.IsType<SettingsUrl.BlockedByPolicy>(workspace.GithubApiBaseUrl).Field);
            Assert.IsType<SettingsUrl.BlockedByPolicy>(workspace.WebhookUrl);
            Assert.IsType<SettingsUrl.BlockedByPolicy>(workspace.ProviderBaseUrl(Guid.Parse(Provider)));
            Assert.IsType<SettingsUrl.BlockedByPolicy>(workspace.DestinationOf("github:token"));
            Assert.IsType<SettingsUrl.NotConfigured>(workspace.ProviderBaseUrl(Guid.NewGuid()));
            Assert.False(workspace.Policy.AllowPrivateNetworkUrls);
        }

        // The stored token is not offered for the blocked URL, and no new one is accepted for it.
        var secrets = await _admin.GetFromJsonAsync<JsonArray>($"/api/workspaces/{_workspaceId}/secrets");
        var token = secrets!.Single(s => (string)s!["name"]! == "github:token")!;
        Assert.True((bool)token["isSet"]!);
        Assert.False((bool)token["matchesCurrentUrl"]!);
        await RefusedAsync(_admin.PutAsJsonAsync($"/api/workspaces/{_workspaceId}/secrets/github:token", new { value = "another" }), "name");

        // The admin opens private networks again: the stored settings apply again.
        var reopened = await OkAsync(PutInstanceAsync((policy, _) => policy["allowPrivateNetworkUrls"] = true));
        Assert.Equal(0, (int)reopened["workspacesWithPolicyViolations"]!);
        var again = await OkAsync(_admin.GetAsync(SettingsUrl));
        Assert.Empty(again["policyViolations"]!.AsArray());
        Assert.Equal("http://192.168.1.50:3000/api/v3", (string)again["effective"]!["github"]!["apiBaseUrl"]!);
        using (var scope = _factory.Services.CreateScope())
        {
            var workspace = await scope.ServiceProvider.GetRequiredService<SettingsResolver>().GetWorkspaceAsync(_workspaceId, default);
            var usable = Assert.IsType<SettingsUrl.Usable>(workspace.GithubApiBaseUrl);
            Assert.Equal("http://192.168.1.50:3000", usable.Origin);
        }
    }

    [Fact]
    public async Task The_instance_settings_body_is_read_strictly()
    {
        var before = await OkAsync(_admin.GetAsync("/api/instance/settings"));

        // An unknown field, and a policy that leaves out three permissions.
        var response = await _admin.PutSettingsAsync("/api/instance/settings",
            Json("""{"policy":{"allowedAiProviderKinds":["openai"],"isInstanceAdmin":true},"defaults":{},"extra":1}"""));
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.BadRequest, body);
        var errors = (await response.Content.ReadFromJsonAsync<ErrorResponse>())!.Errors!;
        Assert.Contains(errors, e => e.Field == "extra");
        Assert.Contains(errors, e => e.Field.StartsWith("policy", StringComparison.Ordinal));

        await RefusedAsync(_admin.PutSettingsAsync("/api/instance/settings", Json(
            """{"policy":{"allowSelfRegistration":true,"allowPrivateNetworkUrls":true,"allowedAiProviderKinds":["openai"]},"defaults":{}}""")), "policy");
        await RefusedAsync(_admin.PutSettingsAsync("/api/instance/settings", Json(
            """{"policy":{"allowSelfRegistration":"yes","allowPrivateNetworkUrls":true,"allowedAiProviderKinds":[],"allowSendingPrivateCodeToAi":true},"defaults":{}}""")),
            "policy.allowSelfRegistration");
        await RefusedAsync(_admin.PutSettingsAsync("/api/instance/settings", Json(
            "{\"policy\":" + _initial["policy"]!.ToJsonString() + ",\"defaults\":{\"cost\":{\"region\":\"eu\\u0000west\"}}}")), "defaults.cost.region");
        await RefusedAsync(_admin.PutSettingsAsync("/api/instance/settings", Json("{\"policy\":" + _initial["policy"]!.ToJsonString() + "}")), "defaults");

        // Nothing was saved: no permission was turned off.
        Assert.True(JsonNode.DeepEquals(before, await OkAsync(_admin.GetAsync("/api/instance/settings"))));
    }

    [Fact]
    public async Task A_workspace_save_is_checked_against_the_policy_that_is_current_when_it_commits()
    {
        // The workspace owner and the admin work at the same time, many times over. A private
        // URL must never be stored and in effect after the admin has closed private networks.
        for (var round = 0; round < 6; round++)
        {
            await OkAsync(PutInstanceAsync((policy, _) => policy["allowPrivateNetworkUrls"] = true));
            await OkAsync(_admin.PutSettingsAsync(SettingsUrl, Json("{}")));

            var save = _admin.PutSettingsAsync(SettingsUrl, Json("""{ "github": { "apiBaseUrl": "http://10.9.8.7:3000" } }"""));
            var close = PutInstanceAsync((policy, _) => policy["allowPrivateNetworkUrls"] = false);
            var saved = await save;
            var closed = await OkAsync(close);

            var settings = await OkAsync(_admin.GetAsync(SettingsUrl));
            if (saved.StatusCode == HttpStatusCode.OK)
            {
                // The save committed first, so the admin's save counted it as a violation.
                Assert.Equal(1, (int)closed["workspacesWithPolicyViolations"]!);
                Assert.Single(settings["policyViolations"]!.AsArray());
            }
            else
            {
                // The save was checked against the closed policy, or gave way to the admin's save.
                Assert.Contains(saved.StatusCode, new[] { HttpStatusCode.BadRequest, HttpStatusCode.Conflict });
                Assert.Empty(settings["overrides"]!.AsObject());
            }

            Assert.NotEqual("http://10.9.8.7:3000", (string)settings["effective"]!["github"]!["apiBaseUrl"]!);
        }
    }

    [Fact]
    public async Task Diagnostics_report_the_state_of_the_instance_and_never_the_key()
    {
        var response = await _admin.GetAsync("/api/instance/diagnostics");
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, body);
        var diagnostics = JsonNode.Parse(body)!;

        Assert.Equal("local", (string)diagnostics["profile"]!);
        Assert.False(string.IsNullOrEmpty((string)diagnostics["version"]!));
        Assert.True((bool)diagnostics["database"]!["reachable"]!);
        Assert.True((bool)diagnostics["eventStore"]!["reachable"]!);
        Assert.Contains(diagnostics["eventStore"]!["consumers"]!.AsArray(), c => (string)c!["name"]! == "model_read_model");
        Assert.Equal(16, ((string)diagnostics["masterKeyId"]!).Length);
        Assert.False((bool)diagnostics["previousMasterKeyLoaded"]!);

        var modules = diagnostics["migrations"]!.AsArray().ToDictionary(m => (string)m!["module"]!, m => m!);
        Assert.Equal(
            ["ai", "ai-design", "docs", "drafts", "identity", "import", "modelling", "scenarios", "schemas", "scoring", "settings", "views", "workspaces"],
            modules.Keys.Order());
        Assert.All(modules.Values, m =>
        {
            Assert.NotEmpty(m["applied"]!.AsArray());
            Assert.Empty(m["pending"]!.AsArray());
        });
        Assert.Contains(modules["settings"]["applied"]!.AsArray(), id => ((string)id!).EndsWith("_InitialSettings"));
        // Each module lists its own migrations only.
        Assert.DoesNotContain(modules["identity"]["applied"]!.AsArray(), id => ((string)id!).EndsWith("_InitialSettings"));

        Assert.DoesNotContain(_masterKey, body);
        Assert.DoesNotContain(Convert.ToHexStringLower(Convert.FromBase64String(_masterKey)), body, StringComparison.OrdinalIgnoreCase);
    }
}
