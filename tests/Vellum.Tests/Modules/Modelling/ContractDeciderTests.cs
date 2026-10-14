using Deedbox.Testing;
using Vellum.Modules.Modelling;
using Vellum.Modules.Trace.Engine;
using Vellum.Shared;
using Decision = Vellum.Shared.CommandResult<System.Collections.Generic.IReadOnlyList<object>>;

namespace Vellum.Tests.Modules.Modelling;

public class ContractDeciderTests
{
    private static readonly ElementId Shop = G(1), Orders = G(2), Inventory = G(3);
    private static readonly OperationId PlaceOrder = G(10);
    private static readonly ChannelId OrderEvents = G(20), DeadLetters = G(21);
    private static readonly SubscriptionId InventorySub = G(30);

    private static Guid G(int n) => new($"00000000-0000-0000-0000-{n:D12}");

    /// <summary>A system with two apps, one operation, two channels and one subscription with a dead-letter channel.</summary>
    private static ModelState Model() => Decider.Given<ModelState>(
        new ModelEvent.ElementAdded(Shop, ElementKind.System, "Shop", null, null, null, ElementStatus.Current, null, []),
        new ModelEvent.ElementAdded(Orders, ElementKind.App, "Orders", null, null, null, ElementStatus.Current, Shop, []),
        new ModelEvent.ElementAdded(Inventory, ElementKind.App, "Inventory", null, null, null, ElementStatus.Current, Shop, []),
        new ModelEvent.OperationAdded(PlaceOrder, Orders, "POST /orders", Transport.Http, null, null, 2000, RetryPolicy.None),
        new ModelEvent.ChannelAdded(OrderEvents, "order-events", ChannelKind.Topic),
        new ModelEvent.ChannelAdded(DeadLetters, "order-events.dlq", ChannelKind.Queue),
        new ModelEvent.SubscriptionAdded(InventorySub, OrderEvents, Inventory, new RetryPolicy(3, 100), DeadLetters)).State;

    private static AddOperationCommand NewOperation(
        ElementId? owner = null, string name = "GET /orders", int timeoutMs = 0, RetryPolicy? retry = null, Guid? id = null) =>
        new(id ?? Guid.NewGuid(), owner ?? Orders, name, Transport.Http, null, null, timeoutMs, retry ?? RetryPolicy.None);

    private static string Field(Decision decision) =>
        Assert.Single(Assert.IsType<Decision.Invalid>(decision).Errors).Field;

    [Fact]
    public void An_operation_records_its_whole_contract()
    {
        var command = new AddOperationCommand(G(11), Orders, "Charge", Transport.Grpc, G(50), G(51), 500, new RetryPolicy(2, 50));

        var added = Assert.IsType<ModelEvent.OperationAdded>(Assert.Single(
            Assert.IsType<Decision.Success>(ContractDecider.AddOperation(Model(), command)).Value));

        Assert.Equal(new ModelEvent.OperationAdded(G(11), Orders, "Charge", Transport.Grpc, G(50), G(51), 500, new RetryPolicy(2, 50)), added);
    }

    [Fact]
    public void An_operation_needs_a_name_an_existing_owner_and_bounded_timing()
    {
        var model = Model();

        Assert.Equal("name", Field(ContractDecider.AddOperation(model, NewOperation(name: " "))));
        Assert.Equal("ownerId", Field(ContractDecider.AddOperation(model, NewOperation(owner: G(99)))));
        Assert.Equal("timeoutMs", Field(ContractDecider.AddOperation(model, NewOperation(timeoutMs: -1))));
        Assert.Equal("timeoutMs", Field(ContractDecider.AddOperation(model, NewOperation(timeoutMs: TraceLimits.MaxDurationMs + 1))));
        Assert.Equal("retry.maxAttempts", Field(ContractDecider.AddOperation(model, NewOperation(retry: new RetryPolicy(0, 0)))));
        Assert.Equal("retry.maxAttempts", Field(ContractDecider.AddOperation(model, NewOperation(retry: new RetryPolicy(TraceLimits.MaxAttempts + 1, 0)))));
        Assert.Equal("retry.backoffMs", Field(ContractDecider.AddOperation(model, NewOperation(retry: new RetryPolicy(2, -1)))));
        Assert.Equal("retry.backoffMs", Field(ContractDecider.AddOperation(model, NewOperation(retry: new RetryPolicy(2, TraceLimits.MaxDurationMs + 1)))));

        // The limits themselves are allowed.
        Assert.IsType<Decision.Success>(ContractDecider.AddOperation(model, NewOperation(
            timeoutMs: TraceLimits.MaxDurationMs, retry: new RetryPolicy(TraceLimits.MaxAttempts, TraceLimits.MaxDurationMs))));
    }

