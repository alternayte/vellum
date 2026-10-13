using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vellum.Modules.LoadTests;
using Vellum.Tests.Modules.Promises;

namespace Vellum.Tests.Modules.LoadTests;

/// <summary>
/// Load-test targets, generated k6 scripts and measured results through the HTTP API on real
/// Postgres, over the Orders reference flow. The result files under <c>Fixtures/LoadTests</c>
/// are what k6 wrote for two runs of a generated script against a local stand-in of the
/// Orders API: a baseline, and a run against a slower stand-in that fails 2 % of the requests.
/// A test binds a file to its own script, because each test has its own project and IDs.
/// </summary>
[Collection("Integration")]
public class LoadTestEndpointTests(IntegrationFixture fixture)
{
    private const string Baseline = "orders-baseline.vellum-k6-result.json";
    private const string Regressed = "orders-regressed.vellum-k6-result.json";
    private const string BaselineExport = "orders-baseline.k6-summary-export.json";

    // The committed numbers of the baseline run.
    private const long BaselineRequests = 2000;
    private const double BaselineSeconds = 50.003;
    private const double BaselineMean = 8.406916500000005, BaselineMin = 1.416, BaselineP50 = 8.041, BaselineP95 = 14.42865, BaselineMax = 40.29;

    private static string Url(Guid projectId, string path) => $"/api/projects/{projectId}/load-tests/{path}";

