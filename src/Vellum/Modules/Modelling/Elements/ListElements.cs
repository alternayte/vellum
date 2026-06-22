using Microsoft.EntityFrameworkCore;
using Vellum.Shared;

namespace Vellum.Modules.Modelling.Elements;

public static class ListElements
{
    public static async Task<IResult> Handle(
        Guid projectId, Guid streamId,
        string? kind, string? status, Guid? parentId,
        string? cursor, int? limit,
        ModellingDbContext db, CancellationToken ct)
    {
        var pageSize = Math.Clamp(limit ?? 50, 1, 200);

        var query = db.Elements.AsNoTracking()
            .Where(e => e.ProjectId == projectId && e.Branch == streamId);

        if (kind is not null) query = query.Where(e => e.Kind == kind.ToLowerInvariant());
        if (status is not null) query = query.Where(e => e.Status == status.ToLowerInvariant());
        if (parentId.HasValue) query = query.Where(e => e.ParentId == parentId);

        var decoded = CursorEncoder.Decode(cursor);
        if (decoded is not null)
        {
            var (sortKey, afterId) = decoded.Value;
            query = query.Where(e => string.Compare(e.Name, sortKey) > 0
                || (e.Name == sortKey && e.Id.CompareTo(afterId) > 0));
        }

        var items = await query
            .OrderBy(e => e.Name).ThenBy(e => e.Id)
            .Take(pageSize + 1)
            .Select(e => new ElementDto(e.Id, e.Kind, e.Name, e.Description,
                e.Technology, e.OwnerId, e.Status, e.ParentId, e.Tags))
            .ToListAsync(ct);

        string? nextCursor = null;
        if (items.Count > pageSize)
        {
            items = items[..pageSize];
            var last = items[^1];
            nextCursor = CursorEncoder.Encode(last.Name, last.Id);
        }

        return Results.Ok(new Page<ElementDto>(items, nextCursor));
    }
}