    [Fact]
    public void A_contract_id_means_one_thing_in_a_model()
    {
        var model = Model();

        // A scenario names a fault target by ID alone, so no two things share one.
        Assert.IsType<Decision.Conflict>(ContractDecider.AddOperation(model, NewOperation(id: PlaceOrder)));
        Assert.IsType<Decision.Conflict>(ContractDecider.AddOperation(model, NewOperation(id: Orders)));
        Assert.IsType<Decision.Conflict>(ContractDecider.AddChannel(model, new AddChannelCommand(PlaceOrder.Value, "x", ChannelKind.Topic)));
        Assert.IsType<Decision.Conflict>(ContractDecider.AddSubscription(model,
            new AddSubscriptionCommand(OrderEvents.Value, OrderEvents, Inventory, RetryPolicy.None, null)));
    }

    [Fact]
    public void An_operation_update_changes_only_what_it_names_and_revalidates()
    {
        var model = Model();

        var changed = Assert.IsType<ModelEvent.OperationChanged>(Assert.Single(Assert.IsType<Decision.Success>(
            ContractDecider.UpdateOperation(model, new UpdateOperationCommand(PlaceOrder, TimeoutMs: 750, RequestSchemaId: G(50), SetRequestSchemaId: true))).Value));
        Assert.Equal(new ModelEvent.OperationChanged(PlaceOrder, Orders, "POST /orders", Transport.Http, G(50), null, 750, RetryPolicy.None), changed);

        Assert.Empty(Assert.IsType<Decision.Success>(
            ContractDecider.UpdateOperation(model, new UpdateOperationCommand(PlaceOrder, Name: "POST /orders"))).Value);
        Assert.Equal("ownerId", Field(ContractDecider.UpdateOperation(model, new UpdateOperationCommand(PlaceOrder, OwnerId: G(99)))));
        Assert.Equal("retry.maxAttempts", Field(ContractDecider.UpdateOperation(model, new UpdateOperationCommand(PlaceOrder, Retry: new RetryPolicy(11, 0)))));
        Assert.IsType<Decision.NotFound>(ContractDecider.UpdateOperation(model, new UpdateOperationCommand(G(99), Name: "x")));
    }

    [Fact]
    public void A_channel_needs_a_name()
    {
        Assert.Equal("name", Field(ContractDecider.AddChannel(Model(), new AddChannelCommand(Guid.NewGuid(), "", ChannelKind.Queue))));
        Assert.Equal("name", Field(ContractDecider.UpdateChannel(Model(), new UpdateChannelCommand(OrderEvents, Name: " "))));
    }

    [Fact]
    public void A_subscription_needs_its_channel_its_consumer_and_a_different_dead_letter_channel()
    {
        var model = Model();
        AddSubscriptionCommand New(ChannelId? channel = null, ElementId? consumer = null, ChannelId? deadLetter = null, RetryPolicy? retry = null) =>
            new(Guid.NewGuid(), channel ?? OrderEvents, consumer ?? Orders, retry ?? RetryPolicy.None, deadLetter);

        Assert.Equal("channelId", Field(ContractDecider.AddSubscription(model, New(channel: G(99)))));
        Assert.Equal("consumerId", Field(ContractDecider.AddSubscription(model, New(consumer: G(99)))));
        Assert.Equal("deadLetterChannelId", Field(ContractDecider.AddSubscription(model, New(deadLetter: G(99)))));
        Assert.Equal("deadLetterChannelId", Field(ContractDecider.AddSubscription(model, New(deadLetter: OrderEvents))));
        Assert.Equal("retry.maxAttempts", Field(ContractDecider.AddSubscription(model, New(retry: new RetryPolicy(0, 0)))));
        Assert.IsType<Decision.Success>(ContractDecider.AddSubscription(model, New(deadLetter: DeadLetters)));

        // An update is held to the same rules: it cannot move the channel onto its own dead-letter channel.
        Assert.Equal("deadLetterChannelId", Field(ContractDecider.UpdateSubscription(model,
            new UpdateSubscriptionCommand(InventorySub, ChannelId: DeadLetters))));
        var cleared = Assert.IsType<ModelEvent.SubscriptionChanged>(Assert.Single(Assert.IsType<Decision.Success>(
            ContractDecider.UpdateSubscription(model, new UpdateSubscriptionCommand(InventorySub, SetDeadLetterChannelId: true))).Value));
        Assert.Null(cleared.DeadLetterChannelId);
    }

