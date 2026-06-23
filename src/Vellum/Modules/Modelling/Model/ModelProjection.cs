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
                        Tags = added.Tags
                    });
                    break;

                case ModelEvent.ElementRenamed renamed:
                    var renamedEntity = await _db.Elements.FindAsync([renamed.ElementId], ct);
                    if (renamedEntity is not null) renamedEntity.Name = renamed.Name;
                    break;

                case ModelEvent.ElementDescriptionChanged descChanged:
                    var descEntity = await _db.Elements.FindAsync([descChanged.ElementId], ct);
                    if (descEntity is not null) descEntity.Description = descChanged.Description;
                    break;

                case ModelEvent.ElementTechnologyChanged techChanged:
                    var techEntity = await _db.Elements.FindAsync([techChanged.ElementId], ct);
                    if (techEntity is not null) techEntity.Technology = techChanged.Technology;
                    break;

                case ModelEvent.ElementOwnerChanged ownerChanged:
                    var ownerEntity = await _db.Elements.FindAsync([ownerChanged.ElementId], ct);
                    if (ownerEntity is not null) ownerEntity.OwnerId = ownerChanged.OwnerId;
                    break;

                case ModelEvent.ElementReparented reparented:
                    var reparentedEntity = await _db.Elements.FindAsync([reparented.ElementId], ct);
                    if (reparentedEntity is not null) reparentedEntity.ParentId = reparented.ParentId;
                    break;

                case ModelEvent.ElementStatusChanged statusChanged:
                    var statusEntity = await _db.Elements.FindAsync([statusChanged.ElementId], ct);
                    if (statusEntity is not null) statusEntity.Status = statusChanged.Status.ToString().ToLowerInvariant();
                    break;

                case ModelEvent.ElementRetagged retagged:
                    var retaggedEntity = await _db.Elements.FindAsync([retagged.ElementId], ct);
                    if (retaggedEntity is not null) retaggedEntity.Tags = retagged.Tags;
                    break;

                case ModelEvent.ElementRemoved removed:
                    var removedEntity = await _db.Elements.FindAsync([removed.ElementId], ct);
                    if (removedEntity is not null) _db.Elements.Remove(removedEntity);
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
                        MessageId = relAdded.MessageId
                    });
                    break;

                case ModelEvent.RelationshipLabelChanged labelChanged:
                    var labelEntity = await _db.Relationships.FindAsync([labelChanged.RelationshipId], ct);
                    if (labelEntity is not null) labelEntity.Label = labelChanged.Label;
                    break;

                case ModelEvent.RelationshipTechnologyChanged relTechChanged:
                    var relTechEntity = await _db.Relationships.FindAsync([relTechChanged.RelationshipId], ct);
                    if (relTechEntity is not null) relTechEntity.Technology = relTechChanged.Technology;
                    break;

                case ModelEvent.RelationshipRemoved relRemoved:
                    var relRemovedEntity = await _db.Relationships.FindAsync([relRemoved.RelationshipId], ct);
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
                        ConsumerIds = msgAdded.ConsumerIds,
                        SchemaId = msgAdded.SchemaId,
                        Tags = msgAdded.Tags
                    });
                    break;

                case ModelEvent.MessageUpdated msgUpdated:
                    var msgEntity = await _db.Messages.FindAsync([msgUpdated.MessageId], ct);
                    if (msgEntity is not null)
                    {
                        if (msgUpdated.Name is not null) msgEntity.Name = msgUpdated.Name;
                        if (msgUpdated.Description is not null) msgEntity.Description = msgUpdated.Description;
                        if (msgUpdated.ProducerId is not null) msgEntity.ProducerId = msgUpdated.ProducerId.Value;
                        if (msgUpdated.ConsumerIds is not null) msgEntity.ConsumerIds = msgUpdated.ConsumerIds;
                        if (msgUpdated.SetSchemaId) msgEntity.SchemaId = msgUpdated.SchemaId;
                    }
                    break;

                case ModelEvent.MessageRemoved msgRemoved:
                    var msgRemovedEntity = await _db.Messages.FindAsync([msgRemoved.MessageId], ct);
                    if (msgRemovedEntity is not null) _db.Messages.Remove(msgRemovedEntity);
                    break;
            }
        }

        if (events.Count > 0)
            await _db.SaveChangesAsync(ct);
    }
}
