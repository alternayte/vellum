using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;

namespace Vellum.Tests.Modules.Promises;

/// <summary>
/// The Orders reference case, built through the API: a customer places an order,
/// the Orders API stores it and <c>OrderCreated</c> goes to Inventory and Billing. The
/// scenario has two variants. Without an outbox the API publishes after it answered, so an
/// outage of the broker loses the event of an accepted order. With an outbox the API stores
/// the event with the order and a relay publishes it with retries.
/// </summary>
internal sealed class OrdersReference
{
    private static Guid G(int n) => new($"00000000-0000-0000-0000-{n:D12}");

    public static readonly Guid Customer = G(1), Shop = G(2), OrdersApi = G(3), OrdersDb = G(4), Inventory = G(5), Billing = G(6), Relay = G(7);
    public static readonly Guid PlaceOrder = G(10), RelayOutbox = G(11), OrderEvents = G(20), DeadLetters = G(21);
    public static readonly Guid InventorySub = G(30), BillingSub = G(31), OrderCreated = G(40);

    public const string Repository = "acme/orders";
    public const string PublishStep = "publish-order-created";

    public HttpClient Client { get; }
    public Guid ProjectId { get; }
    /// <summary>Schema and scenario streams are keyed by ID across projects, so each project gets its own.</summary>
    public Guid OrderRequestSchema { get; } = Guid.NewGuid();
    public Guid OrderCreatedSchema { get; } = Guid.NewGuid();
    public Guid ScenarioId { get; } = Guid.NewGuid();

    private OrdersReference(HttpClient client, Guid projectId) => (Client, ProjectId) = (client, projectId);

    public string Url(string path) => $"/api/projects/{ProjectId}/{path}";

    /// <summary>The model and the scenario without an outbox.</summary>
    public static async Task<OrdersReference> SeedAsync(HttpClient client, Guid projectId)
    {
        var orders = new OrdersReference(client, projectId);
        async Task Post(string path, object body) => await TestHost.Created(client.PostAsJsonAsync(orders.Url(path), body));

        await Post("schemas", new { id = orders.OrderRequestSchema, name = "OrderRequest", content = """{"type":"object","required":["sku","qty"],"properties":{"sku":{"type":"string"},"qty":{"type":"integer","minimum":1}}}""" });
        await Post("schemas", new { id = orders.OrderCreatedSchema, name = "OrderCreated", content = """{"type":"object","required":["orderId"],"properties":{"orderId":{"type":"string"}}}""" });

        await Post("elements", new { id = Customer, kind = "actor", name = "Customer" });
        await Post("elements", new { id = Shop, kind = "system", name = "Shop" });
        await Post("elements", new { id = OrdersApi, kind = "app", name = "Orders API", description = "Accepts orders and stores them.", technology = "ASP.NET Core", parentId = Shop });
        await Post("elements", new { id = OrdersDb, kind = "store", name = "Orders DB", technology = "PostgreSQL", parentId = Shop });
        await Post("elements", new { id = Inventory, kind = "app", name = "Inventory", parentId = Shop });
        await Post("elements", new { id = Billing, kind = "app", name = "Billing", parentId = Shop });
        await Post("elements", new { id = Relay, kind = "app", name = "Outbox Relay", description = "Publishes stored events to the broker.", parentId = Shop });
        await Post("relationships", new { id = G(50), fromId = OrdersApi, toId = OrdersDb, label = "reads and writes", technology = "SQL" });
        await Post("messages", new { id = OrderCreated, name = "OrderCreated", producerId = OrdersApi, consumerIds = new[] { Inventory, Billing }, schemaId = orders.OrderCreatedSchema });
        await Post("operations", new { id = PlaceOrder, ownerId = OrdersApi, name = "POST /orders", transport = "http", requestSchemaId = orders.OrderRequestSchema, timeoutMs = 2000 });
        await Post("operations", new { id = RelayOutbox, ownerId = Relay, name = "Relay outbox", transport = "command", retry = new { maxAttempts = 3, backoffMs = 1000 } });
        await Post("channels", new { id = OrderEvents, name = "order-events", kind = "topic" });
        await Post("channels", new { id = DeadLetters, name = "order-events.dlq", kind = "queue" });
        await Post("subscriptions", new { id = InventorySub, channelId = OrderEvents, consumerId = Inventory, retry = new { maxAttempts = 2, backoffMs = 100 }, deadLetterChannelId = DeadLetters });
        await Post("subscriptions", new { id = BillingSub, channelId = OrderEvents, consumerId = Billing });

        await TestHost.Created(client.PostAsync(orders.Url("scenarios"), Json(orders.WithoutOutbox())));
        return orders;
    }

