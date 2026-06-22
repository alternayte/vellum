// src/Vellum/Modules/Docs/UpdateDoc.cs
using System.Security.Claims;
using Vellum.Modules.Workspaces.Authorization;
using Vellum.Shared;

namespace Vellum.Modules.Docs;

public sealed record UpdateDocRequest(
    string? Title, string? Content, Guid? SpaceId, Guid? ElementId,
    bool SetSpaceId = false, bool SetElementId = false,
    Guid? DraftId = null, string? AdrStatus = null,
    bool SetDraftId = false, bool SetAdrStatus = false);

public static class UpdateDoc
{
    public static async Task<IResult> Handle(
        Guid projectId, Guid docId,
        UpdateDocRequest request,
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

        if (request.Title is not null) doc.Title = request.Title;
        if (request.Content is not null) doc.Content = request.Content;
        if (request.SetSpaceId || request.SpaceId.HasValue) doc.SpaceId = request.SpaceId;
        if (request.SetElementId || request.ElementId.HasValue) doc.ElementId = request.ElementId;
        if (request.SetDraftId || request.DraftId.HasValue) doc.DraftId = request.DraftId;
        if (request.SetAdrStatus || request.AdrStatus is not null) doc.AdrStatus = request.AdrStatus;
        doc.UpdatedAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(ct);
        return Results.Ok(new DocDto(doc.Id, doc.ProjectId, doc.SpaceId, doc.ElementId,
            doc.Title, doc.Content, doc.CreatedBy, doc.CreatedAt, doc.UpdatedAt, doc.DraftId, doc.AdrStatus));
    }
}
