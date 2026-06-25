using Microsoft.EntityFrameworkCore;
using Vellum.Shared;

namespace Vellum.Modules.Modelling.Relationships;

public static class ListRelationships
{
    public static async Task<IResult> Handle(
        Guid projectId, Guid streamId,
        Guid? fromId, Guid? toId,
        string? cursor, int? limit,
        ModellingDbContext db, CancellationToken ct)
    {
        var pageSize = Math.Clamp(limit ?? 50, 1, 200);

        var query = db.Relationships.AsNoTracking()
            .Where(r => r.ProjectId == projectId && r.Branch == streamId);

        if (fromId.HasValue) query = query.Where(r => r.FromId == fromId);
        if (toId.HasValue) query = query.Where(r => r.ToId == toId);

        var decoded = CursorEncoder.Decode(cursor);
        if (decoded is not null)
        {
            var (_, afterId) = decoded.Value;
            query = query.Where(r => r.Id.CompareTo(afterId) > 0);
        }

        var items = await query
            .OrderBy(r => r.Id)
            .Take(pageSize + 1)
            .Select(r => new RelationshipDto(r.Id, r.FromId, r.ToId, r.Label, r.Technology, r.MessageId, r.LineShape))
            .ToListAsync(ct);

        string? nextCursor = null;
        if (items.Count > pageSize)
        {
            items = items[..pageSize];
            var last = items[^1];
            nextCursor = CursorEncoder.Encode(last.Id.ToString(), last.Id);
        }

        return Results.Ok(new Page<RelationshipDto>(items, nextCursor));
    }
}
