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

    private static async Task<JsonNode> AddTargetAsync(HttpClient client, Guid project, string environment = "test", string baseUrl = "https://orders.test.example", string name = "Test system", bool authorise = true)
    {
        var target = await Read(client.PostAsJsonAsync(Url(project, "targets"), new { name, baseUrl, environment }), HttpStatusCode.Created);
        return authorise
            ? await Read(client.PostAsJsonAsync(Url(project, $"targets/{target["id"]}/authorise"), new { baseUrl = target["baseUrl"]!.GetValue<string>() }))
            : target;
    }

    private static Task<JsonNode> ScriptAsync(HttpClient client, Guid project, JsonNode target, Guid profileId, HttpStatusCode expected = HttpStatusCode.Created) =>
        Read(client.PostAsJsonAsync(Url(project, "scripts"), new { targetId = target["id"]!.GetValue<Guid>(), profileId }), expected);

    private static JsonObject Fixture(string name) =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "LoadTests", name)))!.AsObject();

    /// <summary>A committed result file as k6 would have written it for <paramref name="script"/> a minute ago: the identity is the script's, the numbers stay.</summary>
    private static JsonObject Bind(string name, JsonNode script)
    {
        var file = Fixture(name);
        var startedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
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
        file["startedAt"] = startedAt.ToString("O");
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
        var authorise = new { baseUrl = "https://orders.test.example/api" };
        var setOverride = new { allowNonIdempotent = true, allowFailureInjection = false, reason = "Window", confirmName = "Test system" };
        await Status(HttpStatusCode.Forbidden, editor.PostAsJsonAsync(Url(project, $"targets/{id}/authorise"), authorise), "editor authorise");
        await Status(HttpStatusCode.Forbidden, editor.PutAsJsonAsync(Url(project, $"targets/{id}/override"), setOverride), "editor override");
        await Status(HttpStatusCode.Forbidden, viewer.PostAsJsonAsync(Url(project, "targets"), new { name = "V", baseUrl = "https://v.example", environment = "test" }), "viewer add");
        await Status(HttpStatusCode.OK, viewer.GetAsync(Url(project, "targets")), "viewer list");
        foreach (var refused in new[]
                 {
                     await agent.Client.PostAsJsonAsync(Url(project, $"targets/{id}/authorise"), authorise),
                     await agent.Client.PutAsJsonAsync(Url(project, $"targets/{id}/override"), setOverride),
                     await agent.Client.DeleteAsync(Url(project, $"targets/{id}/authorisation")),
                 })
        {
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
            Assert.Equal("human_only", (await refused.Content.ReadFromJsonAsync<JsonObject>())!["type"]!.GetValue<string>());
        }
        Assert.Null((await Read(owner.GetAsync(Url(project, "targets"))))[0]!["authorisation"]);

        // The owner names the URL that was read. Another URL authorises nothing.
        await Status(HttpStatusCode.Conflict, owner.PostAsJsonAsync(Url(project, $"targets/{id}/authorise"), new { baseUrl = "https://orders.example" }), "authorise another URL");
        var authorised = await Read(owner.PostAsJsonAsync(Url(project, $"targets/{id}/authorise"), authorise));
        var me = (await owner.GetFromJsonAsync<JsonObject>("/api/auth/me"))!["id"]!.GetValue<string>();
        Assert.Equal((me, "https://orders.test.example/api", "test"),
            (authorised["authorisation"]!["by"]!.GetValue<string>(), authorised["authorisation"]!["baseUrl"]!.GetValue<string>(), authorised["authorisation"]!["environment"]!.GetValue<string>()));

        // A new name keeps the authorisation. A new URL voids it, and so does a new class.
        var renamed = await Read(editor.PutAsJsonAsync(Url(project, $"targets/{id}"), new { name = "Orders test", baseUrl = "https://orders.test.example/api", environment = "test" }));
        Assert.NotNull(renamed["authorisation"]);
        var moved = await Read(editor.PutAsJsonAsync(Url(project, $"targets/{id}"), new { name = "Orders test", baseUrl = "https://orders.example", environment = "test" }));
        Assert.Null(moved["authorisation"]);
        await Read(owner.PostAsJsonAsync(Url(project, $"targets/{id}/authorise"), new { baseUrl = "https://orders.example" }));

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

        // Each change of a permission is in the audit log of the workspace, with the origin and no path.
        var audit = (await Read(owner.GetAsync($"/api/workspaces/{seeded.WorkspaceId}/audit?limit=50"))).AsArray()
            .Where(a => a!["action"]!.GetValue<string>().StartsWith("load-test.", StringComparison.Ordinal))
            .Select(a => $"{a!["action"]} {a["target"]}".Replace(id.ToString(), "T")).Reverse().ToList();
        Assert.Equal(
        [
            "load-test.target.authorised load-test-target:T environment:test origin:https://orders.test.example",
            "load-test.target.authorisation-voided load-test-target:T environment:test origin:https://orders.example cause:base-url-changed",
            "load-test.target.authorised load-test-target:T environment:test origin:https://orders.example",
            "load-test.target.authorisation-voided load-test-target:T environment:production origin:https://orders.example cause:environment-changed",
            "load-test.target.override-set load-test-target:T environment:production origin:https://orders.example non-idempotent:allowed failure-injection:excluded",
            "load-test.target.override-cleared load-test-target:T environment:staging origin:https://orders.example cause:environment-changed",
        ], audit);

        await Status(HttpStatusCode.NoContent, editor.DeleteAsync(Url(project, $"targets/{id}")), "remove");
        Assert.Empty((await Read(owner.GetAsync(Url(project, "targets")))).AsArray());
        await Status(HttpStatusCode.NotFound, owner.PostAsJsonAsync(Url(project, $"targets/{id}/authorise"), new { baseUrl = "https://orders.example" }), "authorise a removed target");
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

        await Read(owner.PostAsJsonAsync(Url(project, $"targets/{target["id"]}/authorise"), new { baseUrl = "https://orders.test.example" }));
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

        var file = Bind(Baseline, script);
        var imported = await ImportAsync(owner, project, file);
        var result = imported["result"]!;
        Assert.Equal("measured", result["label"]!.GetValue<string>());
        Assert.Equal(("k6-import", "vellum-k6-result/1", script["hash"]!.GetValue<string>(), me, "Test system", "https://orders.test.example", "test"),
            (result["provenance"]!["kind"]!.GetValue<string>(), result["provenance"]!["format"]!.GetValue<string>(), result["provenance"]!["scriptHash"]!.GetValue<string>(),
                result["provenance"]!["importedBy"]!.GetValue<string>(), result["provenance"]!["targetName"]!.GetValue<string>(),
                result["provenance"]!["baseUrl"]!.GetValue<string>(), result["provenance"]!["environment"]!.GetValue<string>()));
        Assert.Null(result["provenance"]!["importedVia"]);
        Assert.Equal((script["scenarioId"]!.GetValue<Guid>(), script["scenarioVersion"]!.GetValue<long>(), script["modelVersion"]!.GetValue<long>()),
            (result["scenarioId"]!.GetValue<Guid>(), result["scenarioVersion"]!.GetValue<long>(), result["modelVersion"]!.GetValue<long>()));
        var operation = Assert.Single(imported["operations"]!.AsArray())!;
        Assert.Equal(("measured", 2000L, 0L, 0d, 10.51905, "measured"),
            (operation["label"]!.GetValue<string>(), operation["requests"]!.GetValue<long>(), operation["failed"]!.GetValue<long>(), operation["errorRate"]!.GetValue<double>(),
                operation["latency"]!["p95Ms"]!.GetValue<double>(), operation["latency"]!["label"]!.GetValue<string>()));
        // 2000 requests in the 49.988 seconds after the warm-up.
        Assert.Equal(2000 / 49.988, operation["throughputPerSecond"]!.GetValue<double>(), 9);
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
        Assert.Equal("operations[0]", Field(await Refuse(f => f["operations"]![0]!["failed"] = 2001)));
        Assert.Equal("operations[0].latencyMs", Field(await Refuse(f => f["operations"]![0]!["latencyMs"]!["count"] = 1999)));
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
        Assert.Equal("scriptHash", Field(await Refuse(f => f["scriptHash"] = "ABC")));
        Assert.Equal("invalid_body", (await Read(owner.PostAsync(Url(project, "results"), new StringContent("{ \"format\": ", Encoding.UTF8, "application/json")), HttpStatusCode.BadRequest))["type"]!.GetValue<string>());
        var large = Bind(Baseline, script);
        large["padding"] = new string('x', 300_000);
        await ImportAsync(owner, project, large, HttpStatusCode.RequestEntityTooLarge);

        // The summary export of the same run carries the identity in the name of a metric, and gives the same numbers.
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
        await Read(owner.DeleteAsync(Url(project, $"targets/{target["id"]}/authorisation")));
        var late = Bind(Regressed, script);
        late["flow"]!["droppedIterations"] = 1;
        Assert.Equal("target_not_authorised", (await ImportAsync(owner, project, late, HttpStatusCode.UnprocessableEntity))["type"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_profile_value_is_measured_only_when_it_names_an_imported_result_of_the_same_project()
    {
        using var seeded = await SeedAsync();
        var (owner, project) = (seeded.Owner, seeded.Project);
        var script = await ScriptAsync(owner, project, await AddTargetAsync(owner, project), seeded.ProfileId);
        var resultId = (await ImportAsync(owner, project, Bind(Baseline, script)))["result"]!["id"]!.GetValue<Guid>();

        // Another project of the same user has a result too. Its ID names nothing here.
        var (_, otherProject) = await TestHost.CreateProjectAsync(owner);
        var other = await OrdersReference.SeedAsync(owner, otherProject);
        var otherProfile = (await Read(owner.PostAsync($"/api/projects/{otherProject}/stress/profiles", OrdersReference.Json(Profile(other))), HttpStatusCode.Created))["id"]!.GetValue<Guid>();
        var otherScript = await ScriptAsync(owner, otherProject, await AddTargetAsync(owner, otherProject), otherProfile);
        var foreign = (await ImportAsync(owner, otherProject, Bind(Baseline, otherScript)))["result"]!["id"]!.GetValue<Guid>();

        async Task<JsonNode> Save(string? reference, HttpStatusCode expected)
        {
            var profile = Profile(seeded.Orders);
            profile["serviceTimes"]![0]!["label"] = "measured";
            profile["serviceTimes"]![0]!["measurementRef"] = reference;
            return await Read(owner.PutAsync($"/api/projects/{project}/stress/profiles/{seeded.ProfileId}", OrdersReference.Json(profile)), expected);
        }

        foreach (var reference in new[] { $"k6:{Guid.NewGuid()}", $"k6:{foreign}", "k6:run-1", resultId.ToString(), $"k6:{resultId.ToString().ToUpperInvariant()}", $"K6:{resultId}" })
            Assert.Equal("serviceTimes[0].measurementRef", Field(await Save(reference, HttpStatusCode.BadRequest)));
        Assert.Equal("serviceTimes[0].measurementRef", Field(await Save(null, HttpStatusCode.BadRequest)));

        var saved = await Save($"k6:{resultId}", HttpStatusCode.OK);
        Assert.Equal("measured", saved["profile"]!["serviceTimes"]![0]!["label"]!.GetValue<string>());

        // A measured input does not make an output measured: every number the simulator computes stays an estimate.
        var run = await Read(owner.PostAsync($"/api/projects/{project}/stress/profiles/{seeded.ProfileId}/runs", null), HttpStatusCode.Created);
        Assert.Equal("estimated", run["result"]!["label"]!.GetValue<string>());
        Assert.Equal("estimated", run["result"]!["operations"]![0]!["latency"]!["label"]!.GetValue<string>());
        Assert.DoesNotContain("\"label\":\"measured\"", run["result"]!["flow"]!.ToJsonString());
        Assert.Contains(run["result"]!["assumptions"]!.AsArray(), a => a!["label"]!.GetValue<string>() == "measured" && a["measurementRef"]!.GetValue<string>() == $"k6:{resultId}");

        // The result stays while a profile names it.
        var held = await Read(owner.DeleteAsync(Url(project, $"results/{resultId}")), HttpStatusCode.Conflict);
        Assert.Contains("Black Friday", held["detail"]!.GetValue<string>());
        await Read(owner.PutAsync($"/api/projects/{project}/stress/profiles/{seeded.ProfileId}", OrdersReference.Json(Profile(seeded.Orders))));
        await Status(HttpStatusCode.NoContent, owner.DeleteAsync(Url(project, $"results/{resultId}")), "delete");
        await Status(HttpStatusCode.NotFound, owner.GetAsync(Url(project, $"results/{resultId}")), "read deleted");
    }

    [Fact]
    public async Task An_estimate_stands_beside_a_measured_value_with_both_labels_and_a_missing_measurement_is_unknown()
    {
        // With the outbox the flow has a second operation, the relay, which is a command: k6 cannot drive it.
        using var seeded = await SeedAsync(outbox: true);
        var (owner, project) = (seeded.Owner, seeded.Project);
        var run = await Read(owner.PostAsync($"/api/projects/{project}/stress/profiles/{seeded.ProfileId}/runs", null), HttpStatusCode.Created);
        var script = await ScriptAsync(owner, project, await AddTargetAsync(owner, project), seeded.ProfileId);
        Assert.Contains(script["plan"]!["notCovered"]!.AsArray(), n => n!["stepId"]!.GetValue<string>() == "relay");
        var resultId = (await ImportAsync(owner, project, Bind(Baseline, script)))["result"]!["id"]!.GetValue<Guid>();

        var comparison = await Read(owner.GetAsync(Url(project, $"compare/estimate?resultId={resultId}&runId={run["run"]!["id"]}")));
        var estimated = run["result"]!["operations"]!.AsArray().Single(o => o!["id"]!.GetValue<Guid>() == OrdersReference.PlaceOrder)!;

        var p95 = Row(comparison, "operation", "latencyP95", OrdersReference.PlaceOrder);
        Assert.Equal((estimated["latency"]!["p95Ms"]!.GetValue<double>(), "estimated", 10.51905, "measured"),
            (p95["estimated"]!.GetValue<double>(), p95["estimatedLabel"]!.GetValue<string>(), p95["measured"]!.GetValue<double>(), p95["measuredLabel"]!.GetValue<string>()));
        Assert.Equal(10.51905 - estimated["latency"]!["p95Ms"]!.GetValue<double>(), p95["difference"]!.GetValue<double>(), 9);
        Assert.Equal((0d, 0d, 0d), (Row(comparison, "operation", "errorRate", OrdersReference.PlaceOrder)["estimated"]!.GetValue<double>(),
            Row(comparison, "operation", "errorRate", OrdersReference.PlaceOrder)["measured"]!.GetValue<double>(),
            Row(comparison, "operation", "errorRate", OrdersReference.PlaceOrder)["difference"]!.GetValue<double>()));
        Assert.Equal(2000 / 49.988, Row(comparison, "operation", "throughput", OrdersReference.PlaceOrder)["measured"]!.GetValue<double>(), 9);
        Assert.Equal(run["result"]!["flow"]!["latency"]!["p50Ms"]!.GetValue<double>(), Row(comparison, "flow", "latencyP50")["estimated"]!.GetValue<double>());

        // The relay has an estimate and no measurement. That is unknown: not zero, and not a match.
        foreach (var metric in new[] { "latencyP50", "latencyP90", "latencyP95", "latencyP99", "throughput", "errorRate" })
        {
            var row = Row(comparison, "operation", metric, OrdersReference.RelayOutbox);
            Assert.Equal("estimated", row["estimatedLabel"]!.GetValue<string>());
            Assert.NotNull(row["estimated"]);
            Assert.Equal("unknown", row["measuredLabel"]!.GetValue<string>());
            Assert.Null(row["measured"]);
            Assert.Null(row["difference"]);
        }
        Assert.Contains(comparison["differences"]!.AsArray(), d => d!.GetValue<string>().StartsWith("The script does not perform 4 step(s)"));
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
        Assert.Equal(10.51905, Row(stale, "operation", "latencyP95", OrdersReference.PlaceOrder)["measured"]!.GetValue<double>());
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

        // The committed numbers of the two runs: 2000 requests each; the second run is 25 ms slower and 41 requests fail.
        string Line(string scope, string metric, Guid? id = null)
        {
            var row = Row(report, scope, metric, id);
            static string N(JsonNode? value) => value is null ? "-" : value.GetValue<double>().ToString("0.####", System.Globalization.CultureInfo.InvariantCulture);
            return $"{N(row["baseline"])} -> {N(row["candidate"])} change {N(row["change"])} relative {N(row["relative"])} {row["verdict"]}";
        }
        Assert.Equal("6.72 -> 31.369 change 24.649 relative 3.668 regressed", Line("operation", "latencyP50", OrdersReference.PlaceOrder));
        Assert.Equal("10.5191 -> 35.4301 change 24.9111 relative 2.3682 regressed", Line("operation", "latencyP95", OrdersReference.PlaceOrder));
        Assert.Equal("13.989 -> 40.1395 change 26.1506 relative 1.8694 regressed", Line("operation", "latencyP99", OrdersReference.PlaceOrder));
        Assert.Equal("0 -> 0.0205 change 0.0205 relative - regressed", Line("operation", "errorRate", OrdersReference.PlaceOrder));
        Assert.Equal("regressed", Row(report, "flow", "latencyP95")["verdict"]!.GetValue<string>());
        // 41 of 2000 requests fail, so 2 % fewer complete in the same window. That is inside the tolerance of 10 %.
        Assert.Equal("unchanged", Row(report, "operation", "throughput", OrdersReference.PlaceOrder)["verdict"]!.GetValue<string>());

        var subjects = report["subjects"]!.AsArray();
        Assert.Equal(2, report["regressions"]!.GetValue<int>());
        var placeOrder = subjects.Single(s => s!["target"]?.GetValue<Guid>() == OrdersReference.PlaceOrder)!;
        Assert.True(placeOrder["regressed"]!.GetValue<bool>());
        Assert.Equal(["latencyP50", "latencyP95", "latencyP99", "errorRate"], placeOrder["regressedMetrics"]!.AsArray().Select(m => m!.GetValue<string>()));
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
        await Status(HttpStatusCode.Forbidden, stranger.PostAsJsonAsync(Url(theirs, $"targets/{target["id"]}/authorise"), new { baseUrl = "https://orders.test.example" }), "authorise");
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
        await Status(HttpStatusCode.NotFound, stranger.PostAsJsonAsync(Url(mine, $"targets/{target["id"]}/authorise"), new { baseUrl = "https://orders.test.example" }), "authorise foreign target");
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

    /// <summary>
    /// The whole path with the real k6: a script that Vellum generates runs against a local
    /// stand-in of the Orders API, and both files that the run leaves import as measured.
    /// </summary>
    [K6Fact]
    public async Task K6_runs_a_generated_script_against_a_local_stand_in_and_both_of_its_result_files_import()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var standIn = builder.Build();
        var requests = 0;
        standIn.MapPost("/orders", async (HttpRequest request) =>
        {
            Interlocked.Increment(ref requests);
            var order = await request.ReadFromJsonAsync<JsonObject>();
            return request.Headers.Authorization == "Bearer stand-in-token" && order!["sku"]!.GetValue<string>() == "apple"
                ? Results.Json(new { orderId = "o-1" }, statusCode: StatusCodes.Status201Created)
                : Results.StatusCode(StatusCodes.Status400BadRequest);
        });
        await standIn.StartAsync();
        var baseUrl = standIn.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();

        // 20 orders each second for 4 seconds; the first second is warm-up.
        using var seeded = await SeedAsync(orders => Profile(orders, duration: 4, warmUp: 1, rate: 20));
        var (owner, project) = (seeded.Owner, seeded.Project);
        var script = await ScriptAsync(owner, project, await AddTargetAsync(owner, project, baseUrl: baseUrl), seeded.ProfileId);

        var directory = Directory.CreateTempSubdirectory("vellum-k6-").FullName;
        try
        {
            var fileName = script["fileName"]!.GetValue<string>();
            await File.WriteAllTextAsync(Path.Combine(directory, fileName), script["content"]!.GetValue<string>());

            // Another URL than the authorised one: the script stops before it sends a request.
            var (wrongExit, wrongOutput, wrongError) = await K6FactAttribute.RunAsync(directory, "run", "--quiet", "-e", "VELLUM_BASE_URL=http://127.0.0.1:9", fileName);
            Assert.True(wrongExit != 0, wrongOutput);
            Assert.Contains("is not the base URL that this script is authorised for", wrongOutput + wrongError);
            Assert.Equal(0, requests);

            var (exitCode, output, error) = await K6FactAttribute.RunAsync(directory,
                "run", "--quiet", "--summary-export", "export.json", "-e", $"VELLUM_BASE_URL={baseUrl}", "-e", "VELLUM_AUTHORIZATION=Bearer stand-in-token", fileName);
            Assert.True(exitCode == 0, output + error);
            Assert.Contains("Vellum k6 result (measured)", output);

            var result = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "vellum-k6-result.json")))!;
            var imported = await ImportAsync(owner, project, result);
            var operation = Assert.Single(imported["operations"]!.AsArray())!;
            // 3 seconds after the warm-up at 20 each second. The first and the last iteration can fall on either side.
            Assert.InRange(operation["requests"]!.GetValue<long>(), 55, 62);
            Assert.Equal(0, operation["failed"]!.GetValue<long>());
            Assert.True(requests > operation["requests"]!.GetValue<long>(), "The warm-up requests are sent and not counted.");
            Assert.Equal("measured", operation["latency"]!["label"]!.GetValue<string>());
            Assert.InRange(operation["throughputPerSecond"]!.GetValue<double>(), 18, 22);

            var export = await ImportAsync(owner, project, JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "export.json")))!);
            Assert.Equal("k6-summary-export", export["result"]!["provenance"]!["format"]!.GetValue<string>());
            Assert.Equal(operation.ToJsonString(), export["operations"]![0]!.ToJsonString());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
            await standIn.StopAsync();
        }
    }
}
