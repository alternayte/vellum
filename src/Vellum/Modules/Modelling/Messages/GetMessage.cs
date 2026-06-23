using Microsoft.EntityFrameworkCore;
using Vellum.Shared;

namespace Vellum.Modules.Modelling.Messages;

public static class GetMessage
{
    public static async Task<IResult> Handle(
        Guid projectId, Guid messageId,
        ModellingDbContext db, CancellationToken ct)
    {
        var entity = await db.Messages.AsNoTracking()
            .FirstOrDefaultAsync(m => m.Id == messageId && m.ProjectId == projectId, ct);

        if (entity is null) return Results.NotFound(new ErrorResponse("not_found", "Message not found"));

        return Results.Ok(new MessageDto(
            entity.Id, entity.Name, entity.Description,
            entity.ProducerId, entity.ConsumerIds, entity.SchemaId, entity.Tags));
    }
}
