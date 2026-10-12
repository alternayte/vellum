using System.Text.Json;
using Vellum.Modules.Modelling.Model;
using Vellum.Modules.Trace.Engine;

namespace Vellum.Tests.Modules.Trace;

public class TraceEngineTests
{
    // Orders reference case: a customer places an order, Orders writes it and publishes
    // OrderCreated, and Inventory and Billing consume it.
    private static readonly ElementId Customer = G(1), Orders = G(2), OrdersDb = G(3), Inventory = G(4), Billing = G(5), Payments = G(6);
    private static readonly OperationId PlaceOrder = G(10), Charge = G(11);
    private static readonly ChannelId OrderEvents = G(20), OrderEventsDlq = G(21);
    private static readonly SubscriptionId InventorySub = G(30), BillingSub = G(31);
    private static readonly MessageId OrderCreated = G(40);
    private static readonly SchemaId OrderRequestSchema = G(50), OrderCreatedSchema = G(51);

    private static Guid G(int n) => new($"00000000-0000-0000-0000-{n:D12}");

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    private static TraceModel Model(
        RetryPolicy? chargeRetry = null, RetryPolicy? inventoryRetry = null, bool inventoryDlq = true) => new(
        new Dictionary<ElementId, string>
        {
            [Customer] = "Customer", [Orders] = "Orders", [OrdersDb] = "Orders DB",
            [Inventory] = "Inventory", [Billing] = "Billing", [Payments] = "Payments",
        },
        new Dictionary<OperationId, OperationContract>
        {
            [PlaceOrder] = new(PlaceOrder, Orders, "POST /orders", Transport.Http, OrderRequestSchema, null, 2000, RetryPolicy.None),
            [Charge] = new(Charge, Payments, "POST /charges", Transport.Http, null, null, 1000, chargeRetry ?? RetryPolicy.None),
        },
        new Dictionary<ChannelId, ChannelContract>
        {
            [OrderEvents] = new(OrderEvents, "order-events", ChannelKind.Topic),
            [OrderEventsDlq] = new(OrderEventsDlq, "order-events.dlq", ChannelKind.Queue),
        },
        new Dictionary<SubscriptionId, SubscriptionContract>
        {
            [InventorySub] = new(InventorySub, OrderEvents, Inventory, inventoryRetry ?? RetryPolicy.None, inventoryDlq ? OrderEventsDlq : null),
            [BillingSub] = new(BillingSub, OrderEvents, Billing, RetryPolicy.None, null),
        },
        new Dictionary<MessageId, MessageContract> { [OrderCreated] = new(OrderCreated, "OrderCreated", OrderCreatedSchema) },
        new Dictionary<SchemaId, string>
        {
            [OrderRequestSchema] = """{"type":"object","required":["sku","qty"],"properties":{"sku":{"type":"string"},"qty":{"type":"integer","minimum":1}}}""",
            [OrderCreatedSchema] = """{"type":"object","required":["orderId"],"properties":{"orderId":{"type":"string"}}}""",
        });

    private static WriteStep SaveOrder(string id = "save") =>
        new(id, Orders, OrdersDb, "orders", Json("""{"orderId":"o-1","status":"accepted"}"""));

    private static PublishStep PublishCreated(string id = "publish", params (SubscriptionId Sub, Step[] Steps)[] handlers) =>
        new(id, Orders, OrderEvents, OrderCreated, Json("""{"orderId":"o-1"}"""), new Dictionary<string, string>(),
            handlers.ToDictionary(h => h.Sub, h => (IReadOnlyList<Step>)h.Steps));

    private static CallStep Place(params Step[] then) =>
        new("place", Customer, PlaceOrder, Json("""{"sku":"apple","qty":2}"""), Json("""{"orderId":"o-1"}"""), then, []);

    private static Scenario Scenario(Step[] steps, Fault[]? faults = null, Assertion[]? assertions = null) =>
        new(G(99), "Place an order", steps, faults ?? [], assertions ?? []);

    private static string[] Kinds(TraceResult result) => result.Events.Select(e => $"{e.Kind}:{e.StepId}").ToArray();

