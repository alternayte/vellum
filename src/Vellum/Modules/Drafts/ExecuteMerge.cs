// src/Vellum/Modules/Drafts/ExecuteMerge.cs
using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Vellum.Kernel.Aggregates;
using Vellum.Modules.Drafts.Merge;
using Vellum.Modules.Modelling;
using Vellum.Modules.Modelling.Entities;
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
        DraftsDbContext draftsDb,
        AggregateStore store,
        ModellingDbContext modellingDb,
        WorkspaceAuthorizationService auth,
        CancellationToken ct)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier)!;
        await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Editor, ct);

        var draft = await draftsDb.Drafts
            .FirstOrDefaultAsync(d => d.Id == draftId && d.ProjectId == projectId, ct);
        if (draft is null)
            return Results.NotFound(new ErrorResponse("not_found", "Draft not found"));
        if (draft.Status != "open")
            return Results.Conflict(new ErrorResponse("conflict", $"Draft is {draft.Status}"));

        var (oursState, mainVersion) = await store.LoadAsync<ModelState, ModelEvent>(draft.BaseStreamId, ct);

        if (mainVersion != request.ExpectedMainVersion)
            return Results.Conflict(new ErrorResponse("conflict",
                "Main has changed since preview. Please re-preview."));

        var duplicateResolution = request.Resolutions
            .GroupBy(r => r.EntityId)
            .FirstOrDefault(g => g.Count() > 1);
        if (duplicateResolution is not null)
            return Results.BadRequest(new ErrorResponse("validation_error",
                $"Duplicate resolution for entity {duplicateResolution.Key}"));

        var baseState = JsonSerializer.Deserialize<ModelState>(draft.BaseSnapshot, JsonOpts)!;
        var (theirsState, _) = await store.LoadAsync<ModelState, ModelEvent>(draft.StreamId, ct);

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

        if (events.Count > 0)
        {
            var metadata = new EventMetadata
            {
                ActorId = Guid.Parse(userId),
                CorrelationId = Guid.NewGuid(),
                DraftId = draftId,
                MergeCorrelationId = Guid.NewGuid(),
            };

            var newState = events.Aggregate(oursState, (s, e) => s.Evolve(e));
            await store.SaveAsync(draft.BaseStreamId, "model", mainVersion, newState, events, metadata, ct);

            // Apply projection changes directly: move draft elements/relationships to main branch,
            // handle additions (upsert to main), updates, and removals.
            await ApplyProjectionAsync(events, projectId, draft.BaseStreamId, draft.StreamId, modellingDb, ct);
        }

        draft.Status = "merged";
        draft.MergedAt = DateTimeOffset.UtcNow;
        await draftsDb.SaveChangesAsync(ct);

        return Results.Ok(CreateDraft.ToDto(draft));
    }

    private static async Task ApplyProjectionAsync(
        IReadOnlyList<ModelEvent> events,
        Guid projectId,
        Guid mainStreamId,
        Guid draftStreamId,
        ModellingDbContext db,
        CancellationToken ct)
    {
        foreach (var @event in events)
        {
            switch (@event)
            {
                case ModelEvent.ElementAdded added:
                {
                    // Element may already exist on the draft branch — upsert to main
                    var existing = await FindElementAsync(db, added.Id, mainStreamId, draftStreamId, ct);
                    if (existing is not null)
                    {
                        existing.Branch = mainStreamId;
                        existing.Kind = added.Kind.ToString().ToLowerInvariant();
                        existing.Name = added.Name;
                        existing.Description = added.Description;
                        existing.Technology = added.Technology;
                        existing.OwnerId = added.OwnerId;
                        existing.Status = added.Status.ToString().ToLowerInvariant();
                        existing.ParentId = added.ParentId;
                        existing.Tags = added.Tags;
                    }
                    else
                    {
                        db.Elements.Add(new ElementEntity
                        {
                            Id = added.Id,
                            ProjectId = projectId,
                            Branch = mainStreamId,
                            Kind = added.Kind.ToString().ToLowerInvariant(),
                            Name = added.Name,
                            Description = added.Description,
                            Technology = added.Technology,
                            OwnerId = added.OwnerId,
                            Status = added.Status.ToString().ToLowerInvariant(),
                            ParentId = added.ParentId,
                            Tags = added.Tags,
                        });
                    }
                    break;
                }

                case ModelEvent.ElementRenamed renamed:
                {
                    var el = await FindElementAsync(db, renamed.ElementId, mainStreamId, draftStreamId, ct);
                    if (el is not null) el.Name = renamed.Name;
                    break;
                }

                case ModelEvent.ElementDescriptionChanged descChanged:
                {
                    var el = await FindElementAsync(db, descChanged.ElementId, mainStreamId, draftStreamId, ct);
                    if (el is not null) el.Description = descChanged.Description;
                    break;
                }

                case ModelEvent.ElementTechnologyChanged techChanged:
                {
                    var el = await FindElementAsync(db, techChanged.ElementId, mainStreamId, draftStreamId, ct);
                    if (el is not null) el.Technology = techChanged.Technology;
                    break;
                }

                case ModelEvent.ElementOwnerChanged ownerChanged:
                {
                    var el = await FindElementAsync(db, ownerChanged.ElementId, mainStreamId, draftStreamId, ct);
                    if (el is not null) el.OwnerId = ownerChanged.OwnerId;
                    break;
                }

                case ModelEvent.ElementReparented reparented:
                {
                    var el = await FindElementAsync(db, reparented.ElementId, mainStreamId, draftStreamId, ct);
                    if (el is not null) el.ParentId = reparented.ParentId;
                    break;
                }

                case ModelEvent.ElementStatusChanged statusChanged:
                {
                    var el = await FindElementAsync(db, statusChanged.ElementId, mainStreamId, draftStreamId, ct);
                    if (el is not null) el.Status = statusChanged.Status.ToString().ToLowerInvariant();
                    break;
                }

                case ModelEvent.ElementRetagged retagged:
                {
                    var el = await FindElementAsync(db, retagged.ElementId, mainStreamId, draftStreamId, ct);
                    if (el is not null) el.Tags = retagged.Tags;
                    break;
                }

                case ModelEvent.ElementRemoved removed:
                {
                    var el = await FindElementAsync(db, removed.ElementId, mainStreamId, draftStreamId, ct);
                    if (el is not null) db.Elements.Remove(el);
                    break;
                }

                case ModelEvent.ElementProvenanceRecorded elementProvenance:
                {
                    var el = await FindElementAsync(db, elementProvenance.ElementId, mainStreamId, draftStreamId, ct);
                    if (el is not null) ApplyProvenance(el, elementProvenance.Status, elementProvenance.Provenance);
                    break;
                }

                case ModelEvent.ElementIconChanged iconChanged:
                {
                    var el = await FindElementAsync(db, iconChanged.ElementId, mainStreamId, draftStreamId, ct);
                    if (el is not null) el.Icon = iconChanged.Icon;
                    break;
                }

                case ModelEvent.RelationshipAdded relAdded:
                {
                    var existing = await FindRelationshipAsync(db, relAdded.Id, mainStreamId, draftStreamId, ct);
                    if (existing is not null)
                    {
                        existing.Branch = mainStreamId;
                        existing.Label = relAdded.Label;
                        existing.Technology = relAdded.Technology;
                        existing.MessageId = relAdded.MessageId;
                        existing.Kind = relAdded.Kind.ToString().ToLowerInvariant();
                    }
                    else
                    {
                        db.Relationships.Add(new RelationshipEntity
                        {
                            Id = relAdded.Id,
                            ProjectId = projectId,
                            Branch = mainStreamId,
                            FromId = relAdded.FromId,
                            ToId = relAdded.ToId,
                            Label = relAdded.Label,
                            Technology = relAdded.Technology,
                            MessageId = relAdded.MessageId,
                            Kind = relAdded.Kind.ToString().ToLowerInvariant(),
                        });
                    }
                    break;
                }

                case ModelEvent.RelationshipLabelChanged labelChanged:
                {
                    var rel = await FindRelationshipAsync(db, labelChanged.RelationshipId, mainStreamId, draftStreamId, ct);
                    if (rel is not null) rel.Label = labelChanged.Label;
                    break;
                }

                case ModelEvent.RelationshipTechnologyChanged relTechChanged:
                {
                    var rel = await FindRelationshipAsync(db, relTechChanged.RelationshipId, mainStreamId, draftStreamId, ct);
                    if (rel is not null) rel.Technology = relTechChanged.Technology;
                    break;
                }

                case ModelEvent.RelationshipRemoved relRemoved:
                {
                    var rel = await FindRelationshipAsync(db, relRemoved.RelationshipId, mainStreamId, draftStreamId, ct);
                    if (rel is not null) db.Relationships.Remove(rel);
                    break;
                }

                case ModelEvent.RelationshipKindChanged kindChanged:
                {
                    var rel = await FindRelationshipAsync(db, kindChanged.RelationshipId, mainStreamId, draftStreamId, ct);
                    if (rel is not null) rel.Kind = kindChanged.Kind.ToString().ToLowerInvariant();
                    break;
                }

                case ModelEvent.RelationshipLineShapeChanged lineShapeChanged:
                {
                    var rel = await FindRelationshipAsync(db, lineShapeChanged.RelationshipId, mainStreamId, draftStreamId, ct);
                    if (rel is not null) rel.LineShape = lineShapeChanged.LineShape;
                    break;
                }

                case ModelEvent.RelationshipProvenanceRecorded relationshipProvenance:
                {
                    var rel = await FindRelationshipAsync(db, relationshipProvenance.RelationshipId, mainStreamId, draftStreamId, ct);
                    if (rel is not null) ApplyProvenance(rel, relationshipProvenance.Status, relationshipProvenance.Provenance);
                    break;
                }

                case ModelEvent.MessageAdded msgAdded:
                {
                    var existing = await FindMessageAsync(db, msgAdded.Id, mainStreamId, draftStreamId, ct);
                    if (existing is not null)
                    {
                        existing.Branch = mainStreamId;
                        existing.Name = msgAdded.Name;
                        existing.Description = msgAdded.Description;
                        existing.ProducerId = msgAdded.ProducerId;
                        existing.ConsumerIds = msgAdded.ConsumerIds.ToGuids();
                        existing.SchemaId = msgAdded.SchemaId;
                        existing.Tags = msgAdded.Tags;
                    }
                    else
                    {
                        db.Messages.Add(new MessageEntity
                        {
                            Id = msgAdded.Id,
                            ProjectId = projectId,
                            Branch = mainStreamId,
                            Name = msgAdded.Name,
                            Description = msgAdded.Description,
                            ProducerId = msgAdded.ProducerId,
                            ConsumerIds = msgAdded.ConsumerIds.ToGuids(),
                            SchemaId = msgAdded.SchemaId,
                            Tags = msgAdded.Tags,
                        });
                    }
                    break;
                }

                case ModelEvent.MessageUpdated msgUpdated:
                {
                    var msg = await FindMessageAsync(db, msgUpdated.MessageId, mainStreamId, draftStreamId, ct);
                    if (msg is not null)
                    {
                        if (msgUpdated.Name is not null) msg.Name = msgUpdated.Name;
                        if (msgUpdated.Description is not null) msg.Description = msgUpdated.Description;
                        if (msgUpdated.ProducerId is not null) msg.ProducerId = msgUpdated.ProducerId.Value;
                        if (msgUpdated.ConsumerIds is not null) msg.ConsumerIds = msgUpdated.ConsumerIds.ToGuids();
                        if (msgUpdated.SetSchemaId) msg.SchemaId = msgUpdated.SchemaId;
                        if (msgUpdated.Tags is not null) msg.Tags = msgUpdated.Tags;
                    }
                    break;
                }

                case ModelEvent.MessageRemoved msgRemoved:
                {
                    var msg = await FindMessageAsync(db, msgRemoved.MessageId, mainStreamId, draftStreamId, ct);
                    if (msg is not null) db.Messages.Remove(msg);
                    break;
                }

                case ModelEvent.MessageProvenanceRecorded messageProvenance:
                {
                    var msg = await FindMessageAsync(db, messageProvenance.MessageId, mainStreamId, draftStreamId, ct);
                    if (msg is not null) ApplyProvenance(msg, messageProvenance.Status, messageProvenance.Provenance);
                    break;
                }
            }
        }

        await db.SaveChangesAsync(ct);
    }

    private static void ApplyProvenance(ElementEntity entity, FactStatus status, Provenance? provenance)
    {
        entity.FactStatus = status.ToApiString();
        entity.ProvenanceSource = provenance?.Source;
        entity.ProvenanceRevision = provenance?.Revision;
        entity.ProvenanceConfidence = provenance?.Confidence;
        entity.ProvenanceAcceptedBy = provenance?.AcceptedBy;
        entity.ProvenanceAcceptedAt = provenance?.AcceptedAt;
    }

    private static void ApplyProvenance(RelationshipEntity entity, FactStatus status, Provenance? provenance)
    {
        entity.FactStatus = status.ToApiString();
        entity.ProvenanceSource = provenance?.Source;
        entity.ProvenanceRevision = provenance?.Revision;
        entity.ProvenanceConfidence = provenance?.Confidence;
        entity.ProvenanceAcceptedBy = provenance?.AcceptedBy;
        entity.ProvenanceAcceptedAt = provenance?.AcceptedAt;
    }

    private static void ApplyProvenance(MessageEntity entity, FactStatus status, Provenance? provenance)
    {
        entity.FactStatus = status.ToApiString();
        entity.ProvenanceSource = provenance?.Source;
        entity.ProvenanceRevision = provenance?.Revision;
        entity.ProvenanceConfidence = provenance?.Confidence;
        entity.ProvenanceAcceptedBy = provenance?.AcceptedBy;
        entity.ProvenanceAcceptedAt = provenance?.AcceptedAt;
    }

    /// <summary>
    /// Finds a row by architecture ID on the main branch, falling back to the draft
    /// branch. Merge events target the main branch; rows created on the draft are moved
    /// over only when main has no row with that ID. Rows added earlier in the same batch
    /// are found through the change tracker before the database.
    /// </summary>
    private static async Task<ElementEntity?> FindElementAsync(
        ModellingDbContext db, ElementId id, Guid mainStreamId, Guid draftStreamId, CancellationToken ct)
    {
        var local = db.Elements.Local
            .Where(e => e.Id == id.Value && (e.Branch == mainStreamId || e.Branch == draftStreamId))
            .OrderBy(e => e.Branch == mainStreamId ? 0 : 1)
            .FirstOrDefault();
        if (local is not null) return local;

        return await db.Elements.FirstOrDefaultAsync(e => e.Id == id.Value && e.Branch == mainStreamId, ct)
            ?? await db.Elements.FirstOrDefaultAsync(e => e.Id == id.Value && e.Branch == draftStreamId, ct);
    }

    private static async Task<RelationshipEntity?> FindRelationshipAsync(
        ModellingDbContext db, RelationshipId id, Guid mainStreamId, Guid draftStreamId, CancellationToken ct)
    {
        var local = db.Relationships.Local
            .Where(r => r.Id == id.Value && (r.Branch == mainStreamId || r.Branch == draftStreamId))
            .OrderBy(r => r.Branch == mainStreamId ? 0 : 1)
            .FirstOrDefault();
        if (local is not null) return local;

        return await db.Relationships.FirstOrDefaultAsync(r => r.Id == id.Value && r.Branch == mainStreamId, ct)
            ?? await db.Relationships.FirstOrDefaultAsync(r => r.Id == id.Value && r.Branch == draftStreamId, ct);
    }

    private static async Task<MessageEntity?> FindMessageAsync(
        ModellingDbContext db, MessageId id, Guid mainStreamId, Guid draftStreamId, CancellationToken ct)
    {
        var local = db.Messages.Local
            .Where(m => m.Id == id.Value && (m.Branch == mainStreamId || m.Branch == draftStreamId))
            .OrderBy(m => m.Branch == mainStreamId ? 0 : 1)
            .FirstOrDefault();
        if (local is not null) return local;

        return await db.Messages.FirstOrDefaultAsync(m => m.Id == id.Value && m.Branch == mainStreamId, ct)
            ?? await db.Messages.FirstOrDefaultAsync(m => m.Id == id.Value && m.Branch == draftStreamId, ct);
    }

    private static List<ModelEvent> BuildMergeEvents(
        MergePreviewResult preview,
        IReadOnlyList<MergeResolution> resolutions,
        ModelState currentMain)
    {
        var events = new List<ModelEvent>();
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

    private static IEnumerable<ModelEvent> ToEvents(
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

    private static List<ModelEvent> BuildElementAddEvents(ElementState el)
    {
        var events = new List<ModelEvent>
        {
            new ModelEvent.ElementAdded(el.Id, el.Kind, el.Name, el.Description,
                el.Technology, el.OwnerId, el.Status, el.ParentId, el.Tags)
        };
        if (el.Icon is not null) events.Add(new ModelEvent.ElementIconChanged(el.Id, el.Icon));
        if (el.FactStatus != FactStatus.Declared || el.Provenance is not null)
            events.Add(new ModelEvent.ElementProvenanceRecorded(el.Id, el.FactStatus, el.Provenance));
        return events;
    }

    private static List<ModelEvent> BuildRelationshipAddEvents(RelationshipState rel)
    {
        var events = new List<ModelEvent>
        {
            new ModelEvent.RelationshipAdded(rel.Id, rel.FromId, rel.ToId,
                rel.Label, rel.Technology, rel.MessageId, rel.Kind)
        };
        if (rel.LineShape is not null) events.Add(new ModelEvent.RelationshipLineShapeChanged(rel.Id, rel.LineShape));
        if (rel.FactStatus != FactStatus.Declared || rel.Provenance is not null)
            events.Add(new ModelEvent.RelationshipProvenanceRecorded(rel.Id, rel.FactStatus, rel.Provenance));
        return events;
    }

    private static List<ModelEvent> BuildElementUpdateEvents(
        Guid id, ElementState target, ModelState currentMain)
    {
        var events = new List<ModelEvent>();
        if (!currentMain.Elements.TryGetValue(id, out var current))
        {
            var addEvents = new List<ModelEvent>
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

    private static List<ModelEvent> BuildRelationshipUpdateEvents(
        Guid id, RelationshipState target, ModelState currentMain)
    {
        var events = new List<ModelEvent>();
        if (!currentMain.Relationships.TryGetValue(id, out var current))
        {
            var addEvents = new List<ModelEvent>
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

    private static List<ModelEvent> BuildMessageAddEvents(MessageState msg)
    {
        var events = new List<ModelEvent>
        {
            new ModelEvent.MessageAdded(msg.Id, msg.Name, msg.Description,
                msg.ProducerId, msg.ConsumerIds, msg.SchemaId, msg.Tags)
        };
        if (msg.FactStatus != FactStatus.Declared || msg.Provenance is not null)
            events.Add(new ModelEvent.MessageProvenanceRecorded(msg.Id, msg.FactStatus, msg.Provenance));
        return events;
    }

    private static List<ModelEvent> BuildMessageUpdateEvents(
        Guid id, MessageState target, ModelState currentMain)
    {
        if (!currentMain.Messages.TryGetValue(id, out var current))
        {
            var addEvents = new List<ModelEvent>
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

        var events = new List<ModelEvent>();
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
