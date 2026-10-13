namespace Vellum.Modules.Modelling.Model;

/// <summary>
/// Sets or clears the stale mark of one fact. The mark travels on the fact's provenance
/// event, with the status and the provenance the fact already has, so the decision changes
/// nothing else: a stale fact keeps its values, its status and its acceptance stamps.
/// </summary>
public static class StaleDecider
{
    public const string EvidenceNotFound = "evidence-not-found";

    /// <summary>
    /// The event that gives the fact <paramref name="id"/> the mark <paramref name="mark"/>
    /// (null clears it). Empty when no fact has the ID or the fact already has that mark.
    /// </summary>
    public static IReadOnlyList<object> Mark(ModelState state, Guid id, StaleMark? mark)
    {
        if (state.Elements.TryGetValue(id, out var element))
            return element.Stale == mark ? [] : [new ModelEvent.ElementProvenanceRecorded(element.Id, element.FactStatus, element.Provenance, mark)];
        if (state.Relationships.TryGetValue(id, out var relationship))
            return relationship.Stale == mark ? [] : [new ModelEvent.RelationshipProvenanceRecorded(relationship.Id, relationship.FactStatus, relationship.Provenance, mark)];
        if (state.Messages.TryGetValue(id, out var message))
            return message.Stale == mark ? [] : [new ModelEvent.MessageProvenanceRecorded(message.Id, message.FactStatus, message.Provenance, mark)];
        if (state.Operations.TryGetValue(id, out var operation))
            return operation.Stale == mark ? [] : [new ModelEvent.OperationProvenanceRecorded(operation.Id, operation.FactStatus, operation.Provenance, mark)];
        if (state.Channels.TryGetValue(id, out var channel))
            return channel.Stale == mark ? [] : [new ModelEvent.ChannelProvenanceRecorded(channel.Id, channel.FactStatus, channel.Provenance, mark)];
        if (state.Subscriptions.TryGetValue(id, out var subscription))
            return subscription.Stale == mark ? [] : [new ModelEvent.SubscriptionProvenanceRecorded(subscription.Id, subscription.FactStatus, subscription.Provenance, mark)];
        return [];
    }
}
