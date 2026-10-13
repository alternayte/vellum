using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Vellum.Tests.Modules.Import;

namespace Vellum.Tests.Modules.Promises;

/// <summary>
/// The change impact of a scan (SDD §8, capability 2, and the "Change impact" row of §10) on
/// the fixtures <c>orders</c> and <c>orders-outbox-removed</c>: what is definite stands on
/// source lines or a contract diff, what is reached through a flow is possible, and what the
/// scan did not look at is unknown.
/// </summary>
[Collection("Integration")]
public class ChangeImpactTests(ImportHost import) : IClassFixture<ImportHost>
{
    private static async Task<JsonObject> Ok(Task<HttpResponseMessage> request, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await request;
        Assert.True(response.StatusCode == expected, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<JsonObject>())!;
    }

    private static JsonObject Promise(string title, JsonObject links, bool requiresOutbox = false) => new()
    {
        ["title"] = title, ["claim"] = "Something must always hold.",
        ["preconditions"] = "p", ["expectedOutcome"] = "e", ["forbiddenOutcome"] = "f", ["evidenceCriteria"] = "c",
        ["requiresPattern"] = requiresOutbox ? new JsonObject { ["repository"] = "acme/orders", ["pattern"] = "transactional-outbox" } : null,
        ["links"] = links,
    };

    private static JsonObject Links(Guid[]? scenarios = null, Guid[]? messages = null, Guid[]? elements = null)
    {
        static JsonArray Ids(Guid[]? ids) => new([.. (ids ?? []).Select(id => (JsonNode)id.ToString())]);
        return new JsonObject
        {
            ["scenarioIds"] = Ids(scenarios), ["operationIds"] = Ids(null), ["channelIds"] = Ids(null),
            ["subscriptionIds"] = Ids(null), ["messageIds"] = Ids(messages), ["elementIds"] = Ids(elements),
        };
    }

