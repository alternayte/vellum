using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Vellum.Shared;

namespace Vellum.Tests.Modules.Settings;

/// <summary>Versions, body limits and input rules of the settings, secrets, instance and setup endpoints.</summary>
[Collection("Integration")]
public class RequestHardeningTests
{
    private readonly IntegrationFixture _fixture;

    public RequestHardeningTests(IntegrationFixture fixture) => _fixture = fixture;

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    private static async Task<HttpResponseMessage> PutAsync(HttpClient client, string url, string json, string? ifMatch)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, url) { Content = Json(json) };
        if (ifMatch is not null)
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        return await client.SendAsync(request);
    }

    private static async Task<string> TypeAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<ErrorResponse>())!.Type;

    [Fact]
    public async Task A_workspace_settings_save_needs_the_version_that_was_read()
    {
        using var factory = TestHost.Create(_fixture);
        using var owner = await TestHost.SignInAsync(factory);
        var (workspaceId, _) = await TestHost.CreateProjectAsync(owner);
        var url = $"/api/workspaces/{workspaceId}/settings";

        var initial = await owner.GetAsync(url);
        Assert.Equal("\"0\"", initial.Headers.ETag!.Tag);
        Assert.Equal(0, (int)(await initial.Content.ReadFromJsonAsync<JsonObject>())!["version"]!);

        var missing = await PutAsync(owner, url, """{ "import": { "maxFiles": 5 } }""", ifMatch: null);
        Assert.Equal(HttpStatusCode.PreconditionRequired, missing.StatusCode);
        Assert.Equal("precondition_required", await TypeAsync(missing));
        var malformed = await PutAsync(owner, url, """{ "import": { "maxFiles": 5 } }""", ifMatch: "\"latest\"");
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);

        // Two people read version 0. The first save wins; the second is told to read again.
        var first = await PutAsync(owner, url, """{ "import": { "maxFiles": 5 } }""", "\"0\"");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal("\"1\"", first.Headers.ETag!.Tag);
        Assert.Equal(1, (int)(await first.Content.ReadFromJsonAsync<JsonObject>())!["version"]!);

        var second = await PutAsync(owner, url, """{ "import": { "maxFiles": 9 } }""", "\"0\"");
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal("conflict", await TypeAsync(second));

        var read = await owner.GetAsync(url);
        Assert.Equal("\"1\"", read.Headers.ETag!.Tag);
        Assert.Equal(5, (int)(await read.Content.ReadFromJsonAsync<JsonObject>())!["effective"]!["import"]!["maxFiles"]!);

        var third = await PutAsync(owner, url, """{ "import": { "maxFiles": 9 } }""", "\"1\"");
        Assert.Equal("\"2\"", third.Headers.ETag!.Tag);

        // Of many saves that start from one version, exactly one is stored.
        var race = await Task.WhenAll(Enumerable.Range(10, 8).Select(n =>
            PutAsync(owner, url, $$"""{ "import": { "maxFiles": {{n}} } }""", "\"2\"")));
        Assert.Single(race, r => r.StatusCode == HttpStatusCode.OK);
        Assert.Equal(7, race.Count(r => r.StatusCode == HttpStatusCode.Conflict));
    }

    [Fact]
    public async Task An_instance_settings_save_needs_the_version_that_was_read()
    {
        var database = await TestHost.CreateDatabaseAsync(_fixture);
        using var factory = TestHost.Create(database);
        var (admin, _, _) = await TestHost.SetUpAsync(factory);
        const string url = "/api/instance/settings";

        var initial = await admin.GetAsync(url);
        Assert.Equal("\"0\"", initial.Headers.ETag!.Tag);
        var settings = (await initial.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.Equal(0, (int)settings["version"]!);
        var body = new JsonObject { ["policy"] = settings["policy"]!.DeepClone(), ["defaults"] = settings["defaults"]!.DeepClone() }.ToJsonString();

        Assert.Equal(HttpStatusCode.PreconditionRequired, (await PutAsync(admin, url, body, ifMatch: null)).StatusCode);

        var first = await PutAsync(admin, url, body, "\"0\"");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal("\"1\"", first.Headers.ETag!.Tag);
        Assert.Equal(1, (int)(await first.Content.ReadFromJsonAsync<JsonObject>())!["version"]!);

        var stale = await PutAsync(admin, url, body, "\"0\"");
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal("conflict", await TypeAsync(stale));
        Assert.Equal("\"1\"", (await admin.GetAsync(url)).Headers.ETag!.Tag);
    }

    [Theory]
    [InlineData("""{ "cost": { "region": "eu\u0000west" } }""", "cost.region")]
    [InlineData("""{ "cost": { "provider": "a\u0007b" } }""", "cost.provider")]
    [InlineData("""{ "github": { "defaultRef": "main\u0000" } }""", "github.defaultRef")]
    [InlineData("""{ "notifications": { "webhookUrl": "https://hooks.example.com/\u0000" } }""", "notifications.webhookUrl")]
    [InlineData("""{ "import": { "excludeGlobs": [ "ok", "a\u0000b" ] } }""", "import.excludeGlobs[1]")]
    [InlineData("""{ "ai": { "providers": [ { "id": "11111111-1111-1111-1111-111111111111", "kind": "openai", "name": "n\u0000", "model": "m" } ] } }""", "ai.providers[0].name")]
    [InlineData("""{ "ai": { "providers": [ { "id": "11111111-1111-1111-1111-111111111111", "kind": "openai", "name": "n", "model": "m\u0000" } ] } }""", "ai.providers[0].model")]
    [InlineData("""{ "ai": { "providers": [ { "id": "11111111-1111-1111-1111-111111111111", "kind": "open\u0000ai", "name": "n", "model": "m" } ] } }""", "ai.providers[0].kind")]
    public async Task A_control_character_in_a_setting_is_a_validation_error(string overrides, string field)
    {
        using var factory = TestHost.Create(_fixture);
        using var owner = await TestHost.SignInAsync(factory);
        var (workspaceId, _) = await TestHost.CreateProjectAsync(owner);

        var response = await owner.PutSettingsAsync($"/api/workspaces/{workspaceId}/settings", Json(overrides));

        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.BadRequest, body);
        Assert.Contains((await response.Content.ReadFromJsonAsync<ErrorResponse>())!.Errors!, e => e.Field == field);
    }

    [Fact]
    public async Task A_body_over_the_limit_is_refused_before_it_is_parsed_and_before_any_role_check()
    {
        using var factory = TestHost.Create(_fixture);
        using var owner = await TestHost.SignInAsync(factory);
        var (workspaceId, _) = await TestHost.CreateProjectAsync(owner);
        using var stranger = await TestHost.SignInAsync(factory);
        using var anonymous = TestHost.Client(factory);
        var root = $"/api/workspaces/{workspaceId}";

        // Not JSON at all: a parser would answer 400, and a role check would answer 403.
        var large = new string('x', 70 * 1024);
        foreach (var (method, url, limitFits) in new[]
                 {
                     (HttpMethod.Put, $"{root}/settings", 64 * 1024),
                     (HttpMethod.Put, $"{root}/secrets/github:token", 32 * 1024),
                     (HttpMethod.Put, "/api/instance/settings", 68 * 1024),
                     (HttpMethod.Post, "/api/setup", 4 * 1024),
                     (HttpMethod.Post, "/api/auth/login", 4 * 1024),
                 })
        {
            using var request = new HttpRequestMessage(method, url) { Content = Json(large) };
            var response = await stranger.SendAsync(request);
            Assert.True(response.StatusCode == HttpStatusCode.RequestEntityTooLarge, $"{method} {url}: {(int)response.StatusCode}");
            Assert.Equal("payload_too_large", await TypeAsync(response));

            // A body of exactly the limit passes the size check and reaches the endpoint.
            using var atLimit = new HttpRequestMessage(method, url) { Content = Json(new string('x', limitFits)) };
            Assert.NotEqual(HttpStatusCode.RequestEntityTooLarge, (await stranger.SendAsync(atLimit)).StatusCode);
        }

        // A caller who is not signed in is refused before the body is read.
        using var unsigned = new HttpRequestMessage(HttpMethod.Put, $"{root}/settings") { Content = Json(large) };
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.SendAsync(unsigned)).StatusCode);

        // The limit counts bytes. This document has 30 000 characters and 90 000 bytes.
        var wide = $$"""{ "cost": { "region": "{{new string('€', 30_000)}}" } }""";
        Assert.True(wide.Length < 64 * 1024 && Encoding.UTF8.GetByteCount(wide) > 64 * 1024);
        var bytes = await owner.PutSettingsAsync($"{root}/settings", Json(wide));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, bytes.StatusCode);

        // The same limit holds when the client does not state a length.
        using var chunked = new HttpRequestMessage(HttpMethod.Put, $"{root}/settings") { Content = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(large))) };
        chunked.Headers.TransferEncodingChunked = true;
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await owner.SendAsync(chunked)).StatusCode);
    }

    [Fact]
    public async Task A_hosted_instance_sends_a_credential_over_https_only()
    {
        var database = await TestHost.CreateDatabaseAsync(_fixture);
        using var factory = TestHost.Create(database, TestHost.Hosted(TestHost.NewMasterKey()),
            services => TestHost.UseResolver(services, new Dictionary<string, string> { ["internal.example"] = "10.0.0.5" }));
        var (admin, created, _) = await TestHost.SetUpAsync(factory, TestHost.SetupToken, https: true);
        var url = $"/api/workspaces/{created.WorkspaceId}/settings";

        async Task<HttpStatusCode> SaveAsync(string overrides) => (await admin.PutSettingsAsync(url, Json(overrides))).StatusCode;
        static string GithubAt(string baseUrl) => $$"""{ "github": { "apiBaseUrl": "{{baseUrl}}" } }""";

        Assert.Equal(HttpStatusCode.BadRequest, await SaveAsync(GithubAt("http://github.public.example/api/v3")));
        Assert.Equal(HttpStatusCode.BadRequest, await SaveAsync("""{ "notifications": { "webhookUrl": "http://hooks.public.example/x" } }"""));
        Assert.Equal(HttpStatusCode.BadRequest, await SaveAsync(
            """{ "ai": { "providers": [ { "id": "11111111-1111-1111-1111-111111111111", "kind": "openai-compatible", "name": "n", "model": "m", "baseUrl": "http://llm.public.example/v1" } ] } }"""));
        Assert.Equal(HttpStatusCode.OK, await SaveAsync(GithubAt("https://github.public.example/api/v3")));

        // The admin allows private networks. Plain http then passes for a private host only.
        var settings = (await admin.GetFromJsonAsync<JsonObject>("/api/instance/settings"))!;
        settings["policy"]!["allowPrivateNetworkUrls"] = true;
        Assert.Equal(HttpStatusCode.OK, (await admin.PutInstanceSettingsAsync(settings)).StatusCode);

        Assert.Equal(HttpStatusCode.OK, await SaveAsync(GithubAt("http://10.0.0.5:3000/api/v3")));
        Assert.Equal(HttpStatusCode.OK, await SaveAsync(GithubAt("http://internal.example:3000/api/v3")));
        Assert.Equal(HttpStatusCode.OK, await SaveAsync(GithubAt("http://localhost:3000")));
        Assert.Equal(HttpStatusCode.BadRequest, await SaveAsync(GithubAt("http://github.public.example/api/v3")));
        Assert.Equal(HttpStatusCode.BadRequest, await SaveAsync(GithubAt("http://93.184.216.34/api/v3")));
    }

    [Fact]
    public async Task An_invited_user_id_must_be_an_existing_account()
    {
        using var factory = TestHost.Create(_fixture);
        using var owner = await TestHost.SignInAsync(factory);
        var (workspaceId, _) = await TestHost.CreateProjectAsync(owner);
        const string marker = "not-a-user role:owner injected-audit-text";

        foreach (var userId in new[] { marker, "", Guid.NewGuid().ToString(), new string('a', 5000) })
        {
            var response = await owner.PostAsJsonAsync($"/api/workspaces/{workspaceId}/members", new { userId, role = "Editor" });
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains((await response.Content.ReadFromJsonAsync<ErrorResponse>())!.Errors!, e => e.Field == "userId");
        }

        var audit = await owner.GetStringAsync($"/api/workspaces/{workspaceId}/audit");
        Assert.DoesNotContain("injected-audit-text", audit);
        Assert.DoesNotContain("aaaaaaaa", audit);
    }
}
