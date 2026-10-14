// src/Vellum/Modules/Drafts/ExecuteMerge.cs
using System.Security.Claims;
using System.Text.Json;
using Deedbox;
using Microsoft.EntityFrameworkCore;
using Vellum.Modules.Costs;
using Vellum.Modules.Drafts.Merge;
using Vellum.Modules.Modelling.Model;
using Vellum.Modules.Workspaces.Authorization;
using Vellum.Shared;

namespace Vellum.Modules.Drafts;

public sealed record ExecuteMergeRequest(
    IReadOnlyList<MergeResolution> Resolutions,
    int ExpectedMainVersion);

public static class ExecuteMerge
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static async Task<IResult> Handle(
        Guid projectId, Guid draftId,
        ExecuteMergeRequest request,
        ClaimsPrincipal user,
        HttpContext http,
        WorkspaceAuthorizationService auth,
        CancellationToken ct)
    {
        var userId = user.UserId();
        await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Editor, ct);

        // The merge events, the read-model rows, the merged cost sheet and the draft status
        // commit together. A unit that fails with DBX038 or a deadlock wrote nothing and runs again.
        return await SharedTransaction.RunAsync(http.RequestServices, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();

            var streams = await db.Drafts.AsNoTracking()
                .Where(d => d.Id == draftId && d.ProjectId == projectId)
                .Select(d => new { d.StreamId, d.BaseStreamId, d.Status })
                .FirstOrDefaultAsync(ct);
            if (streams is null)
                return Results.NotFound(new ErrorResponse("not_found", "Draft not found"));
            // A closed draft has no cost sheet stream to lock. The status is read again under the lock.
            if (streams.Status != "open")
                return Results.Conflict(new ErrorResponse("conflict", $"Draft is {streams.Status}"));

            // A return before the commit rolls everything back.
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var store = services.GetRequiredService<IEventStore>().ForProject(projectId).UseDbContext(db);

            // The two model streams and the two cost sheet streams stay locked until the
            // commit, so no model and no sheet moves under the merge, and a second merge or an
            // abandon of this draft waits its turn. Streams are locked in ID order, the order
            // every transaction with several streams uses.
            ModelState oursState = ModelState.Initial, theirsState = ModelState.Initial;
            CostSheetState oursSheet = CostSheetState.Initial, theirsSheet = CostSheetState.Initial;
            long mainVersion = 0, mainSheetVersion = 0;
            var mainSheetId = CostAccess.SheetStreamId(streams.BaseStreamId);
            var draftSheetId = CostAccess.SheetStreamId(streams.StreamId);
            foreach (var streamId in new[] { streams.StreamId, streams.BaseStreamId, mainSheetId, draftSheetId }.Order())
            {
                if (streamId == mainSheetId)
                    (oursSheet, mainSheetVersion) = await CostBranches.LockSheetAsync(store, streamId, ct);
                else if (streamId == draftSheetId)
                    (theirsSheet, _) = await CostBranches.LockSheetAsync(store, streamId, ct);
                else if (streamId == streams.StreamId)
                    theirsState = (await store.Execute<ModelState>(StreamId.From(streamId), _ => [], ct)).State;
                else
                {
                    var locked = await store.Execute<ModelState>(StreamId.From(streamId), _ => [], ct);
                    (oursState, mainVersion) = (locked.State, locked.Version);
                }
            }

            // Read under the draft's lock: the status is the one the last writer committed.
            var draft = await db.Drafts.FirstAsync(d => d.Id == draftId, ct);
            if (draft.Status != "open")
                return Results.Conflict(new ErrorResponse("conflict", $"Draft is {draft.Status}"));

            if (mainVersion != request.ExpectedMainVersion)
                return MainChanged();

            var duplicateResolution = request.Resolutions
                .GroupBy(r => r.EntityId)
                .FirstOrDefault(g => g.Count() > 1);
            if (duplicateResolution is not null)
                return Results.BadRequest(new ErrorResponse("validation_error",
                    $"Duplicate resolution for entity {duplicateResolution.Key}"));

            var baseState = JsonSerializer.Deserialize<ModelState>(draft.BaseSnapshot, JsonOpts)!;
            var preview = ThreeWayMerge.Compute(baseState, oursState, theirsState);

            if (preview.Conflicts.Count > 0)
            {
                var resolutionMap = request.Resolutions.ToDictionary(r => r.EntityId, r => r.Resolution);
                var unresolved = preview.Conflicts.Where(c => !resolutionMap.ContainsKey(c.EntityId)).ToList();
                if (unresolved.Count > 0)
                    return Results.BadRequest(new ErrorResponse("validation_error",
                        $"{unresolved.Count} conflict(s) without resolution"));
            }

            var events = BuildMergeEvents(preview, request.Resolutions, oursState);

            // No decider produced these events. An event that does not fit main, or a result
            // that points at nothing, refuses the merge before anything is written.
            if (MergedModel.Refusal(oursState, events) is { } refusal)
                return Results.Conflict(new ErrorResponse("conflict", refusal));

            // The cost sheet of the draft merges the way the model does. A conflict has no
            // resolution to pick, so it refuses the merge and nothing is written.
            var now = DateTimeOffset.UtcNow;
            var sheetPlan = SheetMerge.Plan(oursSheet, theirsSheet, projectId, streams.BaseStreamId, userId, now);
            if (SheetMerge.Refusal(sheetPlan) is { } sheetRefusal)
            {
                return Results.Conflict(new ErrorResponse(
                    "cost_sheet_conflict", "The cost sheet of the draft does not merge into the cost sheet of main", sheetRefusal));
            }

            draft.Status = "merged";
            draft.MergedAt = now;

            // One append. Deedbox saves the draft status in the transaction, and the inline
            // projection applies the changes to the main rows and drops the draft's rows.
            // A merge that changes nothing leaves main at its version.
            var closed = new ModelEvent.BranchClosed(draft.StreamId);
            if (events.Count == 0)
            {
                await store.Append(StreamId.From(draft.StreamId), ExpectedVersion.Any, [closed], ct);
            }
            else
            {
                await store.Append(
                    StreamId.From(draft.BaseStreamId), ExpectedVersion.Exact(mainVersion), [.. events, closed], ct);
            }

            // The sheet of main gets the draft's changes. The sheet and the saved estimates of
            // the draft go: the branch is closed and no request reaches them again.
            await CostBranches.ApplyAsync(store, streams.BaseStreamId, mainSheetVersion, sheetPlan, ct);
            await CostBranches.CloseAsync(db, store, projectId, draft.StreamId, ct);

            await transaction.CommitAsync(ct);
            return Results.Ok(CreateDraft.ToDto(draft));
        }, ct);
    }

    private static IResult MainChanged() =>
        Results.Conflict(new ErrorResponse("conflict", "Main has changed since preview. Please re-preview."));

    // A value that the preview resolved to carries the stale mark main has for that fact, or
    // none when main does not have the fact (see ThreeWayMerge.Compute). Every provenance
    // event below writes that mark, so a merge leaves the marks on main as they were.
    private static List<object> BuildMergeEvents(
        MergePreviewResult preview,
        IReadOnlyList<MergeResolution> resolutions,
        ModelState currentMain)
    {
        var events = new List<object>();
        var resolutionMap = resolutions.ToDictionary(r => r.EntityId, r => r.Resolution);

        foreach (var change in preview.AutoResolved)
        {
            events.AddRange(ToEvents(change.EntityType, change.EntityId, change.ChangeKind,
                change.ResolvedValue, currentMain));
        }

        foreach (var conflict in preview.Conflicts)
        {
            if (!resolutionMap.TryGetValue(conflict.EntityId, out var resolution)) continue;

            if (resolution == "take_theirs" && conflict.TheirsValue is not null)
            {
                var kind = conflict.OursValue is null ? "added" : "modified";
                events.AddRange(ToEvents(conflict.EntityType, conflict.EntityId, kind,
                    conflict.TheirsValue, currentMain));
            }
            else if (resolution == "take_theirs" && conflict.TheirsValue is null)
            {
                events.AddRange(ToEvents(conflict.EntityType, conflict.EntityId, "removed",
                    null, currentMain));
            }
        }

        return events;
    }

    private static IEnumerable<object> ToEvents(
        string entityType, Guid entityId, string changeKind,
        object? value, ModelState currentMain)
    {
        if (entityType == "element")
        {
            return changeKind switch
            {
                "added" when value is ElementState el =>
                    BuildElementAddEvents(el),
                "modified" when value is ElementState el =>
                    BuildElementUpdateEvents(entityId, el, currentMain),
                "removed" =>
                    [new ModelEvent.ElementRemoved(entityId)],
                _ => []
            };
        }

        if (entityType == "relationship")
        {
            return changeKind switch
            {
                "added" when value is RelationshipState rel =>
                    BuildRelationshipAddEvents(rel),
                "modified" when value is RelationshipState rel =>
                    BuildRelationshipUpdateEvents(entityId, rel, currentMain),
                "removed" =>
                    [new ModelEvent.RelationshipRemoved(entityId)],
                _ => []
            };
        }

        if (entityType == "message")
        {
            return changeKind switch
            {
                "added" when value is MessageState msg =>
                    BuildMessageAddEvents(msg),
                "modified" when value is MessageState msg =>
                    BuildMessageUpdateEvents(entityId, msg, currentMain),
                "removed" =>
                    [new ModelEvent.MessageRemoved(entityId)],
                _ => []
            };
        }

        if (entityType == "operation")
        {
            return changeKind switch
            {
                "added" or "modified" when value is OperationState operation =>
                    BuildOperationEvents(operation, currentMain),
                "removed" => [new ModelEvent.OperationRemoved(entityId)],
                _ => []
            };
        }

        if (entityType == "channel")
        {
            return changeKind switch
            {
                "added" or "modified" when value is ChannelState channel =>
                    BuildChannelEvents(channel, currentMain),
                "removed" => [new ModelEvent.ChannelRemoved(entityId)],
                _ => []
            };
        }

        if (entityType == "subscription")
        {
            return changeKind switch
            {
                "added" or "modified" when value is SubscriptionState subscription =>
                    BuildSubscriptionEvents(subscription, currentMain),
                "removed" => [new ModelEvent.SubscriptionRemoved(entityId)],
                _ => []
            };
        }

        if (entityType == "binding")
        {
            return changeKind switch
            {
                "added" or "modified" when value is BindingState binding =>
                    BuildBindingEvents(binding, currentMain),
                "removed" => [new ModelEvent.BindingRemoved(entityId)],
                _ => []
            };
        }

        return [];
    }

    /// <summary>The events that make main hold <paramref name="target"/>, whether main has the operation or not.</summary>
    private static List<object> BuildOperationEvents(OperationState target, ModelState currentMain)
    {
        var events = new List<object>();
        var current = currentMain.Operations.GetValueOrDefault(target.Id);
        if (current is null)
        {
            events.Add(new ModelEvent.OperationAdded(
                target.Id, target.OwnerId, target.Name, target.Transport,
                target.RequestSchemaId, target.ResponseSchemaId, target.TimeoutMs, target.Retry));
        }
        else if (current with { FactStatus = target.FactStatus, Provenance = target.Provenance } != target)
        {
            events.Add(new ModelEvent.OperationChanged(
                target.Id, target.OwnerId, target.Name, target.Transport,
                target.RequestSchemaId, target.ResponseSchemaId, target.TimeoutMs, target.Retry));
        }

        if (FactChanged(current?.FactStatus, current?.Provenance, target.FactStatus, target.Provenance))
            events.Add(new ModelEvent.OperationProvenanceRecorded(target.Id, target.FactStatus, target.Provenance, target.Stale));
        return events;
    }

    private static List<object> BuildChannelEvents(ChannelState target, ModelState currentMain)
    {
        var events = new List<object>();
        var current = currentMain.Channels.GetValueOrDefault(target.Id);
        if (current is null)
            events.Add(new ModelEvent.ChannelAdded(target.Id, target.Name, target.Kind));
        else if (current.Name != target.Name || current.Kind != target.Kind)
            events.Add(new ModelEvent.ChannelChanged(target.Id, target.Name, target.Kind));

        if (FactChanged(current?.FactStatus, current?.Provenance, target.FactStatus, target.Provenance))
            events.Add(new ModelEvent.ChannelProvenanceRecorded(target.Id, target.FactStatus, target.Provenance, target.Stale));
        return events;
    }

    private static List<object> BuildBindingEvents(BindingState target, ModelState currentMain)
    {
        var events = new List<object>();
        var current = currentMain.Bindings.GetValueOrDefault(target.Id);
        if (current is null)
        {
            events.Add(new ModelEvent.BindingAdded(
                target.Id, target.SourceChannelId, target.TargetChannelId, target.RoutingKey));
        }
        else if (current with { FactStatus = target.FactStatus, Provenance = target.Provenance } != target)
        {
            events.Add(new ModelEvent.BindingChanged(
                target.Id, target.SourceChannelId, target.TargetChannelId, target.RoutingKey));
        }

        if (FactChanged(current?.FactStatus, current?.Provenance, target.FactStatus, target.Provenance))
            events.Add(new ModelEvent.BindingProvenanceRecorded(target.Id, target.FactStatus, target.Provenance, target.Stale));
        return events;
    }

    private static List<object> BuildSubscriptionEvents(SubscriptionState target, ModelState currentMain)
    {
        var events = new List<object>();
        var current = currentMain.Subscriptions.GetValueOrDefault(target.Id);
        if (current is null)
        {
            events.Add(new ModelEvent.SubscriptionAdded(
                target.Id, target.ChannelId, target.ConsumerId, target.Retry, target.DeadLetterChannelId));
        }
        else if (current with { FactStatus = target.FactStatus, Provenance = target.Provenance } != target)
        {
            events.Add(new ModelEvent.SubscriptionChanged(
                target.Id, target.ChannelId, target.ConsumerId, target.Retry, target.DeadLetterChannelId));
        }

        if (FactChanged(current?.FactStatus, current?.Provenance, target.FactStatus, target.Provenance))
            events.Add(new ModelEvent.SubscriptionProvenanceRecorded(target.Id, target.FactStatus, target.Provenance, target.Stale));
        return events;
    }

    /// <summary>A fact that main does not have yet starts as declared with no provenance.</summary>
    private static bool FactChanged(
        FactStatus? currentStatus, Provenance? currentProvenance, FactStatus targetStatus, Provenance? targetProvenance) =>
        (currentStatus ?? FactStatus.Declared) != targetStatus ||
        currentProvenance?.Source != targetProvenance?.Source ||
        currentProvenance?.Revision != targetProvenance?.Revision ||
        currentProvenance?.Confidence != targetProvenance?.Confidence;

    private static List<object> BuildElementAddEvents(ElementState el)
    {
        var events = new List<object>
        {
            new ModelEvent.ElementAdded(el.Id, el.Kind, el.Name, el.Description,
                el.Technology, el.OwnerId, el.Status, el.ParentId, el.Tags)
        };
        if (el.Icon is not null) events.Add(new ModelEvent.ElementIconChanged(el.Id, el.Icon));
        if (el.FactStatus != FactStatus.Declared || el.Provenance is not null)
            events.Add(new ModelEvent.ElementProvenanceRecorded(el.Id, el.FactStatus, el.Provenance, el.Stale));
        return events;
    }

    private static List<object> BuildRelationshipAddEvents(RelationshipState rel)
    {
        var events = new List<object>
        {
            new ModelEvent.RelationshipAdded(rel.Id, rel.FromId, rel.ToId,
                rel.Label, rel.Technology, rel.MessageId, rel.Kind)
        };
        if (rel.LineShape is not null) events.Add(new ModelEvent.RelationshipLineShapeChanged(rel.Id, rel.LineShape));
        if (rel.FactStatus != FactStatus.Declared || rel.Provenance is not null)
            events.Add(new ModelEvent.RelationshipProvenanceRecorded(rel.Id, rel.FactStatus, rel.Provenance, rel.Stale));
        return events;
    }

    private static List<object> BuildElementUpdateEvents(
        Guid id, ElementState target, ModelState currentMain)
    {
        var events = new List<object>();
        if (!currentMain.Elements.TryGetValue(id, out var current))
        {
            var addEvents = new List<object>
            {
                new ModelEvent.ElementAdded(target.Id, target.Kind, target.Name, target.Description,
                    target.Technology, target.OwnerId, target.Status, target.ParentId, target.Tags)
            };
            if (target.Icon is not null) addEvents.Add(new ModelEvent.ElementIconChanged(target.Id, target.Icon));
            if (target.FactStatus != FactStatus.Declared || target.Provenance is not null)
                addEvents.Add(new ModelEvent.ElementProvenanceRecorded(target.Id, target.FactStatus, target.Provenance, target.Stale));
            return addEvents;
        }

        if (current.Name != target.Name) events.Add(new ModelEvent.ElementRenamed(id, target.Name));
        if (current.Description != target.Description) events.Add(new ModelEvent.ElementDescriptionChanged(id, target.Description));
        if (current.Technology != target.Technology) events.Add(new ModelEvent.ElementTechnologyChanged(id, target.Technology));
        if (current.OwnerId != target.OwnerId) events.Add(new ModelEvent.ElementOwnerChanged(id, target.OwnerId));
        if (current.Status != target.Status) events.Add(new ModelEvent.ElementStatusChanged(id, target.Status));
        if (current.ParentId != target.ParentId) events.Add(new ModelEvent.ElementReparented(id, target.ParentId));
        if (!current.Tags.SequenceEqual(target.Tags)) events.Add(new ModelEvent.ElementRetagged(id, target.Tags));
        if (current.Icon != target.Icon) events.Add(new ModelEvent.ElementIconChanged(id, target.Icon));
        if (ProvenanceChanged(current, target))
            events.Add(new ModelEvent.ElementProvenanceRecorded(id, target.FactStatus, target.Provenance, target.Stale));

        return events;
    }

    private static List<object> BuildRelationshipUpdateEvents(
        Guid id, RelationshipState target, ModelState currentMain)
    {
        var events = new List<object>();
        if (!currentMain.Relationships.TryGetValue(id, out var current))
        {
            var addEvents = new List<object>
            {
                new ModelEvent.RelationshipAdded(target.Id, target.FromId, target.ToId,
                    target.Label, target.Technology, target.MessageId, target.Kind)
            };
            if (target.LineShape is not null) addEvents.Add(new ModelEvent.RelationshipLineShapeChanged(target.Id, target.LineShape));
            if (target.FactStatus != FactStatus.Declared || target.Provenance is not null)
                addEvents.Add(new ModelEvent.RelationshipProvenanceRecorded(target.Id, target.FactStatus, target.Provenance, target.Stale));
            return addEvents;
        }

        if (current.Label != target.Label) events.Add(new ModelEvent.RelationshipLabelChanged(id, target.Label));
        if (current.Technology != target.Technology) events.Add(new ModelEvent.RelationshipTechnologyChanged(id, target.Technology));
        if (current.LineShape != target.LineShape) events.Add(new ModelEvent.RelationshipLineShapeChanged(id, target.LineShape));
        if (current.Kind != target.Kind) events.Add(new ModelEvent.RelationshipKindChanged(id, target.Kind));
        if (ProvenanceChanged(current, target))
            events.Add(new ModelEvent.RelationshipProvenanceRecorded(id, target.FactStatus, target.Provenance, target.Stale));

        return events;
    }

    private static List<object> BuildMessageAddEvents(MessageState msg)
    {
        var events = new List<object>
        {
            new ModelEvent.MessageAdded(msg.Id, msg.Name, msg.Description,
                msg.ProducerId, msg.ConsumerIds, msg.SchemaId, msg.Tags)
        };
        if (msg.FactStatus != FactStatus.Declared || msg.Provenance is not null)
            events.Add(new ModelEvent.MessageProvenanceRecorded(msg.Id, msg.FactStatus, msg.Provenance, msg.Stale));
        return events;
    }

    private static List<object> BuildMessageUpdateEvents(
        Guid id, MessageState target, ModelState currentMain)
    {
        if (!currentMain.Messages.TryGetValue(id, out var current))
        {
            var addEvents = new List<object>
            {
                new ModelEvent.MessageAdded(target.Id, target.Name, target.Description,
                    target.ProducerId, target.ConsumerIds, target.SchemaId, target.Tags)
            };
            if (target.FactStatus != FactStatus.Declared || target.Provenance is not null)
                addEvents.Add(new ModelEvent.MessageProvenanceRecorded(target.Id, target.FactStatus, target.Provenance, target.Stale));
            return addEvents;
        }

        var hasChanges =
            current.Name != target.Name ||
            current.Description != target.Description ||
            current.ProducerId != target.ProducerId ||
            !current.ConsumerIds.SequenceEqual(target.ConsumerIds) ||
            current.SchemaId != target.SchemaId ||
            !current.Tags.SequenceEqual(target.Tags);

        var events = new List<object>();
        if (hasChanges)
        {
            events.Add(new ModelEvent.MessageUpdated(
                id,
                current.Name != target.Name ? target.Name : null,
                current.Description != target.Description ? target.Description : null,
                current.ProducerId != target.ProducerId ? target.ProducerId : null,
                !current.ConsumerIds.SequenceEqual(target.ConsumerIds) ? target.ConsumerIds : null,
                current.SchemaId != target.SchemaId ? target.SchemaId : null,
                current.SchemaId != target.SchemaId,
                !current.Tags.SequenceEqual(target.Tags) ? target.Tags : null));
        }

        if (ProvenanceChanged(current, target))
            events.Add(new ModelEvent.MessageProvenanceRecorded(id, target.FactStatus, target.Provenance, target.Stale));

        return events;
    }

    private static bool ProvenanceChanged(ElementState current, ElementState target) =>
        current.FactStatus != target.FactStatus ||
        current.Provenance?.Source != target.Provenance?.Source ||
        current.Provenance?.Revision != target.Provenance?.Revision ||
        current.Provenance?.Confidence != target.Provenance?.Confidence;

    private static bool ProvenanceChanged(RelationshipState current, RelationshipState target) =>
        current.FactStatus != target.FactStatus ||
        current.Provenance?.Source != target.Provenance?.Source ||
        current.Provenance?.Revision != target.Provenance?.Revision ||
        current.Provenance?.Confidence != target.Provenance?.Confidence;

    private static bool ProvenanceChanged(MessageState current, MessageState target) =>
        current.FactStatus != target.FactStatus ||
        current.Provenance?.Source != target.Provenance?.Source ||
        current.Provenance?.Revision != target.Provenance?.Revision ||
        current.Provenance?.Confidence != target.Provenance?.Confidence;
}
