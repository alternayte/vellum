// src/Vellum/Modules/Drafts/Merge/ThreeWayMerge.cs
using Vellum.Modules.Modelling.Model;

namespace Vellum.Modules.Drafts.Merge;

public static class ThreeWayMerge
{
    public static MergePreviewResult Compute(ModelState baseState, ModelState ours, ModelState theirs)
    {
        var changes = new List<MergeChange>();
        var conflicts = new List<MergeConflict>();

        MergeEntities(baseState.Elements, ours.Elements, theirs.Elements,
            "element", changes, conflicts);
        MergeEntities(baseState.Relationships, ours.Relationships, theirs.Relationships,
            "relationship", changes, conflicts);
        MergeEntities(baseState.Messages, ours.Messages, theirs.Messages,
            "message", changes, conflicts);
        MergeEntities(baseState.Operations, ours.Operations, theirs.Operations,
            "operation", changes, conflicts);
        MergeEntities(baseState.Channels, ours.Channels, theirs.Channels,
            "channel", changes, conflicts);
        MergeEntities(baseState.Subscriptions, ours.Subscriptions, theirs.Subscriptions,
            "subscription", changes, conflicts);

        DetectOrphanedChildren(baseState, ours, theirs, conflicts);
        DetectDanglingRelationships(baseState, ours, theirs, changes, conflicts);
        DetectDanglingMessages(baseState, ours, theirs, changes, conflicts);
        DetectDanglingContracts(baseState, ours, theirs, changes, conflicts);

        return new MergePreviewResult(changes, conflicts);
    }

    private static void MergeEntities<TKey, T>(
        IReadOnlyDictionary<TKey, T> baseEntities,
        IReadOnlyDictionary<TKey, T> oursEntities,
        IReadOnlyDictionary<TKey, T> theirsEntities,
        string entityType,
        List<MergeChange> changes,
        List<MergeConflict> conflicts)
        where TKey : notnull, IArchitectureId
        where T : notnull
    {
        var allIds = baseEntities.Keys
            .Union(oursEntities.Keys)
            .Union(theirsEntities.Keys)
            .ToHashSet();

        foreach (var id in allIds)
        {
            var entityId = id.Value;
            var inBase = baseEntities.TryGetValue(id, out var baseVal);
            var inOurs = oursEntities.TryGetValue(id, out var oursVal);
            var inTheirs = theirsEntities.TryGetValue(id, out var theirsVal);

            if (!inBase && !inOurs && inTheirs)
            {
                changes.Add(new MergeChange(entityType, entityId, "added", theirsVal));
                continue;
            }

            if (!inBase && inOurs && !inTheirs)
            {
                continue;
            }

            if (!inBase && inOurs && inTheirs)
            {
                if (!oursVal!.Equals(theirsVal))
                    conflicts.Add(new MergeConflict(entityType, entityId, "both_modified", null, oursVal, theirsVal));
                continue;
            }

            if (inBase && !inOurs && !inTheirs)
            {
                continue;
            }

            if (inBase && inOurs && !inTheirs)
            {
                var oursChanged = !baseVal!.Equals(oursVal);
                if (oursChanged)
                    conflicts.Add(new MergeConflict(entityType, entityId, "delete_modify", baseVal, oursVal, null));
                else
                    changes.Add(new MergeChange(entityType, entityId, "removed", null));
                continue;
            }

            if (inBase && !inOurs && inTheirs)
            {
                var theirsChanged = !baseVal!.Equals(theirsVal);
                if (theirsChanged)
                    conflicts.Add(new MergeConflict(entityType, entityId, "delete_modify", baseVal, null, theirsVal));
                continue;
            }

            if (inBase && inOurs && inTheirs)
            {
                var oursChanged = !baseVal!.Equals(oursVal);
                var theirsChanged = !baseVal.Equals(theirsVal);

                if (!oursChanged && theirsChanged)
                    changes.Add(new MergeChange(entityType, entityId, "modified", theirsVal));
                else if (oursChanged && theirsChanged && !oursVal!.Equals(theirsVal))
                    conflicts.Add(new MergeConflict(entityType, entityId, "both_modified", baseVal, oursVal, theirsVal));
            }
        }
    }

