using System.Text.Json;
using Vellum.Modules.Modelling.Model;
using Vellum.Modules.Trace.Engine;

namespace Vellum.Tests.Modules.Trace;

// Regressions from the independent review of the first engine: each test names the defect.
public partial class TraceEngineTests
{
    private static CallStep ChargeCall(string id, Step[]? then = null, Step[]? onError = null) =>
        new(id, Orders, Charge, null, null, then ?? [], onError ?? []);

    private static CallStep PlaceCall(string id, Step[]? then = null, Step[]? onError = null) =>
        new(id, Payments, PlaceOrder, Json("""{"sku":"a","qty":1}"""), null, then ?? [], onError ?? []);

    private static TraceModel WithSchema(string requestSchema)
    {
        var model = Model();
        return model with { Schemas = new Dictionary<SchemaId, string>(model.Schemas) { [OrderRequestSchema] = requestSchema } };
    }

    [Fact]
    public void A_schema_with_an_id_replays_and_does_not_collide_with_another_schema_of_the_same_id()
    {
        const string schema = """{"$id":"http://vellum.test/order","type":"object","required":["sku"]}""";
        var model = WithSchema(schema);
        model = model with { Schemas = new Dictionary<SchemaId, string>(model.Schemas) { [OrderCreatedSchema] = """{"$id":"http://vellum.test/order","type":"object"}""" } };
        var scenario = Scenario([Place(PublishCreated())]);

        var first = TraceEngine.Run(model, scenario);
        var second = TraceEngine.Run(model, scenario);

        Assert.True(first.IsValid, string.Join("; ", first.Issues.Select(i => i.Message)));
        Assert.Equal(first.Hash, second.Hash);
        Assert.NotEmpty(second.Events);
    }

    [Theory]
    [InlineData("""{"$ref":"http://example.invalid/nope.json"}""")]
    [InlineData("""{"properties":{"sku":{"pattern":"("}}}""")]
    [InlineData("42")]
    [InlineData("not json")]
    public void A_schema_that_cannot_be_evaluated_is_an_issue_not_a_crash(string schema)
    {
        var result = TraceEngine.Run(WithSchema(schema), Scenario([Place()]));

        Assert.Contains(result.Issues, i => i.Code == "invalid-schema");
        Assert.Empty(result.Events);
    }

    [Theory]
    [InlineData("""{"id":"00000000-0000-0000-0000-000000000099","name":"x"}""")]
    [InlineData("""{"id":"00000000-0000-0000-0000-000000000099","name":"x","steps":[null],"faults":[null],"assertions":[null]}""")]
    [InlineData("""{"id":"00000000-0000-0000-0000-000000000099","name":"x","faults":[],"assertions":[],"steps":[{"type":"call","id":"c","from":"00000000-0000-0000-0000-000000000002","operation":"00000000-0000-0000-0000-000000000011"}]}""")]
    [InlineData("""{"id":"00000000-0000-0000-0000-000000000099","name":"x","faults":[],"assertions":[],"steps":[{"type":"publish","id":"p","from":"00000000-0000-0000-0000-000000000002","channel":"00000000-0000-0000-0000-000000000020","message":"00000000-0000-0000-0000-000000000040","payload":{"orderId":"o"}}]}""")]
    [InlineData("""{"id":"00000000-0000-0000-0000-000000000099","name":"x","faults":[],"assertions":[],"steps":[{"type":"parallel","id":"p"},{"type":"branch","id":"b"}]}""")]
    public void Scenario_json_with_missing_or_null_parts_is_reported_as_malformed(string json)
    {
        var scenario = JsonSerializer.Deserialize<Scenario>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        var result = TraceEngine.Run(Model(), scenario);

        Assert.Contains(result.Issues, i => i.Code == "malformed-scenario");
        Assert.Empty(result.Events);
    }

    [Fact]
    public void Nested_retries_stop_at_the_event_budget_instead_of_running_for_ever()
    {
        Step step = new WriteStep("w", Orders, OrdersDb, "orders", null);
        for (var i = 0; i < 12; i++)
            step = ChargeCall($"c{i}", [step]);

        var result = TraceEngine.Run(
            Model(chargeRetry: new RetryPolicy(3, 0)),
            Scenario([step], [new Fault(OrdersDb, FaultKind.Error, null)]));

        Assert.Equal("trace-too-large", Assert.Single(result.Issues).Code);
        Assert.Empty(result.Events);
    }

