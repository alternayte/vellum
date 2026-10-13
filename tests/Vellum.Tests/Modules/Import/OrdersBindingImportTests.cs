using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;

namespace Vellum.Tests.Modules.Import;

/// <summary>
/// The Orders repository publishes <c>OrderCreated</c> to the exchange <c>order-events</c> and
/// consumes it from queues. The scan proposes the one binding the source states: the Go
/// worker's <c>QueueBind</c>. The Billing service uses MassTransit, which binds its queue by
/// convention; no line of the source states that binding, so no fact and no proposal exists
/// for it, and a person declares it.
/// </summary>
[Collection("Integration")]
public class OrdersBindingImportTests(ImportHost import) : IClassFixture<ImportHost>
{
    private static JsonObject PublishToExchange(
        Guid id, string producer, string exchange, string message, params string[] handlerSubscriptions) => JsonNode.Parse($$"""
        {
          "id": "{{id}}",
          "name": "An order is created",
          "steps": [
            { "type": "publish", "id": "publish", "from": "{{producer}}", "channel": "{{exchange}}", "message": "{{message}}",
              "payload": { "orderId": "0b0e7a52-6a8e-4c63-9a3b-2f4d6c1c9f10", "customerId": "5d3c2b1a-0f9e-4d8c-b7a6-5e4d3c2b1a0f", "total": 12.5, "createdAt": "2026-01-01T00:00:00Z" },
              "headers": {},
              "handlers": { {{string.Join(", ", handlerSubscriptions.Select(s => $"\"{s}\": []"))}} } }
          ],
          "faults": [],
          "assertions": []
        }
        """)!.AsObject();

    private static StringContent Json(JsonNode body) => new(body.ToJsonString(), Encoding.UTF8, "application/json");

