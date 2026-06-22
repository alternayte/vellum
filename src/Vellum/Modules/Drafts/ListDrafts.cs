// src/Vellum/Modules/Drafts/ListDrafts.cs
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Vellum.Modules.Workspaces.Authorization;
using Vellum.Shared;

namespace Vellum.Modules.Drafts;

public static class ListDrafts
{
    public static async Task<IResult> Handle(
        Guid projectId,
        string? status, string? cursor, int? limit,
        ClaimsPrincipal user,
        DraftsDbContext db,
        WorkspaceAuthorizationService auth,
        CancellationToken ct)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier)!;
        await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Viewer, ct);

        var pageSize = Math.Clamp(limit ?? 50, 1, 200);

        var query = db.Drafts.AsNoTracking()
            .Where(d => d.ProjectId == projectId);

        if (!string.IsNullOrEmpty(status))
            query = query.Where(d => d.Status == status);

        query = query.OrderByDescending(d => d.CreatedAt).ThenBy(d => d.Id);

        var decoded = CursorEncoder.Decode(cursor);
        if (decoded is not null)
        {
            var (sortKey, afterId) = decoded.Value;
            var afterDate = DateTimeOffset.Parse(sortKey);
            query = query.Where(d => d.CreatedAt < afterDate
                || (d.CreatedAt == afterDate && d.Id.CompareTo(afterId) > 0));
        }

        var items = await query
            .Take(pageSize + 1)
            .Select(d => CreateDraft.ToDto(d))
            .ToListAsync(ct);

        string? nextCursor = null;
        if (items.Count > pageSize)
        {
            items.RemoveAt(items.Count - 1);
            var last = items[^1];
            nextCursor = CursorEncoder.Encode(last.CreatedAt.ToString("O"), last.Id);
        }

        return Results.Ok(new Page<DraftDto>(items, nextCursor));
    }
}