    [Theory]
    [InlineData(1_000_000, 0, 1000)]
    [InlineData(0, 0, 1000)]
    [InlineData(2, -500, 1000)]
    [InlineData(2, 0, -100)]
    public void Retry_and_timeout_values_outside_the_limits_are_rejected(int attempts, int backoffMs, int timeoutMs)
    {
        var model = Model();
        model = model with
        {
            Operations = new Dictionary<OperationId, OperationContract>(model.Operations)
            {
                [Charge] = model.Operations[Charge] with { Retry = new RetryPolicy(attempts, backoffMs), TimeoutMs = timeoutMs },
            },
        };

        var result = TraceEngine.Run(model, Scenario([ChargeCall("c")]));

        Assert.Equal("invalid-model", Assert.Single(result.Issues).Code);
    }

    [Fact]
    public void Steps_nested_deeper_than_the_limit_are_rejected_without_overflowing_the_stack()
    {
        Step step = SaveOrder("leaf");
        for (var i = 0; i < 2000; i++)
            step = new ParallelStep($"p{i}", [[step]]);

        var result = TraceEngine.Run(Model(), Scenario([step]));

        Assert.Contains(result.Issues, i => i.Code == "too-deep");
    }

    [Fact]
    public void A_retry_forgets_the_outcomes_of_the_attempt_it_replaces()
    {
        // Attempt 1: `a` fails, its fallback `x` succeeds, then `w` fails and the attempt is
        // abandoned. Attempt 2: `a` succeeds, so `x` never runs. The branch must not see the
        // `x` of attempt 1.
        var outer = ChargeCall("outer",
        [
            PlaceCall("a", onError: [SaveOrder("x")]),
            new WriteStep("w", Payments, Billing, "ledger", null),
            new BranchStep("decide", "x", StepOutcome.Succeeded, [SaveOrder("stale")], [SaveOrder("fresh")]),
        ]);

        var result = TraceEngine.Run(
            Model(chargeRetry: new RetryPolicy(2, 0)),
            Scenario([outer], [new Fault(PlaceOrder, FaultKind.Error, 1), new Fault(Billing, FaultKind.Error, 1)]));

        Assert.Equal(StepOutcome.Succeeded, result.Outcomes["outer"]);
        Assert.False(result.Outcomes.ContainsKey("x"));
        Assert.False(result.Outcomes.ContainsKey("stale"));
        Assert.Equal(StepOutcome.Succeeded, result.Outcomes["fresh"]);
    }

    [Fact]
    public void A_branch_cannot_read_a_step_that_may_finish_after_it()
    {
        var onParallelSibling = Scenario(
        [
            new ParallelStep("par",
            [
                [ChargeCall("slow", onError: [SaveOrder("late")])],
                [new BranchStep("b", "late", StepOutcome.Succeeded, [], [])],
            ]),
        ]);
        var onConsumerStep = Scenario(
        [
            PublishCreated("publish", (InventorySub, [SaveOrder("consumed")])),
            new BranchStep("b", "consumed", StepOutcome.Succeeded, [], []),
        ]);
        var onItself = Scenario([ChargeCall("c", [new BranchStep("b", "c", StepOutcome.Succeeded, [], [])])]);

        Assert.All(new[] { onParallelSibling, onConsumerStep, onItself }, scenario =>
            Assert.Equal("unknown-step", Assert.Single(ScenarioValidator.Validate(Model(), scenario)).Code));
    }

    [Fact]
    public void A_caller_times_out_when_the_callee_takes_longer_than_the_declared_timeout()
    {
        // Charge allows 1000 ms. Inside it, PlaceOrder hangs for 2000 ms before its fallback.
        var outer = ChargeCall("outer", [PlaceCall("inner", onError: [SaveOrder("fallback")])]);

        var result = TraceEngine.Run(Model(), Scenario([outer], [new Fault(PlaceOrder, FaultKind.Timeout, null)]));

        var timedOut = result.Events.First(e => e.Kind == TraceEventKind.RequestTimedOut);
        Assert.Equal(("outer", 1000L), (timedOut.StepId, timedOut.AtMs));
        Assert.Equal(StepOutcome.Failed, result.Outcomes["outer"]);
        Assert.DoesNotContain(result.Events, e => e.Kind == TraceEventKind.ResponseReceived);
        // The callee does not know its caller gave up: its work still shows, at its own time,
        // but it decides nothing.
        Assert.Equal(2000, result.Events.Single(e => e.StepId == "fallback").AtMs);
        Assert.False(result.Outcomes.ContainsKey("fallback"));
    }

