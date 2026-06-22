// src/Vellum/Modules/Docs/ListDocs.cs
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Vellum.Modules.Workspaces.Authorization;
using Vellum.Shared;

namespace Vellum.Modules.Docs;

public static class ListDocs
{
    public static async Task<IResult> Handle(
        Guid projectId,
        Guid? spaceId, Guid? elementId, Guid? draftId, string? adrStatus, string? cursor, int? limit,
        ClaimsPrincipal user,
        DocsDbContext db,
        WorkspaceAuthorizationService auth,
        CancellationToken ct)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier)!;
        await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Viewer, ct);

        var pageSize = Math.Clamp(limit ?? 50, 1, 200);

        var query = db.Documents.AsNoTracking()
            .Where(d => d.ProjectId == projectId);

        if (spaceId.HasValue) query = query.Where(d => d.SpaceId == spaceId.Value);
        if (elementId.HasValue) query = query.Where(d => d.ElementId == elementId.Value);
        if (draftId.HasValue) query = query.Where(d => d.DraftId == draftId.Value);
        if (adrStatus is not null) query = query.Where(d => d.AdrStatus == adrStatus);

        query = query.OrderBy(d => d.Title).ThenBy(d => d.Id);

        var decoded = CursorEncoder.Decode(cursor);
        if (decoded is not null)
        {
            var (sortKey, afterId) = decoded.Value;
            query = query.Where(d => d.Title.CompareTo(sortKey) > 0
                || (d.Title == sortKey && d.Id.CompareTo(afterId) > 0));
        }

        var items = await query
            .Take(pageSize + 1)
            .Select(d => new DocDto(d.Id, d.ProjectId, d.SpaceId, d.ElementId,
                d.Title, string.Empty, d.CreatedBy, d.CreatedAt, d.UpdatedAt, d.DraftId, d.AdrStatus))
            .ToListAsync(ct);

        string? nextCursor = null;
        if (items.Count > pageSize)
        {
            items.RemoveAt(items.Count - 1);
            var last = items[^1];
            nextCursor = CursorEncoder.Encode(last.Title, last.Id);
        }

        return Results.Ok(new Page<DocDto>(items, nextCursor));
    }
}