    [Fact]
    public void Happy_path_plays_request_write_publish_fan_out_and_response_in_order()
    {
        var result = TraceEngine.Run(Model(), Scenario([Place(SaveOrder(), PublishCreated())]));

        Assert.True(result.IsValid);
        Assert.Equal(
            [
                "RequestSent:place", "DataWritten:save", "Published:publish",
                "Delivered:publish", "Delivered:publish", "ResponseReceived:place",
            ],
            Kinds(result));
        // Fan-out follows subscription ID order, not dictionary order.
        Assert.Equal([Inventory.Value, Billing.Value], result.Events.Where(e => e.Kind == TraceEventKind.Delivered).Select(e => e.To!.Value));
        Assert.Equal(Enumerable.Range(1, 6), result.Events.Select(e => e.Sequence));
        Assert.All(["place", "save", "publish"], id => Assert.Equal(StepOutcome.Succeeded, result.Outcomes[id]));
    }

    [Fact]
    public void Same_model_and_scenario_give_the_same_events_and_hash()
    {
        var scenario = Scenario(
            [Place(SaveOrder(), PublishCreated())],
            [new Fault(InventorySub, FaultKind.Error, 1)]);
        var model = Model(inventoryRetry: new RetryPolicy(3, 200));

        var first = TraceEngine.Run(model, scenario);
        var second = TraceEngine.Run(model, scenario);

        Assert.Equal(first.Hash, second.Hash);
        Assert.Equal(JsonSerializer.Serialize(first.Events), JsonSerializer.Serialize(second.Events));
        Assert.Equal(TraceEngine.Version, first.EngineVersion);
    }

    [Fact]
    public void A_timeout_advances_virtual_time_and_a_retry_adds_backoff()
    {
        var call = new CallStep("charge", Orders, Charge, null, null, [], []);
        var result = TraceEngine.Run(
            Model(chargeRetry: new RetryPolicy(2, 250)),
            Scenario([call], [new Fault(Charge, FaultKind.Timeout, 1)]));

        Assert.Equal(
            [
                (TraceEventKind.RequestSent, 0L, 1), (TraceEventKind.RequestTimedOut, 1000L, 1),
                (TraceEventKind.RetryScheduled, 1250L, 2), (TraceEventKind.RequestSent, 1250L, 2),
                (TraceEventKind.ResponseReceived, 1250L, 2),
            ],
            result.Events.Select(e => (e.Kind, e.AtMs, e.Attempt)));
        Assert.Equal(StepOutcome.Succeeded, result.Outcomes["charge"]);
    }

    [Fact]
    public void A_call_that_fails_every_attempt_skips_the_steps_after_it()
    {
        var result = TraceEngine.Run(
            Model(),
            Scenario(
                [new CallStep("charge", Orders, Charge, null, null, [], []), SaveOrder()],
                [new Fault(Payments, FaultKind.Outage, null)]));

        Assert.Equal(["RequestSent:charge", "RequestFailed:charge", "StepSkipped:save"], Kinds(result));
        Assert.Equal(StepOutcome.Failed, result.Outcomes["charge"]);
        Assert.Equal(StepOutcome.Skipped, result.Outcomes["save"]);
    }

    [Fact]
    public void OnError_handles_the_failure_so_later_steps_still_run()
    {
        var charge = new CallStep("charge", Orders, Charge, null, null, [], [SaveOrder("mark-pending")]);
        var result = TraceEngine.Run(
            Model(),
            Scenario([charge, PublishCreated()], [new Fault(Charge, FaultKind.Error, null)]));

        Assert.Equal(StepOutcome.Failed, result.Outcomes["charge"]);
        Assert.Equal(StepOutcome.Succeeded, result.Outcomes["mark-pending"]);
        Assert.Equal(StepOutcome.Succeeded, result.Outcomes["publish"]);
    }