    [Fact]
    public async Task A_comparison_that_removes_the_outbox_names_the_flow_and_the_promise_and_marks_only_what_it_can_show_as_definite()
    {
        var s = await ImportScenario.CreateAsync(import);
        await s.ScanAndAcceptAllAsync();
        async Task<Guid> IdOf(string kind, string name) => (await s.AllAsync(s.Model(kind))).Single("name", name).Id();
        var (ordersApi, billing, database) = (await IdOf("elements", "Orders.Api"), await IdOf("elements", "Billing"), await IdOf("elements", "postgres"));
        var (placeOrder, channel, message) = (await IdOf("operations", "POST /orders"), await IdOf("channels", "order-events"), await IdOf("messages", "OrderCreated"));

        // The declared flow: the API stores the order, then publishes OrderCreated.
        var (customer, scenario) = (Guid.NewGuid(), Guid.NewGuid());
        await TestHost.Created(s.Owner.PostAsJsonAsync(s.Model("elements"), new { id = customer, kind = "actor", name = "Customer" }));
        await TestHost.Created(s.Owner.PostAsync(s.Model("scenarios"), OrdersReference.Json(JsonNode.Parse($$"""
            {
              "id": "{{scenario}}", "name": "Place an order",
              "steps": [
                { "type": "call", "id": "place", "from": "{{customer}}", "operation": "{{placeOrder}}", "request": null, "response": null, "onError": [], "then": [
                  { "type": "write", "id": "save-order", "by": "{{ordersApi}}", "store": "{{database}}", "dataset": "orders", "change": { "orderId": "o-1" } } ] },
                { "type": "publish", "id": "publish-order-created", "from": "{{ordersApi}}", "channel": "{{channel}}", "message": "{{message}}", "headers": {}, "handlers": {},
                  "payload": { "orderId": "7d0f1a6e-3c1b-4f5e-9a51-0f4f0c1d2e3f", "customerId": "2b8a9c10-5d4e-4f60-8a7b-9c0d1e2f3a4b", "total": 4.2, "createdAt": "2026-01-01T10:00:00Z" } }
              ],
              "faults": [],
              "assertions": [ { "type": "outcome", "id": "order-accepted", "step": "place", "expected": "Succeeded" } ]
            }
            """)!)));

        await Ok(s.Owner.PutAsJsonAsync(s.Model("promises/prefix"), new { prefix = "ORD", startAt = 14 }));
        async Task<Guid> CreateAsync(JsonObject body) =>
            (await Ok(s.Owner.PostAsync(s.Model("promises"), OrdersReference.Json(body)), HttpStatusCode.Created)).Id();
        var ord014 = await CreateAsync(Promise("An accepted order must survive a broker outage", Links([scenario], [message], [ordersApi]), requiresOutbox: true));
        var ord015 = await CreateAsync(Promise("Billing keeps its ledger", Links(elements: [billing])));
        var ord016 = await CreateAsync(Promise("An order is placed", Links([scenario])));
        var generated = await Ok(s.Owner.PostAsync(s.Model($"promises/{ord014}/obligations/generate"), null));
        var sourceCheck = generated["promise"]!["current"]!["obligations"]!.AsArray().Single(o => o!["kind"]!.GetValue<string>() == "source-pattern-check")!["key"]!.GetValue<string>();
        var hash = (await s.Owner.GetFromJsonAsync<JsonObject>(s.Model($"promises/{ord014}")))!["current"]!["contentHash"]!.GetValue<string>();
        await Ok(s.Owner.PostAsJsonAsync(s.Model($"promises/{ord014}/approve"), new { revision = 1, contentHash = hash }));

        // The branch that removes the outbox, scanned beside the tracked ref.
        s.GitHub.Commit(ImportScenario.Commit2, FixtureGitHub.Load("orders-outbox-removed"));
        s.GitHub.Head = ImportScenario.Commit1;
        s.GitHub.Branches["remove-outbox"] = ImportScenario.Commit2;
        // A scan of the same ref that reads only billing did not look where the outbox was: unknown, never definite.
        var scoped = await s.ScanAsync(body: new { repository = "acme/orders", @ref = "remove-outbox", includePaths = new[] { "billing" } });
        var narrow = await Ok(s.Owner.GetAsync(s.Url($"/{scoped.Id()}/impact")));
        var unseen = narrow["changes"]!.AsArray().Select(c => c!.AsObject()).Single(c => c.Text("kind") == "source-pattern");
        Assert.Equal(("remove", "unknown"), (unseen.Text("action"), unseen.Text("certainty")));
        Assert.Contains("did not look at every file", unseen.Text("basis"));
        var unsure = narrow["promises"]!.AsArray().Select(p => p!.AsObject()).Single(p => p.Id() == ord014);
        Assert.DoesNotContain(unsure["reasons"]!.AsArray(), r => r!["changeId"]!.GetValue<string>() == unseen.Text("id") && r["certainty"]!.GetValue<string>() != "unknown");
        Assert.NotEqual("broken", unsure.Text("standing"));

        // The same ref, read whole.
        var comparison = await s.ScanAsync(body: new { repository = "acme/orders", @ref = "remove-outbox" });
        Assert.Equal(("completed", "comparison"), (comparison.Text("status"), comparison.Text("kind")));

        var impact = await Ok(s.Owner.GetAsync(s.Url($"/{comparison.Id()}/impact")));
        Assert.Equal(("comparison", ImportScenario.Commit1, ImportScenario.Commit2, "remove-outbox"), (
            impact.Text("kind"), impact["base"]!["commitSha"]!.GetValue<string>(), impact["head"]!["commitSha"]!.GetValue<string>(), impact["head"]!["ref"]!.GetValue<string>()));
        var changes = impact["changes"]!.AsArray().Select(c => c!.AsObject()).ToList();

        // The removed outbox: definite, on the lines of the base commit where it was, in the service of the import.
        var outbox = changes.Single(c => c.Text("kind") == "source-pattern");
        Assert.Equal(("remove", "definite", "transactional-outbox", ordersApi), (outbox.Text("action"), outbox.Text("certainty"), outbox.Text("pattern"), outbox.Id("elementId")));
        Assert.NotEmpty(outbox["evidence"]!.AsArray());
        Assert.All(outbox["evidence"]!.AsArray(), e => Assert.StartsWith($"{s.GitHubHost}/acme/orders@{ImportScenario.Commit1}:orders-api/", e!["pointer"]!.GetValue<string>()));

        // The changed contract: definite, with the diff and the lines of the head commit.
        var schema = changes.Single(c => c.Text("entityKind") == "schema");
        Assert.Equal(("contract", "change", "OrderCreated", "definite"), (schema.Text("kind"), schema.Text("action"), schema.Text("name"), schema.Text("certainty")));
        Assert.Equal("content", schema["diff"]![0]!["field"]!.GetValue<string>());
        Assert.Contains($"@{ImportScenario.Commit2}:", schema["evidence"]![0]!["pointer"]!.GetValue<string>());
        // Nothing of Billing changed, so no change is of Billing.
        Assert.DoesNotContain(changes, c => c["elementId"]?.GetValue<Guid>() == billing);

        // The flow: definite for the step that publishes the changed message, possible for the service of the outbox.
        var flow = Assert.Single(impact["flows"]!.AsArray())!.AsObject();
        Assert.Equal((scenario, "definite"), (flow.Id("scenarioId"), flow.Text("certainty")));
        Assert.Contains("publish-order-created", flow["stepIds"]!.AsArray().Select(step => step!.GetValue<string>()));
        var flowReasons = flow["reasons"]!.AsArray().Select(r => r!.AsObject()).ToList();
        Assert.Equal("definite", flowReasons.Single(r => r.Text("changeId") == schema.Text("id")).Text("certainty"));
        Assert.Equal("possible", flowReasons.Single(r => r.Text("changeId") == outbox.Text("id")).Text("certainty"));

        // The promises: ORD-014 is at risk on a definite reason; the promise that only plays the flow is possible; the unrelated one is absent.
        var promises = impact["promises"]!.AsArray().Select(p => p!.AsObject()).ToList();
        Assert.Equal([ord014, ord016], promises.Select(p => p.Id()));
        Assert.DoesNotContain(promises, p => p.Id() == ord015);
        Assert.Equal(1, impact["promisesNotAffected"]!.GetValue<int>());
        var atRisk = promises[0];
        Assert.Equal(("ORD-014", "approved", "at-risk", "definite"), (atRisk.Text("requirementId"), atRisk.Text("state"), atRisk.Text("standing"), atRisk.Text("certainty")));
        var why = atRisk["reasons"]!.AsArray().Select(r => r!.AsObject()).ToList();
        Assert.Equal("definite", why.First(r => r.Text("changeId") == outbox.Text("id")).Text("certainty"));
        Assert.Contains("requires the pattern 'transactional-outbox'", why.First(r => r.Text("changeId") == outbox.Text("id")).Text("text"));
        Assert.Contains(why, r => r.Text("certainty") == "definite" && r.Text("text")!.Contains("links the message 'OrderCreated'"));
        Assert.Equal(("not-run", sourceCheck), (atRisk["checks"]![0]!["result"]!.GetValue<string>(), atRisk["checks"]![0]!["key"]!.GetValue<string>()));
        Assert.Equal(("ORD-016", "draft", "possible", "possible"), (promises[1].Text("requirementId"), promises[1].Text("state"), promises[1].Text("standing"), promises[1].Text("certainty")));
        Assert.All(promises[1]["reasons"]!.AsArray(), r => Assert.Equal("possible", r!["certainty"]!.GetValue<string>()));

        // The check against the comparison is unsupported, as the check defines. That is not a failure, so the promise is not broken.
        var run = await Ok(s.Owner.PostAsJsonAsync(s.Model($"promises/{ord014}/obligations/{sourceCheck}/run"), new { scanId = comparison.Id() }));
        Assert.Equal("unsupported", run.Text("result"));
        var afterRun = (await Ok(s.Owner.GetAsync(s.Url($"/{comparison.Id()}/impact"))))["promises"]![0]!;
        Assert.Equal(("at-risk", "unsupported"), (afterRun["standing"]!.GetValue<string>(), afterRun["checks"]![0]!["result"]!.GetValue<string>()));

        // The brief of the change selects what the impact names and holds the report.
        var brief = await Ok(s.Owner.PostAsJsonAsync(s.Model("build/brief"), new { changeScanId = comparison.Id() }));
        Assert.Equal(comparison.Id(), brief["manifest"]!.AsObject().Id("changeScanId"));
        Assert.Contains("ORD-014", brief["manifest"]!["included"]!["requirementIds"]!.AsArray().Select(r => r!.GetValue<string>()));
        Assert.Contains(ord014, brief["manifest"]!["selection"]!["promiseIds"]!.AsArray().Select(p => p!.GetValue<Guid>()));
        var text = brief.Text("brief")!;
        Assert.Contains("## 8. Change under review", text);
        Assert.Contains("| remove | pattern | Transactional outbox | definite |", text);
        Assert.Contains("| `ORD-014` | at-risk | definite |", text);
        // A brief of a selection alone has no such section and names no scan.
        var plain = await Ok(s.Owner.PostAsJsonAsync(s.Model("build/brief"), new { promiseIds = new[] { ord014 } }));
        Assert.DoesNotContain("Change under review", plain.Text("brief"));
        Assert.Null(plain["manifest"]!["changeScanId"]);

        // The model follows the ref: the source check fails against that scan, and only then the promise is broken.
        await Ok(s.Owner.PostAsync(s.Url($"/{comparison.Id()}/track"), null));
        var failed = await Ok(s.Owner.PostAsJsonAsync(s.Model($"promises/{ord014}/obligations/{sourceCheck}/run"), new { scanId = comparison.Id() }));
        Assert.Equal("failed", failed.Text("result"));
        var followed = await Ok(s.Owner.GetAsync(s.Url($"/{comparison.Id()}/impact")));
        Assert.Equal("tracking", followed.Text("kind"));
        Assert.Equal(("broken", "failed"), (followed["promises"]![0]!["standing"]!.GetValue<string>(), followed["promises"]![0]!["checks"]![0]!["result"]!.GetValue<string>()));
        // The scan is now the one the model follows. The report compares it with the newest whole scan of another commit.
        Assert.Equal("remove", followed["changes"]!.AsArray().Select(c => c!.AsObject()).Single(c => c.Text("kind") == "source-pattern").Text("action"));
    }

    [Fact]
    public async Task The_impact_of_a_scan_is_closed_to_another_workspace_and_to_another_project()
    {
        var s = await ImportScenario.CreateAsync(import);
        var scan = await s.ScanAsync();
        Assert.Equal(HttpStatusCode.OK, (await s.Owner.GetAsync(s.Url($"/{scan.Id()}/impact"))).StatusCode);

        using var stranger = await TestHost.SignInAsync(import.Host.Factory);
        var (_, mine) = await TestHost.CreateProjectAsync(stranger);
        Assert.Equal(HttpStatusCode.Forbidden, (await stranger.GetAsync(s.Url($"/{scan.Id()}/impact"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"/api/projects/{mine}/imports/{scan.Id()}/impact")).StatusCode);
        // A brief of the stranger's own project cannot read the scan of another project either.
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.PostAsJsonAsync($"/api/projects/{mine}/build/brief", new { changeScanId = scan.Id() })).StatusCode);

        // A scan that is not completed has no report.
        var queued = await s.StartAsync();
        Assert.Equal(HttpStatusCode.Conflict, (await s.Owner.GetAsync(s.Url($"/{queued}/impact"))).StatusCode);
        await s.DrainAsync();
    }
}
