using Microsoft.EntityFrameworkCore;
using Vellum.Shared;

namespace Vellum.Modules.Schemas;

public static class GetSchema
{
    public static async Task<IResult> Handle(
        Guid projectId, Guid schemaId,
        SchemasDbContext db, CancellationToken ct)
    {
        var entity = await db.Schemas.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == schemaId && s.ProjectId == projectId, ct);

        if (entity is null) return Results.NotFound(new ErrorResponse("not_found", "Schema not found"));

        return Results.Ok(new SchemaDto(
            entity.Id, entity.Name, entity.Description, entity.Content, entity.Version));
    }
}
