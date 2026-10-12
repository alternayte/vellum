using Vellum.Modules.Modelling.Model;

namespace Vellum.Modules.Drafts.Merge;

/// <summary>
/// The check a decider gives every other write. A merge builds its events from a comparison
/// of three models, not from a decision on main, and <see cref="ModelState.Evolve"/> skips an
/// event that does not fit. So the events are applied to main here first, one by one.
/// </summary>
public static class MergedModel
{
    /// <summary>
    /// Null when every event fits main as it claims (an add of something absent, a change or
    /// removal of something present) and the result points at nothing that main did not
    /// already point at. Otherwise the reason, and the merge must write nothing.
    /// </summary>
    public static string? Refusal(ModelState main, IReadOnlyList<object> events)
    {
        var merged = main;
        foreach (var @event in events)
        {
            if (Misfit(merged, @event) is { } misfit)
                return $"The merge would write a change that main cannot take: {misfit}. Change the resolutions or the draft.";

            merged = ModelState.Evolve(merged, @event);
        }

        var dangling = DanglingReferences(merged).Except(DanglingReferences(main)).ToList();
        return dangling.Count == 0
            ? null
            : $"The merge would leave a dangling reference: {string.Join("; ", dangling)}. Change the resolutions or the draft.";
    }

    /// <summary>Every reference in <paramref name="state"/> that points at nothing.</summary>
    public static IReadOnlyList<string> DanglingReferences(ModelState state)
    {
        var problems = new List<string>();

        foreach (var element in state.Elements.Values.OrderBy(e => e.Id.Value))
        {
            if (element.ParentId is { } parent && !state.Elements.ContainsKey(parent))
                problems.Add($"element '{element.Name}' ({element.Id}) is inside element {parent}, which does not exist");
        }

        foreach (var relationship in state.Relationships.Values.OrderBy(r => r.Id.Value))
        {
            if (!state.Elements.ContainsKey(relationship.FromId))
                problems.Add($"relationship {relationship.Id} starts at element {relationship.FromId}, which does not exist");
            if (!state.Elements.ContainsKey(relationship.ToId))
                problems.Add($"relationship {relationship.Id} ends at element {relationship.ToId}, which does not exist");
            if (relationship.MessageId is { } message && !state.Messages.ContainsKey(message))
                problems.Add($"relationship {relationship.Id} carries message {message}, which does not exist");
        }

        foreach (var message in state.Messages.Values.OrderBy(m => m.Id.Value))
        {
            if (!state.Elements.ContainsKey(message.ProducerId))
                problems.Add($"message '{message.Name}' ({message.Id}) has producer {message.ProducerId}, which does not exist");
            foreach (var consumer in message.ConsumerIds.Where(c => !state.Elements.ContainsKey(c)))
                problems.Add($"message '{message.Name}' ({message.Id}) has consumer {consumer}, which does not exist");
        }

        problems.AddRange(ContractDecider.DanglingReferences(state));
        return problems;
    }

