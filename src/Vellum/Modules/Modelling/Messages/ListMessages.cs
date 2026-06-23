using Microsoft.EntityFrameworkCore;
using Vellum.Shared;

namespace Vellum.Modules.Modelling.Messages;

public static class ListMessages
{
    public static async Task<IResult> Handle(
        Guid projectId, Guid streamId,
        Guid? producerId, string? cursor, int? limit,
        ModellingDbContext db, CancellationToken ct)
    {
        var pageSize = Math.Clamp(limit ?? 50, 1, 200);

        var query = db.Messages.AsNoTracking()
            .Where(m => m.ProjectId == projectId && m.Branch == streamId);

        if (producerId.HasValue) query = query.Where(m => m.ProducerId == producerId);

        var decoded = CursorEncoder.Decode(cursor);
        if (decoded is not null)
        {
            var (sortKey, afterId) = decoded.Value;
            query = query.Where(m => string.Compare(m.Name, sortKey) > 0
                || (m.Name == sortKey && m.Id.CompareTo(afterId) > 0));
        }

        var items = await query
            .OrderBy(m => m.Name).ThenBy(m => m.Id)
            .Take(pageSize + 1)
            .Select(m => new MessageDto(m.Id, m.Name, m.Description,
                m.ProducerId, m.ConsumerIds, m.SchemaId, m.Tags))
            .ToListAsync(ct);

        string? nextCursor = null;
        if (items.Count > pageSize)
        {
            items = items[..pageSize];
            var last = items[^1];
            nextCursor = CursorEncoder.Encode(last.Name, last.Id);
        }

        return Results.Ok(new Page<MessageDto>(items, nextCursor));
    }
}