    /// <summary>The API answers, then publishes. Nothing retries the publish.</summary>
    public JsonObject WithoutOutbox() => JsonNode.Parse($$"""
        {
          "id": "{{ScenarioId}}",
          "name": "Place an order",
          "steps": [
            {
              "type": "call", "id": "place", "from": "{{Customer}}", "operation": "{{PlaceOrder}}",
              "request": { "sku": "apple", "qty": 2 }, "response": { "orderId": "o-1" },
              "then": [
                { "type": "write", "id": "save-order", "by": "{{OrdersApi}}", "store": "{{OrdersDb}}", "dataset": "orders", "change": { "orderId": "o-1", "status": "accepted" } }
              ],
              "onError": []
            },
            { "type": "publish", "id": "{{PublishStep}}", "from": "{{OrdersApi}}", "channel": "{{OrderEvents}}", "message": "{{OrderCreated}}",
              "payload": { "orderId": "o-1" }, "headers": { "x-correlation-id": "c-1" }, "handlers": {} }
          ],
          "faults": [],
          "assertions": [
            { "type": "outcome", "id": "order-accepted", "step": "place", "expected": "Succeeded" },
            { "type": "outcome", "id": "accepted-order-reaches-the-broker", "step": "{{PublishStep}}", "expected": "Succeeded" }
          ]
        }
        """)!.AsObject();

    /// <summary>The API stores the event with the order. The relay publishes it and tries again after a failure.</summary>
    public JsonObject WithOutbox() => JsonNode.Parse($$"""
        {
          "id": "{{ScenarioId}}",
          "name": "Place an order",
          "steps": [
            {
              "type": "call", "id": "place", "from": "{{Customer}}", "operation": "{{PlaceOrder}}",
              "request": { "sku": "apple", "qty": 2 }, "response": { "orderId": "o-1" },
              "then": [
                { "type": "write", "id": "save-order", "by": "{{OrdersApi}}", "store": "{{OrdersDb}}", "dataset": "orders", "change": { "orderId": "o-1", "status": "accepted" } },
                { "type": "write", "id": "save-outbox", "by": "{{OrdersApi}}", "store": "{{OrdersDb}}", "dataset": "outbox", "change": { "type": "OrderCreated", "orderId": "o-1" } }
              ],
              "onError": []
            },
            {
              "type": "call", "id": "relay", "from": "{{Relay}}", "operation": "{{RelayOutbox}}",
              "then": [
                { "type": "publish", "id": "{{PublishStep}}", "from": "{{Relay}}", "channel": "{{OrderEvents}}", "message": "{{OrderCreated}}",
                  "payload": { "orderId": "o-1" }, "headers": { "x-correlation-id": "c-1" }, "handlers": {} }
              ],
              "onError": []
            }
          ],
          "faults": [],
          "assertions": [
            { "type": "outcome", "id": "order-accepted", "step": "place", "expected": "Succeeded" },
            { "type": "outcome", "id": "accepted-order-reaches-the-broker", "step": "{{PublishStep}}", "expected": "Succeeded" }
          ]
        }
        """)!.AsObject();

