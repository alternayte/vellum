using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Deedbox;
using Microsoft.Extensions.DependencyInjection;
using Vellum.Modules.Drafts;
using Vellum.Modules.Trace.Scenarios;
using Vellum.Shared;

namespace Vellum.Tests.Modules.Trace;

[Collection("Integration")]
public class ScenarioEndpointTests(IntegrationFixture fixture)
{
    private static Guid G(int n) => new($"00000000-0000-0000-0000-{n:D12}");

    // Orders reference case. Fixed IDs: fan-out follows subscription ID order, so Inventory
    // is delivered before Billing.
    private static readonly Guid Customer = G(1), Shop = G(2), Orders = G(3), OrdersDb = G(4), Inventory = G(5), Billing = G(6);
    private static readonly Guid PlaceOrder = G(10), OrderEvents = G(20), DeadLetters = G(21);
    private static readonly Guid InventorySub = G(30), BillingSub = G(31), OrderCreated = G(40);

    private static string Url(Guid projectId, string path, Guid? branchId = null) =>
        $"/api/projects/{projectId}/{path}" + (branchId is null ? "" : $"?branchId={branchId}");

    /// <summary>
    /// A customer places an order; Orders writes it and publishes OrderCreated; Inventory and
    /// Billing consume it. Inventory retries once and has a dead-letter channel.
    /// </summary>
    private static async Task SeedOrdersAsync(HttpClient client, Guid projectId)
    {
        async Task Post(string path, object body) => await TestHost.Created(client.PostAsJsonAsync(Url(projectId, path), body));

        // Schema streams are keyed by schema ID across projects, so each project gets its own.
        var orderRequest = Guid.NewGuid();
        var orderCreated = Guid.NewGuid();
        await Post("schemas", new { id = orderRequest, name = "OrderRequest", content = """{"type":"object","required":["sku","qty"],"properties":{"qty":{"type":"integer","minimum":1}}}""" });
        await Post("schemas", new { id = orderCreated, name = "OrderCreated", content = """{"type":"object","required":["orderId"]}""" });

        await Post("elements", new { id = Customer, kind = "actor", name = "Customer" });
        await Post("elements", new { id = Shop, kind = "system", name = "Shop" });
        await Post("elements", new { id = Orders, kind = "app", name = "Orders", parentId = Shop });
        await Post("elements", new { id = OrdersDb, kind = "store", name = "Orders DB", parentId = Shop });
        await Post("elements", new { id = Inventory, kind = "app", name = "Inventory", parentId = Shop });
        await Post("elements", new { id = Billing, kind = "app", name = "Billing", parentId = Shop });
        await Post("messages", new { id = OrderCreated, name = "OrderCreated", producerId = Orders, consumerIds = new[] { Inventory, Billing }, schemaId = orderCreated });
        await Post("operations", new { id = PlaceOrder, ownerId = Orders, name = "POST /orders", transport = "http", requestSchemaId = orderRequest, timeoutMs = 2000 });
        await Post("channels", new { id = OrderEvents, name = "order-events", kind = "topic" });
        await Post("channels", new { id = DeadLetters, name = "order-events.dlq", kind = "queue" });
        await Post("subscriptions", new { id = InventorySub, channelId = OrderEvents, consumerId = Inventory, retry = new { maxAttempts = 2, backoffMs = 100 }, deadLetterChannelId = DeadLetters });
        await Post("subscriptions", new { id = BillingSub, channelId = OrderEvents, consumerId = Billing });
    }

    /// <summary>Place order, write, publish OrderCreated; Inventory fails every delivery.</summary>
    private static JsonObject PlaceOrderScenario(Guid id, Guid? operation = null) => JsonNode.Parse($$"""
        {
          "id": "{{id}}",
          "name": "Place an order",
          "steps": [
            {
              "type": "call", "id": "place", "from": "{{Customer}}", "operation": "{{operation ?? PlaceOrder}}",
              "request": { "sku": "apple", "qty": 2 }, "response": { "orderId": "o-1" },
              "then": [
                { "type": "write", "id": "save", "by": "{{Orders}}", "store": "{{OrdersDb}}", "dataset": "orders", "change": { "status": "accepted", "orderId": "o-1" } },
                { "type": "publish", "id": "publish", "from": "{{Orders}}", "channel": "{{OrderEvents}}", "message": "{{OrderCreated}}",
                  "payload": { "orderId": "o-1" }, "headers": { "x-correlation-id": "c-1" }, "handlers": {} }
              ],
              "onError": []
            }
          ],
          "faults": [ { "target": "{{Inventory}}", "kind": "Error", "failFirst": null } ],
          "assertions": [
            { "type": "outcome", "id": "order-accepted", "step": "place", "expected": "Succeeded" },
            { "type": "event-count", "id": "one-dead-letter", "kind": "DeadLettered", "target": "{{DeadLetters}}", "expected": 1 }
          ]
        }
        """)!.AsObject();

