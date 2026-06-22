using Microsoft.EntityFrameworkCore;
using Vellum.Shared;

namespace Vellum.Modules.Modelling.Elements;

public static class GetElement
{
    public static async Task<IResult> Handle(
        Guid projectId, Guid elementId,
        ModellingDbContext db, CancellationToken ct)
    {
        var entity = await db.Elements.AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == elementId && e.ProjectId == projectId, ct);

        if (entity is null) return Results.NotFound(new ErrorResponse("not_found", "Element not found"));

        return Results.Ok(new ElementDto(
            entity.Id, entity.Kind, entity.Name, entity.Description,
            entity.Technology, entity.OwnerId, entity.Status,
            entity.ParentId, entity.Tags));
    }
}
