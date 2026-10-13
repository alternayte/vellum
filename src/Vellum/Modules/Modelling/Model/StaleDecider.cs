namespace Vellum.Modules.Modelling.Model;

/// <summary>
/// Sets or clears the stale mark of one fact. The mark travels on the fact's provenance
/// event, with the status and the provenance the fact already has, so the decision changes
/// nothing else: a stale fact keeps its values, its status and its acceptance stamps.
/// <para>
/// Three writers clear a mark: a scan, through <see cref="Mark"/>; a person who accepts an
/// import proposal for the fact; and a person who states the fact's status or provenance
/// through the API. The last two go through the <c>Record…Provenance</c> decisions, whose
/// event carries no mark. No edit of a value clears a mark, and no draft changes a mark on
/// main.
/// </para>
/// </summary>
public static class StaleDecider
{
    public const string EvidenceNotFound = "evidence-not-found";

    /// <summary>
    /// The event that gives the fact <paramref name="id"/> the mark <paramref name="mark"/>
    /// (null clears it). Empty when no fact has the ID, when the fact already has a mark and
    /// one is asked for, and when it has none and none is asked for. A fact that is already
    /// marked keeps its mark, which names the first scan that did not find the evidence.
    /// </summary>
    public static IReadOnlyList<object> Mark(ModelState state, Guid id, StaleMark? mark) =>
        Find(state, id) is { } fact && (fact.Stale is null) != (mark is null)
            ? [fact.Record(fact.Status, fact.Provenance, mark)]
            : [];

    /// <summary>
    /// The event that gives the fact <paramref name="id"/> a status and a provenance and
    /// leaves its mark, for a writer that is neither a scan nor a person's statement about
    /// this one fact (a model import). A fact without a mark gets
    /// <paramref name="markIfNone"/>. Empty when no fact has the ID or nothing changes; the
    /// acceptance stamps alone are no change.
    /// </summary>
    public static IReadOnlyList<object> Relabel(
        ModelState state, Guid id, FactStatus status, Provenance? provenance, StaleMark? markIfNone)
    {
        if (Find(state, id) is not { } fact)
            return [];

        var mark = fact.Stale ?? markIfNone;
        var sameClaim = fact.Status == status
            && fact.Provenance?.Source == provenance?.Source
            && fact.Provenance?.Revision == provenance?.Revision
            && fact.Provenance?.Confidence == provenance?.Confidence;

        return sameClaim && mark == fact.Stale ? [] : [fact.Record(status, provenance, mark)];
    }

    /// <summary>The truth label of one fact of any kind, and the event that restates it.</summary>
    private sealed record Label(
        FactStatus Status, Provenance? Provenance, StaleMark? Stale,
        Func<FactStatus, Provenance?, StaleMark?, object> Record);

    private static Label? Find(ModelState state, Guid id)
    {
        if (state.Elements.TryGetValue(id, out var element))
            return new(element.FactStatus, element.Provenance, element.Stale,
                (status, provenance, mark) => new ModelEvent.ElementProvenanceRecorded(element.Id, status, provenance, mark));
        if (state.Relationships.TryGetValue(id, out var relationship))
            return new(relationship.FactStatus, relationship.Provenance, relationship.Stale,
                (status, provenance, mark) => new ModelEvent.RelationshipProvenanceRecorded(relationship.Id, status, provenance, mark));
        if (state.Messages.TryGetValue(id, out var message))
            return new(message.FactStatus, message.Provenance, message.Stale,
                (status, provenance, mark) => new ModelEvent.MessageProvenanceRecorded(message.Id, status, provenance, mark));
        if (state.Operations.TryGetValue(id, out var operation))
            return new(operation.FactStatus, operation.Provenance, operation.Stale,
                (status, provenance, mark) => new ModelEvent.OperationProvenanceRecorded(operation.Id, status, provenance, mark));
        if (state.Channels.TryGetValue(id, out var channel))
            return new(channel.FactStatus, channel.Provenance, channel.Stale,
                (status, provenance, mark) => new ModelEvent.ChannelProvenanceRecorded(channel.Id, status, provenance, mark));
        if (state.Subscriptions.TryGetValue(id, out var subscription))
            return new(subscription.FactStatus, subscription.Provenance, subscription.Stale,
                (status, provenance, mark) => new ModelEvent.SubscriptionProvenanceRecorded(subscription.Id, status, provenance, mark));
        if (state.Bindings.TryGetValue(id, out var binding))
            return new(binding.FactStatus, binding.Provenance, binding.Stale,
                (status, provenance, mark) => new ModelEvent.BindingProvenanceRecorded(binding.Id, status, provenance, mark));
        return null;
    }
}
