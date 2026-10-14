using System.Text.Json;
using System.Text.Json.Nodes;
using Deedbox.Testing;
using Vellum.Modules.Ai.Design;
using Vellum.Modules.Modelling;
using Vellum.Modules.Trace.Engine;

namespace Vellum.Tests.Modules.Ai.Design;

/// <summary>Bindings in the three places the assistant meets them: the table it reads, the flow it answers and the items it proposes.</summary>
public class BindingDesignTests
{
    private static readonly ElementId Orders = G(1), Billing = G(2);
    private static readonly ChannelId Exchange = G(20), Queue = G(21), Other = G(22);
    private static readonly SubscriptionId BillingSub = G(30);
    private static readonly BindingId Bound = G(40);
    private static readonly MessageId OrderCreated = G(50);
    private static readonly PrivateGate Open = new(new HashSet<Guid>(), false);

    private static Guid G(int n) => new($"00000000-0000-0000-0000-{n:D12}");

    private static ModelState Model() => Decider.Given<ModelState>(
        new ModelEvent.ElementAdded(Orders, ElementKind.System, "Orders", null, null, null, ElementStatus.Current, null, []),
        new ModelEvent.ElementAdded(Billing, ElementKind.System, "Billing", null, null, null, ElementStatus.Current, null, []),
        new ModelEvent.ChannelAdded(Exchange, "order-events", ChannelKind.Exchange),
        new ModelEvent.ChannelAdded(Queue, "billing.order-created", ChannelKind.Queue),
        new ModelEvent.ChannelAdded(Other, "inventory.order-created", ChannelKind.Queue),
        new ModelEvent.SubscriptionAdded(BillingSub, Queue, Billing, RetryPolicy.None, null),
        new ModelEvent.MessageAdded(OrderCreated, "OrderCreated", null, Orders, [Billing], null, []),
        new ModelEvent.BindingAdded(Bound, Exchange, Queue, "order.created")).State;

    private static ModelAliases Aliases(ModelState model, PrivateGate? gate = null) =>
        ModelAliases.Build(model, new Dictionary<SchemaId, string>(), AliasNeeds.For("orders"), gate ?? Open);

    [Fact]
    public void The_table_lists_a_binding_by_the_aliases_of_its_channels_and_withholds_a_private_one()
    {
        var model = Model();
        var aliases = Aliases(model);

        var listed = Assert.Single(JsonNode.Parse(aliases.Table)!["bindings"]!.AsArray())!;
        Assert.Equal((aliases.AliasOf(Exchange), aliases.AliasOf(Queue), "order.created"),
            ((string?)listed["source"], (string?)listed["target"], (string?)listed["routingKey"]));
        Assert.Null(aliases.AliasOf(Bound));

        var closed = Aliases(model, new PrivateGate(new HashSet<Guid> { Bound }, false));
        Assert.Empty(JsonNode.Parse(closed.Table)!["bindings"]!.AsArray());
        Assert.Equal(1, closed.OmittedPrivateCount);
    }

    [Fact]
    public void A_flow_answer_carries_the_routing_key_of_a_publish_step_and_the_empty_string_means_none()
    {
        var model = Model();
        var aliases = Aliases(model);
        FlowStepOutput Publish(string id, string routingKey) => new(
            id, "", "root", "", 1, "publish", aliases.AliasOf(Orders), "", aliases.AliasOf(Exchange), aliases.AliasOf(OrderCreated),
            "", "", "", "", "", "", "", "", "", routingKey);
        var handler = new FlowStepOutput(
            "invoice", "keyed", "handler", aliases.AliasOf(BillingSub), 1, "write", "", "", "", "",
            aliases.AliasOf(Billing), aliases.AliasOf(Billing), "invoices", "", "", "", "", "", "", "");

        var (output, issues) = FlowAssembler.Normalize(new FlowOutput(
            "Flow", "", [Publish("keyed", "order.created"), Publish("plain", ""), handler], [], [], [], [], "", []));
        var build = FlowAssembler.Build(output, aliases.ByAlias, new Dictionary<string, Guid>(), Guid.NewGuid());

        Assert.Empty(issues);
        Assert.Empty(build.Rejected);
        var steps = build.Scenario.Steps.Cast<PublishStep>().ToDictionary(s => s.Id);
        Assert.Equal(("order.created", null), (steps["keyed"].RoutingKey, steps["plain"].RoutingKey));
        // The answer schema has the member, and a revision shows the key to the model again.
        Assert.Contains(DesignSchemas.Flow.GetProperty("properties").GetProperty("steps").GetProperty("items").GetProperty("required")
            .EnumerateArray(), r => r.GetString() == "routingKey");
        Assert.Equal("order.created", FlowAssembler.Flatten(build.Scenario, id => id.ToString()).Single(s => s.Id == "keyed").RoutingKey);
    }

    private static ArchitectureItemOutput Item(string alias, string kind, string name = "", string? from = null, string? to = null, string? label = null, string? channelKind = null) =>
        new(alias, "add", "", kind, name, "", "", "", "", from ?? "", to ?? "", label ?? "", "", "", "", channelKind ?? "", "", "", "", [], 0.8, "The request names it.");

    [Fact]
    public void An_architecture_proposal_adds_a_binding_between_channels_and_drops_one_that_the_model_cannot_hold()
    {
        var model = Model();
        var aliases = Aliases(model);
        var (exchange, queue, other) = (aliases.AliasOf(Exchange)!, aliases.AliasOf(Queue)!, aliases.AliasOf(Other)!);

        var (items, resolved, rejected) = ArchitectureProposals.Build(new ArchitectureOutput("", [
            Item("N1", "channel", "audit", channelKind: "queue"),
            Item("N2", "binding", from: exchange, to: "N1", label: "order.#"),
            Item("N3", "binding", from: exchange, to: other),
            // The model holds this one already.
            Item("N4", "binding", from: exchange, to: queue, label: "order.created"),
            // A queue routes nothing, a binding needs two channels, and an element is not a channel.
            Item("N5", "binding", from: queue, to: other),
            Item("N6", "binding", from: exchange, to: exchange),
            Item("N7", "binding", from: exchange, to: aliases.AliasOf(Billing)),
        ], [], ""), aliases.ByAlias, model);

        Assert.Equal(["N1", "N2", "N3"], items.Select(i => i.Alias));
        var keyed = items[1];
        Assert.Equal(("binding", "order-events to audit on order.#", Exchange.Value, items[0].Id, "order.#"),
            (keyed.EntityKind, keyed.Name, keyed.Proposed.FromId, keyed.Proposed.ToId, keyed.Proposed.Label));
        Assert.Equal([items[0].Id], keyed.DependsOn);
        Assert.Equal(("order-events to inventory.order-created", null), (items[2].Name, items[2].Proposed.Label));
        Assert.Equal(("N4", Bound.Value), (Assert.Single(resolved).Alias, resolved[0].Id));
        Assert.Equal(["item:N5", "item:N6", "item:N7.to"], rejected.Select(r => r.Where).Order());
    }
}