    [Fact]
    public void A_retried_request_repeats_the_side_effects_of_the_first_attempt()
    {
        // The callee writes, then its downstream call fails once. The retry writes again:
        // the duplicate is visible in the trace instead of hidden by the retry.
        var place = new CallStep("charge", Orders, Charge, null, null,
            [SaveOrder("ledger"), new CallStep("inner", Payments, PlaceOrder, Json("""{"sku":"a","qty":1}"""), null, [], [])], []);
        var result = TraceEngine.Run(
            Model(chargeRetry: new RetryPolicy(2, 0)),
            Scenario([place], [new Fault(PlaceOrder, FaultKind.Error, 1)]));

        Assert.Equal(2, result.Events.Count(e => e.Kind == TraceEventKind.DataWritten));
        Assert.Equal(StepOutcome.Succeeded, result.Outcomes["charge"]);
    }

    [Fact]
    public void A_consumer_that_keeps_failing_is_retried_then_dead_lettered()
    {
        var result = TraceEngine.Run(
            Model(inventoryRetry: new RetryPolicy(3, 500)),
            Scenario(
                [PublishCreated()],
                [new Fault(InventorySub, FaultKind.Error, null)],
                [
                    new EventCountAssertion("dlq", TraceEventKind.DeadLettered, OrderEventsDlq, 1),
                    new EventCountAssertion("billing-once", TraceEventKind.Delivered, Billing, 1),
                    new OutcomeAssertion("published", "publish", StepOutcome.Succeeded),
                ]));

        var inventory = result.Events.Where(e => e.Target == InventorySub.Value || e.Kind == TraceEventKind.DeadLettered).ToList();
        Assert.Equal(3, inventory.Count(e => e.Kind == TraceEventKind.Delivered));
        Assert.Equal(3, inventory.Count(e => e.Kind == TraceEventKind.DeliveryFailed));
        Assert.Equal(1000, inventory.Single(e => e.Kind == TraceEventKind.DeadLettered).AtMs);
        Assert.All(result.Assertions, a => Assert.True(a.Passed, a.Detail));
    }

    [Fact]
    public void A_failing_consumer_without_a_dead_letter_channel_loses_the_message_visibly()
    {
        var result = TraceEngine.Run(
            Model(inventoryDlq: false),
            Scenario([PublishCreated()], [new Fault(Inventory, FaultKind.Outage, null)]));

        var dropped = Assert.Single(result.Events, e => e.Kind == TraceEventKind.Dropped);
        Assert.Contains("lost", dropped.Detail);
        Assert.DoesNotContain(result.Events, e => e.Kind == TraceEventKind.DeadLettered);
    }

    [Fact]
    public void A_failed_handler_step_fails_the_delivery_but_not_the_publisher()
    {
        var handler = new CallStep("reserve", Inventory, Charge, null, null, [], []);
        var result = TraceEngine.Run(
            Model(),
            Scenario(
                [Place(SaveOrder(), PublishCreated("publish", (InventorySub, [handler])))],
                [new Fault(Charge, FaultKind.Error, null)]));

        Assert.Equal(StepOutcome.Succeeded, result.Outcomes["place"]);
        Assert.Equal(StepOutcome.Failed, result.Outcomes["reserve"]);
        Assert.Contains(result.Events, e => e.Kind == TraceEventKind.DeadLettered);
    }

    [Fact]
    public void A_broker_outage_fails_the_publish_and_the_request_that_needed_it()
    {
        var result = TraceEngine.Run(
            Model(),
            Scenario(
                [Place(SaveOrder(), PublishCreated())],
                [new Fault(OrderEvents, FaultKind.Outage, null)],
                [new OutcomeAssertion("accepted", "place", StepOutcome.Succeeded)]));

        // The order row is written but the event is never published: the lost-event gap the
        // promise loop exists to expose.
        Assert.Equal(["RequestSent:place", "DataWritten:save", "PublishFailed:publish", "RequestFailed:place"], Kinds(result));
        Assert.False(Assert.Single(result.Assertions).Passed);
    }