    private static async Task<JsonNode> Read(Task<HttpResponseMessage> request, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await request;
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"expected {expected}, got {(int)response.StatusCode}: {text}");
        return text.Length > 0 ? JsonNode.Parse(text)! : new JsonObject();
    }

    private static async Task Status(HttpStatusCode expected, Task<HttpResponseMessage> request, string what)
    {
        var response = await request;
        Assert.True(response.StatusCode == expected, $"{what}: expected {expected}, got {response.StatusCode} {await response.Content.ReadAsStringAsync()}");
    }

    private static string Field(JsonNode refusal) => refusal["errors"]![0]!["field"]!.GetValue<string>();

    /// <summary>The profile the committed runs were made with: 40 orders each second for a minute, 10 seconds of warm-up.</summary>
    private static JsonObject Profile(OrdersReference orders, double duration = 60, double warmUp = 10, double rate = 40, bool withRelay = false)
    {
        var profile = JsonNode.Parse($$"""
            {
              "name": "Black Friday", "scenarioId": "{{orders.ScenarioId}}", "seed": "42",
              "arrivals": { "kind": "poisson", "ratePerSecond": {{rate}}, "durationSeconds": {{duration}}, "warmUpSeconds": {{warmUp}} },
              "resources": [
                { "elementId": "{{OrdersReference.OrdersApi}}", "concurrency": 8, "queueCapacity": 100 },
                { "elementId": "{{OrdersReference.OrdersDb}}", "concurrency": 4, "queueCapacity": null }
              ],
              "serviceTimes": [
                { "target": "{{OrdersReference.PlaceOrder}}", "time": { "kind": "lognormal", "medianMs": 20, "p95Ms": 90 } },
                { "target": "{{OrdersReference.OrdersDb}}", "time": { "kind": "exponential", "meanMs": 8 } },
                { "target": "{{OrdersReference.InventorySub}}", "time": { "kind": "uniform", "minMs": 5, "maxMs": 25 } },
                { "target": "{{OrdersReference.BillingSub}}", "time": { "kind": "fixed", "ms": 30 } }
              ],
              "subscriptions": [
                { "subscriptionId": "{{OrdersReference.InventorySub}}", "consumers": 2, "queueCapacity": null },
                { "subscriptionId": "{{OrdersReference.BillingSub}}", "consumers": 1, "queueCapacity": 500 }
              ],
              "branches": [], "overrides": [], "outages": []
            }
            """)!.AsObject();
        if (withRelay)
        {
            profile["resources"]!.AsArray().Add(new JsonObject { ["elementId"] = OrdersReference.Relay.ToString(), ["concurrency"] = 1, ["queueCapacity"] = null });
            profile["serviceTimes"]!.AsArray().Add(new JsonObject { ["target"] = OrdersReference.RelayOutbox.ToString(), ["time"] = new JsonObject { ["kind"] = "fixed", ["ms"] = 5 } });
        }
        return profile;
    }

    private sealed record Seeded(WebApplicationFactory<Program> Factory, HttpClient Owner, Guid WorkspaceId, OrdersReference Orders, Guid ProfileId) : IDisposable
    {
        public Guid Project => Orders.ProjectId;
        public void Dispose() => Factory.Dispose();
    }

    private async Task<Seeded> SeedAsync(Func<OrdersReference, JsonObject>? profile = null, bool outbox = false)
    {
        var factory = TestHost.Create(fixture.ConnectionString);
        var owner = await TestHost.SignInAsync(factory);
        var (workspaceId, projectId) = await TestHost.CreateProjectAsync(owner);
        var orders = await OrdersReference.SeedAsync(owner, projectId);
        if (outbox)
            await orders.UseOutboxAsync();
        var created = await Read(owner.PostAsync($"/api/projects/{projectId}/stress/profiles", OrdersReference.Json((profile ?? (o => Profile(o, withRelay: outbox)))(orders))), HttpStatusCode.Created);
        return new Seeded(factory, owner, workspaceId, orders, created["id"]!.GetValue<Guid>());
    }

    /// <summary>What an owner authorises: the base URL, the class and the version of the target as it was read.</summary>
    private static Task<HttpResponseMessage> AuthoriseAsync(HttpClient client, Guid project, JsonNode target, string? baseUrl = null, string? environment = null, long? version = null) =>
        client.PostAsJsonAsync(Url(project, $"targets/{target["id"]}/authorise"), new
        {
            baseUrl = baseUrl ?? target["baseUrl"]!.GetValue<string>(),
            environment = environment ?? target["environment"]!.GetValue<string>(),
            version = version ?? target["version"]!.GetValue<long>(),
        });

    private static async Task<JsonNode> AddTargetAsync(HttpClient client, Guid project, string environment = "test", string baseUrl = "https://orders.test.example", string name = "Test system", bool authorise = true)
    {
        var target = await Read(client.PostAsJsonAsync(Url(project, "targets"), new { name, baseUrl, environment }), HttpStatusCode.Created);
        return authorise ? await Read(AuthoriseAsync(client, project, target)) : target;
    }

    private static Task<JsonNode> ScriptAsync(HttpClient client, Guid project, JsonNode target, Guid profileId, HttpStatusCode expected = HttpStatusCode.Created) =>
        Read(client.PostAsJsonAsync(Url(project, "scripts"), new { targetId = target["id"]!.GetValue<Guid>(), profileId }), expected);

    private static JsonObject Fixture(string name) =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "LoadTests", name)))!.AsObject();

    /// <summary>A start of a run two minutes ago, in whole milliseconds as k6 writes it. Each call gives another instant, so each is another run.</summary>
    private static DateTimeOffset Start() =>
        DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.AddMinutes(-2).ToUnixTimeMilliseconds() - Interlocked.Increment(ref _runs));

    private static long _runs;

    /// <summary>A committed result file as k6 would have written it for <paramref name="script"/> two minutes ago: the identity is the script's, the numbers stay.</summary>
    private static JsonObject Bind(string name, JsonNode script, DateTimeOffset? at = null)
    {
        var file = Fixture(name);
        var startedAt = at ?? Start();
        if (file["metrics"] is JsonObject metrics)
        {
            var old = metrics.Single(m => m.Key.StartsWith("vellum_script_", StringComparison.Ordinal));
            metrics.Remove(old.Key);
            metrics[$"vellum_script_{script["hash"]}"] = old.Value!.DeepClone();
            file["setup_data"]!["startedAtMs"] = startedAt.ToUnixTimeMilliseconds();
            return file;
        }

        file["scriptHash"] = script["hash"]!.GetValue<string>();
        foreach (var member in new[] { "scenarioId", "scenarioVersion", "modelStreamId", "modelVersion", "targetId", "environment" })
            file[member] = script[member]!.DeepClone();
        file["baseUrlHash"] = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(script["baseUrl"]!.GetValue<string>())));
        file["startedAt"] = startedAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");
        return file;
    }

    private static Task<JsonNode> ImportAsync(HttpClient client, Guid project, JsonNode file, HttpStatusCode expected = HttpStatusCode.Created) =>
        Read(client.PostAsync(Url(project, "results"), OrdersReference.Json(file)), expected);

    private static JsonNode Row(JsonNode comparison, string scope, string metric, Guid? target = null) =>
        comparison["rows"]!.AsArray().Single(r => r!["scope"]!.GetValue<string>() == scope && r["metric"]!.GetValue<string>() == metric
            && r["target"]?.GetValue<Guid>() == target)!;

    [Fact]
    public async Task A_target_states_its_class_and_only_an_owner_who_is_a_person_authorises_it()
    {
        using var seeded = await SeedAsync();
        var (owner, project) = (seeded.Owner, seeded.Project);

        // No class, no target: production is never what a target is by omission.
        Assert.Equal("environment", Field(await Read(owner.PostAsJsonAsync(Url(project, "targets"), new { name = "Orders", baseUrl = "https://orders.example" }), HttpStatusCode.BadRequest)));
        Assert.Equal("environment", Field(await Read(owner.PostAsJsonAsync(Url(project, "targets"), new { name = "Orders", baseUrl = "https://orders.example", environment = "prod" }), HttpStatusCode.BadRequest)));
        // The URL is stored and shown, so it holds no credential, and it is a base: no query.
        foreach (var url in new[] { "https://user:secret@orders.example", "https://orders.example/?key=1", "ftp://orders.example", "orders.example", "" })
            Assert.Equal("baseUrl", Field(await Read(owner.PostAsJsonAsync(Url(project, "targets"), new { name = "Orders", baseUrl = url, environment = "test" }), HttpStatusCode.BadRequest)));

        var target = await AddTargetAsync(owner, project, baseUrl: "https://Orders.Test.example/api/", authorise: false);
        var id = target["id"]!.GetValue<Guid>();
        Assert.Equal("https://orders.test.example/api", target["baseUrl"]!.GetValue<string>());
        Assert.Null(target["authorisation"]);

        // An editor changes a target and cannot authorise it. An agent token cannot either, whatever its scopes and its user.
        using var editor = await TestHost.SignInMemberAsync(seeded.Factory, owner, seeded.WorkspaceId, "Editor");
        using var viewer = await TestHost.SignInMemberAsync(seeded.Factory, owner, seeded.WorkspaceId, "Viewer");
        var agent = await TestHost.CreateAgentAsync(seeded.Factory, owner, seeded.WorkspaceId);
        var setOverride = new { allowNonIdempotent = true, allowFailureInjection = false, reason = "Window", confirmName = "Test system" };
        await Status(HttpStatusCode.Forbidden, AuthoriseAsync(editor, project, target), "editor authorise");
        await Status(HttpStatusCode.Forbidden, editor.PutAsJsonAsync(Url(project, $"targets/{id}/override"), setOverride), "editor override");
        await Status(HttpStatusCode.Forbidden, viewer.PostAsJsonAsync(Url(project, "targets"), new { name = "V", baseUrl = "https://v.example", environment = "test" }), "viewer add");
        await Status(HttpStatusCode.OK, viewer.GetAsync(Url(project, "targets")), "viewer list");
        foreach (var refused in new[]
                 {
                     await AuthoriseAsync(agent.Client, project, target),
                     await agent.Client.PutAsJsonAsync(Url(project, $"targets/{id}/override"), setOverride),
                     await agent.Client.DeleteAsync(Url(project, $"targets/{id}/authorisation")),
                 })
        {
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
            Assert.Equal("human_only", (await refused.Content.ReadFromJsonAsync<JsonObject>())!["type"]!.GetValue<string>());
        }
        Assert.Null((await Read(owner.GetAsync(Url(project, "targets"))))[0]!["authorisation"]);

        // The owner names what was read: the stored URL, the class and the version. A difference in one of them authorises nothing.
        await Status(HttpStatusCode.Conflict, AuthoriseAsync(owner, project, target, baseUrl: "https://orders.example"), "authorise another URL");
        await Status(HttpStatusCode.Conflict, AuthoriseAsync(owner, project, target, baseUrl: "https://Orders.Test.example/api/"), "authorise another spelling of the URL");
        await Status(HttpStatusCode.Conflict, AuthoriseAsync(owner, project, target, environment: "staging"), "authorise another class");
        await Status(HttpStatusCode.Conflict, AuthoriseAsync(owner, project, target, version: target["version"]!.GetValue<long>() + 1), "authorise another version");
        await Status(HttpStatusCode.Conflict, owner.PostAsJsonAsync(Url(project, $"targets/{id}/authorise"), new { baseUrl = "https://orders.test.example/api" }), "authorise with the URL only");
        // The class changes to production and back between the read and the authorisation: the URL and the class are the same, the version is not.
        await Read(editor.PutAsJsonAsync(Url(project, $"targets/{id}"), new { name = "Test system", baseUrl = "https://orders.test.example/api", environment = "production" }));
        var back = await Read(editor.PutAsJsonAsync(Url(project, $"targets/{id}"), new { name = "Test system", baseUrl = "https://orders.test.example/api", environment = "test" }));
        await Status(HttpStatusCode.Conflict, AuthoriseAsync(owner, project, target), "authorise what was read before two changes");
        Assert.Null((await Read(owner.GetAsync(Url(project, "targets"))))[0]!["authorisation"]);
        var authorised = await Read(AuthoriseAsync(owner, project, back));
        var me = (await owner.GetFromJsonAsync<JsonObject>("/api/auth/me"))!["id"]!.GetValue<string>();
        Assert.Equal((me, "https://orders.test.example/api", "test"),
            (authorised["authorisation"]!["by"]!.GetValue<string>(), authorised["authorisation"]!["baseUrl"]!.GetValue<string>(), authorised["authorisation"]!["environment"]!.GetValue<string>()));

        // A new name keeps the authorisation. A new URL voids it, and so does a new class.
        var renamed = await Read(editor.PutAsJsonAsync(Url(project, $"targets/{id}"), new { name = "Orders test", baseUrl = "https://orders.test.example/api", environment = "test" }));
        Assert.NotNull(renamed["authorisation"]);
        var moved = await Read(editor.PutAsJsonAsync(Url(project, $"targets/{id}"), new { name = "Orders test", baseUrl = "https://orders.example", environment = "test" }));
        Assert.Null(moved["authorisation"]);
        await Read(AuthoriseAsync(owner, project, moved));

        // An override is for production only, with a reason and the name typed again.
        Assert.Equal("environment", Field(await Read(owner.PutAsJsonAsync(Url(project, $"targets/{id}/override"), setOverride), HttpStatusCode.BadRequest)));
        var production = await Read(editor.PutAsJsonAsync(Url(project, $"targets/{id}"), new { name = "Orders test", baseUrl = "https://orders.example", environment = "production" }));
        Assert.Null(production["authorisation"]);
        Assert.Equal("reason", Field(await Read(owner.PutAsJsonAsync(Url(project, $"targets/{id}/override"), new { allowNonIdempotent = true, confirmName = "Orders test" }), HttpStatusCode.BadRequest)));
        Assert.Equal("confirmName", Field(await Read(owner.PutAsJsonAsync(Url(project, $"targets/{id}/override"), setOverride), HttpStatusCode.BadRequest)));
        var overridden = await Read(owner.PutAsJsonAsync(Url(project, $"targets/{id}/override"), new { allowNonIdempotent = true, allowFailureInjection = false, reason = "Window CHG-1", confirmName = "Orders test" }));
        Assert.Equal((true, false, "Window CHG-1", me),
            (overridden["override"]!["allowNonIdempotent"]!.GetValue<bool>(), overridden["override"]!["allowFailureInjection"]!.GetValue<bool>(),
                overridden["override"]!["reason"]!.GetValue<string>(), overridden["override"]!["by"]!.GetValue<string>()));
        // The class changes: the override was for a production system that this target no longer is.
        var staged = await Read(editor.PutAsJsonAsync(Url(project, $"targets/{id}"), new { name = "Orders test", baseUrl = "https://orders.example", environment = "staging" }));
        Assert.Null(staged["override"]);

        // A removal takes the authorisation with it.
        await Read(AuthoriseAsync(owner, project, staged));
        await Status(HttpStatusCode.NoContent, editor.DeleteAsync(Url(project, $"targets/{id}")), "remove");
        Assert.Empty((await Read(owner.GetAsync(Url(project, "targets")))).AsArray());
        await Status(HttpStatusCode.NotFound, AuthoriseAsync(owner, project, staged), "authorise a removed target");

        // Each change of a permission is in the audit log of the workspace, with the origin and no path. An entry
        // names the class and the origin that the permission was for: the state before the event that takes it.
        var audit = (await Read(owner.GetAsync($"/api/workspaces/{seeded.WorkspaceId}/audit?limit=50"))).AsArray()
            .Where(a => a!["action"]!.GetValue<string>().StartsWith("load-test.", StringComparison.Ordinal))
            .Select(a => $"{a!["action"]} {a["target"]}".Replace(id.ToString(), "T")).Reverse().ToList();
        Assert.Equal(
        [
            "load-test.target.authorised load-test-target:T environment:test origin:https://orders.test.example",
            "load-test.target.authorisation-voided load-test-target:T environment:test origin:https://orders.test.example cause:base-url-changed",
            "load-test.target.authorised load-test-target:T environment:test origin:https://orders.example",
            "load-test.target.authorisation-voided load-test-target:T environment:test origin:https://orders.example cause:environment-changed",
            "load-test.target.override-set load-test-target:T environment:production origin:https://orders.example non-idempotent:allowed failure-injection:excluded",
            "load-test.target.override-cleared load-test-target:T environment:production origin:https://orders.example cause:environment-changed",
            "load-test.target.authorised load-test-target:T environment:staging origin:https://orders.example",
            "load-test.target.authorisation-voided load-test-target:T environment:staging origin:https://orders.example cause:target-removed",
        ], audit);
    }

    [Fact]
    public async Task A_script_needs_an_authorised_target_and_a_production_target_excludes_writes_until_an_owner_overrides()
    {
        using var seeded = await SeedAsync();
        var (owner, project) = (seeded.Owner, seeded.Project);

        var target = await AddTargetAsync(owner, project, authorise: false);
        var refused = await ScriptAsync(owner, project, target, seeded.ProfileId, HttpStatusCode.UnprocessableEntity);
        Assert.Equal("target-not-authorised", refused["type"]!.GetValue<string>());
        Assert.Empty((await Read(owner.GetAsync(Url(project, "scripts")))).AsArray());

        await Read(AuthoriseAsync(owner, project, target));
        var script = await ScriptAsync(owner, project, target, seeded.ProfileId);
        var content = script["content"]!.GetValue<string>();
        Assert.Contains($"const SCRIPT_HASH = \"{script["hash"]}\";", content);
        Assert.Equal($"k6 run -e VELLUM_BASE_URL='https://orders.test.example' vellum-k6-{script["hash"]!.GetValue<string>()[..12]}.js", script["command"]!.GetValue<string>());
        Assert.Contains("VELLUM_AUTHORIZATION: ${{ secrets.VELLUM_AUTHORIZATION }}", script["ciWorkflow"]!.GetValue<string>());
        Assert.Contains("actions/upload-artifact@v4", script["ciWorkflow"]!.GetValue<string>());
        Assert.Equal(["save-order", OrdersReference.PublishStep], script["plan"]!["notCovered"]!.AsArray().Select(n => n!["stepId"]!.GetValue<string>()));

        // The same inputs give the same script: one row, the same bytes.
        var again = await ScriptAsync(owner, project, target, seeded.ProfileId, HttpStatusCode.OK);
        Assert.Equal((script["id"]!.GetValue<Guid>(), content), (again["id"]!.GetValue<Guid>(), again["content"]!.GetValue<string>()));
        var listed = Assert.Single((await Read(owner.GetAsync(Url(project, "scripts")))).AsArray())!;
        Assert.Null(listed["content"]);
        Assert.Equal(content, (await Read(owner.GetAsync(Url(project, $"scripts/{script["id"]}"))))["content"]!.GetValue<string>());

        // A viewer reads a script and generates none. A token with the export scope generates one.
        using var viewer = await TestHost.SignInMemberAsync(seeded.Factory, owner, seeded.WorkspaceId, "Viewer");
        await Status(HttpStatusCode.Forbidden, viewer.PostAsJsonAsync(Url(project, "scripts"), new { targetId = target["id"]!.GetValue<Guid>(), profileId = seeded.ProfileId }), "viewer generate");
        await Status(HttpStatusCode.OK, viewer.GetAsync(Url(project, $"scripts/{script["id"]}")), "viewer read");
        var exporter = await TestHost.CreateAgentAsync(seeded.Factory, owner, seeded.WorkspaceId, ["export"]);
        await Status(HttpStatusCode.OK, exporter.Client.PostAsJsonAsync(Url(project, "scripts"), new { targetId = target["id"]!.GetValue<Guid>(), profileId = seeded.ProfileId }), "agent generate");

        // Production: the only HTTP step is a POST, so nothing is left to drive.
        var production = await AddTargetAsync(owner, project, "production", "https://orders.example", "Production");
        var excluded = await ScriptAsync(owner, project, production, seeded.ProfileId, HttpStatusCode.UnprocessableEntity);
        Assert.Equal("no-http-step", excluded["type"]!.GetValue<string>());
        Assert.Contains(excluded["reasons"]!.AsArray(), r => r!.GetValue<string>().Contains("Excluded: POST is a non-idempotent operation"));

        await Read(owner.PutAsJsonAsync(Url(project, $"targets/{production["id"]}/override"), new { allowNonIdempotent = true, allowFailureInjection = false, reason = "Agreed window", confirmName = "Production" }));
        var allowed = await ScriptAsync(owner, project, production, seeded.ProfileId);
        Assert.Contains("//   Non-idempotent operations (POST, PUT, PATCH, DELETE): ALLOWED by override.", allowed["content"]!.GetValue<string>());
        Assert.Contains("//   Reason: \"Agreed window\"", allowed["content"]!.GetValue<string>());

        // The base URL changes: the authorisation is void, and no script is generated for the new URL.
        await Read(owner.PutAsJsonAsync(Url(project, $"targets/{target["id"]}"), new { name = "Test system", baseUrl = "https://other.example", environment = "test" }));
        Assert.Equal("target-not-authorised", (await ScriptAsync(owner, project, target, seeded.ProfileId, HttpStatusCode.UnprocessableEntity))["type"]!.GetValue<string>());
    }

    [Fact]
    public async Task An_imported_result_is_measured_and_bound_to_its_script_and_a_file_without_that_identity_is_refused()
    {
        using var seeded = await SeedAsync();
        var (owner, project) = (seeded.Owner, seeded.Project);
        var target = await AddTargetAsync(owner, project);
        var script = await ScriptAsync(owner, project, target, seeded.ProfileId);
        var me = (await owner.GetFromJsonAsync<JsonObject>("/api/auth/me"))!["id"]!.GetValue<string>();

        var startedAt = Start();
        var file = Bind(Baseline, script, startedAt);
        var imported = await ImportAsync(owner, project, file);
        var result = imported["result"]!;
        Assert.Equal("measured", result["label"]!.GetValue<string>());
        Assert.Equal(("k6-import", "vellum-k6-result/2", script["hash"]!.GetValue<string>(), me, "Test system", "https://orders.test.example", "test"),
            (result["provenance"]!["kind"]!.GetValue<string>(), result["provenance"]!["format"]!.GetValue<string>(), result["provenance"]!["scriptHash"]!.GetValue<string>(),
                result["provenance"]!["importedBy"]!.GetValue<string>(), result["provenance"]!["targetName"]!.GetValue<string>(),
                result["provenance"]!["baseUrl"]!.GetValue<string>(), result["provenance"]!["environment"]!.GetValue<string>()));
        Assert.Null(result["provenance"]!["importedVia"]);
        Assert.Equal((script["scenarioId"]!.GetValue<Guid>(), script["scenarioVersion"]!.GetValue<long>(), script["modelVersion"]!.GetValue<long>()),
            (result["scenarioId"]!.GetValue<Guid>(), result["scenarioVersion"]!.GetValue<long>(), result["modelVersion"]!.GetValue<long>()));
        var operation = Assert.Single(imported["operations"]!.AsArray())!;
        Assert.Equal(("measured", BaselineRequests, 0L, 0d, BaselineP95, "measured"),
            (operation["label"]!.GetValue<string>(), operation["requests"]!.GetValue<long>(), operation["unexpectedStatus"]!.GetValue<long>(), operation["unexpectedStatusRate"]!.GetValue<double>(),
                operation["latency"]!["p95Ms"]!.GetValue<double>(), operation["latency"]!["label"]!.GetValue<string>()));
        // The requests in the seconds after the warm-up.
        Assert.Equal(BaselineRequests / BaselineSeconds, operation["throughputPerSecond"]!.GetValue<double>(), 9);
        Assert.Equal(["save-order", OrdersReference.PublishStep], imported["notCovered"]!.AsArray().Select(n => n!["stepId"]!.GetValue<string>()));

        // The same file is imported once.
        await ImportAsync(owner, project, file, HttpStatusCode.Conflict);

        async Task<JsonNode> Refuse(Action<JsonObject> change, HttpStatusCode expected = HttpStatusCode.BadRequest, string name = Baseline)
        {
            var changed = Bind(name, script);
            change(changed);
            return await ImportAsync(owner, project, changed, expected);
        }

        // A hash that names no script of this project: nothing binds the numbers, and nothing is stored.
        var unknown = await Refuse(f => f["scriptHash"] = new string('a', 64), HttpStatusCode.UnprocessableEntity);
        Assert.Equal(("unknown_script", "scriptHash"), (unknown["type"]!.GetValue<string>(), Field(unknown)));

        // A file that states another scenario revision or target than its script was generated for.
        var otherRevision = await Refuse(f => f["scenarioVersion"] = f["scenarioVersion"]!.GetValue<long>() + 1, HttpStatusCode.UnprocessableEntity);
        Assert.Equal(("identity_mismatch", "scenarioVersion"), (otherRevision["type"]!.GetValue<string>(), Field(otherRevision)));
        Assert.Equal("targetId", Field(await Refuse(f => f["targetId"] = Guid.NewGuid().ToString(), HttpStatusCode.UnprocessableEntity)));
        Assert.Equal("baseUrlHash", Field(await Refuse(f => f["baseUrlHash"] = new string('b', 64), HttpStatusCode.UnprocessableEntity)));
        Assert.Equal("modelVersion", Field(await Refuse(f => f["modelVersion"] = 1, HttpStatusCode.UnprocessableEntity)));

        // Numbers that the script cannot have written. Each error names the member of the file.
        Assert.Equal("operations[0]", Field(await Refuse(f => f["operations"]![0]!["unexpectedStatus"] = BaselineRequests + 1)));
        Assert.Equal("operations[0].latencyMs", Field(await Refuse(f => f["operations"]![0]!["latencyMs"]!["count"] = BaselineRequests - 1)));
        // A success latency that holds the durations of expected failures: this script expects none.
        Assert.Equal("operations[0].expectedFailureLatencyMs", Field(await Refuse(f =>
        {
            f["operations"]![0]!["expectedFailures"] = 1;
            f["operations"]![0]!["unexpectedStatus"] = 0;
            f["operations"]![0]!["requests"] = BaselineRequests + 1;
        })));
        Assert.Equal("operations[0].latencyMs", Field(await Refuse(f => f["operations"]![0]!["latencyMs"]!["p95"] = 99)));
        Assert.Equal("operations[0].latencyMs.p50", Field(await Refuse(f => f["operations"]![0]!["latencyMs"]!["p50"] = -1)));
        Assert.Equal("operations[0].requests", Field(await Refuse(f => f["operations"]![0]!["requests"] = 12.5)));
        Assert.Equal("operations[0].operationId", Field(await Refuse(f => f["operations"]![0]!["operationId"] = Guid.NewGuid().ToString())));
        Assert.Equal("operations", Field(await Refuse(f => f["operations"]!.AsArray().Clear())));
        Assert.Equal("window.warmUpMs", Field(await Refuse(f => f["window"]!["warmUpMs"] = 0)));
        Assert.Equal("window.elapsedMs", Field(await Refuse(f => f["window"]!["elapsedMs"] = 9000)));
        Assert.Equal("startedAt", Field(await Refuse(f => f["startedAt"] = "2020-01-01T00:00:00Z")));
        Assert.Equal("flow.latencyMs", Field(await Refuse(f => f["flow"]!["latencyMs"] = null)));
        Assert.Equal("format", Field(await Refuse(f => f["format"] = "vellum-k6-result/9")));

        // The load. The profile plans 2400 iterations in 60 seconds. A run that k6 made with other options,
        // a shorter run and a run with other iteration counts did not apply that load: each is refused with the reason.
        var otherOptions = await Refuse(f => f["load"]!["effective"] = "scenarios=default");
        Assert.Equal("load.effective", Field(otherOptions));
        Assert.Contains("did not apply the load of the profile", otherOptions["errors"]![0]!["message"]!.GetValue<string>());
        var shorter = await Refuse(f => f["window"]!["elapsedMs"] = 30_000);
        Assert.Equal("window.elapsedMs", Field(shorter));
        Assert.Contains("needs 57000 ms", shorter["errors"]![0]!["message"]!.GetValue<string>());
        var fewer = await Refuse(f => f["load"]!["iterations"] = 2200);
        Assert.Equal("load.iterations", Field(fewer));
        Assert.Contains("may differ by 120", fewer["errors"]![0]!["message"]!.GetValue<string>());
        Assert.Equal("load.plannedIterations", Field(await Refuse(f => f["load"]!["plannedIterations"] = 2200)));
        // The end of a run is bounded as its start is: a run that started two minutes ago has not run for an hour.
        var endless = await Refuse(f => f["window"]!["elapsedMs"] = 3_600_000);
        Assert.Equal("window.elapsedMs", Field(endless));
        Assert.Contains("ends in the future", endless["errors"]![0]!["message"]!.GetValue<string>());
        Assert.Equal("scriptHash", Field(await Refuse(f => f["scriptHash"] = "ABC")));
        Assert.Equal("invalid_body", (await Read(owner.PostAsync(Url(project, "results"), new StringContent("{ \"format\": ", Encoding.UTF8, "application/json")), HttpStatusCode.BadRequest))["type"]!.GetValue<string>());
        var large = Bind(Baseline, script);
        large["padding"] = new string('x', 300_000);
        await ImportAsync(owner, project, large, HttpStatusCode.RequestEntityTooLarge);

        // The summary export of the same run is the same run. It is not stored a second time, and the refusal names the stored result.
        var sameRun = await ImportAsync(owner, project, Bind(BaselineExport, script, startedAt), HttpStatusCode.Conflict);
        Assert.Contains(result["id"]!.GetValue<Guid>().ToString(), sameRun["detail"]!.GetValue<string>());
        Assert.Single((await Read(owner.GetAsync(Url(project, "results")))).AsArray());

        // The summary export of a run carries the identity in the name of a metric, and gives the same numbers.
        var export = await ImportAsync(owner, project, Bind(BaselineExport, script));
        Assert.Equal(("measured", "k6-summary-export"), (export["result"]!["label"]!.GetValue<string>(), export["result"]!["provenance"]!["format"]!.GetValue<string>()));
        Assert.Equal(operation["latency"]!.ToJsonString(), export["operations"]![0]!["latency"]!.ToJsonString());
        Assert.Equal(operation["throughputPerSecond"]!.GetValue<double>(), export["operations"]![0]!["throughputPerSecond"]!.GetValue<double>());
        // An export of a script that Vellum did not generate has no such metric: it cannot say what it measured.
        var anonymous = await Refuse(f => f["metrics"]!.AsObject().Remove($"vellum_script_{script["hash"]}"), name: BaselineExport);
        Assert.Equal("metrics", Field(anonymous));
        Assert.Contains("names no Vellum script", anonymous["errors"]![0]!["message"]!.GetValue<string>());
        Assert.Equal("metrics.vellum_op0_duration", Field(await Refuse(f => f["metrics"]!["vellum_op0_duration"]!["count"] = 5, name: BaselineExport)));

        // An agent may submit evidence. The same checks run, and the row names the token.
        using var viewer = await TestHost.SignInMemberAsync(seeded.Factory, owner, seeded.WorkspaceId, "Viewer");
        await ImportAsync(viewer, project, Bind(Regressed, script), HttpStatusCode.Forbidden);
        var agent = await TestHost.CreateAgentAsync(seeded.Factory, owner, seeded.WorkspaceId, ["propose"]);
        var forged = Bind(Regressed, script);
        forged["scriptHash"] = new string('c', 64);
        Assert.Equal("unknown_script", (await ImportAsync(agent.Client, project, forged, HttpStatusCode.UnprocessableEntity))["type"]!.GetValue<string>());
        var byAgent = await ImportAsync(agent.Client, project, Bind(Regressed, script));
        Assert.Equal($"agent-token:{agent.TokenId}", byAgent["result"]!["provenance"]!["importedVia"]!.GetValue<string>());

        // Only what was accepted is stored.
        Assert.Equal(3, (await Read(owner.GetAsync(Url(project, "results")))).AsArray().Count);

        // The authorisation is revoked: a later run of the script is a run against a target that nobody allows.
        var revoked = await Read(owner.DeleteAsync(Url(project, $"targets/{target["id"]}/authorisation")));
        Assert.Equal("target_not_authorised", (await ImportAsync(owner, project, Bind(Regressed, script), HttpStatusCode.UnprocessableEntity))["type"]!.GetValue<string>());

        // An owner authorises the target again. That is another authorisation: it does not admit a run of the old script.
        var again = await Read(AuthoriseAsync(owner, project, revoked));
        Assert.Equal("target_not_authorised", (await ImportAsync(owner, project, Bind(Regressed, script), HttpStatusCode.UnprocessableEntity))["type"]!.GetValue<string>());
        var fresh = await ScriptAsync(owner, project, again, seeded.ProfileId);
        Assert.NotEqual(script["hash"]!.GetValue<string>(), fresh["hash"]!.GetValue<string>());
        await ImportAsync(owner, project, Bind(Regressed, fresh));
    }

    [Fact]
    public async Task A_value_is_measured_only_as_the_service_time_of_an_operation_with_the_numbers_of_a_result_that_covers_it()
    {
        using var seeded = await SeedAsync();
        var (owner, project) = (seeded.Owner, seeded.Project);
        var script = await ScriptAsync(owner, project, await AddTargetAsync(owner, project), seeded.ProfileId);
        var resultId = (await ImportAsync(owner, project, Bind(Baseline, script)))["result"]!["id"]!.GetValue<Guid>();
        var good = $"k6:{resultId}";

        // Another project of the same user has a result too. Its ID names nothing here.
        var (_, otherProject) = await TestHost.CreateProjectAsync(owner);
        var other = await OrdersReference.SeedAsync(owner, otherProject);
        var otherProfile = (await Read(owner.PostAsync($"/api/projects/{otherProject}/stress/profiles", OrdersReference.Json(Profile(other))), HttpStatusCode.Created))["id"]!.GetValue<Guid>();
        var otherScript = await ScriptAsync(owner, otherProject, await AddTargetAsync(owner, otherProject), otherProfile);
        var foreign = (await ImportAsync(owner, otherProject, Bind(Baseline, otherScript)))["result"]!["id"]!.GetValue<Guid>();

        // A second scenario of this project. The result is of the first one.
        var second = seeded.Orders.WithoutOutbox();
        (second["id"], second["name"]) = (Guid.NewGuid().ToString(), "Place an order again");
        await TestHost.Created(owner.PostAsync($"/api/projects/{project}/scenarios", OrdersReference.Json(second)));

        async Task<JsonNode> Save(Action<JsonObject> change, HttpStatusCode expected)
        {
            var profile = Profile(seeded.Orders);
            change(profile);
            return await Read(owner.PutAsync($"/api/projects/{project}/stress/profiles/{seeded.ProfileId}", OrdersReference.Json(profile)), expected);
        }
        static void Measure(JsonNode entry, string? reference) => (entry["label"], entry["measurementRef"]) = ("measured", reference);

        // The name resolves to an imported result of this project, in its one spelling.
        foreach (var reference in new[] { $"k6:{Guid.NewGuid()}", $"k6:{foreign}", "k6:run-1", resultId.ToString(), $"k6:{resultId.ToString().ToUpperInvariant()}", $"K6:{resultId}", null })
            Assert.Equal("serviceTimes[0].measurementRef", Field(await Save(p => Measure(p["serviceTimes"]![0]!, reference), HttpStatusCode.BadRequest)));

        // The result covers the operation that the script drove. It holds no latency of the write to the store or of a handler.
        Assert.Equal("serviceTimes[1].measurementRef", Field(await Save(p => Measure(p["serviceTimes"]![1]!, good), HttpStatusCode.BadRequest)));
        Assert.Equal("serviceTimes[2].measurementRef", Field(await Save(p => Measure(p["serviceTimes"]![2]!, good), HttpStatusCode.BadRequest)));
        // The result is of the scenario of the profile.
        var otherScenario = await Save(p => { p["scenarioId"] = second["id"]!.GetValue<string>(); Measure(p["serviceTimes"]![0]!, good); }, HttpStatusCode.BadRequest);
        Assert.Equal("serviceTimes[0].measurementRef", Field(otherScenario));
        Assert.Contains("another scenario", otherScenario["errors"]![0]!["message"]!.GetValue<string>());

        // A k6 result measures no worker count, no consumer count, no probability, no policy and no arrival rate.
        Assert.Equal("resources[0].label", Field(await Save(p => Measure(p["resources"]![0]!, good), HttpStatusCode.BadRequest)));
        Assert.Equal("subscriptions[0].label", Field(await Save(p => Measure(p["subscriptions"]![0]!, good), HttpStatusCode.BadRequest)));
        Assert.Equal("arrivals.label", Field(await Save(p => Measure(p["arrivals"]!, good), HttpStatusCode.BadRequest)));
        Assert.Equal("branches[0].label", Field(await Save(p =>
        {
            p["branches"]!.AsArray().Add(new JsonObject { ["stepId"] = "choose", ["probability"] = 0.5 });
            Measure(p["branches"]![0]!, good);
        }, HttpStatusCode.BadRequest)));
        Assert.Equal("overrides[0].label", Field(await Save(p =>
        {
            p["overrides"]!.AsArray().Add(new JsonObject { ["target"] = OrdersReference.PlaceOrder.ToString(), ["timeoutMs"] = 500 });
            Measure(p["overrides"]![0]!, good);
        }, HttpStatusCode.BadRequest)));

        // A person picks the result and the distribution. The numbers are the numbers of the result: a typed number is not read.
        async Task<JsonNode> Stored(JsonObject time)
        {
            var saved = await Save(p => { Measure(p["serviceTimes"]![0]!, good); p["serviceTimes"]![0]!["time"] = time; }, HttpStatusCode.OK);
            Assert.Equal("measured", saved["profile"]!["serviceTimes"]![0]!["label"]!.GetValue<string>());
            return saved["profile"]!["serviceTimes"]![0]!["time"]!;
        }
        var typed = await Stored(new JsonObject { ["kind"] = "lognormal", ["medianMs"] = 1, ["p95Ms"] = 2 });
        Assert.Equal((BaselineP50, BaselineP95), (typed["medianMs"]!.GetValue<double>(), typed["p95Ms"]!.GetValue<double>()));
        Assert.Equal(BaselineMean, (await Stored(new JsonObject { ["kind"] = "fixed", ["ms"] = 0.001 }))["ms"]!.GetValue<double>());
        Assert.Equal(BaselineMean, (await Stored(new JsonObject { ["kind"] = "exponential" }))["meanMs"]!.GetValue<double>());
        var uniform = await Stored(new JsonObject { ["kind"] = "uniform", ["minMs"] = 0, ["maxMs"] = 1 });
        Assert.Equal((BaselineMin, BaselineMax), (uniform["minMs"]!.GetValue<double>(), uniform["maxMs"]!.GetValue<double>()));

        var profileUrl = $"/api/projects/{project}/stress/profiles/{seeded.ProfileId}";
        var measurement = Assert.Single((await Read(owner.GetAsync(profileUrl)))["measurements"]!.AsArray())!;
        Assert.Equal((OrdersReference.PlaceOrder, good, false), (measurement["target"]!.GetValue<Guid>(), measurement["measurementRef"]!.GetValue<string>(), measurement["measurementStale"]!.GetValue<bool>()));

        // A measured input does not make an output measured: every number the simulator computes stays an estimate.
        var run = await Read(owner.PostAsync($"{profileUrl}/runs", null), HttpStatusCode.Created);
        Assert.Equal("estimated", run["result"]!["label"]!.GetValue<string>());
        Assert.Equal("estimated", run["result"]!["operations"]![0]!["latency"]!["label"]!.GetValue<string>());
        Assert.DoesNotContain("\"label\":\"measured\"", run["result"]!["flow"]!.ToJsonString());
        static List<JsonNode> MeasuredRows(JsonNode stressRun) => [.. stressRun["result"]!["assumptions"]!.AsArray().Where(a => a!["label"]!.GetValue<string>() == "measured").Select(a => a!)];
        Assert.All(MeasuredRows(run), a => Assert.Equal(("service-time", OrdersReference.PlaceOrder, good, false),
            (a["group"]!.GetValue<string>(), a["target"]!.GetValue<Guid>(), a["measurementRef"]!.GetValue<string>(), a["measurementStale"]!.GetValue<bool>())));
        Assert.Equal(3, MeasuredRows(run).Count);
        static List<string> Reasons(JsonNode stressRun) => [.. stressRun["result"]!["confidence"]!["reasons"]!.AsArray().Select(r => r!.GetValue<string>())];
        Assert.DoesNotContain(Reasons(run), r => r.Contains("POST /orders"));

        // The model moves on. The result is stale, and the label says so wherever it shows: in the profile and in a new run.
        await TestHost.Created(owner.PostAsJsonAsync($"/api/projects/{project}/elements", new { id = Guid.NewGuid(), kind = "system", name = "Later" }));
        measurement = Assert.Single((await Read(owner.GetAsync(profileUrl)))["measurements"]!.AsArray())!;
        Assert.Equal((true, "model"), (measurement["measurementStale"]!.GetValue<bool>(), measurement["staleBecause"]![0]!.GetValue<string>()));
        var later = await Read(owner.PostAsync($"{profileUrl}/runs", null), HttpStatusCode.Created);
        Assert.All(MeasuredRows(later), a => Assert.True(a["measurementStale"]!.GetValue<bool>()));
        Assert.Contains(Reasons(later), r => r.StartsWith("The measurement behind the service time of 'POST /orders' does not count: the measured result is stale."));

        // The result stays while a profile names it.
        var held = await Read(owner.DeleteAsync(Url(project, $"results/{resultId}")), HttpStatusCode.Conflict);
        Assert.Contains("Black Friday", held["detail"]!.GetValue<string>());
        await Read(owner.PutAsync(profileUrl, OrdersReference.Json(Profile(seeded.Orders))));
        await Status(HttpStatusCode.NoContent, owner.DeleteAsync(Url(project, $"results/{resultId}")), "delete");
        await Status(HttpStatusCode.NotFound, owner.GetAsync(Url(project, $"results/{resultId}")), "read deleted");
    }

    [Fact]
    public async Task Imports_at_the_same_instant_store_one_run_once_and_do_not_pass_the_limit_of_a_project()
    {
        using var seeded = await SeedAsync();
        var (owner, project) = (seeded.Owner, seeded.Project);
        var script = await ScriptAsync(owner, project, await AddTargetAsync(owner, project), seeded.ProfileId);

        // The two files of one run, sent at once: one is stored, and the other is told which result holds the run.
        var at = Start();
        var both = await Task.WhenAll(
            owner.PostAsync(Url(project, "results"), OrdersReference.Json(Bind(Baseline, script, at))),
            owner.PostAsync(Url(project, "results"), OrdersReference.Json(Bind(BaselineExport, script, at))));
        Assert.Equal([HttpStatusCode.Created, HttpStatusCode.Conflict], both.Select(r => r.StatusCode).Order());
        var stored = (await Read(owner.GetAsync(Url(project, "results")))).AsArray().Single()!["id"]!.GetValue<Guid>();
        Assert.Contains(stored.ToString(), await both.Single(r => r.StatusCode == HttpStatusCode.Conflict).Content.ReadAsStringAsync());

        // Rows up to two below the limit, written to the table. Then six imports at once: two places are left.
        using (var scope = seeded.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LoadTestsDbContext>();
            var row = await db.Results.AsNoTracking().SingleAsync(r => r.Id == stored);
            for (var i = 1; i < MeasuredResults.MaxResultsPerProject - 2; i++)
            {
                db.Results.Add(new ResultEntity
                {
                    Id = Guid.NewGuid(), ProjectId = row.ProjectId, ScriptId = row.ScriptId, ScriptHash = row.ScriptHash, ScenarioId = row.ScenarioId, ScenarioVersion = row.ScenarioVersion,
                    ModelStreamId = row.ModelStreamId, ModelVersion = row.ModelVersion, TargetId = row.TargetId, Environment = row.Environment, Format = row.Format,
                    StartedAt = row.StartedAt.AddDays(-i), EndedAt = row.EndedAt.AddDays(-i), ElapsedMs = row.ElapsedMs, ContentHash = $"{i:x64}", Document = row.Document,
                    ImportedBy = row.ImportedBy, ImportedAt = row.ImportedAt,
                });
            }
            await db.SaveChangesAsync();
        }
        var atOnce = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => owner.PostAsync(Url(project, "results"), OrdersReference.Json(Bind(Regressed, script)))));
        Assert.Equal(2, atOnce.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(4, atOnce.Count(r => r.StatusCode == HttpStatusCode.Conflict));
        Assert.Equal(MeasuredResults.MaxResultsPerProject, (await Read(owner.GetAsync(Url(project, "results")))).AsArray().Count);
    }

    [Fact]
    public async Task An_estimate_stands_beside_a_measured_value_with_both_labels_and_a_missing_measurement_is_unknown()
    {
        // With the outbox the flow has a second operation, the relay, which is a command: k6 cannot drive it.
        // The profile replaces a timeout of the model: an input of the simulation that no k6 run applies.
        using var seeded = await SeedAsync(orders =>
        {
            var profile = Profile(orders, withRelay: true);
            profile["overrides"]!.AsArray().Add(new JsonObject { ["target"] = OrdersReference.PlaceOrder.ToString(), ["timeoutMs"] = 30_000 });
            return profile;
        }, outbox: true);
        var (owner, project) = (seeded.Owner, seeded.Project);
        var run = await Read(owner.PostAsync($"/api/projects/{project}/stress/profiles/{seeded.ProfileId}/runs", null), HttpStatusCode.Created);
        var script = await ScriptAsync(owner, project, await AddTargetAsync(owner, project), seeded.ProfileId);
        Assert.Contains(script["plan"]!["notCovered"]!.AsArray(), n => n!["stepId"]!.GetValue<string>() == "relay");
        var resultId = (await ImportAsync(owner, project, Bind(Baseline, script)))["result"]!["id"]!.GetValue<Guid>();

        var comparison = await Read(owner.GetAsync(Url(project, $"compare/estimate?resultId={resultId}&runId={run["run"]!["id"]}")));
        var estimated = run["result"]!["operations"]!.AsArray().Single(o => o!["id"]!.GetValue<Guid>() == OrdersReference.PlaceOrder)!;

        var p95 = Row(comparison, "operation", "latencyP95", OrdersReference.PlaceOrder);
        Assert.Equal((estimated["latency"]!["p95Ms"]!.GetValue<double>(), "estimated", BaselineP95, "measured"),
            (p95["estimated"]!.GetValue<double>(), p95["estimatedLabel"]!.GetValue<string>(), p95["measured"]!.GetValue<double>(), p95["measuredLabel"]!.GetValue<string>()));
        Assert.Equal(BaselineP95 - estimated["latency"]!["p95Ms"]!.GetValue<double>(), p95["difference"]!.GetValue<double>(), 9);
        Assert.Null(p95["reason"]);
        Assert.Equal((0d, 0d, 0d), (Row(comparison, "operation", "unexpectedStatusRate", OrdersReference.PlaceOrder)["estimated"]!.GetValue<double>(),
            Row(comparison, "operation", "unexpectedStatusRate", OrdersReference.PlaceOrder)["measured"]!.GetValue<double>(),
            Row(comparison, "operation", "unexpectedStatusRate", OrdersReference.PlaceOrder)["difference"]!.GetValue<double>()));
        Assert.Equal(BaselineRequests / BaselineSeconds, Row(comparison, "operation", "throughput", OrdersReference.PlaceOrder)["measured"]!.GetValue<double>(), 9);
        Assert.Equal(run["result"]!["flow"]!["latency"]!["p50Ms"]!.GetValue<double>(), Row(comparison, "flow", "latencyP50")["estimated"]!.GetValue<double>());

        // The relay has an estimate and no measurement. That is unknown: not zero, and not a match.
        foreach (var metric in new[] { "latencyP50", "latencyP90", "latencyP95", "latencyP99", "throughput", "unexpectedStatusRate" })
        {
            var row = Row(comparison, "operation", metric, OrdersReference.RelayOutbox);
            Assert.Equal("estimated", row["estimatedLabel"]!.GetValue<string>());
            Assert.NotNull(row["estimated"]);
            Assert.Equal("unknown", row["measuredLabel"]!.GetValue<string>());
            Assert.Null(row["measured"]);
            Assert.Null(row["difference"]);
        }
        var differences = comparison["differences"]!.AsArray().Select(d => d!.GetValue<string>()).ToList();
        Assert.Contains(differences, d => d.StartsWith("The script does not perform 4 step(s)"));
        // Every known difference of the two sides is stated: the shape of the arrivals, the inputs that the run did not apply, and the two windows.
        Assert.Contains("Load of the k6 run: The profile has Poisson arrivals. k6 starts iterations at a constant rate with the same mean; it does not reproduce the random gaps.", differences);
        Assert.Contains(differences, d => d.StartsWith("The simulation applies 0 outage window(s) and 1 policy override(s) of its profile. The k6 run applied none of them"));
        Assert.Contains($"The k6 run measured {BaselineSeconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)} s after its warm-up. The simulation measured 50 s: its profile has a duration of 60 s and a warm-up of 10 s.", differences);
        Assert.DoesNotContain(comparison.AsObject(), p => p.Key is "verdict" or "match" or "confirmed");

        // A run of another scenario is no estimate of this result.
        await Status(HttpStatusCode.NotFound, owner.GetAsync(Url(project, $"compare/estimate?resultId={resultId}&runId={Guid.NewGuid()}")), "unknown run");

        // The result is bound to a model revision and a scenario revision. It is stale when either moves on.
        async Task<string> Staleness()
        {
            var listed = (await Read(owner.GetAsync(Url(project, "results")))).AsArray().Single()!;
            return $"{listed["stale"]}: {string.Join(", ", listed["staleBecause"]!.AsArray().Select(b => b!.GetValue<string>()))}";
        }
        Assert.Equal("false: ", await Staleness());
        await TestHost.Created(owner.PostAsJsonAsync($"/api/projects/{project}/elements", new { id = Guid.NewGuid(), kind = "system", name = "Later" }));
        Assert.Equal("true: model", await Staleness());
        Assert.True((await owner.PutAsync($"/api/projects/{project}/scenarios/{seeded.Orders.ScenarioId}", OrdersReference.Json(seeded.Orders.WithoutOutbox()))).IsSuccessStatusCode);
        Assert.Equal("true: model, scenario", await Staleness());
        var stale = await Read(owner.GetAsync(Url(project, $"compare/estimate?resultId={resultId}&runId={run["run"]!["id"]}")));
        Assert.Contains(stale["differences"]!.AsArray(), d => d!.GetValue<string>() == "The measured result is stale: the model and the scenario changed after the script was generated.");
        // The numbers of a stale result stay what k6 measured.
        Assert.Equal(BaselineP95, Row(stale, "operation", "latencyP95", OrdersReference.PlaceOrder)["measured"]!.GetValue<double>());
    }

    [Fact]
    public async Task Two_measured_results_of_one_scenario_and_target_report_the_regression_of_each_operation()
    {
        using var seeded = await SeedAsync();
        var (owner, project) = (seeded.Owner, seeded.Project);
        var target = await AddTargetAsync(owner, project);
        var script = await ScriptAsync(owner, project, target, seeded.ProfileId);
        var baseline = (await ImportAsync(owner, project, Bind(Baseline, script)))["result"]!["id"]!.GetValue<Guid>();
        var regressed = (await ImportAsync(owner, project, Bind(Regressed, script)))["result"]!["id"]!.GetValue<Guid>();

        var report = await Read(owner.GetAsync(Url(project, $"compare/results?baseline={baseline}&candidate={regressed}")));
        Assert.Equal("measured", report["label"]!.GetValue<string>());

        // The committed numbers of the two runs: 2000 and 2001 requests; the second run is 25 ms slower and 40 requests get a 500 status.
        string Line(string scope, string metric, Guid? id = null)
        {
            var row = Row(report, scope, metric, id);
            static string N(JsonNode? value) => value is null ? "-" : value.GetValue<double>().ToString("0.####", System.Globalization.CultureInfo.InvariantCulture);
            return $"{N(row["baseline"])} -> {N(row["candidate"])} change {N(row["change"])} relative {N(row["relative"])} {row["verdict"]}";
        }
        Assert.Equal("8.041 -> 32.41 change 24.369 relative 3.0306 regressed", Line("operation", "latencyP50", OrdersReference.PlaceOrder));
        Assert.Equal("14.4287 -> 37.243 change 22.8144 relative 1.5812 regressed", Line("operation", "latencyP95", OrdersReference.PlaceOrder));
        Assert.Equal("25.7391 -> 42.1266 change 16.3875 relative 0.6367 regressed", Line("operation", "latencyP99", OrdersReference.PlaceOrder));
        Assert.Equal("0 -> 0.02 change 0.02 relative - regressed", Line("operation", "unexpectedStatusRate", OrdersReference.PlaceOrder));
        Assert.Equal("regressed", Row(report, "flow", "latencyP95")["verdict"]!.GetValue<string>());
        // 40 of 2001 requests get an unexpected status, so 2 % fewer complete in the same window. That is inside the tolerance of 10 %.
        Assert.Equal("unchanged", Row(report, "operation", "throughput", OrdersReference.PlaceOrder)["verdict"]!.GetValue<string>());

        var subjects = report["subjects"]!.AsArray();
        Assert.Equal(2, report["regressions"]!.GetValue<int>());
        var placeOrder = subjects.Single(s => s!["target"]?.GetValue<Guid>() == OrdersReference.PlaceOrder)!;
        Assert.True(placeOrder["regressed"]!.GetValue<bool>());
        Assert.Equal(["latencyP50", "latencyP95", "latencyP99", "unexpectedStatusRate"], placeOrder["regressedMetrics"]!.AsArray().Select(m => m!.GetValue<string>()));
        Assert.Contains("more than 10 % and more than 1 ms", report["rule"]!.GetValue<string>());

        // The other way round the same numbers are an improvement, and a result against itself is unchanged.
        var reverse = await Read(owner.GetAsync(Url(project, $"compare/results?baseline={regressed}&candidate={baseline}")));
        Assert.Equal((0, "improved"), (reverse["regressions"]!.GetValue<int>(), Row(reverse, "operation", "latencyP95", OrdersReference.PlaceOrder)["verdict"]!.GetValue<string>()));
        var same = await Read(owner.GetAsync(Url(project, $"compare/results?baseline={baseline}&candidate={baseline}")));
        Assert.All(same["rows"]!.AsArray(), r => Assert.Equal("unchanged", r!["verdict"]!.GetValue<string>()));

        // A run against another target is no baseline for this one.
        var elsewhere = await ScriptAsync(owner, project, await AddTargetAsync(owner, project, "staging", "https://orders.staging.example", "Staging"), seeded.ProfileId);
        var onStaging = Bind(Regressed, elsewhere);
        var staging = (await ImportAsync(owner, project, onStaging))["result"]!["id"]!.GetValue<Guid>();
        Assert.Equal("candidate", Field(await Read(owner.GetAsync(Url(project, $"compare/results?baseline={baseline}&candidate={staging}")), HttpStatusCode.BadRequest)));
        await Status(HttpStatusCode.NotFound, owner.GetAsync(Url(project, $"compare/results?baseline={baseline}&candidate={Guid.NewGuid()}")), "unknown candidate");
    }

    [Fact]
    public async Task Load_test_data_of_one_workspace_is_closed_to_a_user_of_another_and_goes_with_its_project()
    {
        using var seeded = await SeedAsync();
        var (owner, theirs) = (seeded.Owner, seeded.Project);
        var target = await AddTargetAsync(owner, theirs);
        var script = await ScriptAsync(owner, theirs, target, seeded.ProfileId);
        var file = Bind(Baseline, script);
        var resultId = (await ImportAsync(owner, theirs, file))["result"]!["id"]!.GetValue<Guid>();
        var before = await owner.GetStringAsync(Url(theirs, "targets"));

        using var stranger = await TestHost.SignInAsync(seeded.Factory);
        var (_, mine) = await TestHost.CreateProjectAsync(stranger);
        var own = await OrdersReference.SeedAsync(stranger, mine);
        var ownProfile = (await Read(stranger.PostAsync($"/api/projects/{mine}/stress/profiles", OrdersReference.Json(Profile(own))), HttpStatusCode.Created))["id"]!.GetValue<Guid>();
        var ownTarget = await AddTargetAsync(stranger, mine);
        var ownResult = (await ImportAsync(stranger, mine, Bind(Baseline, await ScriptAsync(stranger, mine, ownTarget, ownProfile))))["result"]!["id"]!.GetValue<Guid>();

        // 1. The owner's project: everything is forbidden.
        foreach (var path in new[] { "targets", "scripts", $"scripts/{script["id"]}", "results", $"results/{resultId}", $"compare/results?baseline={resultId}&candidate={resultId}" })
            await Status(HttpStatusCode.Forbidden, stranger.GetAsync(Url(theirs, path)), $"GET {path}");
        await Status(HttpStatusCode.Forbidden, stranger.PostAsJsonAsync(Url(theirs, "targets"), new { name = "X", baseUrl = "https://x.example", environment = "test" }), "POST target");
        await Status(HttpStatusCode.Forbidden, AuthoriseAsync(stranger, theirs, target), "authorise");
        await Status(HttpStatusCode.Forbidden, stranger.PutAsJsonAsync(Url(theirs, $"targets/{target["id"]}"), new { name = "X", baseUrl = "https://x.example", environment = "test" }), "PUT target");
        await Status(HttpStatusCode.Forbidden, stranger.PostAsJsonAsync(Url(theirs, "scripts"), new { targetId = target["id"]!.GetValue<Guid>(), profileId = seeded.ProfileId }), "POST script");
        await Status(HttpStatusCode.Forbidden, stranger.PostAsync(Url(theirs, "results"), OrdersReference.Json(Bind(Regressed, script))), "POST result");
        await Status(HttpStatusCode.Forbidden, stranger.DeleteAsync(Url(theirs, $"results/{resultId}")), "DELETE result");

        // 2. The stranger's own project with the owner's IDs: they name nothing here.
        await Status(HttpStatusCode.NotFound, stranger.GetAsync(Url(mine, $"scripts/{script["id"]}")), "GET foreign script");
        await Status(HttpStatusCode.NotFound, stranger.GetAsync(Url(mine, $"results/{resultId}")), "GET foreign result");
        await Status(HttpStatusCode.NotFound, stranger.DeleteAsync(Url(mine, $"results/{resultId}")), "DELETE foreign result");
        await Status(HttpStatusCode.NotFound, stranger.GetAsync(Url(mine, $"compare/results?baseline={ownResult}&candidate={resultId}")), "compare with a foreign result");
        await Status(HttpStatusCode.NotFound, stranger.PutAsJsonAsync(Url(mine, $"targets/{target["id"]}"), new { name = "X", baseUrl = "https://x.example", environment = "test" }), "PUT foreign target");
        await Status(HttpStatusCode.NotFound, AuthoriseAsync(stranger, mine, target), "authorise foreign target");
        await Status(HttpStatusCode.NotFound, stranger.DeleteAsync(Url(mine, $"targets/{target["id"]}")), "DELETE foreign target");
        await Status(HttpStatusCode.NotFound, stranger.PostAsJsonAsync(Url(mine, "scripts"), new { targetId = target["id"]!.GetValue<Guid>(), profileId = ownProfile }), "script for a foreign target");
        await Status(HttpStatusCode.NotFound, stranger.PostAsJsonAsync(Url(mine, "scripts"), new { targetId = ownTarget["id"]!.GetValue<Guid>(), profileId = seeded.ProfileId }), "script for a foreign profile");
        // The owner's result file names a script of the owner's project. In this project that script does not exist.
        var regressedOfOwner = Bind(Regressed, script);
        Assert.Equal("unknown_script", (await ImportAsync(stranger, mine, regressedOfOwner, HttpStatusCode.UnprocessableEntity))["type"]!.GetValue<string>());

        Assert.Equal(before, await owner.GetStringAsync(Url(theirs, "targets")));
        Assert.Single((await Read(owner.GetAsync(Url(theirs, "results")))).AsArray());

        // The delete of the project takes the targets, the scripts and the results with it.
        async Task<(int Targets, int Scripts, int Results)> CountAsync()
        {
            using var scope = seeded.Factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<LoadTestsDbContext>();
            return (await db.Targets.CountAsync(t => t.ProjectId == theirs), await db.Scripts.CountAsync(s => s.ProjectId == theirs), await db.Results.CountAsync(r => r.ProjectId == theirs));
        }
        Assert.Equal((1, 1, 1), await CountAsync());
        await Status(HttpStatusCode.NoContent, owner.DeleteAsync($"/api/projects/{theirs}"), "DELETE project");
        Assert.Equal((0, 0, 0), await CountAsync());
        Assert.Single((await Read(stranger.GetAsync(Url(mine, "results")))).AsArray());
    }

    /// <summary>A local stand-in of the Orders API. <paramref name="answer"/> gets the number of the request and gives the status.</summary>
    private sealed class StandIn(WebApplication app, string baseUrl) : IAsyncDisposable
    {
        public string BaseUrl => baseUrl;
        public int Requests;
        /// <summary>Requests that reached the other URL, the one a redirect names.</summary>
        public int Elsewhere;
        public bool CredentialElsewhere;

        public static async Task<StandIn> StartAsync(Func<int, HttpRequest, Task<IResult>> answer)
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            var app = builder.Build();
            StandIn? standIn = null;
            app.MapPost("/orders", (HttpRequest request) => answer(Interlocked.Increment(ref standIn!.Requests), request));
            app.Map("/elsewhere/{**rest}", (HttpRequest request) =>
            {
                Interlocked.Increment(ref standIn!.Elsewhere);
                standIn.CredentialElsewhere |= request.Headers.Authorization.Count > 0;
                return Results.Ok();
            });
            await app.StartAsync();
            return standIn = new StandIn(app, app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
        }

        public static async Task<IResult> Orders(HttpRequest request)
        {
            var order = await request.ReadFromJsonAsync<JsonObject>();
            return request.Headers.Authorization == "Bearer stand-in-token" && order!["sku"]!.GetValue<string>() == "apple"
                ? Results.Json(new { orderId = "o-1" }, statusCode: StatusCodes.Status201Created)
                : Results.StatusCode(StatusCodes.Status400BadRequest);
        }

        public async ValueTask DisposeAsync()
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    private static async Task<(int ExitCode, string Output, JsonNode? Result)> K6Async(string directory, JsonNode script, string baseUrl, params string[] options)
    {
        var fileName = script["fileName"]!.GetValue<string>();
        await File.WriteAllTextAsync(Path.Combine(directory, fileName), script["content"]!.GetValue<string>());
        var resultFile = Path.Combine(directory, "vellum-k6-result.json");
        File.Delete(resultFile);
        var (exitCode, output, error) = await K6FactAttribute.RunAsync(directory,
            ["run", "--quiet", .. options, "-e", $"VELLUM_BASE_URL={baseUrl}", "-e", "VELLUM_AUTHORIZATION=Bearer stand-in-token", fileName]);
        return (exitCode, output + error, File.Exists(resultFile) ? JsonNode.Parse(await File.ReadAllTextAsync(resultFile)) : null);
    }

    /// <summary>
    /// The whole path with the real k6: a script that Vellum generates runs against a local
    /// stand-in of the Orders API, and both files that the run leaves import as measured. A run
    /// with another load than the profile's stops, and no redirect takes a request elsewhere.
    /// </summary>
    [K6Fact]
    public async Task K6_runs_a_generated_script_against_a_local_stand_in_and_both_of_its_result_files_import()
    {
        var redirect = false;
        await using var standIn = await StandIn.StartAsync((_, request) => redirect
            ? Task.FromResult(Results.Redirect("/elsewhere/orders", permanent: false, preserveMethod: true))
            : StandIn.Orders(request));

        // 20 orders each second for 4 seconds; the first second is warm-up.
        using var seeded = await SeedAsync(orders => Profile(orders, duration: 4, warmUp: 1, rate: 20));
        var (owner, project) = (seeded.Owner, seeded.Project);
        var script = await ScriptAsync(owner, project, await AddTargetAsync(owner, project, baseUrl: standIn.BaseUrl), seeded.ProfileId);

        var directory = Directory.CreateTempSubdirectory("vellum-k6-").FullName;
        try
        {
            // Another URL than the authorised one: the script stops before it sends a request.
            var wrong = await K6Async(directory, script, "http://127.0.0.1:9");
            Assert.True(wrong.ExitCode != 0, wrong.Output);
            Assert.Contains("is not the base URL that this script is authorised for", wrong.Output);

            // Options of the command line replace the executor of the script: 4 virtual users for 3 seconds are not
            // 20 orders each second for 4 seconds. The script stops in setup(), sends nothing and writes no measurement.
            var otherLoad = await K6Async(directory, script, standIn.BaseUrl, "--vus", "4", "--duration", "3s");
            Assert.True(otherLoad.ExitCode != 0, otherLoad.Output);
            Assert.Contains("k6 does not run the load that this script was generated for", otherLoad.Output);
            Assert.Contains("Effective: scenarios=default", otherLoad.Output);
            Assert.Equal(0, standIn.Requests);
            Assert.Null(otherLoad.Result);
            Assert.Contains("No result file is written", otherLoad.Output);

            var run = await K6Async(directory, script, standIn.BaseUrl, "--summary-export", "export.json");
            Assert.True(run.ExitCode == 0, run.Output);
            Assert.Contains("Vellum k6 result (measured)", run.Output);

            var imported = await ImportAsync(owner, project, run.Result!);
            var operation = Assert.Single(imported["operations"]!.AsArray())!;
            // 3 seconds after the warm-up at 20 each second. The first and the last iteration can fall on either side.
            Assert.InRange(operation["requests"]!.GetValue<long>(), 55, 62);
            Assert.Equal(0, operation["unexpectedStatus"]!.GetValue<long>());
            Assert.True(standIn.Requests > operation["requests"]!.GetValue<long>(), "The warm-up requests are sent and not counted.");
            Assert.Equal("measured", operation["latency"]!["label"]!.GetValue<string>());
            Assert.InRange(operation["throughputPerSecond"]!.GetValue<double>(), 17, 22);
            Assert.Equal("constant-arrival-rate rate=20 unit=1000 duration=4000 pre=20 max=200", run.Result!["load"]!["effective"]!.GetValue<string>());

            // The export of the same run is the same run.
            var export = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "export.json")))!;
            await ImportAsync(owner, project, export, HttpStatusCode.Conflict);
            await Status(HttpStatusCode.NoContent, owner.DeleteAsync(Url(project, $"results/{imported["result"]!["id"]}")), "delete the result");
            var fromExport = await ImportAsync(owner, project, export);
            Assert.Equal("k6-summary-export", fromExport["result"]!["provenance"]!["format"]!.GetValue<string>());
            Assert.Equal(operation.ToJsonString(), fromExport["operations"]![0]!.ToJsonString());

            // The target answers 307 with another URL. The script follows no redirect: nothing goes there, with or
            // without the credential, and each 3xx is an unexpected status.
            redirect = true;
            var redirected = await K6Async(directory, script, standIn.BaseUrl);
            Assert.True(redirected.ExitCode == 0, redirected.Output);
            Assert.Equal((0, false), (standIn.Elsewhere, standIn.CredentialElsewhere));
            var refused = Assert.Single((await ImportAsync(owner, project, redirected.Result!))["operations"]!.AsArray())!;
            Assert.True(refused["requests"]!.GetValue<long>() > 50, redirected.Output);
            Assert.Equal(refused["requests"]!.GetValue<long>(), refused["unexpectedStatus"]!.GetValue<long>());
            Assert.Null(refused["latency"]);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// A scenario with a fault: the playback has the call as failed, so the script expects a
    /// failure. The real k6 keeps the durations of those failures out of the success latency,
    /// and the comparison does not set the run beside a simulation that applied no fault.
    /// </summary>
    [K6Fact]
    public async Task K6_keeps_expected_failures_apart_and_a_run_with_faults_is_unknown_beside_an_estimate()
    {
        await using var standIn = await StandIn.StartAsync((_, _) => Task.FromResult(Results.StatusCode(StatusCodes.Status503ServiceUnavailable)));
        using var seeded = await SeedAsync(orders => Profile(orders, duration: 4, warmUp: 1, rate: 20));
        var (owner, project) = (seeded.Owner, seeded.Project);

        var stress = await Read(owner.PostAsync($"/api/projects/{project}/stress/profiles/{seeded.ProfileId}/runs", null), HttpStatusCode.Created);
        var faulted = seeded.Orders.WithoutOutbox();
        faulted["faults"] = new JsonArray(new JsonObject { ["target"] = OrdersReference.PlaceOrder.ToString(), ["kind"] = "Error" });
        faulted["assertions"] = new JsonArray();
        Assert.True((await owner.PutAsync($"/api/projects/{project}/scenarios/{seeded.Orders.ScenarioId}", OrdersReference.Json(faulted))).IsSuccessStatusCode);
        var script = await ScriptAsync(owner, project, await AddTargetAsync(owner, project, baseUrl: standIn.BaseUrl), seeded.ProfileId);
        Assert.Equal(("failure", 1), (script["plan"]!["steps"]![0]!["expect"]!.GetValue<string>(), script["plan"]!["faults"]!.GetValue<int>()));

        var directory = Directory.CreateTempSubdirectory("vellum-k6-").FullName;
        try
        {
            var run = await K6Async(directory, script, standIn.BaseUrl);
            Assert.True(run.ExitCode == 0, run.Output);
            var imported = await ImportAsync(owner, project, run.Result!);
            var operation = Assert.Single(imported["operations"]!.AsArray())!;
            var requests = operation["requests"]!.GetValue<long>();
            Assert.True(requests > 50, run.Output);

            // Every request failed, as expected: none has an unexpected status, and none is in a success latency.
            Assert.Equal((0L, requests, requests), (operation["unexpectedStatus"]!.GetValue<long>(), operation["expectedFailures"]!.GetValue<long>(), operation["expectedFailureLatency"]!["count"]!.GetValue<long>()));
            Assert.Null(operation["latency"]);
            Assert.Null(imported["result"]!["flow"]!["latency"]);
            Assert.Equal((1, true), (imported["faults"]!.GetValue<int>(), imported["expectsFailure"]!.GetValue<bool>()));

            // Beside the estimate, the measured side is unknown with the reason. "0 % unexpected" is not "0 % errors".
            var comparison = await Read(owner.GetAsync(Url(project, $"compare/estimate?resultId={imported["result"]!["id"]}&runId={stress["run"]!["id"]}")));
            foreach (var row in comparison["rows"]!.AsArray().Where(r => r!["scope"]!.GetValue<string>() == "flow" || r["target"]?.GetValue<Guid>() == OrdersReference.PlaceOrder))
            {
                Assert.Equal(("unknown", Comparisons.FaultsReason), (row!["measuredLabel"]!.GetValue<string>(), row["reason"]!.GetValue<string>()));
                Assert.Null(row["measured"]);
                Assert.Null(row["difference"]);
            }
            Assert.Contains(Comparisons.FaultsReason, comparison["differences"]!.AsArray().Select(d => d!.GetValue<string>()));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
