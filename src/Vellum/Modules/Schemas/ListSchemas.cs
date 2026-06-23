using Microsoft.EntityFrameworkCore;
using Vellum.Shared;

namespace Vellum.Modules.Schemas;

public static class ListSchemas
{
    public static async Task<IResult> Handle(
        Guid projectId,
        string? cursor, int? limit,
        SchemasDbContext db, CancellationToken ct)
    {
        var pageSize = Math.Clamp(limit ?? 50, 1, 200);

        var query = db.Schemas.AsNoTracking()
            .Where(s => s.ProjectId == projectId);

        var decoded = CursorEncoder.Decode(cursor);
        if (decoded is not null)
        {
            var (sortKey, afterId) = decoded.Value;
            query = query.Where(s => string.Compare(s.Name, sortKey) > 0
                || (s.Name == sortKey && s.Id.CompareTo(afterId) > 0));
        }

        var items = await query
            .OrderBy(s => s.Name).ThenBy(s => s.Id)
            .Take(pageSize + 1)
            .Select(s => new SchemaDto(s.Id, s.Name, s.Description, s.Content, s.Version))
            .ToListAsync(ct);

        string? nextCursor = null;
        if (items.Count > pageSize)
        {
            items = items[..pageSize];
            var last = items[^1];
            nextCursor = CursorEncoder.Encode(last.Name, last.Id);
        }

        return Results.Ok(new Page<SchemaDto>(items, nextCursor));
    }
}