    [Fact]
    public void A_branch_follows_the_outcome_of_an_earlier_step()
    {
        var charge = new CallStep("charge", Orders, Charge, null, null, [], [SaveOrder("noop")]);
        var branch = new BranchStep("decide", "charge", StepOutcome.Failed, [SaveOrder("compensate")], [PublishCreated()]);

        var failed = TraceEngine.Run(Model(), Scenario([charge, branch], [new Fault(Charge, FaultKind.Error, null)]));
        var passed = TraceEngine.Run(Model(), Scenario([charge, branch]));

        Assert.Equal(StepOutcome.Succeeded, failed.Outcomes["compensate"]);
        Assert.False(failed.Outcomes.ContainsKey("publish"));
        Assert.Equal(StepOutcome.Succeeded, passed.Outcomes["publish"]);
        Assert.False(passed.Outcomes.ContainsKey("compensate"));
    }

    [Fact]
    public void Parallel_branches_start_together_and_end_with_the_slowest()
    {
        var slow = new CallStep("slow", Orders, Charge, null, null, [], [SaveOrder("fallback")]);
        var parallel = new ParallelStep("both", [[slow], [SaveOrder("fast")]]);
        var result = TraceEngine.Run(
            Model(),
            Scenario([parallel, PublishCreated()], [new Fault(Charge, FaultKind.Timeout, null)]));

        Assert.Equal(0, result.Events.Single(e => e.StepId == "fast").AtMs);
        Assert.Equal(1000, result.Events.Single(e => e.Kind == TraceEventKind.Published).AtMs);
        Assert.True(result.Events.Zip(result.Events.Skip(1)).All(p => p.First.AtMs <= p.Second.AtMs));
    }

    [Fact]
    public void An_invalid_scenario_reports_every_dangling_reference_and_runs_nothing()
    {
        var steps = new Step[]
        {
            new CallStep("a", G(700), G(701), null, null, [], []),
            new PublishStep("a", Orders, G(702), G(703), null, new Dictionary<string, string>(),
                new Dictionary<SubscriptionId, IReadOnlyList<Step>> { [G(704)] = [] }),
            new BranchStep("b", "later", StepOutcome.Failed, [], []),
            new WriteStep("later", Orders, G(705), "", null),
        };
        var result = TraceEngine.Run(
            Model(),
            Scenario(steps, [new Fault(G(706), FaultKind.Error, 0)], [new OutcomeAssertion("x", "missing", StepOutcome.Failed)]));

        Assert.False(result.IsValid);
        Assert.Empty(result.Events);
        Assert.Equal(
            [
                "duplicate-step-id", "invalid-fault", "missing-dataset", "unknown-channel", "unknown-element",
                "unknown-fault-target", "unknown-message", "unknown-operation", "unknown-step", "unknown-subscription",
            ],
            result.Issues.Select(i => i.Code).Distinct().Order());
    }

    [Fact]
    public void A_handler_for_a_subscription_on_another_channel_is_rejected()
    {
        var publish = new PublishStep("p", Orders, OrderEventsDlq, OrderCreated, Json("""{"orderId":"o-1"}"""),
            new Dictionary<string, string>(), new Dictionary<SubscriptionId, IReadOnlyList<Step>> { [InventorySub] = [] });

        var issue = Assert.Single(ScenarioValidator.Validate(Model(), Scenario([publish])));
        Assert.Equal("subscription-not-on-channel", issue.Code);
    }

    [Theory]
    [InlineData("""{"sku":"apple","qty":0}""", "schema-mismatch")]
    [InlineData("""{"sku":"apple"}""", "schema-mismatch")]
    [InlineData(null, "missing-payload")]
    public void A_payload_that_breaks_its_schema_fails_validation(string? request, string code)
    {
        var call = new CallStep("place", Customer, PlaceOrder, request is null ? null : Json(request), null, [], []);

        var issue = Assert.Single(ScenarioValidator.Validate(Model(), Scenario([call])));
        Assert.Equal(code, issue.Code);
        Assert.Equal("place", issue.StepId);
    }

