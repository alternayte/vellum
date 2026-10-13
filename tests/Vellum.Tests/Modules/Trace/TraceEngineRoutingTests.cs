using System.Security.Cryptography;
using System.Text.Json;
using Vellum.Modules.Ai.Design;
using Vellum.Modules.Modelling.Model;
using Vellum.Modules.Trace.Engine;

namespace Vellum.Tests.Modules.Trace;

public partial class TraceEngineTests
{
    // A RabbitMQ topology: the producer publishes to an exchange, and each consumer reads a
    // queue that is bound to it.
    private static readonly ChannelId Exchange = G(60), BillingQueue = G(61), InventoryQueue = G(62),
        DeadLetterExchange = G(63), Parking = G(64), Relay = G(65);
    private static readonly SubscriptionId BillingOnQueue = G(70), InventoryOnQueue = G(71), AuditOnExchange = G(72);
    private static readonly ElementId Audit = G(7);

    private static BindingContract Bind(int id, ChannelId source, ChannelId target, string? key = null) =>
        new(G(id), source, target, key);

    private static TraceModel Routed(params BindingContract[] bindings) => Routed(null, bindings);

    private static TraceModel Routed(RetryPolicy? inventoryRetry, params BindingContract[] bindings)
    {
        var model = Model();
        return model with
        {
            Elements = new Dictionary<ElementId, string>(model.Elements) { [Audit] = "Audit" },
            Channels = new Dictionary<ChannelId, ChannelContract>
            {
                [Exchange] = new(Exchange, "order-events", ChannelKind.Exchange),
                [BillingQueue] = new(BillingQueue, "billing.order-created", ChannelKind.Queue),
                [InventoryQueue] = new(InventoryQueue, "inventory.order-created", ChannelKind.Queue),
                [DeadLetterExchange] = new(DeadLetterExchange, "order-events.dlx", ChannelKind.Exchange),
                [Parking] = new(Parking, "parking", ChannelKind.Queue),
                [Relay] = new(Relay, "relay", ChannelKind.Exchange),
            },
            Subscriptions = new Dictionary<SubscriptionId, SubscriptionContract>
            {
                [BillingOnQueue] = new(BillingOnQueue, BillingQueue, Billing, RetryPolicy.None, null),
                [InventoryOnQueue] = new(InventoryOnQueue, InventoryQueue, Inventory, inventoryRetry ?? RetryPolicy.None, DeadLetterExchange),
            },
            Bindings = bindings.ToDictionary(b => b.Id),
        };
    }

    private static PublishStep ToExchange(string? routingKey = null, params (SubscriptionId Sub, Step[] Steps)[] handlers) =>
        new("publish", Orders, Exchange, OrderCreated, Json("""{"orderId":"o-1"}"""), new Dictionary<string, string>(),
            handlers.ToDictionary(h => h.Sub, h => (IReadOnlyList<Step>)h.Steps), routingKey);

    private static string[] Routes(TraceResult result) => result.Events
        .Where(e => e.Kind is TraceEventKind.Routed or TraceEventKind.NotRouted)
        .Select(e => $"{e.Kind}:{e.From}>{e.To}").ToArray();

    private static Guid[] DeliveredTo(TraceResult result) =>
        result.Events.Where(e => e.Kind == TraceEventKind.Delivered).Select(e => e.To!.Value).ToArray();

    [Fact]
    public void A_publish_to_an_exchange_reaches_the_consumers_of_the_bound_queues()
    {
        // Inserted out of ID order on purpose: routing must not follow insertion order.
        var model = Routed(Bind(81, Exchange, InventoryQueue), Bind(80, Exchange, BillingQueue));

        var result = TraceEngine.Run(model, Scenario([ToExchange(null, (BillingOnQueue, [SaveOrder("invoice")]))]));

        Assert.True(result.IsValid);
        Assert.Equal(
            ["Published:publish", "Routed:publish", "Routed:publish", "Delivered:publish", "DataWritten:invoice", "Delivered:publish"],
            Kinds(result));
        Assert.Equal([Billing.Value, Inventory.Value], DeliveredTo(result));

        var routed = result.Events[1];
        Assert.Equal((Exchange.Value, BillingQueue.Value, G(80), OrderCreated.Value), (routed.From, routed.To, routed.Target, routed.Message));
        Assert.Equal($"Binding {G(80)} has no routing key.", routed.Detail);
        Assert.Equal(StepOutcome.Succeeded, result.Outcomes["invoice"]);
    }

    [Fact]
    public void Subscriptions_on_the_published_channel_come_before_its_bindings()
    {
        var model = Routed(Bind(80, Exchange, BillingQueue));
        model = model with
        {
            Subscriptions = new Dictionary<SubscriptionId, SubscriptionContract>(model.Subscriptions)
            {
                [AuditOnExchange] = new(AuditOnExchange, Exchange, Audit, RetryPolicy.None, null),
            },
        };

        var result = TraceEngine.Run(model, Scenario([ToExchange()]));

        Assert.Equal([Audit.Value, Billing.Value], DeliveredTo(result));
    }

