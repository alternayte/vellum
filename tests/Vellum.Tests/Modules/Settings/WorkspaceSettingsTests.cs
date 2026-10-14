using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Vellum.Shared;

namespace Vellum.Tests.Modules.Settings;

/// <summary>Workspace settings on the shared instance: local profile, default policy.</summary>
public class WorkspaceSettingsTests : IClassFixture<IntegrationFixture>
{
    private readonly IntegrationFixture _fixture;

    public WorkspaceSettingsTests(IntegrationFixture fixture) => _fixture = fixture;

    private const string ProviderA = "11111111-1111-1111-1111-111111111111";
    private const string ProviderB = "22222222-2222-2222-2222-222222222222";

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    private static async Task<JsonObject> ReadAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, body);
        return JsonNode.Parse(body)!.AsObject();
    }

    [Fact]
    public async Task A_new_workspace_has_the_defaults_and_no_overrides()
    {
        var factory = _fixture.Factory;
        using var owner = await TestHost.SignInAsync(factory);
        var (workspaceId, _) = await TestHost.CreateProjectAsync(owner);

        var settings = await ReadAsync(await owner.GetAsync($"/api/workspaces/{workspaceId}/settings"));

        Assert.Empty(settings["overrides"]!.AsObject());
        Assert.True(JsonNode.DeepEquals(settings["effective"], settings["defaults"]));
        var effective = settings["effective"]!;
        Assert.Empty(effective["ai"]!["providers"]!.AsArray());
        Assert.Null(effective["ai"]!["taskRouting"]!["inferArchitecture"]);
        Assert.Equal("https://api.github.com", (string)effective["github"]!["apiBaseUrl"]!);
        Assert.Equal(2000, (int)effective["import"]!["maxFiles"]!);
        Assert.Equal(262144, (long)effective["import"]!["maxFileBytes"]!);
        Assert.Equal(20971520, (long)effective["import"]!["maxTotalBytes"]!);
        Assert.Equal(5, effective["import"]!["excludeGlobs"]!.AsArray().Count);
        Assert.Equal("USD", (string)effective["cost"]!["currency"]!);
        Assert.Equal(1, (int)effective["simulation"]!["seed"]!);
        Assert.Equal(300, (int)effective["simulation"]!["durationSeconds"]!);
        Assert.Equal(10, (double)effective["simulation"]!["arrivalRatePerSecond"]!);
        Assert.False((bool)effective["privacy"]!["sendPrivateCodeToAi"]!);
        Assert.Null(effective["usage"]!["monthlyAiBudgetUsd"]);
        Assert.Equal(20, (int)effective["usage"]!["maxScansPerDay"]!);
        Assert.Null(effective["notifications"]!["webhookUrl"]);
    }

    [Fact]
    public async Task An_override_applies_at_once_and_a_removed_override_falls_back_to_the_default()
    {
        var factory = _fixture.Factory;
        using var owner = await TestHost.SignInAsync(factory);
        var (workspaceId, _) = await TestHost.CreateProjectAsync(owner);
        var url = $"/api/workspaces/{workspaceId}/settings";

        var overrides = $$"""
            {
              "ai": {
                "providers": [
                  { "id": "{{ProviderA}}", "kind": "openai-compatible", "name": "Ollama", "baseUrl": "http://localhost:11434/v1", "model": "llama3" },
                  { "id": "{{ProviderB}}", "kind": "anthropic", "name": "Claude", "model": "claude", "enabled": false,
                    "supportsStreaming": false, "temperature": 0.2, "timeoutSeconds": 120, "monthlyBudgetUsd": 25.5, "requestsPerMinute": 30 }
                ],
                "taskRouting": { "explain": "{{ProviderA}}" }
              },
              "import": { "maxFiles": 50 },
              "usage": { "monthlyAiBudgetUsd": 12.5 },
              "notifications": { "webhookUrl": "https://hooks.example.com/vellum" }
            }
            """;
        var saved = await ReadAsync(await owner.PutSettingsAsync(url, Json(overrides)));

        var read = await ReadAsync(await owner.GetAsync(url));
        Assert.True(JsonNode.DeepEquals(saved, read));

        var effective = read["effective"]!;
        Assert.Equal(50, (int)effective["import"]!["maxFiles"]!);
        Assert.Equal(262144, (long)effective["import"]!["maxFileBytes"]!);          // not overridden
        Assert.Equal(12.5m, (decimal)effective["usage"]!["monthlyAiBudgetUsd"]!);
        Assert.Equal(20, (int)effective["usage"]!["maxScansPerDay"]!);              // not overridden
        Assert.Equal(ProviderA, (string)effective["ai"]!["taskRouting"]!["explain"]!);
        Assert.Null(effective["ai"]!["taskRouting"]!["critique"]);                  // not overridden
        var providers = effective["ai"]!["providers"]!.AsArray();
        Assert.Equal(2, providers.Count);
        // A provider entry takes the documented defaults for what it leaves out.
        Assert.True((bool)providers[0]!["enabled"]!);
        Assert.True((bool)providers[0]!["supportsStructuredOutput"]!);
        Assert.Equal(60, (int)providers[0]!["timeoutSeconds"]!);
        Assert.Null(providers[0]!["temperature"]);
        Assert.Equal("", (string)providers[1]!["baseUrl"]!);
        Assert.Equal(0.2, (double)providers[1]!["temperature"]!);
        Assert.Equal(30, (int)providers[1]!["requestsPerMinute"]!);
        Assert.Equal(50, (int)read["overrides"]!["import"]!["maxFiles"]!);
        Assert.Null(read["overrides"]!["github"]);
        Assert.Equal(2000, (int)read["defaults"]!["import"]!["maxFiles"]!);

        // The body replaces the overrides: what it leaves out is no longer overridden.
        var cleared = await ReadAsync(await owner.PutSettingsAsync(url, Json("""{ "cost": { "region": "eu-west-1" } }""")));
        Assert.Equal(2000, (int)cleared["effective"]!["import"]!["maxFiles"]!);
        Assert.Empty(cleared["effective"]!["ai"]!["providers"]!.AsArray());
        Assert.Null(cleared["effective"]!["usage"]!["monthlyAiBudgetUsd"]);
        Assert.Equal("eu-west-1", (string)cleared["effective"]!["cost"]!["region"]!);
        Assert.Equal("USD", (string)cleared["effective"]!["cost"]!["currency"]!);

        var empty = await ReadAsync(await owner.PutSettingsAsync(url, Json("{}")));
        Assert.True(JsonNode.DeepEquals(empty["effective"], empty["defaults"]));
    }

    public static TheoryData<string, string> InvalidOverrides() => new()
    {
        // Shape: unknown fields, wrong types, nulls where a value is required.
        { """{ "unknown": {} }""", "unknown" },
        { """{ "import": { "maxFiles": 5, "maxFiles": 6 } }""", "" },
        { """{ "import": { "maxFilez": 5 } }""", "import.maxFilez" },
        { """{ "import": { "maxFiles": "50" } }""", "import.maxFiles" },
        { """{ "import": { "maxFiles": 1.5 } }""", "import.maxFiles" },
        { """{ "import": { "maxFiles": null } }""", "import.maxFiles" },
        { """{ "import": null }""", "import" },
        { """{ "cost": { "currency": null } }""", "cost.currency" },
        { """{ "privacy": { "sendPrivateCodeToAi": "yes" } }""", "privacy.sendPrivateCodeToAi" },
        { """{ "ai": { "providers": {} } }""", "ai.providers" },
        { """{ "ai": { "providers": [ { "kind": "openai", "name": "n", "model": "m" } ] } }""", "ai.providers[0]" },
        { """{ "ai": { "providers": [ { "id": "not-a-guid", "kind": "openai", "name": "n", "model": "m" } ] } }""", "ai.providers[0].id" },
        { """{ "ai": { "taskRouting": { "explain": 7 } } }""", "ai.taskRouting.explain" },
        // Providers.
        { Provider("\"kind\": \"mistral\""), "ai.providers[0].kind" },
        { Provider("\"name\": \"  \""), "ai.providers[0].name" },
        { Provider("\"model\": \"\""), "ai.providers[0].model" },
        { Provider("\"kind\": \"openai-compatible\", \"baseUrl\": \"\""), "ai.providers[0].baseUrl" },
        { Provider("\"baseUrl\": \"ftp://example.com\""), "ai.providers[0].baseUrl" },
        { Provider("\"baseUrl\": \"https://key:secret@example.com/v1\""), "ai.providers[0].baseUrl" },
        { Provider("\"baseUrl\": \"example.com/v1\""), "ai.providers[0].baseUrl" },
        { Provider("\"temperature\": 2.5"), "ai.providers[0].temperature" },
        { Provider("\"temperature\": -0.1"), "ai.providers[0].temperature" },
        { Provider("\"timeoutSeconds\": 0"), "ai.providers[0].timeoutSeconds" },
        { Provider("\"timeoutSeconds\": 601"), "ai.providers[0].timeoutSeconds" },
        { Provider("\"monthlyBudgetUsd\": -1"), "ai.providers[0].monthlyBudgetUsd" },
        { Provider("\"requestsPerMinute\": 0"), "ai.providers[0].requestsPerMinute" },
        { Provider("\"id\": \"00000000-0000-0000-0000-000000000000\""), "ai.providers[0].id" },
        { $$"""{ "ai": { "providers": [ {{ProviderJson()}}, {{ProviderJson()}} ] } }""", "ai.providers[1].id" },
        // Routing: an unknown provider, and a provider that is not enabled.
        { $$"""{ "ai": { "taskRouting": { "critique": "{{ProviderA}}" } } }""", "ai.taskRouting.critique" },
        { $$"""{ "ai": { "providers": [ {{ProviderJson("\"enabled\": false")}} ], "taskRouting": { "inferArchitecture": "{{ProviderA}}" } } }""", "ai.taskRouting.inferArchitecture" },
        { $$"""{ "ai": { "providers": [ {{ProviderJson()}} ], "taskRouting": { "proposeFlow": "{{ProviderB}}" } } }""", "ai.taskRouting.proposeFlow" },
        // GitHub, import, cost, simulation, usage, notifications.
        { """{ "github": { "apiBaseUrl": "" } }""", "github.apiBaseUrl" },
        { """{ "github": { "apiBaseUrl": "javascript:alert(1)" } }""", "github.apiBaseUrl" },
        { """{ "github": { "apiBaseUrl": "https://user@github.example.com" } }""", "github.apiBaseUrl" },
        { """{ "github": { "defaultRef": "has space" } }""", "github.defaultRef" },
        { """{ "import": { "maxFiles": 0 } }""", "import.maxFiles" },
        { """{ "import": { "maxFiles": 100001 } }""", "import.maxFiles" },
        { """{ "import": { "maxFileBytes": 0 } }""", "import.maxFileBytes" },
        { """{ "import": { "maxFileBytes": 16777217 } }""", "import.maxFileBytes" },
        { """{ "import": { "maxTotalBytes": 1073741825 } }""", "import.maxTotalBytes" },
        { """{ "import": { "maxFileBytes": 2000, "maxTotalBytes": 1000 } }""", "import.maxTotalBytes" },
        { """{ "import": { "excludeGlobs": [ "ok", "" ] } }""", "import.excludeGlobs[1]" },
        { """{ "import": { "excludeGlobs": [ null ] } }""", "import.excludeGlobs[0]" },
        { """{ "cost": { "currency": "usd" } }""", "cost.currency" },
        { """{ "cost": { "currency": "EURO" } }""", "cost.currency" },
        { """{ "simulation": { "seed": -1 } }""", "simulation.seed" },
        { """{ "simulation": { "durationSeconds": 0 } }""", "simulation.durationSeconds" },
        { """{ "simulation": { "durationSeconds": 86401 } }""", "simulation.durationSeconds" },
        { """{ "simulation": { "arrivalRatePerSecond": 0 } }""", "simulation.arrivalRatePerSecond" },
        { """{ "simulation": { "arrivalRatePerSecond": 100001 } }""", "simulation.arrivalRatePerSecond" },
        { """{ "usage": { "monthlyAiBudgetUsd": -5 } }""", "usage.monthlyAiBudgetUsd" },
        { """{ "usage": { "monthlyAiBudgetUsd": 1000001 } }""", "usage.monthlyAiBudgetUsd" },
        { """{ "usage": { "maxScansPerDay": -1 } }""", "usage.maxScansPerDay" },
        { """{ "usage": { "maxScansPerDay": 10001 } }""", "usage.maxScansPerDay" },
        { """{ "notifications": { "webhookUrl": "not a url" } }""", "notifications.webhookUrl" },
        { """{ "notifications": { "webhookUrl": "https://u:p@hooks.example.com" } }""", "notifications.webhookUrl" },
    };

    /// <summary>A valid provider with the fields in <paramref name="field"/> set or replaced.</summary>
    private static string ProviderJson(string? field = null)
    {
        var provider = JsonNode.Parse($$"""{ "id": "{{ProviderA}}", "kind": "openai", "name": "OpenAI", "model": "gpt" }""")!.AsObject();
        foreach (var (name, value) in JsonNode.Parse("{" + field + "}")!.AsObject())
            provider[name] = value?.DeepClone();
        return provider.ToJsonString();
    }

    private static string Provider(string field) => $$"""{ "ai": { "providers": [ {{ProviderJson(field)}} ] } }""";

    [Theory]
    [MemberData(nameof(InvalidOverrides))]
    public async Task An_invalid_override_is_refused_with_the_field_and_nothing_is_saved(string overrides, string field)
    {
        var factory = _fixture.Factory;
        using var owner = await TestHost.SignInAsync(factory);
        var (workspaceId, _) = await TestHost.CreateProjectAsync(owner);
        var url = $"/api/workspaces/{workspaceId}/settings";

        var response = await owner.PutSettingsAsync(url, Json(overrides));

        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.BadRequest, body);
        var error = (await response.Content.ReadFromJsonAsync<ErrorResponse>())!;
        Assert.Equal("validation_error", error.Type);
        Assert.Contains(error.Errors!, e => e.Field == field);

        var settings = await ReadAsync(await owner.GetAsync(url));
        Assert.Empty(settings["overrides"]!.AsObject());
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("\"text\"")]
    public async Task A_body_that_is_not_a_document_is_refused(string body)
    {
        var factory = _fixture.Factory;
        using var owner = await TestHost.SignInAsync(factory);
        var (workspaceId, _) = await TestHost.CreateProjectAsync(owner);

        var response = await owner.PutSettingsAsync($"/api/workspaces/{workspaceId}/settings", Json(body));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task An_explicit_null_clears_a_nullable_value_and_routing_follows_enabled_providers()
    {
        var factory = _fixture.Factory;
        using var owner = await TestHost.SignInAsync(factory);
        var (workspaceId, _) = await TestHost.CreateProjectAsync(owner);
        var url = $"/api/workspaces/{workspaceId}/settings";

        var routed = await ReadAsync(await owner.PutSettingsAsync(url, Json($$"""
            { "ai": { "providers": [ {{ProviderJson()}} ],
                      "taskRouting": { "inferArchitecture": "{{ProviderA}}", "proposeFlow": null, "critique": "{{ProviderA}}", "explain": null } },
              "notifications": { "webhookUrl": null }, "usage": { "monthlyAiBudgetUsd": null } }
            """)));

        var routing = routed["effective"]!["ai"]!["taskRouting"]!;
        Assert.Equal(ProviderA, (string)routing["inferArchitecture"]!);
        Assert.Equal(ProviderA, (string)routing["critique"]!);
        Assert.Null(routing["proposeFlow"]);
        Assert.Null(routed["effective"]!["notifications"]!["webhookUrl"]);
    }

    [Fact]
    public async Task The_local_policy_allows_a_private_address_and_private_code_forwarding()
    {
        var factory = _fixture.Factory;
        using var owner = await TestHost.SignInAsync(factory);
        var (workspaceId, _) = await TestHost.CreateProjectAsync(owner);

        var saved = await ReadAsync(await owner.PutSettingsAsync($"/api/workspaces/{workspaceId}/settings", Json("""
            { "github": { "apiBaseUrl": "http://192.168.1.50:3000/api/v3" },
              "notifications": { "webhookUrl": "http://localhost:9000/hook" },
              "privacy": { "sendPrivateCodeToAi": true } }
            """)));

        Assert.True((bool)saved["effective"]!["privacy"]!["sendPrivateCodeToAi"]!);
        Assert.Equal("http://192.168.1.50:3000/api/v3", (string)saved["effective"]!["github"]!["apiBaseUrl"]!);
    }
}