    [Fact]
    public void A_scenario_survives_a_json_round_trip_with_its_step_types()
    {
        var scenario = Scenario(
            [
                Place(SaveOrder(), PublishCreated("publish", (InventorySub, [SaveOrder("reserve")]))),
                new ParallelStep("par", [[SaveOrder("p1")], [SaveOrder("p2")]]),
                new BranchStep("br", "place", StepOutcome.Succeeded, [SaveOrder("b1")], []),
            ],
            [new Fault(InventorySub, FaultKind.Error, 1)],
            [new OutcomeAssertion("ok", "place", StepOutcome.Succeeded), new EventCountAssertion("n", TraceEventKind.Delivered, null, 3)]);
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        var restored = JsonSerializer.Deserialize<Scenario>(JsonSerializer.Serialize(scenario, options), options)!;

        var model = Model(inventoryRetry: new RetryPolicy(2, 10));
        Assert.Equal(TraceEngine.Run(model, scenario).Hash, TraceEngine.Run(model, restored).Hash);
    }

    [Fact]
    public void Random_valid_scenarios_always_replay_identically_in_time_order()
    {
        var random = new Random(20261013);
        for (var i = 0; i < 300; i++)
        {
            var counter = 0;
            var scenario = Scenario(
                RandomSteps(random, 3, ref counter),
                RandomFaults(random));
            var model = Model(new RetryPolicy(random.Next(1, 4), random.Next(0, 300)), new RetryPolicy(random.Next(1, 4), random.Next(0, 300)), random.Next(2) == 0);

            var first = TraceEngine.Run(model, scenario);
            var second = TraceEngine.Run(model, scenario);

            Assert.True(first.IsValid, string.Join("; ", first.Issues.Select(x => x.Message)));
            Assert.Equal(first.Hash, second.Hash);
            Assert.True(first.Events.Zip(first.Events.Skip(1)).All(p => p.First.AtMs <= p.Second.AtMs && p.First.Sequence + 1 == p.Second.Sequence));
            // Every delivery belongs to a publish that reached the channel.
            var published = first.Events.Where(e => e.Kind == TraceEventKind.Published).Select(e => e.StepId).ToHashSet();
            Assert.All(first.Events.Where(e => e.Kind == TraceEventKind.Delivered), e => Assert.Contains(e.StepId, published));
        }
    }

    private static Step[] RandomSteps(Random random, int depth, ref int counter)
    {
        var steps = new List<Step>();
        var count = random.Next(1, 4);
        for (var i = 0; i < count; i++)
        {
            var id = $"s{++counter}";
            switch (depth == 0 ? 0 : random.Next(5))
            {
                case 0:
                    steps.Add(SaveOrder(id));
                    break;
                case 1:
                    steps.Add(new CallStep(id, Orders, Charge, null, null,
                        RandomSteps(random, depth - 1, ref counter),
                        random.Next(2) == 0 ? RandomSteps(random, depth - 1, ref counter) : []));
                    break;
                case 2:
                    steps.Add(PublishCreated(id, (InventorySub, RandomSteps(random, depth - 1, ref counter))));
                    break;
                case 3:
                    steps.Add(new ParallelStep(id, [RandomSteps(random, depth - 1, ref counter), RandomSteps(random, depth - 1, ref counter)]));
                    break;
                default:
                    steps.Add(new BranchStep(id, "s1", random.Next(2) == 0 ? StepOutcome.Failed : StepOutcome.Succeeded,
                        id == "s1" ? [] : RandomSteps(random, depth - 1, ref counter), []));
                    break;
            }
        }
        // A branch may not name itself; replace a leading branch with a write.
        if (steps[0] is BranchStep { Id: "s1" })
            steps[0] = SaveOrder("s1");
        return steps.ToArray();
    }

    private static Fault[] RandomFaults(Random random)
    {
        Guid[] targets = [Charge, Payments, OrderEvents, InventorySub, Inventory, OrdersDb];
        return targets
            .Where(_ => random.Next(3) == 0)
            .Select(t => new Fault(t, (FaultKind)random.Next(3), random.Next(2) == 0 ? null : random.Next(1, 4)))
            .ToArray();
    }
}