    [Theory]
    [InlineData(null, null, true)]
    [InlineData(null, "order.created", true)]
    [InlineData("order.created", null, false)]
    [InlineData("order.created", "order.created", true)]
    [InlineData("order.created", "order.cancelled", false)]
    [InlineData("order.*", "order.created", true)]
    [InlineData("order.*", "order", false)]
    [InlineData("order.*", "order.created.v2", false)]
    [InlineData("*.created", "order.created", true)]
    [InlineData("*", "order.created", false)]
    [InlineData("#", "order.created.v2", true)]
    [InlineData("order.#", "order", true)]
    [InlineData("order.#", "order.created.v2", true)]
    [InlineData("order.#", "orders.created", false)]
    [InlineData("#.created", "created", true)]
    [InlineData("a.#.z", "a.z", true)]
    [InlineData("a.#.z", "a.b.c.z", true)]
    [InlineData("a.*.z", "a.z", false)]
    [InlineData("a.*.#", "a", false)]
    public void A_binding_key_matches_by_equality_or_by_amqp_topic_rules(string? bindingKey, string? routingKey, bool matches) =>
        Assert.Equal(matches, ChannelRouting.Matches(bindingKey, routingKey));

    [Fact]
    public void A_keyed_binding_is_followed_only_by_a_step_whose_key_matches_it()
    {
        var model = Routed(Bind(80, Exchange, BillingQueue, "order.created"), Bind(81, Exchange, InventoryQueue, "order.*.eu"));

        var result = TraceEngine.Run(model, Scenario([ToExchange("order.created")]));

        Assert.Equal([$"Routed:{Exchange}>{BillingQueue}", $"NotRouted:{Exchange}>{InventoryQueue}"], Routes(result));
        Assert.Equal([Billing.Value], DeliveredTo(result));
        Assert.Equal(
            [
                $"Binding {G(80)}: routing key 'order.created' matches 'order.created'.",
                $"Binding {G(81)}: routing key 'order.created' does not match 'order.*.eu'.",
            ],
            result.Events.Where(e => e.Kind is TraceEventKind.Routed or TraceEventKind.NotRouted).Select(e => e.Detail));
    }

    [Fact]
    public void A_step_without_a_key_does_not_pass_a_binding_that_has_one()
    {
        var model = Routed(Bind(80, Exchange, BillingQueue, "order.created"));

        var result = TraceEngine.Run(model, Scenario([ToExchange()]));

        Assert.Equal(["Published:publish", "NotRouted:publish"], Kinds(result));
        Assert.Equal($"Binding {G(80)} needs routing key 'order.created'; the step states none.", result.Events[1].Detail);
        Assert.Equal(StepOutcome.Succeeded, result.Outcomes["publish"]);
    }

    [Fact]
    public void An_exchange_bound_to_an_exchange_passes_the_message_on()
    {
        var model = Routed(Bind(80, Exchange, Relay), Bind(81, Relay, BillingQueue));

        var result = TraceEngine.Run(model, Scenario([ToExchange()]));

        Assert.Equal([$"Routed:{Exchange}>{Relay}", $"Routed:{Relay}>{BillingQueue}"], Routes(result));
        Assert.Equal([Billing.Value], DeliveredTo(result));
    }

    [Fact]
    public void A_cycle_of_bindings_ends_because_a_channel_takes_a_message_once()
    {
        var model = Routed(Bind(80, Exchange, Relay), Bind(81, Relay, Exchange), Bind(82, Relay, BillingQueue), Bind(83, Exchange, BillingQueue));

        var result = TraceEngine.Run(model, Scenario([ToExchange()]));

        Assert.True(result.IsValid);
        Assert.Equal(
            [
                $"Routed:{Exchange}>{Relay}", $"NotRouted:{Relay}>{Exchange}", $"Routed:{Relay}>{BillingQueue}",
                $"NotRouted:{Exchange}>{BillingQueue}",
            ],
            Routes(result));
        Assert.Equal([Billing.Value], DeliveredTo(result));
        Assert.Equal($"Binding {G(81)}: the message was already routed to this channel.", result.Events.First(e => e.Kind == TraceEventKind.NotRouted).Detail);
    }

    [Fact]
    public void A_dead_lettered_message_follows_the_bindings_of_the_dead_letter_exchange()
    {
        var model = Routed(Bind(80, Exchange, InventoryQueue), Bind(81, DeadLetterExchange, Parking), Bind(82, DeadLetterExchange, BillingQueue, "never"));

        var result = TraceEngine.Run(model, Scenario([ToExchange()], [new Fault(Inventory, FaultKind.Error, null)]));

        Assert.Equal(
            ["Published:publish", "Routed:publish", "Delivered:publish", "DeliveryFailed:publish", "DeadLettered:publish", "Routed:publish", "NotRouted:publish"],
            Kinds(result));
        Assert.Equal(DeadLetterExchange.Value, result.Events[4].Target);
        Assert.Equal((DeadLetterExchange.Value, Parking.Value, G(81)), (result.Events[5].From, result.Events[5].To, result.Events[5].Target));
        // A scenario declares no steps for the consumers of a dead-letter channel: the message rests there.
        Assert.Single(DeliveredTo(result));
    }