    public async Task UseOutboxAsync()
    {
        var response = await Client.PutAsync(Url($"scenarios/{ScenarioId}"), Json(WithOutbox()));
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    /// <summary>The body of ORD-014, "An accepted order must survive a broker outage".</summary>
    public JsonObject Ord014(bool requiresPattern = true) => new()
    {
        ["title"] = "An accepted order must survive a broker outage",
        ["claim"] = "When the Orders API accepts an order, OrderCreated reaches the broker, also when the broker is down at the time of acceptance.",
        ["scope"] = "POST /orders and the OrderCreated event on order-events.",
        ["preconditions"] = "The order is valid. The Orders DB is available. The broker is down when the order is accepted and comes back later.",
        ["expectedOutcome"] = "The order is stored, the request reports success, and OrderCreated is published once the broker is back.",
        ["forbiddenOutcome"] = "The request reports success and OrderCreated is lost.",
        ["evidenceCriteria"] = "The declared flow holds with a broker outage, and the source of the Orders API declares a transactional outbox.",
        ["forbiddenEvents"] = new JsonArray(new JsonObject { ["kind"] = "Dropped" }),
        ["requiresPattern"] = requiresPattern ? new JsonObject { ["repository"] = Repository, ["pattern"] = "transactional-outbox" } : null,
        ["links"] = new JsonObject
        {
            ["scenarioIds"] = new JsonArray(ScenarioId.ToString()),
            ["operationIds"] = new JsonArray(PlaceOrder.ToString()),
            ["channelIds"] = new JsonArray(OrderEvents.ToString()),
            ["subscriptionIds"] = new JsonArray(InventorySub.ToString()),
            ["messageIds"] = new JsonArray(OrderCreated.ToString()),
            ["elementIds"] = new JsonArray(OrdersApi.ToString()),
        },
    };

    /// <summary>
    /// Sets the prefix <c>ORD</c> so that the first promise is ORD-014, creates it, generates
    /// its obligations and adds the broker outage to the scenario check. Returns the draft.
    /// </summary>
    public async Task<JsonObject> CreateOrd014Async(bool requiresPattern = true)
    {
        var prefix = await Client.PutAsJsonAsync(Url("promises/prefix"), new { prefix = "ORD", startAt = 14 });
        Assert.True(prefix.StatusCode == HttpStatusCode.OK, await prefix.Content.ReadAsStringAsync());

        var created = await (await TestHost.Created(Client.PostAsync(Url("promises"), Json(Ord014(requiresPattern))))).Content.ReadFromJsonAsync<JsonObject>();
        var id = created!["id"]!.GetValue<Guid>();
        var generated = await Client.PostAsync(Url($"promises/{id}/obligations/generate"), null);
        Assert.True(generated.StatusCode == HttpStatusCode.OK, await generated.Content.ReadAsStringAsync());

        // A person edits the generated check before approval: the broker is down for the first publish.
        var check = (await generated.Content.ReadFromJsonAsync<JsonObject>())!["promise"]!["current"]!["obligations"]![0]!.AsObject();
        var recipe = check["recipe"]!.DeepClone().AsObject();
        recipe["scenarioCheck"]!["faults"] = new JsonArray(new JsonObject { ["target"] = OrderEvents.ToString(), ["kind"] = "Outage", ["failFirst"] = 1 });
        var edited = await Client.PutAsync(Url($"promises/{id}/obligations/O1"),
            Json(new JsonObject { ["kind"] = "scenario-check", ["title"] = "The order flow holds while the broker is down", ["recipe"] = recipe }));
        Assert.True(edited.StatusCode == HttpStatusCode.OK, await edited.Content.ReadAsStringAsync());
        return (await edited.Content.ReadFromJsonAsync<JsonObject>())!;
    }

    /// <summary>Approves a revision as a person does: reads it, then names its number and the content hash that was read.</summary>
    public async Task<JsonObject> ApproveAsync(Guid promiseId, int revision = 1)
    {
        var response = await Client.PostAsync(Url($"promises/{promiseId}/approve"), Json(await ApprovalAsync(promiseId, revision)));
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<JsonObject>())!;
    }

    /// <summary>The body of an approval of <paramref name="revision"/> as it is now.</summary>
    public async Task<JsonObject> ApprovalAsync(Guid promiseId, int revision = 1)
    {
        var read = (await GetAsync(promiseId))["revisions"]!.AsArray().Single(r => r!["revision"]!.GetValue<int>() == revision)!;
        return new JsonObject { ["revision"] = revision, ["contentHash"] = read["contentHash"]?.GetValue<string>() };
    }

    /// <summary>Runs an obligation of the approved revision in force, or of the revision that <paramref name="body"/> names.</summary>
    public async Task<JsonObject> RunAsync(Guid promiseId, string key, object? body = null)
    {
        var response = await Client.PostAsJsonAsync(Url($"promises/{promiseId}/obligations/{key}/run"), body ?? new { });
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<JsonObject>())!;
    }

    public async Task<JsonObject> GetAsync(Guid promiseId) =>
        (await Client.GetFromJsonAsync<JsonObject>(Url($"promises/{promiseId}")))!;

    public static StringContent Json(JsonNode body) => new(body.ToJsonString(), Encoding.UTF8, "application/json");
}
