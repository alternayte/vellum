using Vellum.Modules.Modelling.Entities;

namespace Vellum.Modules.Modelling.Model;

/// <summary>
/// Writes complete read-model rows for a forked branch so a draft or variant reads as a
/// full model, not just the changes made on it. The same architecture ID can exist on the
/// main branch, drafts and variants; the synthetic row ID is assigned automatically.
/// </summary>
public static class BranchRows
{
    public static void Write(Guid projectId, Guid branch, ModelState state, ModellingDbContext db)
    {
        foreach (var element in state.Elements.Values)
        {
            db.Elements.Add(new ElementEntity
            {
                Id = element.Id,
                ProjectId = projectId,
                Branch = branch,
                Kind = element.Kind.ToString().ToLowerInvariant(),
                Name = element.Name,
                Description = element.Description,
                Technology = element.Technology,
                OwnerId = element.OwnerId,
                Status = element.Status.ToString().ToLowerInvariant(),
                ParentId = element.ParentId,
                Tags = element.Tags,
                Icon = element.Icon,
                FactStatus = element.FactStatus.ToApiString(),
                ProvenanceSource = element.Provenance?.Source,
                ProvenanceRevision = element.Provenance?.Revision,
                ProvenanceConfidence = element.Provenance?.Confidence,
                ProvenanceAcceptedBy = element.Provenance?.AcceptedBy,
                ProvenanceAcceptedAt = element.Provenance?.AcceptedAt,
            });
        }

        foreach (var relationship in state.Relationships.Values)
        {
            db.Relationships.Add(new RelationshipEntity
            {
                Id = relationship.Id,
                ProjectId = projectId,
                Branch = branch,
                FromId = relationship.FromId,
                ToId = relationship.ToId,
                Label = relationship.Label,
                Technology = relationship.Technology,
                MessageId = relationship.MessageId,
                LineShape = relationship.LineShape,
                Kind = relationship.Kind.ToString().ToLowerInvariant(),
                FactStatus = relationship.FactStatus.ToApiString(),
                ProvenanceSource = relationship.Provenance?.Source,
                ProvenanceRevision = relationship.Provenance?.Revision,
                ProvenanceConfidence = relationship.Provenance?.Confidence,
                ProvenanceAcceptedBy = relationship.Provenance?.AcceptedBy,
                ProvenanceAcceptedAt = relationship.Provenance?.AcceptedAt,
            });
        }

        foreach (var message in state.Messages.Values)
        {
            db.Messages.Add(new MessageEntity
            {
                Id = message.Id,
                ProjectId = projectId,
                Branch = branch,
                Name = message.Name,
                Description = message.Description,
                ProducerId = message.ProducerId,
                ConsumerIds = message.ConsumerIds.ToGuids(),
                SchemaId = message.SchemaId,
                Tags = message.Tags,
                FactStatus = message.FactStatus.ToApiString(),
                ProvenanceSource = message.Provenance?.Source,
                ProvenanceRevision = message.Provenance?.Revision,
                ProvenanceConfidence = message.Provenance?.Confidence,
                ProvenanceAcceptedBy = message.Provenance?.AcceptedBy,
                ProvenanceAcceptedAt = message.Provenance?.AcceptedAt,
            });
        }
    }
}
