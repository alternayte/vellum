// src/Vellum/Modules/Docs/CreateDoc.cs
using System.Security.Claims;
using Vellum.Modules.Docs.Entities;
using Vellum.Modules.Workspaces.Authorization;

namespace Vellum.Modules.Docs;

public sealed record CreateDocRequest(Guid Id, string Title, Guid? SpaceId = null, Guid? ElementId = null, Guid? DraftId = null, string? Type = null);

public static class CreateDoc
{
    public static async Task<IResult> Handle(
        Guid projectId,
        CreateDocRequest request,
        ClaimsPrincipal user,
        DocsDbContext db,
        WorkspaceAuthorizationService auth,
        CancellationToken ct)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier)!;
        await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Editor, ct);

        var existing = await db.Documents.FindAsync([request.Id], ct);
        if (existing is not null)
            return Results.Ok(ToDto(existing));

        var doc = new DocumentEntity
        {
            Id = request.Id,
            ProjectId = projectId,
            SpaceId = request.SpaceId,
            ElementId = request.ElementId,
            Title = request.Title,
            CreatedBy = userId,
            DraftId = request.DraftId,
            Type = request.Type
        };
        db.Documents.Add(doc);
        await db.SaveChangesAsync(ct);

        return Results.Created($"/api/projects/{projectId}/docs/{doc.Id}", ToDto(doc));
    }

    private static DocDto ToDto(DocumentEntity d) =>
        new(d.Id, d.ProjectId, d.SpaceId, d.ElementId, d.Title, d.Content, d.CreatedBy, d.CreatedAt, d.UpdatedAt, d.DraftId, d.Type);
}
