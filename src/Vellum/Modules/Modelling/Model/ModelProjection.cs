using Microsoft.EntityFrameworkCore;
using Vellum.Kernel.CommandHandling;
using Vellum.Kernel.EventTypes;
using Vellum.Kernel.Projections;
using Vellum.Modules.Modelling.Entities;

namespace Vellum.Modules.Modelling.Model;

public sealed class ModelProjection : IInlineProjection
{
    private readonly ModellingDbContext _db;
    private readonly IEventTypeRegistry _registry;
    private Guid _projectId;
    private Guid _branch;

    public ModelProjection(ModellingDbContext db, IEventTypeRegistry registry)
    {
        _db = db;
        _registry = registry;
    }

    public void SetContext(Guid projectId, Guid branch)
    {
        _projectId = projectId;
        _branch = branch;
    }

    /// <summary>
    /// Branch-scoped lookups: the same architecture ID can exist on the main branch, a
    /// draft and a variant, so reads and writes must name the branch. Rows added earlier
    /// in the same batch are found through the change tracker before the database.
    /// </summary>
    private async Task<ElementEntity?> FindElementAsync(ElementId id, CancellationToken ct)
    {
        var local = _db.Elements.Local.FirstOrDefault(e => e.Id == id.Value && e.Branch == _branch);
        if (local is not null) return local;
        return await _db.Elements.FirstOrDefaultAsync(e => e.Id == id.Value && e.Branch == _branch, ct);
    }

    private async Task<RelationshipEntity?> FindRelationshipAsync(RelationshipId id, CancellationToken ct)
    {
        var local = _db.Relationships.Local.FirstOrDefault(r => r.Id == id.Value && r.Branch == _branch);
        if (local is not null) return local;
        return await _db.Relationships.FirstOrDefaultAsync(r => r.Id == id.Value && r.Branch == _branch, ct);
    }

    private async Task<MessageEntity?> FindMessageAsync(MessageId id, CancellationToken ct)
    {
        var local = _db.Messages.Local.FirstOrDefault(m => m.Id == id.Value && m.Branch == _branch);
        if (local is not null) return local;
        return await _db.Messages.FirstOrDefaultAsync(m => m.Id == id.Value && m.Branch == _branch, ct);
    }