    [Fact]
    public void Removing_a_channel_that_a_subscription_uses_is_refused_and_names_the_subscription()
    {
        var model = Model();

        var listened = Assert.IsType<Decision.Conflict>(ContractDecider.RemoveChannel(model, OrderEvents));
        Assert.Contains("Inventory on order-events", listened.Message);
        Assert.Contains(InventorySub.ToString(), listened.Message);

        // The dead-letter reference counts too.
        Assert.IsType<Decision.Conflict>(ContractDecider.RemoveChannel(model, DeadLetters));

        var free = ModelState.Evolve(model, new ModelEvent.SubscriptionRemoved(InventorySub));
        Assert.IsType<ModelEvent.ChannelRemoved>(Assert.Single(
            Assert.IsType<Decision.Success>(ContractDecider.RemoveChannel(free, OrderEvents)).Value));
    }

    [Fact]
    public void Removing_an_element_that_a_contract_references_is_refused_and_names_the_contract()
    {
        var model = Model();

        var owner = Assert.IsType<Decision.Conflict>(ModelDecider.RemoveElement(model, Orders));
        Assert.Contains("operation 'POST /orders'", owner.Message);

        var consumer = Assert.IsType<Decision.Conflict>(ModelDecider.RemoveElement(model, Inventory));
        Assert.Contains("subscription 'Inventory on order-events'", consumer.Message);

        // Removing a parent would remove the referenced children with it.
        var parent = Assert.IsType<Decision.Conflict>(ModelDecider.RemoveElement(model, Shop));
        Assert.Contains("operation 'POST /orders'", parent.Message);
        Assert.Contains("subscription 'Inventory on order-events'", parent.Message);

        var free = new object[] { new ModelEvent.OperationRemoved(PlaceOrder), new ModelEvent.SubscriptionRemoved(InventorySub) }
            .Aggregate(model, ModelState.Evolve);
        Assert.IsType<Decision.Success>(ModelDecider.RemoveElement(free, Shop));
    }

    [Fact]
    public void Contract_facts_follow_the_provenance_rules()
    {
        var model = Model();
        var provenance = new Provenance("repo@abc:src/Orders.cs:1-9", "abc", 0.6);

        Assert.IsType<Decision.Invalid>(ContractDecider.RecordOperationProvenance(model, PlaceOrder, FactStatus.Inferred, null));
        Assert.IsType<Decision.Invalid>(ContractDecider.RecordChannelProvenance(model, OrderEvents, FactStatus.ObservedInSource, null));
        Assert.IsType<Decision.Invalid>(ContractDecider.RecordSubscriptionProvenance(model, InventorySub, FactStatus.Measured, null));

        var recorded = Assert.IsType<ModelEvent.OperationProvenanceRecorded>(Assert.Single(Assert.IsType<Decision.Success>(
            ContractDecider.RecordOperationProvenance(model, PlaceOrder, FactStatus.Inferred, provenance)).Value));
        var after = ModelState.Evolve(model, recorded);
        Assert.Equal(FactStatus.Inferred, after.Operations[PlaceOrder].FactStatus);

        // The same claim again, with a new acceptance stamp, is not a change.
        Assert.Empty(Assert.IsType<Decision.Success>(ContractDecider.RecordOperationProvenance(
            after, PlaceOrder, FactStatus.Inferred, provenance with { AcceptedAt = DateTimeOffset.UnixEpoch })).Value);
    }

    [Fact]
    public void A_fork_carries_contracts_and_a_fork_stored_without_them_has_none()
    {
        var model = Model();

        var forked = ModelState.Evolve(ModelState.Initial, ModelStore.Fork(model, Guid.NewGuid(), 7));
        Assert.Equal(model.Operations[PlaceOrder], forked.Operations[PlaceOrder]);
        Assert.Equal(model.Channels[OrderEvents], forked.Channels[OrderEvents]);
        Assert.Equal(model.Subscriptions[InventorySub], forked.Subscriptions[InventorySub]);

        var older = ModelState.Evolve(ModelState.Initial, new ModelEvent.BranchForked(Guid.NewGuid(), 1, [], [], []));
        Assert.Empty(older.Operations);
        Assert.Empty(older.Channels);
        Assert.Empty(older.Subscriptions);
    }
}