    private static void DetectOrphanedChildren(
        ModelState baseState, ModelState ours, ModelState theirs,
        List<MergeConflict> conflicts)
    {
        foreach (var (id, baseEl) in baseState.Elements)
        {
            var deletedOnOurs = !ours.Elements.ContainsKey(id);
            var deletedOnTheirs = !theirs.Elements.ContainsKey(id);

            if (deletedOnOurs)
            {
                var childrenModifiedOnTheirs = theirs.Elements.Values
                    .Where(e => e.ParentId == id)
                    .Where(e =>
                    {
                        if (!baseState.Elements.TryGetValue(e.Id, out var baseChild)) return true;
                        return !baseChild.Equals(e);
                    })
                    .ToList();

                foreach (var child in childrenModifiedOnTheirs)
                {
                    if (!conflicts.Any(c => c.EntityId == child.Id.Value))
                    {
                        baseState.Elements.TryGetValue(child.Id, out var baseChild);
                        conflicts.Add(new MergeConflict("element", child.Id, "orphaned_child",
                            baseChild, null, child));
                    }
                }
            }

            if (deletedOnTheirs)
            {
                var childrenModifiedOnOurs = ours.Elements.Values
                    .Where(e => e.ParentId == id)
                    .Where(e =>
                    {
                        if (!baseState.Elements.TryGetValue(e.Id, out var baseChild)) return true;
                        return !baseChild.Equals(e);
                    })
                    .ToList();

                foreach (var child in childrenModifiedOnOurs)
                {
                    if (!conflicts.Any(c => c.EntityId == child.Id.Value))
                    {
                        baseState.Elements.TryGetValue(child.Id, out var baseChild);
                        conflicts.Add(new MergeConflict("element", child.Id, "orphaned_child",
                            baseChild, child, null));
                    }
                }
            }
        }
    }

    private static void DetectDanglingRelationships(
        ModelState baseState, ModelState ours, ModelState theirs,
        List<MergeChange> changes, List<MergeConflict> conflicts)
    {
        var resolvedElements = new HashSet<ElementId>(ours.Elements.Keys);
        foreach (var c in changes.Where(c => c.EntityType == "element"))
        {
            if (c.ChangeKind == "added") resolvedElements.Add(c.EntityId);
            if (c.ChangeKind == "removed") resolvedElements.Remove(c.EntityId);
        }

        var allRelChanges = changes.Where(c => c.EntityType == "relationship").ToList();
        foreach (var change in allRelChanges)
        {
            if (change.ResolvedValue is RelationshipState rel)
            {
                if (!resolvedElements.Contains(rel.FromId) || !resolvedElements.Contains(rel.ToId))
                {
                    changes.Remove(change);
                    baseState.Relationships.TryGetValue(change.EntityId, out var baseRel);
                    conflicts.Add(new MergeConflict("relationship", change.EntityId,
                        "dangling_relationship", baseRel,
                        ours.Relationships.TryGetValue(change.EntityId, out var ourRel) ? ourRel : null,
                        theirs.Relationships.TryGetValue(change.EntityId, out var theirRel) ? theirRel : null));
                }
            }
        }
    }

    private static void DetectDanglingMessages(
        ModelState baseState, ModelState ours, ModelState theirs,
        List<MergeChange> changes, List<MergeConflict> conflicts)
    {
        var resolvedElements = new HashSet<ElementId>(ours.Elements.Keys);
        foreach (var c in changes.Where(c => c.EntityType == "element"))
        {
            if (c.ChangeKind == "added") resolvedElements.Add(c.EntityId);
            if (c.ChangeKind == "removed") resolvedElements.Remove(c.EntityId);
        }

        var allMsgChanges = changes.Where(c => c.EntityType == "message").ToList();
        foreach (var change in allMsgChanges)
        {
            if (change.ResolvedValue is MessageState msg)
            {
                if (!resolvedElements.Contains(msg.ProducerId) ||
                    msg.ConsumerIds.Any(cid => !resolvedElements.Contains(cid)))
                {
                    changes.Remove(change);
                    baseState.Messages.TryGetValue(change.EntityId, out var baseMsg);
                    conflicts.Add(new MergeConflict("message", change.EntityId,
                        "dangling_message", baseMsg,
                        ours.Messages.TryGetValue(change.EntityId, out var ourMsg) ? ourMsg : null,
                        theirs.Messages.TryGetValue(change.EntityId, out var theirMsg) ? theirMsg : null));
                }
            }
        }
    }

