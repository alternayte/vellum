using Microsoft.EntityFrameworkCore;
using Vellum.Shared;

namespace Vellum.Modules.Modelling.Relationships;

public static class GetRelationship
{
    public static async Task<IResult> Handle(
        Guid projectId, Guid streamId, Guid relationshipId,
        AppDbContext db, CancellationToken ct)
    {
        var entity = await db.Relationships.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == relationshipId && r.ProjectId == projectId && r.Branch == streamId, ct);

        if (entity is null) return Results.NotFound(new ErrorResponse("not_found", "Relationship not found"));

        return Results.Ok(new RelationshipDto(
            entity.Id, entity.FromId, entity.ToId,
            entity.Label, entity.Technology, entity.MessageId, entity.LineShape,
            entity.Kind, entity.FactStatus,
            Model.ProvenanceMapper.ToDto(entity.ProvenanceSource, entity.ProvenanceRevision,
                entity.ProvenanceConfidence, entity.ProvenanceAcceptedBy, entity.ProvenanceAcceptedAt),
            Model.StaleDto.From(entity.StaleSinceRevision, entity.StaleReason)));
    }
}
