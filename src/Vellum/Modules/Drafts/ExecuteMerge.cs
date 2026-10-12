// src/Vellum/Modules/Drafts/ExecuteMerge.cs
using System.Security.Claims;
using System.Text.Json;
using Deedbox;
using Microsoft.EntityFrameworkCore;
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
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier)!;
        await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Editor, ct);

        // The merge events, the main-branch read-model rows and the draft status commit together.
        return await SharedTransaction.RunAsync(http.RequestServices, async services =>
        {
            var draftsDb = services.GetRequiredService<DraftsDbContext>();
            var store = services.GetRequiredService<IEventStore>();

            var draft = await draftsDb.Drafts
                .FirstOrDefaultAsync(d => d.Id == draftId && d.ProjectId == projectId, ct);
            if (draft is null)
                return Results.NotFound(new ErrorResponse("not_found", "Draft not found"));
            if (draft.Status != "open")
                return Results.Conflict(new ErrorResponse("conflict", $"Draft is {draft.Status}"));

            var (oursState, mainVersion) = await store.Load<ModelState>(StreamId.From(draft.BaseStreamId), ct);

            if (mainVersion != request.ExpectedMainVersion)
                return MainChanged();

            var duplicateResolution = request.Resolutions
                .GroupBy(r => r.EntityId)
                .FirstOrDefault(g => g.Count() > 1);
            if (duplicateResolution is not null)
                return Results.BadRequest(new ErrorResponse("validation_error",
                    $"Duplicate resolution for entity {duplicateResolution.Key}"));

            var baseState = JsonSerializer.Deserialize<ModelState>(draft.BaseSnapshot, JsonOpts)!;
            var (theirsState, _) = await store.Load<ModelState>(StreamId.From(draft.StreamId), ct);

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

            draft.Status = "merged";
            draft.MergedAt = DateTimeOffset.UtcNow;

            if (events.Count == 0)
            {
                await draftsDb.SaveChangesAsync(ct);
                return Results.Ok(CreateDraft.ToDto(draft));
            }

            try
            {
                // One append in a transaction that Deedbox opens on the DbContext and commits.
                // Deedbox saves the draft status in it, and the inline projection applies the
                // events to the main branch rows in it.
                await store.ForProject(projectId).UseDbContext(draftsDb).Append(
                    StreamId.From(draft.BaseStreamId), ExpectedVersion.Exact(mainVersion), events, ct);
            }
            catch (ConcurrencyException)
            {
                // Main moved between the load and the append. Nothing was written.
                return MainChanged();
            }

            return Results.Ok(CreateDraft.ToDto(draft));
        }, ct);
    }

    private static IResult MainChanged() =>
        Results.Conflict(new ErrorResponse("conflict", "Main has changed since preview. Please re-preview."));

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

        return [];
    }

    private static List<object> BuildElementAddEvents(ElementState el)
    {
        var events = new List<object>
        {
            new ModelEvent.ElementAdded(el.Id, el.Kind, el.Name, el.Description,
                el.Technology, el.OwnerId, el.Status, el.ParentId, el.Tags)
        };
        if (el.Icon is not null) events.Add(new ModelEvent.ElementIconChanged(el.Id, el.Icon));
        if (el.FactStatus != FactStatus.Declared || el.Provenance is not null)
            events.Add(new ModelEvent.ElementProvenanceRecorded(el.Id, el.FactStatus, el.Provenance));
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
            events.Add(new ModelEvent.RelationshipProvenanceRecorded(rel.Id, rel.FactStatus, rel.Provenance));
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
                addEvents.Add(new ModelEvent.ElementProvenanceRecorded(target.Id, target.FactStatus, target.Provenance));
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
            events.Add(new ModelEvent.ElementProvenanceRecorded(id, target.FactStatus, target.Provenance));

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
                addEvents.Add(new ModelEvent.RelationshipProvenanceRecorded(target.Id, target.FactStatus, target.Provenance));
            return addEvents;
        }

        if (current.Label != target.Label) events.Add(new ModelEvent.RelationshipLabelChanged(id, target.Label));
        if (current.Technology != target.Technology) events.Add(new ModelEvent.RelationshipTechnologyChanged(id, target.Technology));
        if (current.LineShape != target.LineShape) events.Add(new ModelEvent.RelationshipLineShapeChanged(id, target.LineShape));
        if (current.Kind != target.Kind) events.Add(new ModelEvent.RelationshipKindChanged(id, target.Kind));
        if (ProvenanceChanged(current, target))
            events.Add(new ModelEvent.RelationshipProvenanceRecorded(id, target.FactStatus, target.Provenance));

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
            events.Add(new ModelEvent.MessageProvenanceRecorded(msg.Id, msg.FactStatus, msg.Provenance));
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
                addEvents.Add(new ModelEvent.MessageProvenanceRecorded(target.Id, target.FactStatus, target.Provenance));
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
            events.Add(new ModelEvent.MessageProvenanceRecorded(id, target.FactStatus, target.Provenance));

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
