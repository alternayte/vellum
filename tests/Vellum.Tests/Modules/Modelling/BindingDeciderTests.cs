using System.Text.Json;
using Deedbox.Testing;
using Vellum.Modules.Drafts.Merge;
using Vellum.Modules.Modelling.Model;
using Vellum.Modules.Trace.Engine;
using Vellum.Shared;
using Decision = Vellum.Shared.CommandResult<System.Collections.Generic.IReadOnlyList<object>>;

namespace Vellum.Tests.Modules.Modelling;

public class BindingDeciderTests
{
    private static readonly ElementId Billing = G(1);
    private static readonly ChannelId Exchange = G(20), Topic = G(21), Queue = G(22), OtherQueue = G(23), Relay = G(24);
    private static readonly SubscriptionId BillingSub = G(30);
    private static readonly BindingId ToQueue = G(40);

    private static Guid G(int n) => new($"00000000-0000-0000-0000-{n:D12}");

    /// <summary>Two exchanges, a topic and two queues, with one binding from the exchange to the first queue.</summary>
    private static ModelState Model() => Decider.Given<ModelState>(
        new ModelEvent.ElementAdded(Billing, ElementKind.System, "Billing", null, null, null, ElementStatus.Current, null, []),
        new ModelEvent.ChannelAdded(Exchange, "order-events", ChannelKind.Exchange),
        new ModelEvent.ChannelAdded(Topic, "audit", ChannelKind.Topic),
        new ModelEvent.ChannelAdded(Queue, "billing.order-created", ChannelKind.Queue),
        new ModelEvent.ChannelAdded(OtherQueue, "inventory.order-created", ChannelKind.Queue),
        new ModelEvent.ChannelAdded(Relay, "relay", ChannelKind.Exchange),
        new ModelEvent.SubscriptionAdded(BillingSub, Queue, Billing, RetryPolicy.None, null),
        new ModelEvent.BindingAdded(ToQueue, Exchange, Queue, null)).State;

    private static AddBindingCommand New(ChannelId source, ChannelId target, string? key = null) =>
        new(Guid.NewGuid(), source, target, key);

    private static string Field(Decision decision) =>
        Assert.Single(Assert.IsType<Decision.Invalid>(decision).Errors).Field;

    [Fact]
    public void A_binding_goes_from_an_exchange_or_a_topic_to_a_queue_or_an_exchange()
    {
        var model = Model();

        Assert.IsType<Decision.Success>(ContractDecider.AddBinding(model, New(Exchange, OtherQueue)));
        Assert.IsType<Decision.Success>(ContractDecider.AddBinding(model, New(Topic, Queue)));
        Assert.IsType<Decision.Success>(ContractDecider.AddBinding(model, New(Exchange, Relay)));

        Assert.Equal("sourceChannelId", Field(ContractDecider.AddBinding(model, New(Queue, OtherQueue))));
        Assert.Equal("targetChannelId", Field(ContractDecider.AddBinding(model, New(Exchange, Topic))));
        Assert.Equal("targetChannelId", Field(ContractDecider.AddBinding(model, New(Exchange, Exchange))));
        Assert.Equal("sourceChannelId", Field(ContractDecider.AddBinding(model, New(G(98), Queue))));
        Assert.Equal("targetChannelId", Field(ContractDecider.AddBinding(model, New(Exchange, G(99)))));
        Assert.Equal("routingKey", Field(ContractDecider.AddBinding(model, New(Exchange, OtherQueue, " "))));
        Assert.Equal("routingKey", Field(ContractDecider.AddBinding(model, New(Exchange, OtherQueue, "a\nb"))));
    }

