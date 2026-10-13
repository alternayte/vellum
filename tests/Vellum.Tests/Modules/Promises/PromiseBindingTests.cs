using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Deedbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vellum.Modules.Promises;
using Vellum.Tests.Modules.Import;
using Vellum.Tests.Shared.Outbound;

namespace Vellum.Tests.Modules.Promises;

/// <summary>
/// What a result and an approval are bound to. An approval binds the content the approver
/// read; a result binds every input it read and is not shown as current when one moved; a
/// check that exercises nothing is not a pass; and a source check concludes absence only
/// from a scan that could have found the pattern. Everything goes through the HTTP API.
/// </summary>
[Collection("Integration")]
public class PromiseBindingTests(ImportHost import) : IClassFixture<ImportHost>
{
    private static string Url(Guid projectId, string path = "") => $"/api/projects/{projectId}/promises{path}";

    private async Task<(HttpClient Owner, Guid WorkspaceId, Guid ProjectId)> NewProjectAsync()
    {
        var owner = await TestHost.SignInAsync(import.Host.Factory);
        var (workspaceId, projectId) = await TestHost.CreateProjectAsync(owner);
        return (owner, workspaceId, projectId);
    }

    private static async Task<JsonObject> Ok(Task<HttpResponseMessage> request, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await request;
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {text}");
        return JsonNode.Parse(text)!.AsObject();
    }

    private static Task<HttpResponseMessage> Post(HttpClient client, string url, JsonNode? body = null) =>
        client.PostAsync(url, body is null ? null : OrdersReference.Json(body));

    private static JsonObject StatusOf(JsonObject promise, string obligationId, string revision = "current") =>
        promise[revision]!["evidenceStatus"]!["obligations"]!.AsArray().Single(o => o!["obligationId"]!.GetValue<string>() == obligationId)!.AsObject();

    private static List<string> Gaps(JsonObject promise) =>
        promise["current"]!["evidenceStatus"]!["gaps"]!.AsArray().Select(g => g!.GetValue<string>()).ToList();

    private static int Count(JsonObject promise, string name) => promise["current"]!["evidenceStatus"]!["counts"]![name]!.GetValue<int>();

    /// <summary>A scenario-check obligation of the Orders scenario with these faults and assertions.</summary>
    private static JsonObject Check(OrdersReference orders, JsonArray expect, JsonArray? faults = null, string title = "A check") => new()
    {
        ["kind"] = "scenario-check",
        ["title"] = title,
        ["recipe"] = new JsonObject
        {
            ["text"] = "Play the scenario.",
            ["scenarioCheck"] = new JsonObject { ["scenarioId"] = orders.ScenarioId.ToString(), ["faults"] = faults ?? [], ["expect"] = expect },
        },
    };

    private static JsonObject Outcome(string id, string step, string expected = "Succeeded") =>
        new() { ["type"] = "outcome", ["id"] = id, ["step"] = step, ["expected"] = expected };

    private static JsonObject EventCount(string id, string kind, int expected, Guid? target = null) =>
        new() { ["type"] = "event-count", ["id"] = id, ["kind"] = kind, ["expected"] = expected, ["target"] = target?.ToString() };

    private static JsonObject Outage(Guid target, int? failFirst = null) =>
        new() { ["target"] = target.ToString(), ["kind"] = "Outage", ["failFirst"] = failFirst };

    /// <summary>A draft promise with no generated obligation, so a test adds exactly the ones it is about.</summary>
    private static async Task<Guid> DraftAsync(OrdersReference orders)
    {
        await Ok(orders.Client.PutAsJsonAsync(orders.Url("promises/prefix"), new { prefix = "ORD", startAt = 14 }));
        return (await Ok(orders.Client.PostAsync(orders.Url("promises"), OrdersReference.Json(orders.Ord014(requiresPattern: false))), HttpStatusCode.Created))["id"]!.GetValue<Guid>();
    }