    private static string? Misfit(ModelState state, object @event) => @event switch
    {
        ModelEvent.ElementAdded e => Absent(state.Elements.ContainsKey(e.Id), "element", e.Id.Value),
        ModelEvent.ElementRenamed e => Present(state.Elements.ContainsKey(e.ElementId), "element", e.ElementId.Value),
        ModelEvent.ElementDescriptionChanged e => Present(state.Elements.ContainsKey(e.ElementId), "element", e.ElementId.Value),
        ModelEvent.ElementTechnologyChanged e => Present(state.Elements.ContainsKey(e.ElementId), "element", e.ElementId.Value),
        ModelEvent.ElementOwnerChanged e => Present(state.Elements.ContainsKey(e.ElementId), "element", e.ElementId.Value),
        ModelEvent.ElementReparented e => Present(state.Elements.ContainsKey(e.ElementId), "element", e.ElementId.Value),
        ModelEvent.ElementStatusChanged e => Present(state.Elements.ContainsKey(e.ElementId), "element", e.ElementId.Value),
        ModelEvent.ElementRetagged e => Present(state.Elements.ContainsKey(e.ElementId), "element", e.ElementId.Value),
        ModelEvent.ElementIconChanged e => Present(state.Elements.ContainsKey(e.ElementId), "element", e.ElementId.Value),
        ModelEvent.ElementProvenanceRecorded e => Present(state.Elements.ContainsKey(e.ElementId), "element", e.ElementId.Value),
        ModelEvent.ElementRemoved e => Present(state.Elements.ContainsKey(e.ElementId), "element", e.ElementId.Value),

        ModelEvent.RelationshipAdded e => Absent(state.Relationships.ContainsKey(e.Id), "relationship", e.Id.Value),
        ModelEvent.RelationshipLabelChanged e => Present(state.Relationships.ContainsKey(e.RelationshipId), "relationship", e.RelationshipId.Value),
        ModelEvent.RelationshipTechnologyChanged e => Present(state.Relationships.ContainsKey(e.RelationshipId), "relationship", e.RelationshipId.Value),
        ModelEvent.RelationshipLineShapeChanged e => Present(state.Relationships.ContainsKey(e.RelationshipId), "relationship", e.RelationshipId.Value),
        ModelEvent.RelationshipKindChanged e => Present(state.Relationships.ContainsKey(e.RelationshipId), "relationship", e.RelationshipId.Value),
        ModelEvent.RelationshipProvenanceRecorded e => Present(state.Relationships.ContainsKey(e.RelationshipId), "relationship", e.RelationshipId.Value),
        ModelEvent.RelationshipRemoved e => Present(state.Relationships.ContainsKey(e.RelationshipId), "relationship", e.RelationshipId.Value),

        ModelEvent.MessageAdded e => Absent(state.Messages.ContainsKey(e.Id), "message", e.Id.Value),
        ModelEvent.MessageUpdated e => Present(state.Messages.ContainsKey(e.MessageId), "message", e.MessageId.Value),
        ModelEvent.MessageProvenanceRecorded e => Present(state.Messages.ContainsKey(e.MessageId), "message", e.MessageId.Value),
        ModelEvent.MessageRemoved e => Present(state.Messages.ContainsKey(e.MessageId), "message", e.MessageId.Value),

        ModelEvent.OperationAdded e => Absent(state.Operations.ContainsKey(e.Id), "operation", e.Id.Value),
        ModelEvent.OperationChanged e => Present(state.Operations.ContainsKey(e.OperationId), "operation", e.OperationId.Value),
        ModelEvent.OperationProvenanceRecorded e => Present(state.Operations.ContainsKey(e.OperationId), "operation", e.OperationId.Value),
        ModelEvent.OperationRemoved e => Present(state.Operations.ContainsKey(e.OperationId), "operation", e.OperationId.Value),

        ModelEvent.ChannelAdded e => Absent(state.Channels.ContainsKey(e.Id), "channel", e.Id.Value),
        ModelEvent.ChannelChanged e => Present(state.Channels.ContainsKey(e.ChannelId), "channel", e.ChannelId.Value),
        ModelEvent.ChannelProvenanceRecorded e => Present(state.Channels.ContainsKey(e.ChannelId), "channel", e.ChannelId.Value),
        ModelEvent.ChannelRemoved e => Present(state.Channels.ContainsKey(e.ChannelId), "channel", e.ChannelId.Value),

        ModelEvent.SubscriptionAdded e => Absent(state.Subscriptions.ContainsKey(e.Id), "subscription", e.Id.Value),
        ModelEvent.SubscriptionChanged e => Present(state.Subscriptions.ContainsKey(e.SubscriptionId), "subscription", e.SubscriptionId.Value),
        ModelEvent.SubscriptionProvenanceRecorded e => Present(state.Subscriptions.ContainsKey(e.SubscriptionId), "subscription", e.SubscriptionId.Value),
        ModelEvent.SubscriptionRemoved e => Present(state.Subscriptions.ContainsKey(e.SubscriptionId), "subscription", e.SubscriptionId.Value),

        // A new kind of merge event must be given its rule here before a merge may write it.
        _ => throw new InvalidOperationException($"A merge has no rule for the event {@event.GetType().Name}."),
    };

    private static string? Absent(bool exists, string kind, Guid id) =>
        exists ? $"it adds {kind} {id}, which main already has" : null;

    private static string? Present(bool exists, string kind, Guid id) =>
        exists ? null : $"it changes {kind} {id}, which main does not have";
}
