using Deedbox.Testing;
using Vellum.Modules.Modelling;
using Vellum.Modules.Trace.Engine;
using Vellum.Shared;

namespace Vellum.Tests.Modules.Modelling;

/// <summary>The stale mark in <see cref="ModelState.Evolve"/> and <see cref="StaleDecider"/>, with no database.</summary>
public class StaleMarkRuleTests
{
    private static readonly ElementId Shop = G(1), Orders = G(2), Inventory = G(3);
    private static readonly RelationshipId Reserves = G(10);
    private static readonly MessageId OrderPlaced = G(20);
    private static readonly OperationId PlaceOrder = G(30);
    private static readonly ChannelId OrderEvents = G(40), DeadLetters = G(41);
    private static readonly SubscriptionId InventorySub = G(50);
    private static readonly StaleMark Mark = new("4f2a9c1", StaleDecider.EvidenceNotFound);

    private static Guid G(int n) => new($"00000000-0000-0000-0000-{n:D12}");

    private static readonly Guid[] Facts =
        [Orders.Value, Reserves.Value, OrderPlaced.Value, PlaceOrder.Value, OrderEvents.Value, InventorySub.Value];

    private static readonly object[] Unmarked =
    [
        new ModelEvent.ElementAdded(Shop, ElementKind.System, "Shop", null, null, null, ElementStatus.Current, null, []),
        new ModelEvent.ElementAdded(Orders, ElementKind.App, "Orders", null, null, null, ElementStatus.Current, Shop, []),
        new ModelEvent.ElementAdded(Inventory, ElementKind.App, "Inventory", null, null, null, ElementStatus.Current, Shop, []),
        new ModelEvent.RelationshipAdded(Reserves, Orders, Inventory, "reserves", null, null),
        new ModelEvent.MessageAdded(OrderPlaced, "OrderPlaced", null, Orders, [Inventory], null, []),
        new ModelEvent.OperationAdded(PlaceOrder, Orders, "POST /orders", Transport.Http, null, null, 2000, RetryPolicy.None),
        new ModelEvent.ChannelAdded(OrderEvents, "order-events", ChannelKind.Topic),
        new ModelEvent.ChannelAdded(DeadLetters, "order-events.dlq", ChannelKind.Queue),
        new ModelEvent.SubscriptionAdded(InventorySub, OrderEvents, Inventory, RetryPolicy.None, null),
    ];

    private static readonly object[] Marked =
    [
        .. Unmarked,
        new ModelEvent.ElementProvenanceRecorded(Orders, FactStatus.Declared, null, Mark),
        new ModelEvent.RelationshipProvenanceRecorded(Reserves, FactStatus.Declared, null, Mark),
        new ModelEvent.MessageProvenanceRecorded(OrderPlaced, FactStatus.Declared, null, Mark),
        new ModelEvent.OperationProvenanceRecorded(PlaceOrder, FactStatus.Declared, null, Mark),
        new ModelEvent.ChannelProvenanceRecorded(OrderEvents, FactStatus.Declared, null, Mark),
        new ModelEvent.SubscriptionProvenanceRecorded(InventorySub, FactStatus.Declared, null, Mark),
    ];

    private static StaleMark?[] Marks(ModelState state) =>
    [
        state.Elements[Orders].Stale, state.Relationships[Reserves].Stale, state.Messages[OrderPlaced].Stale,
        state.Operations[PlaceOrder].Stale, state.Channels[OrderEvents].Stale, state.Subscriptions[InventorySub].Stale,
    ];

