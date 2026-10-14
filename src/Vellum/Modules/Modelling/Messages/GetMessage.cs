using Microsoft.EntityFrameworkCore;
using Vellum.Shared;

namespace Vellum.Modules.Modelling.Messages;

public static class GetMessage
{
    public static async Task<IResult> Handle(
        Guid projectId, Guid streamId, Guid messageId,
        AppDbContext db, CancellationToken ct)
    {
        var entity = await db.Messages.AsNoTracking()
            .FirstOrDefaultAsync(m => m.Id == messageId && m.ProjectId == projectId && m.Branch == streamId, ct);

        if (entity is null) return Results.NotFound(new ErrorResponse("not_found", "Message not found"));

        return Results.Ok(new MessageDto(
            entity.Id, entity.Name, entity.Description,
            entity.ProducerId, entity.ConsumerIds, entity.SchemaId, entity.Tags,
            entity.FactStatus,
            Model.ProvenanceMapper.ToDto(entity.ProvenanceSource, entity.ProvenanceRevision,
                entity.ProvenanceConfidence, entity.ProvenanceAcceptedBy, entity.ProvenanceAcceptedAt, entity.ProvenanceAcceptedAtVersion, entity.ProvenanceAcceptedOnBranch),
            Model.StaleDto.From(entity.StaleSinceRevision, entity.StaleReason)));
    }
}