    private static StringContent Json(JsonNode body) => new(body.ToJsonString(), Encoding.UTF8, "application/json");

    private static async Task<JsonObject> RunAsync(HttpClient client, Guid projectId, Guid scenarioId, Guid? branchId = null)
    {
        var response = await client.PostAsync(Url(projectId, $"scenarios/{scenarioId}/run", branchId), null);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<JsonObject>())!;
    }

    [Fact]
    public async Task A_scenario_is_created_read_listed_replaced_and_deleted()
    {
        using var factory = TestHost.Create(fixture);
        using var client = await TestHost.SignInAsync(factory);
        var (_, projectId) = await TestHost.CreateProjectAsync(client);
        var id = Guid.NewGuid();
        var scenario = PlaceOrderScenario(id);

        // No model is needed: every reference in this document dangles, and it is still saved.
        var created = await TestHost.Created(client.PostAsync(Url(projectId, "scenarios"), Json(scenario)));
        Assert.Equal($"/api/projects/{projectId}/scenarios/{id}", created.Headers.Location!.ToString());

        var read = (await client.GetFromJsonAsync<JsonObject>(Url(projectId, $"scenarios/{id}")))!;
        Assert.Equal(id, (Guid)read["id"]!);
        Assert.Equal(["call"], read["steps"]!.AsArray().Select(s => (string)s!["type"]!));
        Assert.Equal(["write", "publish"], read["steps"]![0]!["then"]!.AsArray().Select(s => (string)s!["type"]!));
        Assert.Equal("c-1", (string)read["steps"]![0]!["then"]![1]!["headers"]!["x-correlation-id"]!);
        Assert.Equal(2, (int)read["steps"]![0]!["request"]!["qty"]!);
        // Enum members read back by name.
        Assert.Equal("Error", (string)read["faults"]![0]!["kind"]!);
        Assert.Equal("Succeeded", (string)read["assertions"]![0]!["expected"]!);
        Assert.Equal("DeadLettered", (string)read["assertions"]![1]!["kind"]!);

        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync(Url(projectId, "scenarios"), Json(scenario))).StatusCode);

        var second = Guid.NewGuid();
        await TestHost.Created(client.PostAsync(Url(projectId, "scenarios"), Json(new JsonObject { ["id"] = second, ["name"] = "A draft", ["steps"] = new JsonArray(), ["faults"] = new JsonArray(), ["assertions"] = new JsonArray() })));
        var listed = (await client.GetFromJsonAsync<Page<ScenarioSummaryDto>>(Url(projectId, "scenarios")))!;
        Assert.Equal([new ScenarioSummaryDto(second, "A draft"), new ScenarioSummaryDto(id, "Place an order")], listed.Items);
        Assert.Null(listed.Cursor);

        // Replace: the body may leave the ID out, and may not name another scenario.
        var replacement = PlaceOrderScenario(id);
        replacement.Remove("id");
        replacement["name"] = "Place an order (v2)";
        replacement["faults"] = new JsonArray();
        var replaced = await client.PutAsync(Url(projectId, $"scenarios/{id}"), Json(replacement));
        Assert.Equal(HttpStatusCode.OK, replaced.StatusCode);
        var reread = (await client.GetFromJsonAsync<JsonObject>(Url(projectId, $"scenarios/{id}")))!;
        Assert.Equal(("Place an order (v2)", 0, id), ((string)reread["name"]!, reread["faults"]!.AsArray().Count, (Guid)reread["id"]!));
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsync(Url(projectId, $"scenarios/{id}"), Json(PlaceOrderScenario(second)))).StatusCode);
        Assert.Contains((await client.GetFromJsonAsync<Page<ScenarioSummaryDto>>(Url(projectId, "scenarios")))!.Items, s => s.Name == "Place an order (v2)");

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync(Url(projectId, $"scenarios/{id}"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(Url(projectId, $"scenarios/{id}"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync(Url(projectId, $"scenarios/{id}"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PutAsync(Url(projectId, $"scenarios/{id}"), Json(replacement))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync(Url(projectId, $"scenarios/{id}/run"), null)).StatusCode);
        // A deleted scenario keeps its ID.
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync(Url(projectId, "scenarios"), Json(scenario))).StatusCode);
        Assert.Equal([second], (await client.GetFromJsonAsync<Page<ScenarioSummaryDto>>(Url(projectId, "scenarios")))!.Items.Select(s => s.Id));
    }

    [Theory]
    [InlineData("""{ "id": "00000000-0000-0000-0000-000000000099", "name": "x", "steps": [ """)]
    [InlineData("null")]
    [InlineData("""[1, 2, 3]""")]
    [InlineData("""{ "id": "not-a-guid", "name": "x", "steps": [], "faults": [], "assertions": [] }""")]
    [InlineData("""{ "id": "00000000-0000-0000-0000-000000000099", "name": "x", "steps": "none", "faults": [], "assertions": [] }""")]
    [InlineData("""{ "id": "00000000-0000-0000-0000-000000000099", "name": "x", "steps": [ { "type": "teleport", "id": "a" } ], "faults": [], "assertions": [] }""")]
    [InlineData("""{ "id": "00000000-0000-0000-0000-000000000099", "name": "x", "steps": [ { "id": "no-type" } ], "faults": [], "assertions": [] }""")]
    [InlineData("""{ "id": "00000000-0000-0000-0000-000000000099", "name": "x", "steps": [], "faults": [ { "target": "00000000-0000-0000-0000-000000000001", "kind": "Meltdown" } ], "assertions": [] }""")]
    [InlineData("""{ "id": "00000000-0000-0000-0000-000000000099", "name": "", "steps": [], "faults": [], "assertions": [] }""")]
    [InlineData("""{ "name": "no id", "steps": [], "faults": [], "assertions": [] }""")]
    public async Task A_body_that_is_not_a_scenario_document_is_refused_with_400(string body)
    {
        using var factory = TestHost.Create(fixture);
        using var client = await TestHost.SignInAsync(factory);
        var (_, projectId) = await TestHost.CreateProjectAsync(client);

        var response = await client.PostAsync(Url(projectId, "scenarios"), new StringContent(body, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains((await response.Content.ReadFromJsonAsync<ErrorResponse>())!.Type, new[] { "invalid_scenario", "validation_error" });
        Assert.Empty((await client.GetFromJsonAsync<Page<ScenarioSummaryDto>>(Url(projectId, "scenarios")))!.Items);
    }

    [Fact]
    public async Task An_unfinished_scenario_is_saved_as_given_and_a_run_reports_its_issues()
    {
        using var factory = TestHost.Create(fixture);
        using var client = await TestHost.SignInAsync(factory);
        var (_, projectId) = await TestHost.CreateProjectAsync(client);
        await SeedOrdersAsync(client, projectId);

        // No fault list, and a call without its step lists: readable, not yet runnable.
        var id = Guid.NewGuid();
        var draft = JsonNode.Parse($$"""
            { "id": "{{id}}", "name": "Half written", "assertions": [],
              "steps": [ { "type": "call", "id": "place", "from": "{{Customer}}", "operation": "{{PlaceOrder}}" } ] }
            """)!;
        await TestHost.Created(client.PostAsync(Url(projectId, "scenarios"), Json(draft)));

        var read = (await client.GetFromJsonAsync<JsonObject>(Url(projectId, $"scenarios/{id}")))!;
        Assert.Null(read["faults"]);
        Assert.Null(read["steps"]![0]!["then"]);

        var result = await RunAsync(client, projectId, id);
        Assert.Contains(result["issues"]!.AsArray(), i => (string)i!["code"]! == "malformed-scenario");
        Assert.Empty(result["events"]!.AsArray());
    }

    [Fact]
    public async Task A_scenario_belongs_to_one_project()
    {
        using var factory = TestHost.Create(fixture);
        using var client = await TestHost.SignInAsync(factory);
        var (_, projectId) = await TestHost.CreateProjectAsync(client);
        var (_, otherProjectId) = await TestHost.CreateProjectAsync(client);
        var id = Guid.NewGuid();
        await TestHost.Created(client.PostAsync(Url(projectId, "scenarios"), Json(PlaceOrderScenario(id))));

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(Url(otherProjectId, $"scenarios/{id}"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PutAsync(Url(otherProjectId, $"scenarios/{id}"), Json(PlaceOrderScenario(id)))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync(Url(otherProjectId, $"scenarios/{id}"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync(Url(otherProjectId, $"scenarios/{id}/run"), null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync(Url(otherProjectId, "scenarios"), Json(PlaceOrderScenario(id)))).StatusCode);
        Assert.Empty((await client.GetFromJsonAsync<Page<ScenarioSummaryDto>>(Url(otherProjectId, "scenarios")))!.Items);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(Url(projectId, $"scenarios/{id}"))).StatusCode);
    }

    [Fact]
    public async Task An_id_that_names_a_schema_is_not_a_scenario()
    {
        using var factory = TestHost.Create(fixture);
        using var client = await TestHost.SignInAsync(factory);
        var (_, projectId) = await TestHost.CreateProjectAsync(client);

        // Schemas and scenarios are both streams keyed by a caller-chosen ID.
        var schemaId = Guid.NewGuid();
        await TestHost.Created(client.PostAsJsonAsync(Url(projectId, "schemas"), new { id = schemaId, name = "Order", content = """{"type":"object"}""" }));

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(Url(projectId, $"scenarios/{schemaId}"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync(Url(projectId, $"scenarios/{schemaId}/run"), null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PutAsync(Url(projectId, $"scenarios/{schemaId}"), Json(PlaceOrderScenario(schemaId)))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync(Url(projectId, $"scenarios/{schemaId}"))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync(Url(projectId, "scenarios"), Json(PlaceOrderScenario(schemaId)))).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(Url(projectId, $"schemas/{schemaId}"))).StatusCode);
    }

    [Fact]
    public async Task A_viewer_reads_and_runs_scenarios_and_cannot_change_them()
    {
        using var factory = TestHost.Create(fixture);
        using var owner = await TestHost.SignInAsync(factory);
        var (workspaceId, projectId) = await TestHost.CreateProjectAsync(owner);
        var id = Guid.NewGuid();
        await TestHost.Created(owner.PostAsync(Url(projectId, "scenarios"), Json(PlaceOrderScenario(id))));
        using var viewer = await TestHost.SignInMemberAsync(factory, owner, workspaceId, "Viewer");
        using var outsider = await TestHost.SignInAsync(factory);
        using var anonymous = factory.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync(Url(projectId, "scenarios"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync(Url(projectId, $"scenarios/{id}"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await viewer.PostAsync(Url(projectId, $"scenarios/{id}/run"), null)).StatusCode);

        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsync(Url(projectId, "scenarios"), Json(PlaceOrderScenario(Guid.NewGuid())))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PutAsync(Url(projectId, $"scenarios/{id}"), Json(PlaceOrderScenario(id)))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.DeleteAsync(Url(projectId, $"scenarios/{id}"))).StatusCode);

        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.GetAsync(Url(projectId, $"scenarios/{id}"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.PostAsync(Url(projectId, $"scenarios/{id}/run"), null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(Url(projectId, "scenarios"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync(Url(projectId, $"scenarios/{id}/run"), null)).StatusCode);
    }

    [Fact]
    public async Task The_orders_scenario_plays_request_write_publish_fan_out_retry_and_dead_letter()
    {
        using var factory = TestHost.Create(fixture);
        using var client = await TestHost.SignInAsync(factory);
        var (_, projectId) = await TestHost.CreateProjectAsync(client);
        await SeedOrdersAsync(client, projectId);
        var id = Guid.NewGuid();
        await TestHost.Created(client.PostAsync(Url(projectId, "scenarios"), Json(PlaceOrderScenario(id))));

        var result = await RunAsync(client, projectId, id);

        Assert.Equal(
            ["engineVersion", "hash", "modelVersion", "issues", "events", "outcomes", "assertions", "labels"],
            result.Select(p => p.Key));
        Assert.Equal("1", (string)result["engineVersion"]!);
        Assert.Empty(result["issues"]!.AsArray());

        var events = result["events"]!.AsArray();
        Assert.Equal(
            [
                "RequestSent:place:1", "DataWritten:save:1", "Published:publish:1",
                "Delivered:publish:1", "DeliveryFailed:publish:1", "RetryScheduled:publish:2", "Delivered:publish:1",
                "ResponseReceived:place:1",
                "Delivered:publish:2", "DeliveryFailed:publish:2", "DeadLettered:publish:2",
            ],
            events.Select(e => $"{(string)e!["kind"]!}:{(string)e["stepId"]!}:{(int)e["attempt"]!}"));
        Assert.Equal(Enumerable.Range(1, 11), events.Select(e => (int)e!["sequence"]!));
        Assert.Equal([0, 0, 0, 0, 0, 0, 0, 0, 100, 100, 100], events.Select(e => (long)e!["atMs"]!));

        // Every event has the same members, with null for what does not apply.
        Assert.All(events, e => Assert.Equal(
            ["sequence", "atMs", "kind", "stepId", "attempt", "from", "to", "target", "payload", "detail", "message", "headers"],
            e!.AsObject().Select(p => p.Key)));

        var request = events[0]!;
        Assert.Equal((Customer, Orders, PlaceOrder), ((Guid)request["from"]!, (Guid)request["to"]!, (Guid)request["target"]!));
        Assert.Equal("apple", (string)request["payload"]!["sku"]!);
        Assert.Null(request["message"]);
        Assert.Null(request["headers"]);

        var published = events[2]!;
        Assert.Equal((Orders, OrderEvents, OrderCreated), ((Guid)published["from"]!, (Guid)published["target"]!, (Guid)published["message"]!));
        Assert.Null(published["to"]);
        Assert.Equal("c-1", (string)published["headers"]!["x-correlation-id"]!);

        // Inventory fails into the dead-letter channel; Billing is delivered once.
        Assert.Equal([Inventory, Billing, Inventory], events.Where(e => (string)e!["kind"]! == "Delivered").Select(e => (Guid)e!["to"]!));
        Assert.Equal(InventorySub, (Guid)events[4]!["target"]!);
        Assert.Equal(DeadLetters, (Guid)events[10]!["target"]!);

        var outcomes = result["outcomes"]!.AsObject();
        Assert.Equal(["place:Succeeded", "publish:Succeeded", "save:Succeeded"], outcomes.Select(o => $"{o.Key}:{(string)o.Value!}").Order());
        Assert.Equal(["order-accepted:True", "one-dead-letter:True"],
            result["assertions"]!.AsArray().Select(a => $"{(string)a!["id"]!}:{(bool)a["passed"]!}"));
        Assert.All(result["assertions"]!.AsArray(), a => Assert.False(string.IsNullOrWhiteSpace((string)a!["detail"]!)));

        var labels = result["labels"]!.AsObject();
        Assert.Equal(
            new Dictionary<Guid, string>
            {
                [Customer] = "Customer", [Shop] = "Shop", [Orders] = "Orders", [OrdersDb] = "Orders DB",
                [Inventory] = "Inventory", [Billing] = "Billing",
                [PlaceOrder] = "POST /orders", [OrderEvents] = "order-events", [DeadLetters] = "order-events.dlq",
                [InventorySub] = "Inventory on order-events", [BillingSub] = "Billing on order-events",
                [OrderCreated] = "OrderCreated",
            },
            labels.ToDictionary(l => Guid.Parse(l.Key), l => (string)l.Value!));

        // The same scenario over the same model version replays to the same hash, and a run writes nothing.
        var again = await RunAsync(client, projectId, id);
        Assert.Matches("^[0-9a-f]{64}$", (string)result["hash"]!);
        Assert.Equal(result.ToJsonString(), again.ToJsonString());
        Assert.True((long)result["modelVersion"]! > 0);
    }

    [Fact]
    public async Task A_run_is_pinned_to_the_branch_it_names()
    {
        using var factory = TestHost.Create(fixture);
        using var client = await TestHost.SignInAsync(factory);
        var (_, projectId) = await TestHost.CreateProjectAsync(client);
        await SeedOrdersAsync(client, projectId);
        var id = Guid.NewGuid();
        await TestHost.Created(client.PostAsync(Url(projectId, "scenarios"), Json(PlaceOrderScenario(id))));
        var main = await RunAsync(client, projectId, id);

        // On the draft, Billing no longer listens.
        var draft = await (await TestHost.Created(client.PostAsJsonAsync(Url(projectId, "drafts"), new { id = Guid.NewGuid(), name = "No billing" })))
            .Content.ReadFromJsonAsync<DraftDto>();
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync(Url(projectId, $"subscriptions/{BillingSub}", draft!.StreamId))).StatusCode);

        var onDraft = await RunAsync(client, projectId, id, draft.StreamId);

        Assert.Equal(3, main["events"]!.AsArray().Count(e => (string)e!["kind"]! == "Delivered"));
        Assert.Equal(2, onDraft["events"]!.AsArray().Count(e => (string)e!["kind"]! == "Delivered"));
        Assert.NotEqual((string)main["hash"]!, (string)onDraft["hash"]!);
        Assert.False(onDraft["labels"]!.AsObject().ContainsKey(BillingSub.ToString()));
        Assert.Equal(main.ToJsonString(), (await RunAsync(client, projectId, id)).ToJsonString());
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync(Url(projectId, $"scenarios/{id}/run", Guid.NewGuid()), null)).StatusCode);
    }

    [Fact]
    public async Task A_scenario_with_a_dangling_operation_or_a_payload_its_schema_refuses_returns_issues_and_no_events()
    {
        using var factory = TestHost.Create(fixture);
        using var client = await TestHost.SignInAsync(factory);
        var (_, projectId) = await TestHost.CreateProjectAsync(client);
        await SeedOrdersAsync(client, projectId);

        var danglingId = Guid.NewGuid();
        var missingOperation = Guid.NewGuid();
        await TestHost.Created(client.PostAsync(Url(projectId, "scenarios"), Json(PlaceOrderScenario(danglingId, missingOperation))));
        var dangling = await RunAsync(client, projectId, danglingId);

        var issue = Assert.Single(dangling["issues"]!.AsArray());
        Assert.Equal(["stepId", "code", "message"], issue!.AsObject().Select(p => p.Key));
        Assert.Equal(("place", "unknown-operation"), ((string)issue["stepId"]!, (string)issue["code"]!));
        Assert.Contains(missingOperation.ToString(), (string)issue["message"]!);
        Assert.Empty(dangling["events"]!.AsArray());
        Assert.Empty(dangling["outcomes"]!.AsObject());
        Assert.Empty(dangling["assertions"]!.AsArray());
        Assert.Equal("Orders", (string)dangling["labels"]![Orders.ToString()]!);

        // The stored request schema is applied: qty must be at least 1.
        var mismatchId = Guid.NewGuid();
        var mismatch = PlaceOrderScenario(mismatchId);
        mismatch["steps"]![0]!["request"]!["qty"] = 0;
        await TestHost.Created(client.PostAsync(Url(projectId, "scenarios"), Json(mismatch)));
        var refused = await RunAsync(client, projectId, mismatchId);
        Assert.Equal("schema-mismatch", (string)Assert.Single(refused["issues"]!.AsArray())!["code"]!);
        Assert.Empty(refused["events"]!.AsArray());
    }

    [Fact]
    public async Task A_rebuild_reproduces_the_scenario_list()
    {
        using var factory = TestHost.Create(fixture);
        using var client = await TestHost.SignInAsync(factory);
        var (_, projectId) = await TestHost.CreateProjectAsync(client);

        var kept = Guid.NewGuid();
        var renamed = Guid.NewGuid();
        var deleted = Guid.NewGuid();
        foreach (var id in new[] { kept, renamed, deleted })
            await TestHost.Created(client.PostAsync(Url(projectId, "scenarios"), Json(PlaceOrderScenario(id))));
        var replacement = PlaceOrderScenario(renamed);
        replacement["name"] = "Renamed";
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsync(Url(projectId, $"scenarios/{renamed}"), Json(replacement))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync(Url(projectId, $"scenarios/{deleted}"))).StatusCode);

        var before = await client.GetStringAsync(Url(projectId, "scenarios"));
        Assert.Contains("Renamed", before);
        Assert.DoesNotContain(deleted.ToString(), before);

        var admin = factory.Services.GetRequiredService<IEventStoreAdmin>();
        var jobId = await admin.RebuildAsync("scenario_read_model");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        while (true)
        {
            var job = await admin.GetJobAsync(jobId, timeout.Token);
            Assert.NotEqual(JobState.Failed, job!.Status);
            if (job.Status == JobState.Done)
            {
                var consumer = (await admin.GetStatusAsync(timeout.Token)).Consumers.Single(c => c.Name == "scenario_read_model");
                Assert.NotEqual(ConsumerState.Stalled, consumer.Status);
                if (consumer.Status == ConsumerState.Running)
                    break;
            }
            await Task.Delay(100, timeout.Token);
        }

        Assert.Equal(before, await client.GetStringAsync(Url(projectId, "scenarios")));
    }
}
