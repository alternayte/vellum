using System.Collections.Immutable;
using Vellum.Modules.Drafts.Merge;
using Vellum.Modules.Modelling.Model;
using Vellum.Modules.Trace.Engine;

namespace Vellum.Tests.Modules.Drafts;

/// <summary>
/// Operations, channels and subscriptions merge like every other model fact, and a merge
/// never resolves on its own to a contract that points at nothing.
/// </summary>
public class ContractMergeTests
{
    private static readonly ElementId Orders = G(1), Inventory = G(2);
    private static readonly OperationId PlaceOrder = G(10);
    private static readonly ChannelId OrderEvents = G(20), DeadLetters = G(21);
    private static readonly SubscriptionId InventorySub = G(30);

    private static Guid G(int n) => new($"00000000-0000-0000-0000-{n:D12}");

    private static ElementState Element(ElementId id, string name) =>
        new(id, ElementKind.System, name, null, null, null, ElementStatus.Current, null, [], Icon: null);

    private static OperationState Operation(int timeoutMs = 1000) =>
        new(PlaceOrder, Orders, "POST /orders", Transport.Http, null, null, timeoutMs, RetryPolicy.None);

    private static ChannelState Channel(ChannelId id, string name) => new(id, name, ChannelKind.Topic);

    private static SubscriptionState Subscription(ChannelId? deadLetter = null) =>
        new(InventorySub, OrderEvents, Inventory, new RetryPolicy(2, 100), deadLetter);

    private static ModelState State(
        ElementState[]? elements = null, OperationState[]? operations = null,
        ChannelState[]? channels = null, SubscriptionState[]? subscriptions = null) =>
        ModelState.Initial with
        {
            Elements = (elements ?? []).ToImmutableDictionary(e => e.Id),
            Operations = (operations ?? []).ToImmutableDictionary(o => o.Id),
            Channels = (channels ?? []).ToImmutableDictionary(c => c.Id),
            Subscriptions = (subscriptions ?? []).ToImmutableDictionary(s => s.Id),
        };

    private static readonly ElementState[] BothElements = [Element(Orders, "Orders"), Element(Inventory, "Inventory")];

    [Fact]
    public void Contracts_added_on_the_draft_are_taken()
    {
        var main = State(BothElements);
        var draft = State(BothElements, [Operation()], [Channel(OrderEvents, "order-events")], [Subscription()]);

        var result = ThreeWayMerge.Compute(main, main, draft);

        Assert.Empty(result.Conflicts);
        Assert.Equal(
            ["channel:added", "operation:added", "subscription:added"],
            result.AutoResolved.Select(c => $"{c.EntityType}:{c.ChangeKind}").Order());
    }

    [Fact]
    public void A_contract_changed_on_one_side_is_taken_and_changed_on_both_sides_conflicts()
    {
        var start = State(BothElements, [Operation(1000)]);

        var oneSide = ThreeWayMerge.Compute(start, start, State(BothElements, [Operation(2000)]));
        var change = Assert.Single(oneSide.AutoResolved);
        Assert.Equal(("operation", "modified"), (change.EntityType, change.ChangeKind));
        Assert.Equal(2000, ((OperationState)change.ResolvedValue!).TimeoutMs);

        var bothSides = ThreeWayMerge.Compute(start, State(BothElements, [Operation(500)]), State(BothElements, [Operation(2000)]));
        Assert.Equal("both_modified", Assert.Single(bothSides.Conflicts).ConflictKind);
        Assert.Empty(bothSides.AutoResolved);
    }

    [Fact]
    public void A_contract_removed_on_the_draft_is_removed()
    {
        var start = State(BothElements, [Operation()]);

        var result = ThreeWayMerge.Compute(start, start, State(BothElements));

        var change = Assert.Single(result.AutoResolved);
        Assert.Equal(("operation", "removed"), (change.EntityType, change.ChangeKind));
    }

