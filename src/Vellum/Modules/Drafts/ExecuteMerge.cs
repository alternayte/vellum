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
                    var existing = await db.Elements.FindAsync([added.Id], ct);
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
                    var el = await db.Elements.FindAsync([renamed.ElementId], ct);
                    if (el is not null) el.Name = renamed.Name;
                    break;
                }

                case ModelEvent.ElementDescriptionChanged descChanged:
                {
                    var el = await db.Elements.FindAsync([descChanged.ElementId], ct);
                    if (el is not null) el.Description = descChanged.Description;
                    break;
                }

                case ModelEvent.ElementTechnologyChanged techChanged:
                {
                    var el = await db.Elements.FindAsync([techChanged.ElementId], ct);
                    if (el is not null) el.Technology = techChanged.Technology;
                    break;
                }

                case ModelEvent.ElementOwnerChanged ownerChanged:
                {
                    var el = await db.Elements.FindAsync([ownerChanged.ElementId], ct);
                    if (el is not null) el.OwnerId = ownerChanged.OwnerId;
                    break;
                }

                case ModelEvent.ElementReparented reparented:
                {
                    var el = await db.Elements.FindAsync([reparented.ElementId], ct);
                    if (el is not null) el.ParentId = reparented.ParentId;
                    break;
                }

                case ModelEvent.ElementStatusChanged statusChanged:
                {
                    var el = await db.Elements.FindAsync([statusChanged.ElementId], ct);
                    if (el is not null) el.Status = statusChanged.Status.ToString().ToLowerInvariant();
                    break;
                }

                case ModelEvent.ElementRetagged retagged:
                {
                    var el = await db.Elements.FindAsync([retagged.ElementId], ct);
                    if (el is not null) el.Tags = retagged.Tags;
                    break;
                }

                case ModelEvent.ElementRemoved removed:
                {
                    var el = await db.Elements.FindAsync([removed.ElementId], ct);
                    if (el is not null) db.Elements.Remove(el);
                    break;
                }

                case ModelEvent.RelationshipAdded relAdded:
                {
                    var existing = await db.Relationships.FindAsync([relAdded.Id], ct);
                    if (existing is not null)
                    {
                        existing.Branch = mainStreamId;
                        existing.Label = relAdded.Label;
                        existing.Technology = relAdded.Technology;
                        existing.MessageId = relAdded.MessageId;
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
                        });
                    }
                    break;
                }

                case ModelEvent.RelationshipLabelChanged labelChanged:
                {
                    var rel = await db.Relationships.FindAsync([labelChanged.RelationshipId], ct);
                    if (rel is not null) rel.Label = labelChanged.Label;
                    break;
                }

                case ModelEvent.RelationshipTechnologyChanged relTechChanged:
                {
                    var rel = await db.Relationships.FindAsync([relTechChanged.RelationshipId], ct);
                    if (rel is not null) rel.Technology = relTechChanged.Technology;
                    break;
                }

                case ModelEvent.RelationshipRemoved relRemoved:
                {
                    var rel = await db.Relationships.FindAsync([relRemoved.RelationshipId], ct);
                    if (rel is not null) db.Relationships.Remove(rel);
                    break;
                }

                case ModelEvent.MessageAdded msgAdded:
                {
                    var existing = await db.Messages.FindAsync([msgAdded.Id], ct);
                    if (existing is not null)
                    {
                        existing.Branch = mainStreamId;
                        existing.Name = msgAdded.Name;
                        existing.Description = msgAdded.Description;
                        existing.ProducerId = msgAdded.ProducerId;
                        existing.ConsumerIds = msgAdded.ConsumerIds;
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
                            ConsumerIds = msgAdded.ConsumerIds,
                            SchemaId = msgAdded.SchemaId,
                            Tags = msgAdded.Tags,
                        });
                    }
                    break;
                }

                case ModelEvent.MessageUpdated msgUpdated:
                {
                    var msg = await db.Messages.FindAsync([msgUpdated.MessageId], ct);
                    if (msg is not null)
                    {
                        if (msgUpdated.Name is not null) msg.Name = msgUpdated.Name;
                        if (msgUpdated.Description is not null) msg.Description = msgUpdated.Description;
                        if (msgUpdated.ProducerId is not null) msg.ProducerId = msgUpdated.ProducerId.Value;
                        if (msgUpdated.ConsumerIds is not null) msg.ConsumerIds = msgUpdated.ConsumerIds;
                        if (msgUpdated.SetSchemaId) msg.SchemaId = msgUpdated.SchemaId;
                    }
                    break;
                }

                case ModelEvent.MessageRemoved msgRemoved:
                {
                    var msg = await db.Messages.FindAsync([msgRemoved.MessageId], ct);
                    if (msg is not null) db.Messages.Remove(msg);
                    break;
                }
            }
        }

        await db.SaveChangesAsync(ct);
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
                    [new ModelEvent.ElementAdded(el.Id, el.Kind, el.Name, el.Description,
                        el.Technology, el.OwnerId, el.Status, el.ParentId, el.Tags)],
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
                    [new ModelEvent.RelationshipAdded(rel.Id, rel.FromId, rel.ToId,
                        rel.Label, rel.Technology, rel.MessageId)],
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
                    [new ModelEvent.MessageAdded(msg.Id, msg.Name, msg.Description,
                        msg.ProducerId, msg.ConsumerIds, msg.SchemaId, msg.Tags)],
                "modified" when value is MessageState msg =>
                    BuildMessageUpdateEvents(entityId, msg, currentMain),
                "removed" =>
                    [new ModelEvent.MessageRemoved(entityId)],
                _ => []
            };
        }

        return [];
    }

    private static List<ModelEvent> BuildElementUpdateEvents(
        Guid id, ElementState target, ModelState currentMain)
    {
        var events = new List<ModelEvent>();
        if (!currentMain.Elements.TryGetValue(id, out var current))
            return [new ModelEvent.ElementAdded(target.Id, target.Kind, target.Name, target.Description,
                target.Technology, target.OwnerId, target.Status, target.ParentId, target.Tags)];

        if (current.Name != target.Name) events.Add(new ModelEvent.ElementRenamed(id, target.Name));
        if (current.Description != target.Description) events.Add(new ModelEvent.ElementDescriptionChanged(id, target.Description));
        if (current.Technology != target.Technology) events.Add(new ModelEvent.ElementTechnologyChanged(id, target.Technology));
        if (current.OwnerId != target.OwnerId) events.Add(new ModelEvent.ElementOwnerChanged(id, target.OwnerId));
        if (current.Status != target.Status) events.Add(new ModelEvent.ElementStatusChanged(id, target.Status));
        if (current.ParentId != target.ParentId) events.Add(new ModelEvent.ElementReparented(id, target.ParentId));
        if (!current.Tags.SequenceEqual(target.Tags)) events.Add(new ModelEvent.ElementRetagged(id, target.Tags));

        return events;
    }

    private static List<ModelEvent> BuildRelationshipUpdateEvents(
        Guid id, RelationshipState target, ModelState currentMain)
    {
        var events = new List<ModelEvent>();
        if (!currentMain.Relationships.TryGetValue(id, out var current))
            return [new ModelEvent.RelationshipAdded(target.Id, target.FromId, target.ToId,
                target.Label, target.Technology, target.MessageId)];

        if (current.Label != target.Label) events.Add(new ModelEvent.RelationshipLabelChanged(id, target.Label));
        if (current.Technology != target.Technology) events.Add(new ModelEvent.RelationshipTechnologyChanged(id, target.Technology));
        if (current.LineShape != target.LineShape) events.Add(new ModelEvent.RelationshipLineShapeChanged(id, target.LineShape));

        return events;
    }

    private static List<ModelEvent> BuildMessageUpdateEvents(
        Guid id, MessageState target, ModelState currentMain)
    {
        if (!currentMain.Messages.TryGetValue(id, out var current))
            return [new ModelEvent.MessageAdded(target.Id, target.Name, target.Description,
                target.ProducerId, target.ConsumerIds, target.SchemaId, target.Tags)];

        var hasChanges =
            current.Name != target.Name ||
            current.Description != target.Description ||
            current.ProducerId != target.ProducerId ||
            !current.ConsumerIds.SequenceEqual(target.ConsumerIds) ||
            current.SchemaId != target.SchemaId ||
            !current.Tags.SequenceEqual(target.Tags);

        if (!hasChanges) return [];

        return [new ModelEvent.MessageUpdated(
            id,
            current.Name != target.Name ? target.Name : null,
            current.Description != target.Description ? target.Description : null,
            current.ProducerId != target.ProducerId ? target.ProducerId : null,
            !current.ConsumerIds.SequenceEqual(target.ConsumerIds) ? target.ConsumerIds : null,
            current.SchemaId != target.SchemaId ? target.SchemaId : null,
            current.SchemaId != target.SchemaId,
            !current.Tags.SequenceEqual(target.Tags) ? target.Tags : null)];
    }
}