    [Fact]
    public void An_outage_of_one_bound_queue_fails_that_route_and_not_the_others()
    {
        var model = Routed(Bind(80, Exchange, InventoryQueue), Bind(81, Exchange, BillingQueue));

        var result = TraceEngine.Run(model, Scenario([ToExchange()], [new Fault(InventoryQueue, FaultKind.Outage, null)]));

        Assert.Equal(["Published:publish", "PublishFailed:publish", "Routed:publish", "Delivered:publish"], Kinds(result));
        Assert.Equal(InventoryQueue.Value, result.Events[1].Target);
        Assert.Equal($"The bound channel is unavailable; binding {G(80)} is not followed.", result.Events[1].Detail);
        Assert.Equal([Billing.Value], DeliveredTo(result));
        Assert.Equal(StepOutcome.Succeeded, result.Outcomes["publish"]);
    }

    [Fact]
    public void A_channel_with_more_bindings_than_the_limit_is_too_large_to_play()
    {
        var bindings = Enumerable.Range(0, TraceLimits.MaxRoutes + 1)
            .Select(i => Bind(1000 + i, Exchange, BillingQueue, $"key.{i}")).ToArray();

        var atLimit = TraceEngine.Run(Routed(bindings[..TraceLimits.MaxRoutes]), Scenario([ToExchange("none")]));
        var over = TraceEngine.Run(Routed(bindings), Scenario([ToExchange("none")]));

        Assert.True(atLimit.IsValid);
        Assert.Equal(TraceLimits.MaxRoutes, atLimit.Events.Count(e => e.Kind == TraceEventKind.NotRouted));
        Assert.Equal("trace-too-large", Assert.Single(over.Issues).Code);
        Assert.Empty(over.Events);
    }

    [Fact]
    public void A_handler_names_any_subscription_that_the_bindings_reach_and_no_other()
    {
        var model = Routed(Bind(80, Exchange, Relay), Bind(81, Relay, BillingQueue));

        Assert.Empty(ScenarioValidator.Validate(model, Scenario([ToExchange(null, (BillingOnQueue, []))])));

        var issue = Assert.Single(ScenarioValidator.Validate(model, Scenario([ToExchange(null, (InventoryOnQueue, []))])));
        Assert.Equal("subscription-not-on-channel", issue.Code);
    }

    [Fact]
    public void A_routing_key_that_is_empty_or_holds_a_control_character_is_rejected()
    {
        var model = Routed();

        Assert.Equal("invalid-routing-key", Assert.Single(ScenarioValidator.Validate(model, Scenario([ToExchange("")]))).Code);
        Assert.Equal("invalid-routing-key", Assert.Single(ScenarioValidator.Validate(model, Scenario([ToExchange("a\nb")]))).Code);
    }

    /// <summary>
    /// A scenario written before the routing key existed keeps its document and its hash: a
    /// publish step without a key writes no <c>routingKey</c> member.
    /// </summary>
    [Fact]
    public void A_scenario_without_a_routing_key_keeps_the_document_and_the_hash_it_had()
    {
        // The document as a version without the routing key wrote it.
        const string before =
            """
            {"id":"00000000-0000-0000-0000-000000000099","name":"Place an order","steps":[{"type":"publish","from":"00000000-0000-0000-0000-000000000002","channel":"00000000-0000-0000-0000-000000000020","message":"00000000-0000-0000-0000-000000000040","payload":{"orderId":"o-1"},"headers":{},"handlers":{"00000000-0000-0000-0000-000000000030":[]},"id":"publish"}],"faults":[],"assertions":[]}
            """;
        var scenario = JsonSerializer.Deserialize<Scenario>(before, DesignJson.Options)!;

        Assert.Null(Assert.IsType<PublishStep>(scenario.Steps[0]).RoutingKey);
        Assert.Equal(before.Trim(), JsonSerializer.Serialize(scenario, DesignJson.Options));

        using var document = JsonDocument.Parse(before);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
            CanonicalJson.Write(writer, document.RootElement);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(buffer.ToArray())), FlowProposals.ScenarioHash(scenario));

        // A key is written, read back and changes the hash.
        var keyed = scenario with { Steps = [(PublishStep)scenario.Steps[0] with { RoutingKey = "order.created" }] };
        var restored = JsonSerializer.Deserialize<Scenario>(JsonSerializer.Serialize(keyed, DesignJson.Options), DesignJson.Options)!;
        Assert.Equal("order.created", ((PublishStep)restored.Steps[0]).RoutingKey);
        Assert.NotEqual(FlowProposals.ScenarioHash(scenario), FlowProposals.ScenarioHash(keyed));
    }
}