    [Fact]
    public async Task An_approval_binds_the_content_the_approver_read()
    {
        var (owner, _, projectId) = await NewProjectAsync();
        var orders = await OrdersReference.SeedAsync(owner, projectId);
        var id = (await orders.CreateOrd014Async())["id"]!.GetValue<Guid>();

        // Every read gives the hash of what an approval would bind.
        var read = await orders.ApprovalAsync(id);
        var hash = read["contentHash"]!.GetValue<string>();
        Assert.Matches("^[0-9a-f]{64}$", hash);
        Assert.Equal(hash, (await orders.GetAsync(id))["current"]!["contentHash"]!.GetValue<string>());
        Assert.Null((await orders.GetAsync(id))["current"]!["approvedContentHash"]);

        // An approval with no hash binds nothing and is refused.
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync(Url(projectId, $"/{id}/approve"), new { revision = 1 })).StatusCode);

        // The text changes after the approver read it. The revision number is the same; the content is not.
        var changed = orders.Ord014();
        changed["forbiddenOutcome"] = "Nothing is forbidden.";
        await Ok(owner.PutAsync(Url(projectId, $"/{id}"), OrdersReference.Json(changed)));
        var refused = await Ok(Post(owner, Url(projectId, $"/{id}/approve"), read.DeepClone()), HttpStatusCode.Conflict);
        var now = (await orders.GetAsync(id))["current"]!["contentHash"]!.GetValue<string>();
        Assert.Equal(("revision_changed", 1, now), (refused["type"]!.GetValue<string>(), refused["revision"]!.GetValue<int>(), refused["contentHash"]!.GetValue<string>()));
        Assert.NotEqual(hash, now);
        Assert.Equal("draft", (await orders.GetAsync(id))["state"]!.GetValue<string>());

        // The hash covers the obligations and their recipes: a changed fault is other content.
        await Ok(owner.PutAsync(Url(projectId, $"/{id}"), OrdersReference.Json(orders.Ord014())));
        Assert.Equal(hash, (await orders.GetAsync(id))["current"]!["contentHash"]!.GetValue<string>());
        var obligation = (await orders.GetAsync(id))["current"]!["obligations"]![0]!;
        var recipe = obligation["recipe"]!.DeepClone();
        recipe["scenarioCheck"]!["faults"] = new JsonArray();
        await Ok(owner.PutAsync(Url(projectId, $"/{id}/obligations/O1"), OrdersReference.Json(new JsonObject { ["kind"] = "scenario-check", ["title"] = obligation["title"]!.GetValue<string>(), ["recipe"] = recipe })));
        Assert.Equal("revision_changed", (await Ok(Post(owner, Url(projectId, $"/{id}/approve"), read.DeepClone()), HttpStatusCode.Conflict))["type"]!.GetValue<string>());

        // The approver reads again and approves what they read. The read model keeps the hash.
        var approved = await orders.ApproveAsync(id);
        var approvedHash = approved["current"]!["approvedContentHash"]!.GetValue<string>();
        Assert.Equal(approved["current"]!["contentHash"]!.GetValue<string>(), approvedHash);
        Assert.NotEqual(hash, approvedHash);
        Assert.DoesNotContain(Gaps(approved), g => g.Contains("is not the content that was approved"));

        // A rebuild folds the stream again and checks the approval against the content: the same hash, and no gap.
        var admin = import.Host.Factory.Services.GetRequiredService<IEventStoreAdmin>();
        var jobId = await admin.RebuildAsync("promise_read_model");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        while (true)
        {
            var job = await admin.GetJobAsync(jobId, timeout.Token);
            Assert.NotEqual(JobState.Failed, job!.Status);
            if (job.Status == JobState.Done
                && (await admin.GetStatusAsync(timeout.Token)).Consumers.Single(c => c.Name == "promise_read_model").Status == ConsumerState.Running)
                break;
            await Task.Delay(100, timeout.Token);
        }
        var rebuilt = await orders.GetAsync(id);
        Assert.Equal((approvedHash, approvedHash), (rebuilt["current"]!["approvedContentHash"]!.GetValue<string>(), rebuilt["current"]!["contentHash"]!.GetValue<string>()));
        Assert.DoesNotContain(Gaps(rebuilt), g => g.Contains("is not the content that was approved"));
    }

    [Fact]
    public async Task Of_an_edit_and_an_approval_at_the_same_time_one_wins_and_the_approved_content_is_what_was_read()
    {
        var (owner, _, projectId) = await NewProjectAsync();
        var orders = await OrdersReference.SeedAsync(owner, projectId);
        await Ok(owner.PutAsJsonAsync(Url(projectId, "/prefix"), new { prefix = "ORD" }));

        var approvedFirst = 0;
        for (var i = 0; i < 12; i++)
        {
            var id = (await Ok(Post(owner, Url(projectId), orders.Ord014(requiresPattern: false)), HttpStatusCode.Created))["id"]!.GetValue<Guid>();
            var read = await orders.ApprovalAsync(id);
            var edit = orders.Ord014(requiresPattern: false);
            edit["claim"] = $"Another claim {i}.";

            var approving = Post(owner, Url(projectId, $"/{id}/approve"), read);
            var editing = owner.PutAsync(Url(projectId, $"/{id}"), OrdersReference.Json(edit));
            var (approve, change) = (await approving, await editing);

            var after = await orders.GetAsync(id);
            if (approve.StatusCode == HttpStatusCode.OK)
            {
                // The approval was first: the edit met an approved revision. What is in force is what was read.
                approvedFirst++;
                Assert.Equal(HttpStatusCode.Conflict, change.StatusCode);
                Assert.Equal(("approved", read["contentHash"]!.GetValue<string>()), (after["state"]!.GetValue<string>(), after["current"]!["contentHash"]!.GetValue<string>()));
            }
            else
            {
                // The edit was first: the approval names content that is no longer the draft.
                Assert.Equal((HttpStatusCode.Conflict, HttpStatusCode.OK), (approve.StatusCode, change.StatusCode));
                Assert.Equal("revision_changed", (await approve.Content.ReadFromJsonAsync<JsonObject>())!["type"]!.GetValue<string>());
                Assert.Equal(("draft", $"Another claim {i}."), (after["state"]!.GetValue<string>(), after["current"]!["claim"]!.GetValue<string>()));
            }
            Assert.Equal(after["current"]!["approvedContentHash"]?.GetValue<string>(), after["state"]!.GetValue<string>() == "approved" ? after["current"]!["contentHash"]!.GetValue<string>() : null);
        }
        // Both orders are possible; neither leaves an approved revision with content that was not read.
        Assert.InRange(approvedFirst, 0, 12);
    }

    [Fact]
    public async Task A_result_is_stale_when_anything_it_read_changes()
    {
        var (owner, _, projectId) = await NewProjectAsync();
        var orders = await OrdersReference.SeedAsync(owner, projectId);
        var id = await DraftAsync(orders);
        await Ok(Post(owner, Url(projectId, $"/{id}/obligations"),
            Check(orders, [Outcome("reaches-the-broker", OrdersReference.PublishStep)], [Outage(OrdersReference.OrderEvents, 1)])), HttpStatusCode.Created);
        await orders.ApproveAsync(id);
        await orders.UseOutboxAsync();

        // The run names everything it read, at the version it read it.
        var run = await orders.RunAsync(id, "O1");
        Assert.Equal("passed", run["result"]!.GetValue<string>());
        var inputs = run["inputs"]!;
        Assert.Equal((run["modelRevision"]!["streamId"]!.GetValue<Guid>(), run["modelRevision"]!["version"]!.GetValue<long>(), orders.ScenarioId), (
            inputs["modelStreamId"]!.GetValue<Guid>(), inputs["modelVersion"]!.GetValue<long>(), inputs["scenarioId"]!.GetValue<Guid>()));
        Assert.Equal(2, inputs["scenarioVersion"]!.GetValue<long>());
        Assert.Matches("^[0-9a-f]{64}$", inputs["scenarioHash"]!.GetValue<string>());
        Assert.Matches("^[0-9a-f]{64}$", inputs["obligationHash"]!.GetValue<string>());
        Assert.Equal(
            new[] { (orders.OrderCreatedSchema, 1), (orders.OrderRequestSchema, 1) }.ToHashSet(),
            inputs["schemas"]!.AsArray().Select(s => (s!["id"]!.GetValue<Guid>(), s["version"]!.GetValue<int>())).ToHashSet());

        async Task<JsonObject> CurrentAsync()
        {
            var promise = await orders.GetAsync(id);
            var status = StatusOf(promise, "ORD-014/O1");
            Assert.Equal(("passed", true), (status["result"]!.GetValue<string>(), status["current"]!.GetValue<bool>()));
            Assert.Empty(status["changed"]!.AsArray());
            Assert.Equal((1, 0), (Count(promise, "passed"), Count(promise, "stale")));
            return promise;
        }

        async Task StaleAsync(string because)
        {
            var promise = await orders.GetAsync(id);
            var status = StatusOf(promise, "ORD-014/O1");
            // The result is still what the run found. It is shown as not current and is not counted as a pass.
            Assert.Equal(("passed", false), (status["result"]!.GetValue<string>(), status["current"]!.GetValue<bool>()));
            Assert.Contains(status["changed"]!.AsArray(), c => c!.GetValue<string>().Contains(because));
            Assert.Contains(Gaps(promise), g => g.StartsWith("ORD-014/O1 passed (stale): ", StringComparison.Ordinal) && g.Contains(because));
            Assert.Equal((0, 1), (Count(promise, "passed"), Count(promise, "stale")));

            // The list says the same as the promise.
            var summary = (await owner.GetFromJsonAsync<JsonObject>(Url(projectId)))!["items"]!.AsArray().Single()!;
            Assert.Equal((0, 1, true), (summary["counts"]!["passed"]!.GetValue<int>(), summary["counts"]!["stale"]!.GetValue<int>(), summary["hasGaps"]!.GetValue<bool>()));

            // A new run on the current inputs is current again.
            await orders.RunAsync(id, "O1");
            await CurrentAsync();
        }

        await CurrentAsync();

        // The scenario changes: the result was for another scenario.
        var scenario = orders.WithOutbox();
        scenario["name"] = "Place an order (renamed)";
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsync(orders.Url($"scenarios/{orders.ScenarioId}"), OrdersReference.Json(scenario))).StatusCode);
        await StaleAsync($"it played scenario 'Place an order (renamed)' ({orders.ScenarioId}) at version 2. The scenario is now at version 3 and its content changed.");

        // A schema the playback validates against changes.
        Assert.Equal(HttpStatusCode.OK, (await owner.PatchAsJsonAsync(orders.Url($"schemas/{orders.OrderCreatedSchema}"),
            new { content = """{"type":"object","required":["orderId"],"properties":{"orderId":{"type":"string","minLength":1}}}""", setContent = true })).StatusCode);
        await StaleAsync($"it validated against schema {orders.OrderCreatedSchema} at version 1. The schema is now at version 2.");

        // The model changes, even in a part the scenario does not touch: the result names the version it ran on.
        await TestHost.Created(owner.PostAsJsonAsync(orders.Url("elements"), new { id = Guid.NewGuid(), kind = "system", name = "Unrelated" }));
        await StaleAsync("The model is now at version");
    }

    [Fact]
    public async Task A_check_that_exercises_nothing_is_not_a_pass()
    {
        var (owner, _, projectId) = await NewProjectAsync();
        var orders = await OrdersReference.SeedAsync(owner, projectId);
        // The scenario gets a branch that is taken only when the order call fails. Without a fault it never runs.
        var scenario = orders.WithoutOutbox();
        scenario["steps"]!.AsArray().Add(JsonNode.Parse($$"""
            { "type": "branch", "id": "on-failure", "whenStep": "place", "is": "Failed",
              "then": [ { "type": "write", "id": "compensate", "by": "{{OrdersReference.OrdersApi}}", "store": "{{OrdersReference.OrdersDb}}", "dataset": "orders", "change": { "status": "cancelled" } } ],
              "else": [] }
            """));
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsync(orders.Url($"scenarios/{orders.ScenarioId}"), OrdersReference.Json(scenario))).StatusCode);
        var id = await DraftAsync(orders);
        async Task<string> AddAsync(JsonObject check) =>
            (await Ok(Post(owner, Url(projectId, $"/{id}/obligations"), check), HttpStatusCode.Created))["current"]!["obligations"]!.AsArray().Last()!["key"]!.GetValue<string>();

        // O1: no fault makes a consumer fail, so no message can be dropped. "Zero drops" then shows nothing.
        var onlyDrops = await AddAsync(Check(orders, [EventCount("nothing-is-lost", "Dropped", 0)]));
        // O2: no delivery can fail, so nothing can reach the dead-letter channel.
        var otherChannel = await AddAsync(Check(orders, [EventCount("no-dead-letter", "DeadLettered", 0, OrdersReference.DeadLetters)]));
        // O3: the broker is down, and the only assertion is about a step that ends before the broker is used.
        var unreached = await AddAsync(Check(orders, [Outcome("order-accepted", "place")], [Outage(OrdersReference.OrderEvents)]));
        // O4: a step of a branch that is not taken has no outcome, and the engine reads no outcome as "skipped".
        var noSuchStep = await AddAsync(Check(orders, [Outcome("never-runs", "compensate", "Skipped")]));
        // O5: the same vacuous assertions beside one that the outage does reach.
        var mixed = await AddAsync(Check(orders,
            [Outcome("order-accepted", "place"), EventCount("nothing-is-lost", "Dropped", 0), Outcome("broker-refuses", OrdersReference.PublishStep, "Failed")],
            [Outage(OrdersReference.OrderEvents)]));
        // O6: a consumer is down, so a drop can happen; the subscription has no dead-letter channel. The count is a real check.
        var realDrop = await AddAsync(Check(orders, [EventCount("billing-loses-the-event", "Dropped", 1, OrdersReference.BillingSub)], [Outage(OrdersReference.Billing)]));
        await orders.ApproveAsync(id);

        foreach (var key in new[] { onlyDrops, otherChannel, unreached, noSuchStep })
        {
            var run = await orders.RunAsync(id, key);
            Assert.Equal("unsupported", run["result"]!.GetValue<string>());
            var assertion = Assert.Single(run["assertions"]!.AsArray())!;
            Assert.Equal((true, true), (assertion["passed"]!.GetValue<bool>(), assertion["vacuous"]!.GetValue<bool>()));
            Assert.False(string.IsNullOrWhiteSpace(assertion["vacuousReason"]!.GetValue<string>()));
            Assert.Contains("every one is vacuous", run["detail"]!.GetValue<string>());
        }

        var some = await orders.RunAsync(id, mixed);
        Assert.Equal("passed", some["result"]!.GetValue<string>());
        Assert.Equal(
            [("order-accepted", true), ("nothing-is-lost", true), ("broker-refuses", false)],
            some["assertions"]!.AsArray().Select(a => (a!["id"]!.GetValue<string>(), a["vacuous"]!.GetValue<bool>())));
        Assert.Contains("2 of them are vacuous", some["detail"]!.GetValue<string>());

        var real = await orders.RunAsync(id, realDrop);
        Assert.Equal(("passed", false), (real["result"]!.GetValue<string>(), real["assertions"]![0]!["vacuous"]!.GetValue<bool>()));

        // The status counts none of the vacuous checks as passed and names each vacuous assertion.
        var promise = await orders.GetAsync(id);
        Assert.Equal((2, 4), (Count(promise, "passed"), Count(promise, "unsupported")));
        Assert.Contains(Gaps(promise), g => g.StartsWith("ORD-014/O5: assertion 'order-accepted' is vacuous.", StringComparison.Ordinal));
        Assert.Contains(Gaps(promise), g => g.StartsWith("ORD-014/O1 is unsupported for the input it was given", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_evidence_hash_covers_what_was_asserted_and_what_was_read()
    {
        var (owner, _, projectId) = await NewProjectAsync();
        var orders = await OrdersReference.SeedAsync(owner, projectId);
        var id = await DraftAsync(orders);
        // Two checks whose assertions have the same ID and both hold, but assert different things.
        await Ok(Post(owner, Url(projectId, $"/{id}/obligations"), Check(orders, [Outcome("x", "place")])), HttpStatusCode.Created);
        await Ok(Post(owner, Url(projectId, $"/{id}/obligations"), Check(orders, [EventCount("x", "RequestSent", 1, OrdersReference.PlaceOrder)])), HttpStatusCode.Created);
        await orders.ApproveAsync(id);

        var first = await orders.RunAsync(id, "O1");
        var second = await orders.RunAsync(id, "O2");
        Assert.Equal(("passed", "passed"), (first["result"]!.GetValue<string>(), second["result"]!.GetValue<string>()));
        Assert.NotEqual(first["evidence"]![0]!["hash"]!.GetValue<string>(), second["evidence"]![0]!["hash"]!.GetValue<string>());

        // The same check on the same inputs has the same hash; on another model version it has another.
        Assert.Equal(first["evidence"]![0]!["hash"]!.GetValue<string>(), (await orders.RunAsync(id, "O1"))["evidence"]![0]!["hash"]!.GetValue<string>());
        await TestHost.Created(owner.PostAsJsonAsync(orders.Url("elements"), new { id = Guid.NewGuid(), kind = "system", name = "Unrelated" }));
        Assert.NotEqual(first["evidence"]![0]!["hash"]!.GetValue<string>(), (await orders.RunAsync(id, "O1"))["evidence"]![0]!["hash"]!.GetValue<string>());
    }

    [Fact]
    public async Task An_obligation_adds_faults_to_the_scenario_and_never_replaces_one()
    {
        var (owner, _, projectId) = await NewProjectAsync();
        var orders = await OrdersReference.SeedAsync(owner, projectId);
        var id = await DraftAsync(orders);

        // The scenario itself declares that the broker is down for every publish.
        var scenario = orders.WithoutOutbox();
        scenario["faults"] = new JsonArray(Outage(OrdersReference.OrderEvents));
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsync(orders.Url($"scenarios/{orders.ScenarioId}"), OrdersReference.Json(scenario))).StatusCode);

        // An obligation that would let the broker come back after one attempt weakens that fault. It is refused when it is written.
        var weaker = await Ok(Post(owner, Url(projectId, $"/{id}/obligations"),
            Check(orders, [Outcome("reaches-the-broker", OrdersReference.PublishStep)], [Outage(OrdersReference.OrderEvents, 1)])), HttpStatusCode.BadRequest);
        Assert.Equal("recipe.scenarioCheck.faults[0].target", weaker["errors"]![0]!["field"]!.GetValue<string>());
        Assert.Contains("cannot weaken or replace", weaker["errors"]![0]!["message"]!.GetValue<string>());

        // A fault on another target is added. The run lists every fault that was played.
        await Ok(Post(owner, Url(projectId, $"/{id}/obligations"),
            Check(orders, [Outcome("broker-refuses", OrdersReference.PublishStep, "Failed"), EventCount("order-is-stored", "DataWriteFailed", 0, OrdersReference.OrdersDb)],
                [new JsonObject { ["target"] = OrdersReference.Billing.ToString(), ["kind"] = "Error", ["failFirst"] = null }])), HttpStatusCode.Created);
        await orders.ApproveAsync(id);
        var run = await orders.RunAsync(id, "O1");
        Assert.Equal(
            [(OrdersReference.OrderEvents, "Outage"), (OrdersReference.Billing, "Error")],
            run["effectiveFaults"]!.AsArray().Select(f => (f!["target"]!.GetValue<Guid>(), f["kind"]!.GetValue<string>())));
        Assert.Contains("with 2 fault(s) (1 of the scenario, 1 of the obligation)", run["scope"]!.GetValue<string>());

        // The scenario later faults the target the approved obligation faults. Nothing is played: neither fault replaces the other.
        scenario["faults"] = new JsonArray(Outage(OrdersReference.OrderEvents), Outage(OrdersReference.Billing));
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsync(orders.Url($"scenarios/{orders.ScenarioId}"), OrdersReference.Json(scenario))).StatusCode);
        var collided = await orders.RunAsync(id, "O1");
        Assert.Equal("not-run", collided["result"]!.GetValue<string>());
        Assert.Contains("does not replace one of the scenario", collided["detail"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_run_is_of_the_revision_in_force_and_a_run_of_a_draft_never_counts_after_approval()
    {
        var (owner, _, projectId) = await NewProjectAsync();
        var orders = await OrdersReference.SeedAsync(owner, projectId);
        var id = await DraftAsync(orders);
        await Ok(Post(owner, Url(projectId, $"/{id}/obligations"), Check(orders, [Outcome("order-accepted", "place")])), HttpStatusCode.Created);

        // A draft is checked only when it is named, and the run says that it was a draft.
        Assert.Equal("no_approved_revision", (await Ok(Post(owner, Url(projectId, $"/{id}/obligations/O1/run")), HttpStatusCode.Conflict))["type"]!.GetValue<string>());
        var ofDraft = await orders.RunAsync(id, "O1", new { revision = 1 });
        Assert.Equal(("passed", 1, true), (ofDraft["result"]!.GetValue<string>(), ofDraft["promiseRevision"]!.GetValue<int>(), ofDraft["draft"]!.GetValue<bool>()));

        // Approval does not turn the draft's run into evidence of the approved revision.
        var approved = await orders.ApproveAsync(id);
        Assert.Equal("not-run", StatusOf(approved, "ORD-014/O1")["result"]!.GetValue<string>());
        Assert.Equal((0, 1), (Count(approved, "passed"), Count(approved, "notRun")));
        Assert.Contains(Gaps(approved), g => g == "ORD-014/O1 was last run while revision 1 was a draft. A run of a draft does not count for an approved revision; run it again.");

        var inForce = await orders.RunAsync(id, "O1");
        Assert.Equal((1, null), (inForce["promiseRevision"]!.GetValue<int>(), inForce["draft"]?.GetValue<bool>()));
        Assert.Equal(1, Count(await orders.GetAsync(id), "passed"));

        // With a new draft beside it, a run with no revision is still of the revision in force.
        await Ok(Post(owner, Url(projectId, $"/{id}/revise")));
        Assert.Equal(1, (await orders.RunAsync(id, "O1"))["promiseRevision"]!.GetValue<int>());
        var second = await orders.RunAsync(id, "O1", new { revision = 2 });
        Assert.Equal((2, true), (second["promiseRevision"]!.GetValue<int>(), second["draft"]!.GetValue<bool>()));
        var both = await orders.GetAsync(id);
        Assert.Equal(("passed", true), (StatusOf(both, "ORD-014/O1")["result"]!.GetValue<string>(), StatusOf(both, "ORD-014/O1")["current"]!.GetValue<bool>()));
        Assert.Equal(1, Count(both, "passed"));
    }

    [Fact]
    public async Task A_promise_at_its_size_limit_is_still_revised_retired_and_deleted()
    {
        var (owner, _, projectId) = await NewProjectAsync();
        var orders = await OrdersReference.SeedAsync(owner, projectId);
        var id = await DraftAsync(orders);

        // One obligation whose recipe is near the recipe limit: seven steps of the longest text.
        static JsonObject Plan(int steps, char fill) => new()
        {
            ["kind"] = "integration-test",
            ["title"] = "A long plan",
            ["recipe"] = new JsonObject
            {
                ["text"] = "Run the plan.",
                ["plan"] = new JsonObject
                {
                    ["steps"] = new JsonArray(Enumerable.Range(0, steps).Select(_ => (JsonNode)new string(fill, 4000)).ToArray()),
                    ["expectedObservations"] = new JsonArray("It holds."),
                },
            },
        };

        // A recipe has its own limit, and the count of obligations has its own.
        var tooLongRecipe = await Ok(Post(owner, Url(projectId, $"/{id}/obligations"), Plan(9, 'x')), HttpStatusCode.BadRequest);
        Assert.Equal("recipe", tooLongRecipe["errors"]![0]!["field"]!.GetValue<string>());

        // Obligations are added until the revision is at its limit. The write that passes it gets 413 and changes nothing.
        var added = 0;
        while (true)
        {
            var response = await Post(owner, Url(projectId, $"/{id}/obligations"), Plan(7, 'x'));
            if (response.StatusCode != HttpStatusCode.Created)
            {
                Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
                Assert.Equal("promise_too_large", (await response.Content.ReadFromJsonAsync<JsonObject>())!["type"]!.GetValue<string>());
                break;
            }
            added++;
            Assert.True(added < 50, "The revision limit must be reached before the obligation count limit.");
        }
        Assert.InRange(added, 5, 12);
        Assert.Equal(added, (await orders.GetAsync(id))["current"]!["obligations"]!.AsArray().Count);
        var tooLongText = orders.Ord014(requiresPattern: false);
        tooLongText["scope"] = new string('s', 4000);
        tooLongText["preconditions"] = new string('p', 4000);
        tooLongText["expectedOutcome"] = new string('e', 4000);
        tooLongText["forbiddenOutcome"] = new string('f', 4000);
        tooLongText["evidenceCriteria"] = new string('c', 4000);
        tooLongText["claim"] = new string('k', 4000);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await owner.PutAsync(Url(projectId, $"/{id}"), OrdersReference.Json(tooLongText))).StatusCode);

        // At the limit the promise is approved, revised again and again, and retired. A revision that retires copies nothing.
        await orders.ApproveAsync(id);
        for (var revision = 2; revision <= 10; revision++)
        {
            await Ok(Post(owner, Url(projectId, $"/{id}/revise")));
            await orders.ApproveAsync(id, revision);
        }
        var retiring = await Ok(Post(owner, Url(projectId, $"/{id}/revise"), new JsonObject { ["retire"] = true }));
        Assert.Equal((11, 10, true), (retiring["draft"]!["revision"]!.GetValue<int>(), retiring["draft"]!["retiresRevision"]!.GetValue<int>(), retiring["draft"]!["retires"]!.GetValue<bool>()));
        Assert.Empty(retiring["draft"]!["obligations"]!.AsArray());
        // It holds no content to change; it shows the text of what it retires.
        Assert.Equal(retiring["current"]!["claim"]!.GetValue<string>(), retiring["draft"]!["claim"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.Conflict, (await owner.PutAsync(Url(projectId, $"/{id}"), OrdersReference.Json(orders.Ord014(requiresPattern: false)))).StatusCode);
        var retired = await orders.ApproveAsync(id, 11);
        Assert.Equal("retired", retired["state"]!.GetValue<string>());

        // A draft at the limit is deleted.
        var draft = (await Ok(Post(owner, Url(projectId), orders.Ord014(requiresPattern: false)), HttpStatusCode.Created))["id"]!.GetValue<Guid>();
        for (var i = 0; i < added; i++)
            await Ok(Post(owner, Url(projectId, $"/{draft}/obligations"), Plan(7, 'y')), HttpStatusCode.Created);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await Post(owner, Url(projectId, $"/{draft}/obligations"), Plan(7, 'y'))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync(Url(projectId, $"/{draft}"))).StatusCode);
    }

    [Fact]
    public async Task Deleting_a_project_deletes_its_promises_runs_and_numbering_and_a_rebuild_does_not_bring_them_back()
    {
        var (owner, _, projectId) = await NewProjectAsync();
        var orders = await OrdersReference.SeedAsync(owner, projectId);
        var id = await DraftAsync(orders);
        await Ok(Post(owner, Url(projectId, $"/{id}/obligations"), Check(orders, [Outcome("order-accepted", "place")])), HttpStatusCode.Created);
        await orders.ApproveAsync(id);
        await orders.RunAsync(id, "O1");

        async Task<(int Promises, int Runs, int Numbering)> RowsAsync()
        {
            using var scope = import.Host.Factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<PromisesDbContext>();
            return (await db.Promises.CountAsync(p => p.ProjectId == projectId), await db.Runs.CountAsync(r => r.ProjectId == projectId),
                await db.Numbering.CountAsync(n => n.ProjectId == projectId));
        }
        Assert.Equal((1, 1, 1), await RowsAsync());

        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/api/projects/{projectId}")).StatusCode);
        Assert.Equal((0, 0, 0), await RowsAsync());

        var admin = import.Host.Factory.Services.GetRequiredService<IEventStoreAdmin>();
        var jobId = await admin.RebuildAsync("promise_read_model");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        while (true)
        {
            var job = await admin.GetJobAsync(jobId, timeout.Token);
            Assert.NotEqual(JobState.Failed, job!.Status);
            if (job.Status == JobState.Done
                && (await admin.GetStatusAsync(timeout.Token)).Consumers.Single(c => c.Name == "promise_read_model").Status == ConsumerState.Running)
                break;
            await Task.Delay(100, timeout.Token);
        }
        Assert.Equal((0, 0, 0), await RowsAsync());
    }

    private async Task<ImportScenario> WithGitHubAsync()
    {
        var owner = await TestHost.SignInAsync(import.Host.Factory);
        var (workspaceId, projectId) = await TestHost.CreateProjectAsync(owner);
        var host = $"{Guid.NewGuid():N}.gh.test";
        await OutboundHost.PutSettingsAsync(owner, workspaceId, new JsonObject { ["github"] = new JsonObject { ["apiBaseUrl"] = $"https://{host}" } });
        var gitHub = new FixtureGitHub().Commit(ImportScenario.Commit1, FixtureGitHub.Load("orders"));
        import.Host.Transport.For(host).Then(gitHub.Handle);
        return new ImportScenario(import, owner, workspaceId, projectId, host, gitHub);
    }

    /// <summary>A promise that needs the transactional outbox in <c>acme/orders</c>, with its one generated source check, approved.</summary>
    private static async Task<Guid> OutboxPromiseAsync(ImportScenario s, Guid? service = null, bool? requireUnconditional = null, string prefix = "ORD")
    {
        var prefixResponse = await s.Owner.PutAsJsonAsync(Url(s.ProjectId, "/prefix"), new { prefix });
        Assert.True(prefixResponse.StatusCode is HttpStatusCode.OK or HttpStatusCode.Conflict);
        var body = new JsonObject
        {
            ["title"] = "The outbox is declared", ["claim"] = "The service declares a transactional outbox.",
            ["preconditions"] = "None.", ["expectedOutcome"] = "The outbox is declared.", ["forbiddenOutcome"] = "No outbox.", ["evidenceCriteria"] = "The source declares it.",
            ["requiresPattern"] = new JsonObject { ["repository"] = "acme/orders", ["pattern"] = "transactional-outbox", ["service"] = service?.ToString() },
        };
        var id = (await Ok(Post(s.Owner, Url(s.ProjectId), body), HttpStatusCode.Created))["id"]!.GetValue<Guid>();
        var generated = await Ok(Post(s.Owner, Url(s.ProjectId, $"/{id}/obligations/generate")));
        var obligation = Assert.Single(generated["promise"]!["current"]!["obligations"]!.AsArray())!;
        if (requireUnconditional is { } required)
        {
            var recipe = obligation["recipe"]!.DeepClone();
            recipe["sourcePattern"]!["requireUnconditional"] = required;
            await Ok(s.Owner.PutAsync(Url(s.ProjectId, $"/{id}/obligations/O1"),
                OrdersReference.Json(new JsonObject { ["kind"] = "source-pattern-check", ["title"] = obligation["title"]!.GetValue<string>(), ["recipe"] = recipe })));
        }
        var read = (await s.Owner.GetFromJsonAsync<JsonObject>(Url(s.ProjectId, $"/{id}")))!["current"]!;
        await Ok(Post(s.Owner, Url(s.ProjectId, $"/{id}/approve"), new JsonObject { ["revision"] = 1, ["contentHash"] = read["contentHash"]?.GetValue<string>() }));
        return id;
    }

    private static Task<JsonObject> RunAsync(ImportScenario s, Guid id, Guid scanId) =>
        Ok(s.Owner.PostAsJsonAsync(Url(s.ProjectId, $"/{id}/obligations/O1/run"), new { scanId }));

    [Fact]
    public async Task Only_a_tracking_scan_that_covers_the_place_establishes_that_a_pattern_is_absent()
    {
        var s = await WithGitHubAsync();
        // The accepted facts follow main at Commit1.
        var accepted = await s.ScanAndAcceptAllAsync();
        var elements = await s.ElementsAsync();
        var whole = await OutboxPromiseAsync(s);
        var ofOrdersApi = await OutboxPromiseAsync(s, elements.Single("name", "Orders.Api").Id());

        // The run shows the ref, the commit and the kind of scan it judged.
        var passed = await RunAsync(s, whole, accepted.Id());
        Assert.Equal("passed", passed["result"]!.GetValue<string>());
        Assert.Contains($"at ref 'main', commit {ImportScenario.Commit1} (scan {accepted.Id()}, a tracking scan)", passed["scope"]!.GetValue<string>());
        Assert.Equal(("main", "tracking", ImportScenario.Commit1, $"{s.GitHubHost}/acme/orders"), (
            passed["inputs"]!["ref"]!.GetValue<string>(), passed["inputs"]!["scanKind"]!.GetValue<string>(),
            passed["inputs"]!["commitSha"]!.GetValue<string>(), passed["inputs"]!["repository"]!.GetValue<string>()));

        // A branch that removes the outbox, scanned as another ref than the tracked one: a comparison scan.
        s.GitHub.Commit(ImportScenario.Commit2, FixtureGitHub.Load("orders-outbox-removed"));
        s.GitHub.Head = ImportScenario.Commit1;
        s.GitHub.Branches["remove-outbox"] = ImportScenario.Commit2;
        var comparison = await s.ScanAsync(body: new { repository = "acme/orders", @ref = "remove-outbox" });
        Assert.Equal(("completed", ImportScenario.Commit2), (comparison.Text("status"), comparison.Text("commitSha")));
        // It does not find the pattern, and it is complete. It still cannot say that the model's line of history lacks it.
        var notJudged = await RunAsync(s, whole, comparison.Id());
        Assert.Equal("unsupported", notJudged["result"]!.GetValue<string>());
        Assert.Contains("it is a comparison scan (it read another ref than the tracked ref 'main')", notJudged["detail"]!.GetValue<string>());
        Assert.Equal(("remove-outbox", "comparison"), (notJudged["inputs"]!["ref"]!.GetValue<string>(), notJudged["inputs"]!["scanKind"]!.GetValue<string>()));

        // A tracking scan of main that reads only the billing directory does not cover the project of Orders.Api.
        s.GitHub.Head = ImportScenario.Commit2;
        var scoped = await s.ScanAsync(body: new { repository = "acme/orders", includePaths = new[] { "billing" } });
        var uncovered = await RunAsync(s, ofOrdersApi, scoped.Id());
        Assert.Equal("unsupported", uncovered["result"]!.GetValue<string>());
        Assert.Contains("this scan cannot establish absence: it did not look at", uncovered["detail"]!.GetValue<string>());
        Assert.Equal("scope", uncovered["inputs"]!["coverage"]!.GetValue<string>());

        // The same scan says nothing about the whole repository either.
        Assert.Equal("unsupported", (await RunAsync(s, whole, scoped.Id()))["result"]!.GetValue<string>());

        // The whole repository on the tracked ref covers the project of the service and everything else: failed.
        var full = await s.ScanAsync();
        var absentInService = await RunAsync(s, ofOrdersApi, full.Id());
        Assert.Equal(("failed", "covered"), (absentInService["result"]!.GetValue<string>(), absentInService["inputs"]!["coverage"]!.GetValue<string>()));
        Assert.Contains("A tracking scan that covers the project directory of the service at this commit found no such fact.", absentInService["detail"]!.GetValue<string>());
        var absent = await RunAsync(s, whole, full.Id());
        Assert.Equal(("failed", "covered", "main"), (absent["result"]!.GetValue<string>(), absent["inputs"]!["coverage"]!.GetValue<string>(), absent["inputs"]!["ref"]!.GetValue<string>()));
        Assert.Contains("A tracking scan that covers the whole repository at this commit found no such fact.", absent["detail"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_pattern_that_is_declared_only_under_a_condition_fails_unless_the_obligation_accepts_it()
    {
        var s = await WithGitHubAsync();
        var files = FixtureGitHub.Load("orders");
        var program = Encoding.UTF8.GetString(files["orders-api/Program.cs"]);
        const string registration = "    bus.AddEntityFrameworkOutbox<OrdersDbContext>(outbox =>\n    {\n        outbox.UsePostgres();\n        outbox.UseBusOutbox();\n    });\n";
        Assert.Contains(registration, program.ReplaceLineEndings("\n"));
        files["orders-api/Program.cs"] = Encoding.UTF8.GetBytes(
            program.ReplaceLineEndings("\n").Replace(registration, "#if OUTBOX\n" + registration + "#endif\n"));
        s.GitHub.Commit(ImportScenario.Commit1, files);
        var scan = await s.ScanAsync();
        Assert.Equal("completed", scan.Text("status"));

        // The default requires an unconditional declaration: a build without OUTBOX has no outbox.
        var strict = await OutboxPromiseAsync(s);
        var failed = await RunAsync(s, strict, scan.Id());
        Assert.Equal("failed", failed["result"]!.GetValue<string>());
        Assert.Contains("declares the pattern only under #if OUTBOX, and the obligation requires an unconditional declaration", failed["detail"]!.GetValue<string>());
        Assert.NotEmpty(failed["evidence"]!.AsArray());

        // An obligation that accepts a conditional declaration passes, and says under which condition.
        var lenient = await OutboxPromiseAsync(s, requireUnconditional: false);
        var passed = await RunAsync(s, lenient, scan.Id());
        Assert.Equal("passed", passed["result"]!.GetValue<string>());
        Assert.Contains(passed["limitations"]!.AsArray(), l => l!.GetValue<string>().Contains("declared under #if OUTBOX"));
    }
}