    private static async Task<JsonObject> RunAsync(ImportScenario s, Guid scenarioId)
    {
        var response = await s.Owner.PostAsync(s.Model($"scenarios/{scenarioId}/run"), null);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<JsonObject>())!;
    }

    private static string[] Kinds(JsonObject run) => [.. run["events"]!.AsArray().Select(e => e!["kind"]!.GetValue<string>())];

    [Fact]
    public async Task The_scan_proposes_the_binding_the_source_states_and_a_publish_to_the_exchange_follows_it()
    {
        var s = await ImportScenario.CreateAsync(import);
        var repository = $"{s.GitHubHost}/acme/orders";
        var scan = await s.ScanAsync();
        var proposals = await s.ProposalsAsync(scan.Id());

        // 1. One binding: the queue that the Go worker binds to the exchange, with no routing key.
        var binding = Assert.Single(proposals, p => p.Text("entityKind") == "binding");
        var exchangeProposal = proposals.Proposal("channel", "order-events");
        var queueProposal = proposals.Proposal("channel", "inventory.order-created");
        Assert.Equal(("add", "inventory.order-created bound to order-events"), (binding.Text("action"), binding.Text("name")));
        Assert.Equal((exchangeProposal.Text("entityId"), "order-events", queueProposal.Text("entityId"), "inventory.order-created"),
            (binding["proposed"]!["sourceChannelId"]!.GetValue<string>(), binding["proposed"]!["sourceChannelName"]!.GetValue<string>(),
             binding["proposed"]!["targetChannelId"]!.GetValue<string>(), binding["proposed"]!["targetChannelName"]!.GetValue<string>()));
        Assert.Null(binding["proposed"]!["routingKey"]);
        Assert.Equal(new[] { exchangeProposal.Id(), queueProposal.Id() }.Order(), binding["dependsOn"]!.AsArray().Select(d => d!.GetValue<Guid>()).Order());
        var cited = Assert.Single(binding["evidence"]!.AsArray())!.AsObject();
        Assert.Equal(("inventory-worker/consumer.go", 46, 46, "go.amqp-queue-bind"),
            (cited.Text("path"), cited["startLine"]!.GetValue<int>(), cited["endLine"]!.GetValue<int>(), cited.Text("rule")));
        Assert.DoesNotContain(await s.EvidenceAsync(scan.Id()), e => e.Text("reason") == "binding-has-no-model-home");

        // 2. A full accept writes it as an observed fact with its source pointer.
        Assert.Empty((await s.DecideAsync(scan.Id(), accept: proposals.Select(p => p.Id())))["failed"]!.AsArray());
        var elements = await s.ElementsAsync();
        var channels = await s.AllAsync(s.Model("channels"));
        var subscriptions = await s.AllAsync(s.Model("subscriptions"));
        var exchange = channels.Single("name", "order-events").Text("id")!;
        var inventoryQueue = channels.Single("name", "inventory.order-created").Text("id")!;
        var billingQueue = channels.Single("name", "billing.order-created").Text("id")!;
        var stored = Assert.Single(await s.AllAsync(s.Model("bindings")));
        Assert.Equal((exchange, inventoryQueue, "observed-in-source", $"{repository}@{ImportScenario.Commit1}:inventory-worker/consumer.go:46-46"),
            (stored.Text("sourceChannelId"), stored.Text("targetChannelId"), stored.Text("factStatus"), stored["provenance"]!["source"]!.GetValue<string>()));
        Assert.Null(stored["routingKey"]);

        // 3. A scenario publishes OrderCreated to the exchange, as the code does. The message
        // follows the binding to the worker's queue and reaches its consumer.
        var producer = elements.Single("name", "Orders.Api").Text("id")!;
        var worker = elements.Single("name", "inventory-worker").Text("id")!;
        var billing = elements.Single("name", "Billing").Text("id")!;
        var message = Assert.Single(await s.AllAsync(s.Model("messages"))).Text("id")!;
        var workerSubscription = subscriptions.Single("consumerId", worker).Text("id")!;
        var billingSubscription = subscriptions.Single("consumerId", billing).Text("id")!;

        var scenarioId = Guid.NewGuid();
        await TestHost.Created(s.Owner.PostAsync(s.Model("scenarios"), Json(PublishToExchange(scenarioId, producer, exchange, message, workerSubscription))));
        var run = await RunAsync(s, scenarioId);
        Assert.Empty(run["issues"]!.AsArray());
        Assert.Equal(["Published", "Routed", "Delivered"], Kinds(run));
        var routed = run["events"]![1]!.AsObject();
        Assert.Equal((exchange, inventoryQueue, stored.Text("id")), (routed.Text("from"), routed.Text("to"), routed.Text("target")));
        Assert.Equal(worker, run["events"]![2]!.AsObject().Text("to"));

        // 4. Nothing in the source binds the Billing queue to this exchange, so the message does
        // not reach Billing, and a handler for its subscription is refused.
        var withBilling = PublishToExchange(scenarioId, producer, exchange, message, workerSubscription, billingSubscription);
        Assert.Equal(HttpStatusCode.OK, (await s.Owner.PutAsync(s.Model($"scenarios/{scenarioId}"), Json(withBilling))).StatusCode);
        Assert.Equal("subscription-not-on-channel", Assert.Single((await RunAsync(s, scenarioId))["issues"]!.AsArray())!["code"]!.GetValue<string>());

        // 5. A person declares that binding. The same scenario then reaches the Billing consumer.
        await TestHost.Created(s.Owner.PostAsJsonAsync(s.Model("bindings"),
            new { id = Guid.NewGuid(), sourceChannelId = exchange, targetChannelId = billingQueue }));
        var both = await RunAsync(s, scenarioId);
        Assert.Empty(both["issues"]!.AsArray());
        Assert.Equal(2, Kinds(both).Count(k => k == "Routed"));
        Assert.Equal(new[] { worker, billing }.Order(),
            both["events"]!.AsArray().Where(e => e!["kind"]!.GetValue<string>() == "Delivered").Select(e => e!["to"]!.GetValue<string>()).Order());

        // 6. The same commit again proposes nothing: the observed binding is linked, and the
        // declared one is a person's statement that no fact contradicts.
        Assert.Empty(await s.ProposalsAsync((await s.ScanAsync()).Id()));
        Assert.Equal(["declared", "observed-in-source"], (await s.AllAsync(s.Model("bindings"))).Values("factStatus"));
    }
}
