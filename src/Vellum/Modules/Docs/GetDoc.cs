// src/Vellum/Modules/Docs/GetDoc.cs
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Vellum.Modules.Workspaces.Authorization;
using Vellum.Shared;

namespace Vellum.Modules.Docs;

public static class GetDoc
{
    public static async Task<IResult> Handle(
        Guid projectId, Guid docId,
        ClaimsPrincipal user,
        DocsDbContext db,
        WorkspaceAuthorizationService auth,
        CancellationToken ct)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier)!;
        await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Viewer, ct);

        var doc = await db.Documents.AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == docId && d.ProjectId == projectId, ct);
        if (doc is null) return Results.NotFound(new ErrorResponse("not_found", "Document not found"));

        return Results.Ok(new DocDto(doc.Id, doc.ProjectId, doc.SpaceId, doc.ElementId,
            doc.Title, doc.Content, doc.CreatedBy, doc.CreatedAt, doc.UpdatedAt, doc.DraftId, doc.Type));
    }
}
