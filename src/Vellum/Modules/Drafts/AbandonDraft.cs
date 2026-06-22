// src/Vellum/Modules/Drafts/AbandonDraft.cs
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Vellum.Modules.Workspaces.Authorization;
using Vellum.Shared;

namespace Vellum.Modules.Drafts;

public static class AbandonDraft
{
    public static async Task<IResult> Handle(
        Guid projectId, Guid draftId,
        ClaimsPrincipal user,
        DraftsDbContext db,
        WorkspaceAuthorizationService auth,
        CancellationToken ct)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier)!;
        await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Editor, ct);

        var draft = await db.Drafts
            .FirstOrDefaultAsync(d => d.Id == draftId && d.ProjectId == projectId, ct);
        if (draft is null)
            return Results.NotFound(new ErrorResponse("not_found", "Draft not found"));

        if (draft.Status != "open")
            return Results.Conflict(new ErrorResponse("conflict", $"Draft is already {draft.Status}"));

        draft.Status = "abandoned";
        draft.AbandonedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        return Results.Ok(CreateDraft.ToDto(draft));
    }
}