    [Fact]
    public void Faults_on_an_operation_and_on_its_owner_each_count_every_attempt()
    {
        var result = TraceEngine.Run(
            Model(chargeRetry: new RetryPolicy(2, 0)),
            Scenario([ChargeCall("c")], [new Fault(Charge, FaultKind.Error, 1), new Fault(Payments, FaultKind.Error, 1)]));

        Assert.Equal(StepOutcome.Succeeded, result.Outcomes["c"]);
        Assert.Equal(2, result.Events.Count(e => e.Kind == TraceEventKind.RequestSent));
    }

    [Fact]
    public void A_fault_on_the_first_attempts_hits_the_earliest_attempts_in_time()
    {
        var parallel = new ParallelStep("par",
        [
            [ChargeCall("slow", onError: [SaveOrder("late")])],
            [SaveOrder("early")],
        ]);

        var result = TraceEngine.Run(
            Model(),
            Scenario([parallel], [new Fault(Charge, FaultKind.Timeout, null), new Fault(OrdersDb, FaultKind.Error, 1)]));

        Assert.Equal(StepOutcome.Failed, result.Outcomes["early"]);
        Assert.Equal(StepOutcome.Succeeded, result.Outcomes["late"]);
        Assert.Equal(1000, result.Events.Single(e => e.StepId == "late").AtMs);
    }

    [Fact]
    public void A_dead_letter_channel_that_is_down_loses_the_message()
    {
        var result = TraceEngine.Run(
            Model(),
            Scenario([PublishCreated()], [new Fault(InventorySub, FaultKind.Error, null), new Fault(OrderEventsDlq, FaultKind.Outage, null)]));

        Assert.DoesNotContain(result.Events, e => e.Kind == TraceEventKind.DeadLettered);
        Assert.Contains("dead-letter channel is unavailable", Assert.Single(result.Events, e => e.Kind == TraceEventKind.Dropped).Detail);
    }

    [Fact]
    public void References_and_values_that_mean_nothing_are_rejected()
    {
        var scenario = Scenario(
            [SaveOrder()],
            [
                new Fault(OrdersDb, FaultKind.Timeout, null),
                new Fault(Charge, FaultKind.Error, 1), new Fault(Charge, FaultKind.Outage, 1),
                new Fault(Payments, (FaultKind)99, null),
            ],
            [
                new EventCountAssertion("n", TraceEventKind.Delivered, G(900), 0),
                new EventCountAssertion("n", (TraceEventKind)99, null, 0),
                new OutcomeAssertion("o", "save", (StepOutcome)42),
            ]);

        Assert.Equal(
            ["duplicate-fault", "invalid-assertion", "invalid-fault", "unknown-assertion-target"],
            ScenarioValidator.Validate(Model(), scenario).Select(i => i.Code).Distinct().Order());
    }

    [Fact]
    public void A_contract_stored_under_another_id_is_rejected()
    {
        var model = Model();
        model = model with
        {
            Subscriptions = new Dictionary<SubscriptionId, SubscriptionContract>
            {
                [InventorySub] = model.Subscriptions[InventorySub] with { Id = G(901) },
            },
        };

        Assert.Contains(ScenarioValidator.Validate(model, Scenario([PublishCreated()])), i => i.Code == "invalid-model");
    }

    [Fact]
    public void The_hash_covers_headers_and_message_and_ignores_payload_key_order()
    {
        PublishStep With(string payload, string header) =>
            new("p", Orders, OrderEvents, OrderCreated, Json(payload), new Dictionary<string, string> { ["x-tenant"] = header },
                new Dictionary<SubscriptionId, IReadOnlyList<Step>>());
        string HashOf(PublishStep step) => TraceEngine.Run(Model(), Scenario([step])).Hash;

        var original = HashOf(With("""{"orderId":"o-1","note":"a"}""", "acme"));

        Assert.Equal(original, HashOf(With("""{"note":"a","orderId":"o-1"}""", "acme")));
        Assert.NotEqual(original, HashOf(With("""{"orderId":"o-1","note":"a"}""", "globex")));
        Assert.NotEqual(original, HashOf(With("""{"orderId":"o-2","note":"a"}""", "acme")));

        var published = TraceEngine.Run(Model(), Scenario([With("""{"orderId":"o-1"}""", "acme")])).Events[0];
        Assert.Equal(OrderCreated.Value, published.Message);
        Assert.Equal("acme", published.Headers!["x-tenant"]);
    }
}