    /// <summary>
    /// A contract never points at something the merged model lacks. Two cases turn an
    /// automatic change into a conflict the user must resolve:
    /// a contract from the draft whose element or channel main no longer has, and a removal
    /// from the draft of an element or channel that a contract on main now uses.
    /// </summary>
    private static void DetectDanglingContracts(
        ModelState baseState, ModelState ours, ModelState theirs,
        List<MergeChange> changes, List<MergeConflict> conflicts)
    {
        var elements = new HashSet<ElementId>(ours.Elements.Keys);
        var channels = new HashSet<ChannelId>(ours.Channels.Keys);
        foreach (var change in changes)
        {
            if (change.EntityType == "element" && change.ChangeKind == "added") elements.Add(change.EntityId);
            if (change.EntityType == "element" && change.ChangeKind == "removed") elements.Remove(change.EntityId);
            if (change.EntityType == "channel" && change.ChangeKind == "added") channels.Add(change.EntityId);
            if (change.EntityType == "channel" && change.ChangeKind == "removed") channels.Remove(change.EntityId);
        }

        var operations = ours.Operations.ToDictionary(o => o.Key, o => o.Value);
        var subscriptions = ours.Subscriptions.ToDictionary(s => s.Key, s => s.Value);

        foreach (var change in changes.Where(c => c.EntityType == "operation").ToList())
        {
            if (change.ResolvedValue is not OperationState operation)
            {
                operations.Remove(change.EntityId);
            }
            else if (elements.Contains(operation.OwnerId))
            {
                operations[operation.Id] = operation;
            }
            else
            {
                changes.Remove(change);
                conflicts.Add(new MergeConflict("operation", change.EntityId, "dangling_operation",
                    baseState.Operations.GetValueOrDefault(change.EntityId),
                    ours.Operations.GetValueOrDefault(change.EntityId),
                    theirs.Operations.GetValueOrDefault(change.EntityId)));
            }
        }

        foreach (var change in changes.Where(c => c.EntityType == "subscription").ToList())
        {
            if (change.ResolvedValue is not SubscriptionState subscription)
            {
                subscriptions.Remove(change.EntityId);
            }
            else if (channels.Contains(subscription.ChannelId)
                && elements.Contains(subscription.ConsumerId)
                && (subscription.DeadLetterChannelId is not { } deadLetter || channels.Contains(deadLetter)))
            {
                subscriptions[subscription.Id] = subscription;
            }
            else
            {
                changes.Remove(change);
                conflicts.Add(new MergeConflict("subscription", change.EntityId, "dangling_subscription",
                    baseState.Subscriptions.GetValueOrDefault(change.EntityId),
                    ours.Subscriptions.GetValueOrDefault(change.EntityId),
                    theirs.Subscriptions.GetValueOrDefault(change.EntityId)));
            }
        }

        foreach (var change in changes.Where(c => c.ChangeKind == "removed").ToList())
        {
            var referenced = change.EntityType switch
            {
                "element" => operations.Values.Any(o => o.OwnerId.Value == change.EntityId)
                    || subscriptions.Values.Any(s => s.ConsumerId.Value == change.EntityId),
                "channel" => subscriptions.Values.Any(s =>
                    s.ChannelId.Value == change.EntityId || s.DeadLetterChannelId?.Value == change.EntityId),
                _ => false,
            };
            if (!referenced)
                continue;

            changes.Remove(change);
            conflicts.Add(change.EntityType == "element"
                ? new MergeConflict("element", change.EntityId, "referenced_by_contract",
                    baseState.Elements.GetValueOrDefault(change.EntityId),
                    ours.Elements.GetValueOrDefault(change.EntityId), null)
                : new MergeConflict("channel", change.EntityId, "referenced_by_contract",
                    baseState.Channels.GetValueOrDefault(change.EntityId),
                    ours.Channels.GetValueOrDefault(change.EntityId), null));
        }
    }
}