    [Fact]
    public void A_source_a_target_and_a_routing_key_name_one_binding()
    {
        var model = Model();

        Assert.IsType<Decision.Conflict>(ContractDecider.AddBinding(model, New(Exchange, Queue)));
        // An empty key states no key, so it is the binding that exists.
        Assert.IsType<Decision.Conflict>(ContractDecider.AddBinding(model, New(Exchange, Queue, "")));

        var keyed = Assert.IsType<ModelEvent.BindingAdded>(Assert.Single(
            Assert.IsType<Decision.Success>(ContractDecider.AddBinding(model, New(Exchange, Queue, "order.created"))).Value));
        Assert.Equal("order.created", keyed.RoutingKey);
        model = ModelState.Evolve(model, keyed);
        Assert.IsType<Decision.Conflict>(ContractDecider.AddBinding(model, New(Exchange, Queue, "order.created")));

        // An update cannot turn one binding into a copy of another.
        Assert.IsType<Decision.Conflict>(ContractDecider.UpdateBinding(model, new UpdateBindingCommand(keyed.Id, SetRoutingKey: true)));
        var moved = Assert.IsType<ModelEvent.BindingChanged>(Assert.Single(Assert.IsType<Decision.Success>(
            ContractDecider.UpdateBinding(model, new UpdateBindingCommand(keyed.Id, TargetChannelId: OtherQueue))).Value));
        Assert.Equal((Exchange, OtherQueue, "order.created"), (moved.SourceChannelId, moved.TargetChannelId, moved.RoutingKey));
        Assert.Empty(Assert.IsType<Decision.Success>(ContractDecider.UpdateBinding(model, new UpdateBindingCommand(ToQueue))).Value);
    }

    [Fact]
    public void A_channel_that_a_binding_names_cannot_be_removed_or_take_a_kind_the_binding_does_not_allow()
    {
        var model = Model();

        var source = Assert.IsType<Decision.Conflict>(ContractDecider.RemoveChannel(model, Exchange));
        Assert.Contains($"binding 'order-events to billing.order-created' ({ToQueue})", source.Message);
        Assert.IsType<Decision.Conflict>(ContractDecider.RemoveChannel(model, Queue));

        Assert.IsType<Decision.Conflict>(ContractDecider.UpdateChannel(model, new UpdateChannelCommand(Exchange, Kind: ChannelKind.Queue)));
        Assert.IsType<Decision.Conflict>(ContractDecider.UpdateChannel(model, new UpdateChannelCommand(Queue, Kind: ChannelKind.Topic)));
        // A topic routes as an exchange does, and a rename is no change of kind.
        Assert.IsType<Decision.Success>(ContractDecider.UpdateChannel(model, new UpdateChannelCommand(Exchange, Kind: ChannelKind.Topic)));
        Assert.IsType<Decision.Success>(ContractDecider.UpdateChannel(model, new UpdateChannelCommand(Queue, Name: "billing")));

        // Without the binding and the subscription, the channels are free.
        model = ModelState.Evolve(ModelState.Evolve(model, new ModelEvent.BindingRemoved(ToQueue)), new ModelEvent.SubscriptionRemoved(BillingSub));
        Assert.IsType<Decision.Success>(ContractDecider.RemoveChannel(model, Exchange));
        Assert.IsType<Decision.Success>(ContractDecider.RemoveChannel(model, Queue));
    }

