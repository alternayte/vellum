// src/Vellum/Modules/Docs/DeleteDoc.cs
using System.Security.Claims;
using Vellum.Modules.Workspaces.Authorization;
using Vellum.Shared;

namespace Vellum.Modules.Docs;

public static class DeleteDoc
{
    public static async Task<IResult> Handle(
        Guid projectId, Guid docId,
        ClaimsPrincipal user,
        DocsDbContext db,
        WorkspaceAuthorizationService auth,
        CancellationToken ct)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier)!;
        await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Editor, ct);

        var doc = await db.Documents.FindAsync([docId], ct);
        if (doc is null || doc.ProjectId != projectId)
            return Results.NotFound(new ErrorResponse("not_found", "Document not found"));

        db.Documents.Remove(doc);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }
}