    [Fact]
    public void No_edit_event_clears_a_mark()
    {
        object[] edits =
        [
            new ModelEvent.ElementRenamed(Orders, "Order Service"),
            new ModelEvent.ElementDescriptionChanged(Orders, "Takes orders"),
            new ModelEvent.ElementTechnologyChanged(Orders, "dotnet"),
            new ModelEvent.ElementOwnerChanged(Orders, G(99)),
            new ModelEvent.ElementReparented(Orders, null),
            new ModelEvent.ElementStatusChanged(Orders, ElementStatus.Deprecated),
            new ModelEvent.ElementRetagged(Orders, ["core"]),
            new ModelEvent.ElementIconChanged(Orders, "box"),
            new ModelEvent.RelationshipLabelChanged(Reserves, "reserves stock"),
            new ModelEvent.RelationshipTechnologyChanged(Reserves, "grpc"),
            new ModelEvent.RelationshipLineShapeChanged(Reserves, "step"),
            new ModelEvent.RelationshipKindChanged(Reserves, RelationshipKind.Behavioural),
            new ModelEvent.MessageUpdated(OrderPlaced, "OrderPlacedV2", "An order", Inventory, [Orders], null, true, ["event"]),
            new ModelEvent.OperationChanged(PlaceOrder, Inventory, "POST /v2/orders", Transport.Grpc, null, null, 500, new RetryPolicy(3, 250)),
            new ModelEvent.ChannelChanged(OrderEvents, "orders.events", ChannelKind.Queue),
            new ModelEvent.SubscriptionChanged(InventorySub, OrderEvents, Orders, new RetryPolicy(5, 10), DeadLetters),
        ];

        var state = Decider.Given<ModelState>([.. Marked, .. edits]).State;

        Assert.Equal("Order Service", state.Elements[Orders].Name);
        Assert.Equal(500, state.Operations[PlaceOrder].TimeoutMs);
        Assert.All(Marks(state), mark => Assert.Equal(Mark, mark));
    }

    [Fact]
    public void A_mark_on_a_marked_fact_appends_nothing_whatever_revision_it_names()
    {
        var state = Decider.Given<ModelState>(Marked).State;
        var later = new StaleMark("9d0e7b3", StaleDecider.EvidenceNotFound);
        foreach (var id in Facts)
        {
            Assert.Empty(StaleDecider.Mark(state, id, later));
            Assert.Empty(StaleDecider.Mark(state, id, Mark));
        }
    }

    [Fact]
    public void A_clear_appends_one_event_for_a_marked_fact_and_nothing_for_an_unmarked_one()
    {
        var unmarked = Decider.Given<ModelState>(Unmarked).State;
        var marked = Decider.Given<ModelState>(Marked).State;
        foreach (var id in Facts)
        {
            Assert.Empty(StaleDecider.Mark(unmarked, id, null));

            var cleared = StaleDecider.Mark(marked, id, null);
            Assert.Single(cleared);
            Assert.Equal(5, Marks(cleared.Aggregate(marked, ModelState.Evolve)).Count(mark => mark == Mark));
        }
    }

    [Fact]
    public void Recording_the_claim_a_marked_fact_already_has_clears_the_mark()
    {
        var state = Decider.Given<ModelState>(Marked).State;
        IReadOnlyList<object>[] decisions =
        [
            Events(ModelDecider.RecordElementProvenance(state, Orders, FactStatus.Declared, null)),
            Events(ModelDecider.RecordRelationshipProvenance(state, Reserves, FactStatus.Declared, null)),
            Events(ModelDecider.RecordMessageProvenance(state, OrderPlaced, FactStatus.Declared, null)),
            Events(ContractDecider.RecordOperationProvenance(state, PlaceOrder, FactStatus.Declared, null)),
            Events(ContractDecider.RecordChannelProvenance(state, OrderEvents, FactStatus.Declared, null)),
            Events(ContractDecider.RecordSubscriptionProvenance(state, InventorySub, FactStatus.Declared, null)),
        ];

        Assert.All(decisions, events => Assert.Single(events));
        var after = decisions.SelectMany(events => events).Aggregate(state, ModelState.Evolve);
        Assert.All(Marks(after), Assert.Null);
    }

    private static IReadOnlyList<object> Events(CommandResult<IReadOnlyList<object>> decision) =>
        Assert.IsType<CommandResult<IReadOnlyList<object>>.Success>(decision).Value;
}