    [Fact]
    public void A_binding_id_means_one_thing_in_a_model_and_carries_a_truth_label()
    {
        var model = Model();

        Assert.IsType<Decision.Conflict>(ContractDecider.AddBinding(model, new AddBindingCommand(Queue.Value, Exchange, OtherQueue, null)));
        Assert.IsType<Decision.Conflict>(ContractDecider.AddChannel(model, new AddChannelCommand(ToQueue.Value, "x", ChannelKind.Topic)));

        var provenance = new Provenance("github.com/acme/orders@abc:worker/consumer.go:46-46", "abc", null, G(7), DateTimeOffset.UnixEpoch);
        var recorded = Assert.Single(Assert.IsType<Decision.Success>(
            ContractDecider.RecordBindingProvenance(model, ToQueue, FactStatus.ObservedInSource, provenance)).Value);
        model = ModelState.Evolve(model, recorded);
        Assert.Equal((FactStatus.ObservedInSource, provenance), (model.Bindings[ToQueue].FactStatus, model.Bindings[ToQueue].Provenance));

        // The stale mark travels on the provenance event, as for every other fact.
        var marked = Assert.IsType<ModelEvent.BindingProvenanceRecorded>(
            Assert.Single(StaleDecider.Mark(model, ToQueue, new StaleMark("def", StaleDecider.EvidenceNotFound))));
        Assert.Equal("def", ModelState.Evolve(model, marked).Bindings[ToQueue].Stale!.SinceRevision);
        Assert.IsType<Decision.Invalid>(ContractDecider.RecordBindingProvenance(model, ToQueue, FactStatus.ObservedInSource, null));
    }

    [Fact]
    public void A_fork_and_a_stored_state_written_before_bindings_existed_read_with_none()
    {
        var before = Model() with { Bindings = Model().Bindings.Clear() };

        var fork = ModelStore.Fork(before, G(90), 7) with { Bindings = null };
        Assert.Empty(ModelState.Evolve(ModelState.Initial, fork).Bindings);
        Assert.Single(ModelState.Evolve(ModelState.Initial, ModelStore.Fork(Model(), G(90), 7)).Bindings);

        // A draft keeps its base as a serialized state. One written before has no such member.
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var stored = JsonSerializer.SerializeToNode(before, options)!.AsObject();
        Assert.True(stored.Remove("bindings"));
        var read = stored.Deserialize<ModelState>(options)!;
        Assert.Empty(read.Bindings);
        Assert.Equal(before.Channels.Keys.OrderBy(c => c.Value), read.Channels.Keys.OrderBy(c => c.Value));
    }

    [Fact]
    public void A_merge_takes_a_binding_from_the_draft_and_refuses_one_that_main_no_longer_allows()
    {
        var @base = Model();
        var added = new ModelEvent.BindingAdded(G(41), Exchange, OtherQueue, "order.*");
        var draft = ModelState.Evolve(@base, added);

        var clean = ThreeWayMerge.Compute(@base, @base, draft);
        Assert.Empty(clean.Conflicts);
        Assert.Equal(("binding", "added"), (Assert.Single(clean.AutoResolved).EntityType, clean.AutoResolved[0].ChangeKind));
        Assert.Null(MergedModel.Refusal(@base, [added]));

        // Main removed the target queue: the binding would point at nothing.
        var withoutQueue = ModelState.Evolve(@base, new ModelEvent.ChannelRemoved(OtherQueue));
        Assert.Equal("dangling_binding", Assert.Single(ThreeWayMerge.Compute(@base, withoutQueue, draft).Conflicts).ConflictKind);

        // The draft removes a channel that a binding on main now names.
        var mainBinds = ModelState.Evolve(@base, new ModelEvent.BindingAdded(G(42), Exchange, Relay, null));
        var draftRemoves = ModelState.Evolve(@base, new ModelEvent.ChannelRemoved(Relay));
        Assert.Equal("referenced_by_contract", Assert.Single(ThreeWayMerge.Compute(@base, mainBinds, draftRemoves).Conflicts).ConflictKind);

        // Main made the source a queue: each branch is valid, the merged model is not.
        var sourceIsQueue = @base with { Bindings = @base.Bindings.Clear() };
        sourceIsQueue = ModelState.Evolve(sourceIsQueue, new ModelEvent.ChannelChanged(Exchange, "order-events", ChannelKind.Queue));
        Assert.Contains("which is a queue", MergedModel.Refusal(sourceIsQueue, [added]));
        // Both branches added the same route under two IDs.
        Assert.Contains("repeats another binding", MergedModel.Refusal(@base, [new ModelEvent.BindingAdded(G(43), Exchange, Queue, null)]));
    }
}
