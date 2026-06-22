using Microsoft.EntityFrameworkCore;

namespace Vellum.Modules.Modelling.Relationships;

public static class GetRelationship
{
    public static async Task<IResult> Handle(
        Guid projectId, Guid relationshipId,
        ModellingDbContext db, CancellationToken ct)
    {
        var entity = await db.Relationships.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == relationshipId && r.ProjectId == projectId, ct);

        if (entity is null) return Results.NotFound();

        return Results.Ok(new RelationshipDto(
            entity.Id, entity.FromId, entity.ToId,
            entity.Label, entity.Technology, entity.MessageId));
    }
}
