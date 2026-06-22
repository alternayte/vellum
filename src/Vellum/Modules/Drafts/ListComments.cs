// src/Vellum/Modules/Drafts/ListComments.cs
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Vellum.Modules.Workspaces.Authorization;
using Vellum.Shared;

namespace Vellum.Modules.Drafts;

public static class ListComments
{
    public static async Task<IResult> Handle(
        Guid projectId, Guid draftId,
        Guid? entityId, string? cursor, int? limit,
        ClaimsPrincipal user,
        DraftsDbContext db,
        WorkspaceAuthorizationService auth,
        CancellationToken ct)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier)!;
        await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Viewer, ct);

        var draft = await db.Drafts.AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == draftId && d.ProjectId == projectId, ct);
        if (draft is null)
            return Results.NotFound(new ErrorResponse("not_found", "Draft not found"));

        var pageSize = Math.Clamp(limit ?? 50, 1, 200);

        var query = db.Comments.AsNoTracking()
            .Where(c => c.DraftId == draftId);

        if (entityId.HasValue)
            query = query.Where(c => c.EntityId == entityId.Value);

        query = query.OrderBy(c => c.CreatedAt).ThenBy(c => c.Id);

        var decoded = CursorEncoder.Decode(cursor);
        if (decoded is not null)
        {
            var (sortKey, afterId) = decoded.Value;
            var afterDate = DateTimeOffset.Parse(sortKey);
            query = query.Where(c => c.CreatedAt > afterDate
                || (c.CreatedAt == afterDate && c.Id.CompareTo(afterId) > 0));
        }

        var items = await query
            .Take(pageSize + 1)
            .Select(c => new CommentDto(c.Id, c.DraftId, c.EntityId, c.EntityType, c.Author, c.Body, c.CreatedAt, c.UpdatedAt))
            .ToListAsync(ct);

        string? nextCursor = null;
        if (items.Count > pageSize)
        {
            items.RemoveAt(items.Count - 1);
            var last = items[^1];
            nextCursor = CursorEncoder.Encode(last.CreatedAt.ToString("O"), last.Id);
        }

        return Results.Ok(new Page<CommentDto>(items, nextCursor));
    }
}