    [Fact]
    public void An_operation_from_the_draft_whose_owner_main_removed_is_a_conflict()
    {
        var start = State(BothElements);
        var main = State([Element(Inventory, "Inventory")]);
        var draft = State(BothElements, [Operation()]);

        var result = ThreeWayMerge.Compute(start, main, draft);

        var conflict = Assert.Single(result.Conflicts);
        Assert.Equal(("operation", "dangling_operation"), (conflict.EntityType, conflict.ConflictKind));
        Assert.DoesNotContain(result.AutoResolved, c => c.EntityType == "operation");
    }

    [Fact]
    public void A_subscription_from_the_draft_whose_channel_or_dead_letter_channel_main_lacks_is_a_conflict()
    {
        var channels = new[] { Channel(OrderEvents, "order-events"), Channel(DeadLetters, "dlq") };
        var start = State(BothElements, channels: channels);
        var draft = State(BothElements, channels: channels, subscriptions: [Subscription(DeadLetters)]);

        var withoutDeadLetter = ThreeWayMerge.Compute(start, State(BothElements, channels: [channels[0]]), draft);
        Assert.Equal("dangling_subscription", Assert.Single(withoutDeadLetter.Conflicts).ConflictKind);

        var withoutChannel = ThreeWayMerge.Compute(start, State(BothElements, channels: [channels[1]]), draft);
        Assert.Equal("dangling_subscription", Assert.Single(withoutChannel.Conflicts).ConflictKind);
    }

    [Fact]
    public void A_subscription_that_arrives_with_its_new_channel_merges_cleanly()
    {
        var start = State(BothElements);
        var draft = State(BothElements, channels: [Channel(OrderEvents, "order-events")], subscriptions: [Subscription()]);

        var result = ThreeWayMerge.Compute(start, start, draft);

        Assert.Empty(result.Conflicts);
        Assert.Equal(2, result.AutoResolved.Count);
    }

    [Fact]
    public void Removing_on_the_draft_an_element_that_a_contract_on_main_now_uses_is_a_conflict()
    {
        var start = State(BothElements);
        var main = State(BothElements, [Operation()]);
        var draft = State([Element(Inventory, "Inventory")]);

        var result = ThreeWayMerge.Compute(start, main, draft);

        var conflict = Assert.Single(result.Conflicts);
        Assert.Equal(("element", Orders.Value, "referenced_by_contract"), (conflict.EntityType, conflict.EntityId, conflict.ConflictKind));
        Assert.DoesNotContain(result.AutoResolved, c => c.ChangeKind == "removed");
    }

    [Fact]
    public void Removing_on_the_draft_a_channel_that_a_subscription_on_main_now_uses_is_a_conflict()
    {
        var channels = new[] { Channel(OrderEvents, "order-events") };
        var start = State(BothElements, channels: channels);
        var main = State(BothElements, channels: channels, subscriptions: [Subscription()]);
        var draft = State(BothElements);

        var result = ThreeWayMerge.Compute(start, main, draft);

        var conflict = Assert.Single(result.Conflicts);
        Assert.Equal(("channel", "referenced_by_contract"), (conflict.EntityType, conflict.ConflictKind));
    }

    [Fact]
    public void A_merged_model_reports_every_reference_that_points_at_nothing()
    {
        var broken = State(
            [Element(Inventory, "Inventory")],
            [Operation()],
            [Channel(OrderEvents, "order-events")],
            [Subscription(DeadLetters)]);

        var problems = ModelInvariants.Check(broken);

        Assert.Equal(2, problems.Count);
        Assert.Contains(problems, p => p.Contains("operation 'POST /orders'") && p.Contains(Orders.ToString()));
        Assert.Contains(problems, p => p.Contains("Dead-letter channel") && p.Contains(DeadLetters.ToString()));
        Assert.Empty(ModelInvariants.Check(State(BothElements, [Operation()])));
    }
}
