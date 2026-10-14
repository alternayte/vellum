using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vellum.Tests.Modules.Import;
using Vellum.Tests.Shared.Outbound;

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

    /// <summary>What the outbox tests share: the accepted import, the declared flow and three promises.</summary>
    internal sealed record Arranged(
        ImportScenario S, Guid OrdersApi, Guid Billing, Guid Scenario, Guid Ord014, Guid Ord015, Guid Ord016, string SourceCheck, Guid MainScan);

    /// <summary>
    /// Imports <paramref name="files"/> (the fixture <c>orders</c> when null) and accepts every
    /// proposal; declares the flow "Place an order"; creates ORD-014 (requires the outbox, links
    /// the flow, OrderCreated and Orders.Api, approved, with a source pattern check), ORD-015
    /// (links Billing) and ORD-016 (a draft that links the flow).
    /// </summary>
    private Task<Arranged> ArrangeAsync(IDictionary<string, byte[]>? files = null) => ArrangeAsync(import, files);

    internal static async Task<Arranged> ArrangeAsync(ImportHost import, IDictionary<string, byte[]>? files = null)
    {
        var s = await ImportScenario.CreateAsync(import);
        if (files is not null)
            s.GitHub.Commit(ImportScenario.Commit1, files);
        var main = await s.ScanAndAcceptAllAsync();
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
        return new Arranged(s, ordersApi, billing, scenario, ord014, ord015, ord016, sourceCheck, main.Id());
    }

    /// <summary>Puts <paramref name="files"/> on the branch <c>remove-outbox</c> as Commit2; <c>main</c> stays at Commit1.</summary>
    internal static void Branch(ImportScenario s, IDictionary<string, byte[]> files, string name = "remove-outbox")
    {
        s.GitHub.Commit(ImportScenario.Commit2, files);
        s.GitHub.Head = ImportScenario.Commit1;
        s.GitHub.Branches[name] = ImportScenario.Commit2;
    }

    [Fact]
    public async Task A_comparison_that_removes_the_outbox_names_the_flow_and_the_promise_and_marks_only_what_it_can_show_as_definite()
    {
        var (s, ordersApi, billing, scenario, ord014, ord015, ord016, sourceCheck, _) = await ArrangeAsync();

        // The branch that removes the outbox, scanned beside the tracked ref.
        Branch(s, FixtureGitHub.Load("orders-outbox-removed"));
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
        Assert.Equal(["definite", "possible"], flowReasons.Where(r => r.Text("changeId") == schema.Text("id")).Select(r => r.Text("certainty")));
        Assert.Equal("possible", flowReasons.Single(r => r.Text("changeId") == outbox.Text("id")).Text("certainty"));

        // Each step on its own, in the order the scenario writes them. Only the step that
        // publishes the message whose schema changed is definite; it stands on the schema diff.
        var steps = flow["steps"]!.AsArray().Select(step => step!.AsObject()).ToList();
        Assert.Equal(
            [("place", "call", "POST /orders", "possible"), ("save-order", "write", "postgres · orders", "possible"), ("publish-order-created", "publish", "OrderCreated", "definite")],
            steps.Select(step => (step.Text("stepId"), step.Text("kind"), step.Text("label"), step.Text("certainty"))));
        var published = steps[2]["reasons"]!.AsArray().Select(r => r!.AsObject()).ToList();
        Assert.Contains(published, r => r.Text("changeId") == schema.Text("id") && r.Text("certainty") == "definite");
        Assert.DoesNotContain(published, r => r.Text("changeId") == schema.Text("id") && r.Text("certainty") != "definite");
        // A possible step names what reaches it: the removed outbox of its service and the schema its service produces.
        Assert.All(steps[..2], step => Assert.Contains(step["reasons"]!.AsArray(), r => r!["changeId"]!.GetValue<string>() == outbox.Text("id") && r["certainty"]!.GetValue<string>() == "possible"));
        Assert.All(steps[..2], step => Assert.DoesNotContain(step["reasons"]!.AsArray(), r => r!["certainty"]!.GetValue<string>() == "definite"));

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

        // The comparison read the whole commit, so the check fails against it, and the promise is broken in this report.
        var run = await Ok(s.Owner.PostAsJsonAsync(s.Model($"promises/{ord014}/obligations/{sourceCheck}/run"), new { scanId = comparison.Id() }));
        Assert.Equal("failed", run.Text("result"));
        var afterRun = (await Ok(s.Owner.GetAsync(s.Url($"/{comparison.Id()}/impact"))))["promises"]![0]!;
        Assert.Equal(("broken", "failed"), (afterRun["standing"]!.GetValue<string>(), afterRun["checks"]![0]!["result"]!.GetValue<string>()));

        // The brief of the change selects what the impact names and holds the report.
        var brief = await Ok(s.Owner.PostAsJsonAsync(s.Model("build/brief"), new { changeScanId = comparison.Id() }));
        Assert.Equal(comparison.Id(), brief["manifest"]!.AsObject().Id("changeScanId"));
        Assert.Contains("ORD-014", brief["manifest"]!["included"]!["requirementIds"]!.AsArray().Select(r => r!.GetValue<string>()));
        Assert.Contains(ord014, brief["manifest"]!["selection"]!["promiseIds"]!.AsArray().Select(p => p!.GetValue<Guid>()));
        var text = brief.Text("brief")!;
        Assert.Contains("## 8. Change under review", text);
        Assert.Contains("| remove | pattern | Transactional outbox | definite |", text);
        Assert.Contains("| `ORD-014` | broken | definite |", text);
        // A brief of a selection alone has no such section and names no scan.
        var plain = await Ok(s.Owner.PostAsJsonAsync(s.Model("build/brief"), new { promiseIds = new[] { ord014 } }));
        Assert.DoesNotContain("Change under review", plain.Text("brief"));
        Assert.Null(plain["manifest"]!["changeScanId"]);

        // The model follows the ref: the check against the scan, now a tracking scan, fails as well.
        await Ok(s.Owner.PostAsync(s.Url($"/{comparison.Id()}/track"), null));
        var failed = await Ok(s.Owner.PostAsJsonAsync(s.Model($"promises/{ord014}/obligations/{sourceCheck}/run"), new { scanId = comparison.Id() }));
        Assert.Equal("failed", failed.Text("result"));
        var followed = await Ok(s.Owner.GetAsync(s.Url($"/{comparison.Id()}/impact")));
        Assert.Equal("tracking", followed.Text("kind"));
        Assert.Equal(("broken", "failed"), (followed["promises"]![0]!["standing"]!.GetValue<string>(), followed["promises"]![0]!["checks"]![0]!["result"]!.GetValue<string>()));
        // The scan is now the one the model follows. The report compares it with the newest whole scan of another commit.
        Assert.Equal("remove", followed["changes"]!.AsArray().Select(c => c!.AsObject()).Single(c => c.Text("kind") == "source-pattern").Text("action"));
    }

    private static List<JsonObject> Items(JsonObject impact, string member) => [.. impact[member]!.AsArray().Select(c => c!.AsObject())];

    private static Task<JsonObject> RemoveOutboxAsync(ImportScenario s) =>
        s.ScanAsync(body: new { repository = "acme/orders", @ref = "remove-outbox" });

    [Fact]
    public async Task A_later_run_of_the_check_against_another_scan_leaves_the_promise_broken_against_the_scan_where_it_failed()
    {
        var (s, _, _, _, ord014, _, _, sourceCheck, main) = await ArrangeAsync();
        Branch(s, FixtureGitHub.Load("orders-outbox-removed"));
        var comparison = await RemoveOutboxAsync(s);
        await Ok(s.Owner.PostAsync(s.Url($"/{comparison.Id()}/track"), null));
        var failed = await Ok(s.Owner.PostAsJsonAsync(s.Model($"promises/{ord014}/obligations/{sourceCheck}/run"), new { scanId = comparison.Id() }));
        Assert.Equal("failed", failed.Text("result"));

        // Someone runs the same check again, against another scan. That run says nothing about this scan.
        await Ok(s.Owner.PostAsJsonAsync(s.Model($"promises/{ord014}/obligations/{sourceCheck}/run"), new { scanId = main }));

        var impact = await Ok(s.Owner.GetAsync(s.Url($"/{comparison.Id()}/impact")));
        var promise = Items(impact, "promises").Single(p => p.Id() == ord014);
        Assert.Equal(("broken", "failed"), (promise.Text("standing"), promise["checks"]![0]!["result"]!.GetValue<string>()));
        var brief = await Ok(s.Owner.PostAsJsonAsync(s.Model("build/brief"), new { changeScanId = comparison.Id() }));
        Assert.Contains("| `ORD-014` | broken |", brief.Text("brief"));
    }

    private static Dictionary<string, byte[]> WithHandWrittenOutbox(string fixture) =>
        new(FixtureGitHub.Load(fixture), StringComparer.Ordinal)
        {
            ["orders-api/OutboxDb.cs"] = System.Text.Encoding.UTF8.GetBytes("""
                using Microsoft.EntityFrameworkCore;

                namespace Orders.Api;

                public class OutboxDb(DbContextOptions<OutboxDb> options) : DbContext(options)
                {
                    public DbSet<OutboxMessage> Outbox { get; set; }
                }
                """),
            ["orders-api/OutboxDispatcher.cs"] = System.Text.Encoding.UTF8.GetBytes("""
                namespace Orders.Api;

                public sealed class OutboxDispatcher(IServiceScopeFactory scopes) : BackgroundService
                {
                    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
                    {
                        using var scope = scopes.CreateScope();
                        var db = scope.ServiceProvider.GetRequiredService<OutboxDb>();
                        var bus = scope.ServiceProvider.GetRequiredService<IPublishEndpoint>();
                        var pending = await db.Outbox.Where(m => m.SentAt == null).ToListAsync(stoppingToken);
                        foreach (var message in pending)
                            await bus.Publish(message.ToEvent(), stoppingToken);
                    }
                }
                """),
        };

    [Fact]
    public async Task One_outbox_fact_gone_while_another_of_the_same_pattern_and_service_stays_does_not_put_the_promise_at_risk()
    {
        // Both commits hold a hand-written outbox in orders-api. Only the MassTransit outbox goes.
        var (s, _, billing, _, _, _, _, _, _) = await ArrangeAsync(WithHandWrittenOutbox("orders"));
        var needsOutbox = (await Ok(s.Owner.PostAsync(s.Model("promises"),
            OrdersReference.Json(Promise("An order survives an outage", Links(elements: [billing]), requiresOutbox: true))), HttpStatusCode.Created)).Id();
        Branch(s, WithHandWrittenOutbox("orders-outbox-removed"));
        var comparison = await RemoveOutboxAsync(s);

        var impact = await Ok(s.Owner.GetAsync(s.Url($"/{comparison.Id()}/impact")));
        // The MassTransit fact is gone, and the change says so. The hand-written one is in both snapshots, so it is no change.
        var gone = Assert.Single(Items(impact, "changes"), c => c.Text("kind") == "source-pattern");
        Assert.Equal(("remove", "definite", "transactional-outbox"), (gone.Text("action"), gone.Text("certainty"), gone.Text("pattern")));
        Assert.Contains("still declares the pattern", gone.Text("basis"));

        // The pattern is still declared in the same service: the promise is not at risk on a definite reason.
        var promise = Items(impact, "promises").Single(p => p.Id() == needsOutbox);
        Assert.Equal(("possible", "possible"), (promise.Text("standing"), promise.Text("certainty")));
        var reason = Assert.Single(promise["reasons"]!.AsArray().Select(r => r!.AsObject()), r => r.Text("changeId") == gone.Text("id"));
        Assert.Equal("possible", reason.Text("certainty"));
        Assert.Contains("still declares the pattern", reason.Text("text"));
    }

    [Fact]
    public async Task A_pattern_that_the_head_adds_is_not_definite_against_a_partial_base_scan()
    {
        // The followed scan read five files of a commit with no outbox.
        var s = await ImportScenario.CreateAsync(import, fixture: null);
        s.GitHub.Commit(ImportScenario.Commit1, FixtureGitHub.Load("orders-outbox-removed"));
        JsonObject GitHub() => new() { ["apiBaseUrl"] = $"https://{s.GitHubHost}" };
        await OutboundHost.PutSettingsAsync(s.Owner, s.WorkspaceId, new JsonObject { ["github"] = GitHub(), ["import"] = new JsonObject { ["maxFiles"] = 5 } });
        var partial = await s.ScanAsync();
        Assert.Equal(("completed", false), (partial.Text("status"), partial["isComplete"]!.GetValue<bool>()));
        var proposals = await s.ProposalsAsync(partial.Id());
        Assert.NotEmpty(proposals);
        await s.DecideAsync(partial.Id(), accept: proposals.Select(p => p.Id()));

        // A whole scan of a branch with the outbox.
        await OutboundHost.PutSettingsAsync(s.Owner, s.WorkspaceId, new JsonObject { ["github"] = GitHub() });
        Branch(s, FixtureGitHub.Load("orders"), "add-outbox");
        var head = await s.ScanAsync(body: new { repository = "acme/orders", @ref = "add-outbox" });
        Assert.Equal(("completed", true, "comparison"), (head.Text("status"), head["isComplete"]!.GetValue<bool>(), head.Text("kind")));

        var impact = await Ok(s.Owner.GetAsync(s.Url($"/{head.Id()}/impact")));
        Assert.Equal(partial.Id(), impact["base"]!.AsObject().Id("scanId"));
        var added = Items(impact, "changes").Single(c => c.Text("kind") == "source-pattern" && c.Text("pattern") == "transactional-outbox");
        Assert.Equal(("add", "unknown"), (added.Text("action"), added.Text("certainty")));
        Assert.Contains("did not look", added.Text("basis"));
    }

    [Fact]
    public void The_impact_reads_only_the_pattern_facts_of_a_snapshot_from_the_database()
    {
        using var scope = import.Host.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Vellum.Modules.Import.ImportDbContext>();
        var sql = Vellum.Modules.Promises.ChangeImpact.PatternRows(db, Guid.NewGuid()).ToQueryString();
        Assert.Contains("attributes ? 'pattern'", sql);
    }

    [Fact]
    public async Task A_brief_of_a_change_is_the_same_for_the_same_pinned_state_and_names_only_approved_promises_in_the_change()
    {
        var (s, ordersApi, _, _, ord014, _, ord016, _, _) = await ArrangeAsync();
        Branch(s, FixtureGitHub.Load("orders-outbox-removed"));
        var comparison = await RemoveOutboxAsync(s);
        var revision = Guid.NewGuid();
        await TestHost.Created(s.Owner.PostAsJsonAsync(s.Model("revisions"), new { id = revision, name = "Before the rename" }));
        Task<JsonObject> BriefAsync() => Ok(s.Owner.PostAsJsonAsync(s.Model("build/brief"), new { changeScanId = comparison.Id(), revisionId = revision }));

        var first = await BriefAsync();
        // The main model moves on. The revision, the scan and every other input stay as they were.
        Assert.Equal(HttpStatusCode.OK, (await s.Owner.PatchAsJsonAsync(s.Model($"elements/{ordersApi}"), new { name = "Orders API" })).StatusCode);
        var second = await BriefAsync();
        Assert.Equal(first["manifest"]!["pinned"]!.ToJsonString(), second["manifest"]!["pinned"]!.ToJsonString());
        Assert.Equal(first.Text("brief"), second.Text("brief"));
        Assert.Equal(first["manifest"]!["sha256"]!.GetValue<string>(), second["manifest"]!["sha256"]!.GetValue<string>());

        // The draft ORD-016 plays the affected flow. The change does not select it and its section does not name it.
        var text = first.Text("brief")!;
        var change = text[text.IndexOf("## 8. Change under review", StringComparison.Ordinal)..];
        Assert.Contains("| `ORD-014` | at-risk |", change);
        Assert.DoesNotContain("ORD-016", change);
        var selected = first["manifest"]!["selection"]!["promiseIds"]!.AsArray().Select(p => p!.GetValue<Guid>()).ToList();
        Assert.Contains(ord014, selected);
        Assert.DoesNotContain(ord016, selected);
    }

    // A later scan of the followed ref supersedes the pending proposals of earlier scans of it.
    // The proposals of a scan are inputs of its pinned brief, so the brief does not change.
    [Fact]
    public async Task A_brief_of_a_change_stays_the_same_when_a_later_scan_of_the_ref_supersedes_its_proposals()
    {
        var (s, _, _, _, _, _, _, _, _) = await ArrangeAsync();
        s.GitHub.Commit(ImportScenario.Commit2, FixtureGitHub.Load("orders-outbox-removed"));
        var tracking = await s.ScanAsync();
        Assert.Equal(("completed", "tracking"), (tracking.Text("status"), tracking.Text("kind")));
        Assert.Contains(await s.ProposalsAsync(tracking.Id()), p => p.Text("status") == "pending" && p.Text("entityKind") == "schema");
        var revision = Guid.NewGuid();
        await TestHost.Created(s.Owner.PostAsJsonAsync(s.Model("revisions"), new { id = revision, name = "Before the next scan" }));
        Task<JsonObject> BriefAsync() => Ok(s.Owner.PostAsJsonAsync(s.Model("build/brief"), new { changeScanId = tracking.Id(), revisionId = revision }));

        var first = await BriefAsync();
        Assert.Contains("| change | schema | OrderCreated |", first.Text("brief"));
        var later = await s.ScanAsync();
        Assert.NotEqual(tracking.Id(), later.Id());
        Assert.Contains(await s.ProposalsAsync(tracking.Id()), p => p.Text("status") == "superseded");

        var second = await BriefAsync();
        Assert.Equal(first.Text("brief"), second.Text("brief"));
        Assert.Equal(first["manifest"]!["sha256"]!.GetValue<string>(), second["manifest"]!["sha256"]!.GetValue<string>());
    }

    [Fact]
    public async Task The_impact_of_a_scan_whose_proposals_were_accepted_does_not_say_the_model_lacks_them()
    {
        var s = await ImportScenario.CreateAsync(import);
        var main = await s.ScanAndAcceptAllAsync();
        var accepted = (await s.ProposalsAsync(main.Id())).Count;
        async Task<List<JsonObject>> ContractsAsync() =>
            Items(await Ok(s.Owner.GetAsync(s.Url($"/{main.Id()}/impact"))), "changes").Where(c => c.Text("kind") == "contract").ToList();

        var before = await ContractsAsync();
        Branch(s, FixtureGitHub.Load("orders-outbox-removed"));
        await RemoveOutboxAsync(s);
        var after = await ContractsAsync();

        foreach (var changes in new[] { before, after })
        {
            Assert.Equal(accepted, changes.Count);
            Assert.All(changes, c => Assert.Equal("add", c.Text("action")));
            Assert.All(changes, c => Assert.DoesNotContain("The model does not hold it", c.Text("basis")));
            Assert.All(changes, c => Assert.Contains("The model holds it", c.Text("basis")));
        }
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