    public async Task ProjectAsync(IReadOnlyList<CollectedEvent> events, CancellationToken ct = default)
    {
        foreach (var e in events)
        {
            var deserialized = _registry.DeserializeEvent(e.EventType, e.Payload);
            if (deserialized is not ModelEvent modelEvent) continue;

            switch (modelEvent)
            {
                case ModelEvent.ElementAdded added:
                    _db.Elements.Add(new ElementEntity
                    {
                        Id = added.Id,
                        ProjectId = _projectId,
                        Branch = _branch,
                        Kind = added.Kind.ToString().ToLowerInvariant(),
                        Name = added.Name,
                        Description = added.Description,
                        Technology = added.Technology,
                        OwnerId = added.OwnerId,
                        Status = added.Status.ToString().ToLowerInvariant(),
                        ParentId = added.ParentId,
                        Tags = added.Tags,
                        Icon = null
                    });
                    break;

                case ModelEvent.ElementRenamed renamed:
                    var renamedEntity = await FindElementAsync(renamed.ElementId, ct);
                    if (renamedEntity is not null) renamedEntity.Name = renamed.Name;
                    break;

                case ModelEvent.ElementDescriptionChanged descChanged:
                    var descEntity = await FindElementAsync(descChanged.ElementId, ct);
                    if (descEntity is not null) descEntity.Description = descChanged.Description;
                    break;

                case ModelEvent.ElementTechnologyChanged techChanged:
                    var techEntity = await FindElementAsync(techChanged.ElementId, ct);
                    if (techEntity is not null) techEntity.Technology = techChanged.Technology;
                    break;

                case ModelEvent.ElementOwnerChanged ownerChanged:
                    var ownerEntity = await FindElementAsync(ownerChanged.ElementId, ct);
                    if (ownerEntity is not null) ownerEntity.OwnerId = ownerChanged.OwnerId;
                    break;

                case ModelEvent.ElementReparented reparented:
                    var reparentedEntity = await FindElementAsync(reparented.ElementId, ct);
                    if (reparentedEntity is not null) reparentedEntity.ParentId = reparented.ParentId;
                    break;

                case ModelEvent.ElementStatusChanged statusChanged:
                    var statusEntity = await FindElementAsync(statusChanged.ElementId, ct);
                    if (statusEntity is not null) statusEntity.Status = statusChanged.Status.ToString().ToLowerInvariant();
                    break;

                case ModelEvent.ElementRetagged retagged:
                    var retaggedEntity = await FindElementAsync(retagged.ElementId, ct);
                    if (retaggedEntity is not null) retaggedEntity.Tags = retagged.Tags;
                    break;

                case ModelEvent.ElementIconChanged iconChanged:
                    var iconEntity = await FindElementAsync(iconChanged.ElementId, ct);
                    if (iconEntity is not null) iconEntity.Icon = iconChanged.Icon;
                    break;

                case ModelEvent.ElementRemoved removed:
                    var removedEntity = await FindElementAsync(removed.ElementId, ct);
                    if (removedEntity is not null) _db.Elements.Remove(removedEntity);
                    break;

                case ModelEvent.ElementProvenanceRecorded elementProvenance:
                    var provenanceEntity = await FindElementAsync(elementProvenance.ElementId, ct);
                    if (provenanceEntity is not null)
                        ApplyProvenance(provenanceEntity, elementProvenance.Status, elementProvenance.Provenance);
                    break;

                case ModelEvent.RelationshipAdded relAdded:
                    _db.Relationships.Add(new RelationshipEntity
                    {
                        Id = relAdded.Id,
                        ProjectId = _projectId,
                        Branch = _branch,
                        FromId = relAdded.FromId,
                        ToId = relAdded.ToId,
                        Label = relAdded.Label,
                        Technology = relAdded.Technology,
                        MessageId = relAdded.MessageId,
                        LineShape = null,
                        Kind = relAdded.Kind.ToString().ToLowerInvariant()
                    });
                    break;

                case ModelEvent.RelationshipLabelChanged labelChanged:
                    var labelEntity = await FindRelationshipAsync(labelChanged.RelationshipId, ct);
                    if (labelEntity is not null) labelEntity.Label = labelChanged.Label;
                    break;

                case ModelEvent.RelationshipTechnologyChanged relTechChanged:
                    var relTechEntity = await FindRelationshipAsync(relTechChanged.RelationshipId, ct);
                    if (relTechEntity is not null) relTechEntity.Technology = relTechChanged.Technology;
                    break;

                case ModelEvent.RelationshipLineShapeChanged lineShapeChanged:
                    var lineShapeEntity = await FindRelationshipAsync(lineShapeChanged.RelationshipId, ct);
                    if (lineShapeEntity is not null) lineShapeEntity.LineShape = lineShapeChanged.LineShape;
                    break;

                case ModelEvent.RelationshipKindChanged kindChanged:
                    var kindEntity = await FindRelationshipAsync(kindChanged.RelationshipId, ct);
                    if (kindEntity is not null) kindEntity.Kind = kindChanged.Kind.ToString().ToLowerInvariant();
                    break;

                case ModelEvent.RelationshipProvenanceRecorded relationshipProvenance:
                    var relProvenanceEntity = await FindRelationshipAsync(relationshipProvenance.RelationshipId, ct);
                    if (relProvenanceEntity is not null)
                        ApplyProvenance(relProvenanceEntity, relationshipProvenance.Status, relationshipProvenance.Provenance);
                    break;

                case ModelEvent.RelationshipRemoved relRemoved:
                    var relRemovedEntity = await FindRelationshipAsync(relRemoved.RelationshipId, ct);
                    if (relRemovedEntity is not null) _db.Relationships.Remove(relRemovedEntity);
                    break;

                case ModelEvent.MessageAdded msgAdded:
                    _db.Messages.Add(new MessageEntity
                    {
                        Id = msgAdded.Id,
                        ProjectId = _projectId,
                        Branch = _branch,
                        Name = msgAdded.Name,
                        Description = msgAdded.Description,
                        ProducerId = msgAdded.ProducerId,
                        ConsumerIds = msgAdded.ConsumerIds.ToGuids(),
                        SchemaId = msgAdded.SchemaId,
                        Tags = msgAdded.Tags
                    });
                    break;

                case ModelEvent.MessageUpdated msgUpdated:
                    var msgEntity = await FindMessageAsync(msgUpdated.MessageId, ct);
                    if (msgEntity is not null)
                    {
                        if (msgUpdated.Name is not null) msgEntity.Name = msgUpdated.Name;
                        if (msgUpdated.Description is not null) msgEntity.Description = msgUpdated.Description;
                        if (msgUpdated.ProducerId is not null) msgEntity.ProducerId = msgUpdated.ProducerId.Value;
                        if (msgUpdated.ConsumerIds is not null) msgEntity.ConsumerIds = msgUpdated.ConsumerIds.ToGuids();
                        if (msgUpdated.SetSchemaId) msgEntity.SchemaId = msgUpdated.SchemaId;
                        if (msgUpdated.Tags is not null) msgEntity.Tags = msgUpdated.Tags;
                    }
                    break;

                case ModelEvent.MessageRemoved msgRemoved:
                    var msgRemovedEntity = await FindMessageAsync(msgRemoved.MessageId, ct);
                    if (msgRemovedEntity is not null) _db.Messages.Remove(msgRemovedEntity);
                    break;

                case ModelEvent.MessageProvenanceRecorded messageProvenance:
                    var msgProvenanceEntity = await FindMessageAsync(messageProvenance.MessageId, ct);
                    if (msgProvenanceEntity is not null)
                        ApplyProvenance(msgProvenanceEntity, messageProvenance.Status, messageProvenance.Provenance);
                    break;
            }
        }

        if (events.Count > 0)
            await _db.SaveChangesAsync(ct);
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
}
