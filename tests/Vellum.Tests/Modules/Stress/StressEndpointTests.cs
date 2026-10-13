using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vellum.Modules.Stress;
using Vellum.Modules.Stress.Engine;
using Vellum.Tests.Modules.Promises;

namespace Vellum.Tests.Modules.Stress;

/// <summary>
/// Stress profiles, stored runs and comparison through the HTTP API on real Postgres, over the
/// Orders reference flow: a customer places an order, the Orders API stores it and
/// <c>OrderCreated</c> goes to Inventory and Billing.
/// </summary>
[Collection("Integration")]
public class StressEndpointTests(IntegrationFixture fixture)
{
    private static string Url(Guid projectId, string path) => $"/api/projects/{projectId}/stress/{path}";

    private static async Task<JsonNode> Read(Task<HttpResponseMessage> request, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await request;
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"expected {expected}, got {(int)response.StatusCode}: {text}");
        return JsonNode.Parse(text)!;
    }

    private static async Task Status(HttpStatusCode expected, Task<HttpResponseMessage> request, string what)
    {
        var response = await request;
        Assert.True(response.StatusCode == expected, $"{what}: expected {expected}, got {response.StatusCode} {await response.Content.ReadAsStringAsync()}");
    }

    /// <summary>A complete profile for the Orders flow: 40 orders each second for a minute, with every input the flow needs.</summary>
    private static JsonObject Profile(OrdersReference orders, int apiWorkers = 8, string seed = "42", string name = "Black Friday") => JsonNode.Parse($$"""
        {
          "name": "{{name}}", "scenarioId": "{{orders.ScenarioId}}", "seed": "{{seed}}",
          "arrivals": { "kind": "poisson", "ratePerSecond": 40, "durationSeconds": 60, "warmUpSeconds": 10 },
          "resources": [
            { "elementId": "{{OrdersReference.OrdersApi}}", "concurrency": {{apiWorkers}}, "queueCapacity": 100 },
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

    private async Task<(WebApplicationFactory<Program> Factory, HttpClient Owner, Guid WorkspaceId, OrdersReference Orders)> SeedAsync(
        Action<IServiceCollection>? configure = null)
    {
        var factory = TestHost.Create(fixture.ConnectionString, configure: configure);
        var owner = await TestHost.SignInAsync(factory);
        var (workspaceId, projectId) = await TestHost.CreateProjectAsync(owner);
        return (factory, owner, workspaceId, await OrdersReference.SeedAsync(owner, projectId));
    }

    private static async Task<Guid> CreateAsync(HttpClient client, Guid projectId, JsonObject profile) =>
        (await Read(client.PostAsync(Url(projectId, "profiles"), OrdersReference.Json(profile)), HttpStatusCode.Created))["id"]!.GetValue<Guid>();

    private static Task<JsonNode> RunAsync(HttpClient client, Guid projectId, Guid profileId, HttpStatusCode expected = HttpStatusCode.Created) =>
        Read(client.PostAsync(Url(projectId, $"profiles/{profileId}/runs"), null), expected);

    [Fact]
    public async Task The_orders_reference_flow_runs_is_stored_and_keeps_its_committed_numbers()
    {
        var (factory, owner, _, orders) = await SeedAsync();
        using var _f = factory;
        var project = orders.ProjectId;

        // What the form asks for comes from the scenario and the model.
        var needs = await Read(owner.GetAsync(Url(project, $"requirements?scenarioId={orders.ScenarioId}")));
        Assert.Equal([OrdersReference.OrdersApi, OrdersReference.OrdersDb], needs["resources"]!.AsArray().Select(r => r!["elementId"]!.GetValue<Guid>()));
        Assert.Equal(["operation", "store", "subscription", "subscription"], needs["serviceTimes"]!.AsArray().Select(t => t!["kind"]!.GetValue<string>()));
        Assert.Equal(2000, needs["serviceTimes"]![0]!["timeoutMs"]!.GetValue<int>());
        Assert.Equal(2, needs["subscriptions"]!.AsArray().Count);

        var profileId = await CreateAsync(owner, project, Profile(orders));
        var stored = await RunAsync(owner, project, profileId);
        var run = stored["run"]!;
        var result = stored["result"]!;

        // The committed summary of the reference case. A change here is a change of the engine: raise StressEngine.Version with it.
        var flow = result["flow"]!;
        string Ms(JsonNode? value) => value!.GetValue<double>().ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        JsonNode Element(Guid id) => result["elements"]!.AsArray().Single(e => e!["id"]!.GetValue<Guid>() == id)!;
        JsonNode Subscription(Guid id) => result["subscriptions"]!.AsArray().Single(e => e!["id"]!.GetValue<Guid>() == id)!;
        Assert.Equal(
            "arrivals=2002 completed=2002 failed=0 p50=28.077 p95=93.763 p99=180.132 api.util=0.189 db.util=0.08 inventory.util=0.301 billing.util=1 billing.p95=11051.99 bottleneck=Billing on order-events confidence=low",
            $"arrivals={flow["arrivals"]} completed={flow["counts"]!["completed"]} failed={flow["counts"]!["failed"]} "
            + $"p50={Ms(flow["latency"]!["p50Ms"])} p95={Ms(flow["latency"]!["p95Ms"])} p99={Ms(flow["latency"]!["p99Ms"])} "
            + $"api.util={Ms(Element(OrdersReference.OrdersApi)["utilisation"])} db.util={Ms(Element(OrdersReference.OrdersDb)["utilisation"])} "
            + $"inventory.util={Ms(Subscription(OrdersReference.InventorySub)["utilisation"])} billing.util={Ms(Subscription(OrdersReference.BillingSub)["utilisation"])} "
            + $"billing.p95={Ms(Subscription(OrdersReference.BillingSub)["latency"]!["p95Ms"])} "
            + $"bottleneck={result["bottleneck"]!["name"]?.GetValue<string>() ?? "none"} confidence={result["confidence"]!["level"]}");

        // What pins the run, and the labels a reader must see.
        Assert.Equal("estimated", result["label"]!.GetValue<string>());
        Assert.Equal("estimated", run["summary"]!["label"]!.GetValue<string>());
        Assert.Equal((1, "42", StressEngine.Version), (run["profileVersion"]!.GetValue<int>(), run["seed"]!.GetValue<string>(), run["engineVersion"]!.GetValue<string>()));
        Assert.Equal(orders.ScenarioId, run["scenarioId"]!.GetValue<Guid>());
        Assert.True(run["modelVersion"]!.GetValue<long>() > 0 && run["scenarioVersion"]!.GetValue<long>() > 0);
        Assert.False(run["outdated"]!.GetValue<bool>());
        Assert.Equal(StressEngine.AlwaysUnmodelled.Count, result["unmodelled"]!.AsArray().Count(u => u!["kind"]!.GetValue<string>() == "fixed"));
        // The scenario has assertions; the run says that it does not evaluate them.
        Assert.Contains(result["unmodelled"]!.AsArray(), u => u!["code"]!.GetValue<string>() == "scenario-assertions");
        Assert.Contains(result["assumptions"]!.AsArray(), a => a!["field"]!.GetValue<string>() == "medianMs" && a["label"]!.GetValue<string>() == "declared");

        // A second run of the same inputs has the same hash, and a read gives the stored result back.
        var again = await RunAsync(owner, project, profileId);
        Assert.Equal(run["hash"]!.GetValue<string>(), again["run"]!["hash"]!.GetValue<string>());
        Assert.NotEqual(run["id"]!.GetValue<Guid>(), again["run"]!["id"]!.GetValue<Guid>());
        var read = await Read(owner.GetAsync(Url(project, $"runs/{run["id"]}")));
        Assert.Equal(result.ToJsonString(), read["result"]!.ToJsonString());
        Assert.Equal(Profile(orders)["arrivals"]!["ratePerSecond"]!.GetValue<double>(), read["profile"]!["arrivals"]!["ratePerSecond"]!.GetValue<double>());

        var runs = (await Read(owner.GetAsync(Url(project, $"profiles/{profileId}/runs")))).AsArray();
        Assert.Equal(2, runs.Count);
        Assert.Null(runs[0]!["result"]);
        var profiles = (await Read(owner.GetAsync(Url(project, "profiles")))).AsArray();
        Assert.Equal(("Black Friday", 2), (profiles.Single()!["name"]!.GetValue<string>(), profiles.Single()!["runs"]!.GetValue<int>()));
    }

    [Fact]
    public async Task Two_runs_compare_by_metric_deltas_and_by_the_inputs_that_differ_without_a_verdict()
    {
        var (factory, owner, _, orders) = await SeedAsync();
        using var _f = factory;
        var project = orders.ProjectId;
        var profileId = await CreateAsync(owner, project, Profile(orders, apiWorkers: 2));
        var left = (await RunAsync(owner, project, profileId))["run"]!["id"]!.GetValue<Guid>();

        var changed = Profile(orders, apiWorkers: 8, seed: "43");
        changed["overrides"] = new JsonArray(new JsonObject { ["target"] = OrdersReference.PlaceOrder.ToString(), ["timeoutMs"] = 500 });
        var saved = await Read(owner.PutAsync(Url(project, $"profiles/{profileId}"), OrdersReference.Json(changed)));
        Assert.Equal(2, saved["version"]!.GetValue<int>());
        var rightRun = await RunAsync(owner, project, profileId);
        var right = rightRun["run"]!["id"]!.GetValue<Guid>();
        // The override is in the result next to what the model declares.
        var replaced = Assert.Single(rightRun["result"]!["overrides"]!.AsArray())!;
        Assert.Equal((2000, 500), (replaced["modelValue"]!.GetValue<int>(), replaced["profileValue"]!.GetValue<int>()));

        var comparison = await Read(owner.GetAsync(Url(project, $"compare?left={left}&right={right}")));
        var inputs = comparison["inputs"]!.AsArray().ToDictionary(i => $"{i!["input"]}:{i["target"]}", i => i!);
        Assert.Contains("profile:", inputs.Keys);
        Assert.Contains("seed:", inputs.Keys);
        Assert.DoesNotContain("model:", inputs.Keys);
        Assert.DoesNotContain("scenario:", inputs.Keys);
        var workers = inputs[$"resource.concurrency:{OrdersReference.OrdersApi}"];
        Assert.Equal(("2 workers (declared)", "8 workers (declared)"), (workers["left"]!.GetValue<string>(), workers["right"]!.GetValue<string>()));
        var timeout = inputs[$"override.timeoutMs:{OrdersReference.PlaceOrder}"];
        Assert.Null(timeout["left"]);
        Assert.Equal("500 (declared)", timeout["right"]!.GetValue<string>());
        // Only the inputs that differ are listed.
        Assert.DoesNotContain(inputs.Keys, k => k.StartsWith("arrivals.") || k.StartsWith("service-time."));

        var metrics = comparison["metrics"]!.AsArray();
        var p95 = metrics.Single(m => m!["scope"]!.GetValue<string>() == "flow" && m["metric"]!.GetValue<string>() == "latencyP95")!;
        Assert.Equal(p95["right"]!.GetValue<double>() - p95["left"]!.GetValue<double>(), p95["delta"]!.GetValue<double>());
        Assert.Contains(metrics, m => m!["scope"]!.GetValue<string>() == "element" && m["metric"]!.GetValue<string>() == "utilisation" && m["target"]!.GetValue<Guid>() == OrdersReference.OrdersApi);
        Assert.Equal("estimated", comparison["label"]!.GetValue<string>());
        Assert.Contains("no verdict", comparison["note"]!.GetValue<string>());
        Assert.DoesNotContain(comparison.AsObject(), p => p.Key is "verdict" or "winner" or "better");

        // The run of version 1 says that the profile has moved on.
        var first = await Read(owner.GetAsync(Url(project, $"runs/{left}")));
        Assert.Equal((1, 2), (first["run"]!["profileVersion"]!.GetValue<int>(), first["run"]!["currentProfileVersion"]!.GetValue<int>()));
    }

    [Fact]
    public async Task A_stored_run_is_outdated_when_the_model_or_the_scenario_changed_after_it()
    {
        var (factory, owner, _, orders) = await SeedAsync();
        using var _f = factory;
        var project = orders.ProjectId;
        var profileId = await CreateAsync(owner, project, Profile(orders));
        await RunAsync(owner, project, profileId);

        async Task<string[]> Because() =>
            (await Read(owner.GetAsync(Url(project, $"profiles/{profileId}/runs")))).AsArray().Single()!["outdatedBecause"]!.AsArray().Select(b => b!.GetValue<string>()).ToArray();

        Assert.Empty(await Because());

        await TestHost.Created(owner.PostAsJsonAsync($"/api/projects/{project}/elements", new { id = Guid.NewGuid(), kind = "system", name = "Later" }));
        Assert.Equal(["model"], await Because());

        await orders.UseOutboxAsync();
        Assert.Equal(["model", "scenario"], await Because());

        // The changed scenario calls the relay, which the profile says nothing about: the next run is refused.
        var refused = await RunAsync(owner, project, profileId, HttpStatusCode.UnprocessableEntity);
        Assert.Equal("stress-refused", refused["type"]!.GetValue<string>());
        Assert.Contains(refused["issues"]!.AsArray(), i => i!["code"]!.GetValue<string>() == "missing-service-time" && i["target"]!.GetValue<Guid>() == OrdersReference.RelayOutbox);
    }

    [Fact]
    public async Task A_refused_run_and_a_run_over_a_limit_store_nothing()
    {
        var (factory, owner, _, orders) = await SeedAsync(services => services.AddSingleton(StressLimits.Default with { MaxEvents = 2_000 }));
        using var _f = factory;
        var project = orders.ProjectId;

        // An incomplete profile is saved; the run names what it lacks.
        var incomplete = Profile(orders);
        incomplete["serviceTimes"]!.AsArray().RemoveAt(3);
        incomplete["subscriptions"]!.AsArray().Clear();
        var incompleteId = await CreateAsync(owner, project, incomplete);
        var refused = await RunAsync(owner, project, incompleteId, HttpStatusCode.UnprocessableEntity);
        Assert.Equal("stress-refused", refused["type"]!.GetValue<string>());
        Assert.Equal(
            ["missing-consumers", "missing-consumers", "missing-service-time"],
            refused["issues"]!.AsArray().Select(i => i!["code"]!.GetValue<string>()).Order());
        Assert.Contains(refused["issues"]!.AsArray(), i => i!["code"]!.GetValue<string>() == "missing-service-time" && i["target"]!.GetValue<Guid>() == OrdersReference.BillingSub);

        var completeId = await CreateAsync(owner, project, Profile(orders, name: "Complete"));
        var tooLarge = await RunAsync(owner, project, completeId, HttpStatusCode.UnprocessableEntity);
        Assert.Equal("stress-too-large", tooLarge["type"]!.GetValue<string>());
        Assert.Equal("events", Assert.Single(tooLarge["issues"]!.AsArray())!["limit"]!.GetValue<string>());

        Assert.Empty((await Read(owner.GetAsync(Url(project, $"profiles/{incompleteId}/runs")))).AsArray());
        Assert.Empty((await Read(owner.GetAsync(Url(project, $"profiles/{completeId}/runs")))).AsArray());
    }

    [Fact]
    public async Task A_save_refuses_a_profile_outside_its_rules()
    {
        var (factory, owner, _, orders) = await SeedAsync();
        using var _f = factory;
        var project = orders.ProjectId;

        async Task<string> Field(JsonObject profile)
        {
            var body = await Read(owner.PostAsync(Url(project, "profiles"), OrdersReference.Json(profile)), HttpStatusCode.BadRequest);
            Assert.Equal("validation_error", body["type"]!.GetValue<string>());
            return body["errors"]![0]!["field"]!.GetValue<string>();
        }

        var negative = Profile(orders);
        negative["resources"]![0]!["concurrency"] = 0;
        Assert.Equal("resources[0].concurrency", await Field(negative));

        // A person cannot promote a typed number to a measured one: nothing imports a measurement yet.
        var measured = Profile(orders);
        measured["serviceTimes"]![0]!["label"] = "measured";
        measured["serviceTimes"]![0]!["measurementRef"] = "k6:run-1";
        Assert.Equal("label", await Field(measured));

        var elsewhere = Profile(orders);
        elsewhere["scenarioId"] = Guid.NewGuid().ToString();
        Assert.Equal("scenarioId", await Field(elsewhere));

        var broken = await Read(owner.PostAsync(Url(project, "profiles"), new StringContent("{ \"name\": ", System.Text.Encoding.UTF8, "application/json")), HttpStatusCode.BadRequest);
        Assert.Equal("invalid_body", broken["type"]!.GetValue<string>());
        Assert.Empty((await Read(owner.GetAsync(Url(project, "profiles")))).AsArray());
    }

    [Fact]
    public async Task A_viewer_reads_and_only_an_editor_saves_runs_and_deletes()
    {
        var (factory, owner, workspaceId, orders) = await SeedAsync();
        using var _f = factory;
        var project = orders.ProjectId;
        var profileId = await CreateAsync(owner, project, Profile(orders));
        var runId = (await RunAsync(owner, project, profileId))["run"]!["id"]!.GetValue<Guid>();
        using var viewer = await TestHost.SignInMemberAsync(factory, owner, workspaceId, "Viewer");

        foreach (var path in new[] { "profiles", $"profiles/{profileId}", $"profiles/{profileId}/runs", $"runs/{runId}", $"compare?left={runId}&right={runId}", $"requirements?scenarioId={orders.ScenarioId}" })
            await Status(HttpStatusCode.OK, viewer.GetAsync(Url(project, path)), $"viewer GET {path}");

        await Status(HttpStatusCode.Forbidden, viewer.PostAsync(Url(project, "profiles"), OrdersReference.Json(Profile(orders))), "viewer POST profile");
        await Status(HttpStatusCode.Forbidden, viewer.PutAsync(Url(project, $"profiles/{profileId}"), OrdersReference.Json(Profile(orders))), "viewer PUT profile");
        await Status(HttpStatusCode.Forbidden, viewer.PostAsync(Url(project, $"profiles/{profileId}/runs"), null), "viewer POST run");
        await Status(HttpStatusCode.Forbidden, viewer.DeleteAsync(Url(project, $"runs/{runId}")), "viewer DELETE run");
        await Status(HttpStatusCode.Forbidden, viewer.DeleteAsync(Url(project, $"profiles/{profileId}")), "viewer DELETE profile");

        using var editor = await TestHost.SignInMemberAsync(factory, owner, workspaceId, "Editor");
        await Status(HttpStatusCode.Created, editor.PostAsync(Url(project, $"profiles/{profileId}/runs"), null), "editor POST run");
        await Status(HttpStatusCode.NoContent, editor.DeleteAsync(Url(project, $"runs/{runId}")), "editor DELETE run");
        await Status(HttpStatusCode.NotFound, editor.GetAsync(Url(project, $"runs/{runId}")), "GET deleted run");
        // The runs of a profile go with it.
        await Status(HttpStatusCode.NoContent, editor.DeleteAsync(Url(project, $"profiles/{profileId}")), "editor DELETE profile");
        await Status(HttpStatusCode.NotFound, editor.GetAsync(Url(project, $"profiles/{profileId}/runs")), "runs of a deleted profile");

        using var anonymous = factory.CreateClient();
        await Status(HttpStatusCode.Unauthorized, anonymous.GetAsync(Url(project, "profiles")), "anonymous GET");
    }

    [Fact]
    public async Task Stress_data_of_one_workspace_is_closed_to_a_user_of_another()
    {
        var (factory, owner, _, orders) = await SeedAsync();
        using var _f = factory;
        var theirs = orders.ProjectId;
        var profileId = await CreateAsync(owner, theirs, Profile(orders));
        var runId = (await RunAsync(owner, theirs, profileId))["run"]!["id"]!.GetValue<Guid>();
        var before = await owner.GetStringAsync(Url(theirs, $"profiles/{profileId}"));

        using var stranger = await TestHost.SignInAsync(factory);
        var (_, mine) = await TestHost.CreateProjectAsync(stranger);
        var own = await OrdersReference.SeedAsync(stranger, mine);
        var ownProfile = await CreateAsync(stranger, mine, Profile(own));
        var ownRun = (await RunAsync(stranger, mine, ownProfile))["run"]!["id"]!.GetValue<Guid>();

        // 1. The owner's project: everything is forbidden.
        foreach (var path in new[] { "profiles", $"profiles/{profileId}", $"profiles/{profileId}/runs", $"runs/{runId}", $"compare?left={runId}&right={runId}", $"requirements?scenarioId={orders.ScenarioId}" })
            await Status(HttpStatusCode.Forbidden, stranger.GetAsync(Url(theirs, path)), $"GET {path}");
        await Status(HttpStatusCode.Forbidden, stranger.PostAsync(Url(theirs, "profiles"), OrdersReference.Json(Profile(orders))), "POST profile");
        await Status(HttpStatusCode.Forbidden, stranger.PutAsync(Url(theirs, $"profiles/{profileId}"), OrdersReference.Json(Profile(orders))), "PUT profile");
        await Status(HttpStatusCode.Forbidden, stranger.PostAsync(Url(theirs, $"profiles/{profileId}/runs"), null), "POST run");
        await Status(HttpStatusCode.Forbidden, stranger.DeleteAsync(Url(theirs, $"profiles/{profileId}")), "DELETE profile");
        await Status(HttpStatusCode.Forbidden, stranger.DeleteAsync(Url(theirs, $"runs/{runId}")), "DELETE run");

        // 2. The stranger's own project with the owner's IDs: they name nothing here.
        await Status(HttpStatusCode.NotFound, stranger.GetAsync(Url(mine, $"profiles/{profileId}")), "GET foreign profile");
        await Status(HttpStatusCode.NotFound, stranger.PutAsync(Url(mine, $"profiles/{profileId}"), OrdersReference.Json(Profile(own))), "PUT foreign profile");
        await Status(HttpStatusCode.NotFound, stranger.DeleteAsync(Url(mine, $"profiles/{profileId}")), "DELETE foreign profile");
        await Status(HttpStatusCode.NotFound, stranger.PostAsync(Url(mine, $"profiles/{profileId}/runs"), null), "run foreign profile");
        await Status(HttpStatusCode.NotFound, stranger.GetAsync(Url(mine, $"profiles/{profileId}/runs")), "runs of foreign profile");
        await Status(HttpStatusCode.NotFound, stranger.GetAsync(Url(mine, $"runs/{runId}")), "GET foreign run");
        await Status(HttpStatusCode.NotFound, stranger.DeleteAsync(Url(mine, $"runs/{runId}")), "DELETE foreign run");
        await Status(HttpStatusCode.NotFound, stranger.GetAsync(Url(mine, $"compare?left={ownRun}&right={runId}")), "compare with a foreign run");
        await Status(HttpStatusCode.NotFound, stranger.GetAsync(Url(mine, $"requirements?scenarioId={orders.ScenarioId}")), "requirements of a foreign scenario");
        // A profile of the stranger's project cannot name the owner's scenario.
        await Status(HttpStatusCode.BadRequest, stranger.PostAsync(Url(mine, "profiles"), OrdersReference.Json(Profile(orders))), "profile for a foreign scenario");

        Assert.Equal(before, await owner.GetStringAsync(Url(theirs, $"profiles/{profileId}")));
        Assert.Single((await Read(owner.GetAsync(Url(theirs, $"profiles/{profileId}/runs")))).AsArray());
    }

    [Fact]
    public async Task Deleting_a_project_removes_its_profiles_and_runs()
    {
        var (factory, owner, _, orders) = await SeedAsync();
        using var _f = factory;
        var project = orders.ProjectId;
        var profileId = await CreateAsync(owner, project, Profile(orders));
        await RunAsync(owner, project, profileId);

        async Task<(int Profiles, int Runs)> CountAsync()
        {
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<StressDbContext>();
            return (await db.Profiles.CountAsync(p => p.ProjectId == project), await db.Runs.CountAsync(r => r.ProjectId == project));
        }

        Assert.Equal((1, 1), await CountAsync());
        await Status(HttpStatusCode.NoContent, owner.DeleteAsync($"/api/projects/{project}"), "DELETE project");
        Assert.Equal((0, 0), await CountAsync());
    }

    [Fact]
    public async Task A_profile_keeps_its_newest_runs_and_a_workspace_has_a_run_rate()
    {
        var (factory, owner, _, orders) = await SeedAsync(services =>
            services.AddSingleton(new StressRunPolicy(RunsPerMinutePerWorkspace: 3, MaxRunsPerProfile: 2, MaxProfilesPerProject: 1)));
        using var _f = factory;
        var project = orders.ProjectId;
        var profileId = await CreateAsync(owner, project, Profile(orders));

        var ids = new List<Guid>();
        for (var i = 0; i < 3; i++)
            ids.Add((await RunAsync(owner, project, profileId))["run"]!["id"]!.GetValue<Guid>());

        var kept = (await Read(owner.GetAsync(Url(project, $"profiles/{profileId}/runs")))).AsArray().Select(r => r!["id"]!.GetValue<Guid>()).ToArray();
        Assert.Equal([ids[2], ids[1]], kept);

        var limited = await owner.PostAsync(Url(project, $"profiles/{profileId}/runs"), null);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.True(limited.Headers.RetryAfter is not null);
        Assert.Equal("rate_limited", (await limited.Content.ReadFromJsonAsync<JsonObject>())!["type"]!.GetValue<string>());

        // Another workspace has its own rate.
        using var other = await TestHost.SignInAsync(factory);
        var (_, otherProject) = await TestHost.CreateProjectAsync(other);
        var own = await OrdersReference.SeedAsync(other, otherProject);
        await RunAsync(other, otherProject, await CreateAsync(other, otherProject, Profile(own)));

        await Status(HttpStatusCode.Conflict, owner.PostAsync(Url(project, "profiles"), OrdersReference.Json(Profile(orders, name: "Second"))), "profile over the limit");
    }

    [Fact]
    public void A_workspace_runs_a_bounded_number_of_stress_runs_at_the_same_time()
    {
        using var gate = new StressRunGate(new StressRunPolicy(MaxConcurrentPerWorkspace: 2));
        Guid workspace = Guid.NewGuid(), other = Guid.NewGuid();

        var first = gate.TryEnter(workspace, out _);
        var second = gate.TryEnter(workspace, out _);
        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Null(gate.TryEnter(workspace, out _));
        using (var elsewhere = gate.TryEnter(other, out _))
            Assert.NotNull(elsewhere);

        first!.Dispose();
        using var third = gate.TryEnter(workspace, out _);
        Assert.NotNull(third);
        second!.Dispose();
    }
}
