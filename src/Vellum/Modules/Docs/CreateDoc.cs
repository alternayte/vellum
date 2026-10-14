// src/Vellum/Modules/Docs/CreateDoc.cs
using System.Security.Claims;
using Vellum.Modules.Docs.Entities;
using Vellum.Modules.Workspaces.Authorization;
using Vellum.Shared;

namespace Vellum.Modules.Docs;

public sealed record CreateDocRequest(Guid Id, string Title, Guid? SpaceId = null, Guid? ElementId = null, Guid? DraftId = null, string? Type = null);

public static class CreateDoc
{
    public static async Task<IResult> Handle(
        Guid projectId,
        CreateDocRequest request,
        ClaimsPrincipal user,
        AppDbContext db,
        WorkspaceAuthorizationService auth,
        CancellationToken ct)
    {
        var userId = user.UserId();
        await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Editor, ct);

        if (ResultExtensions.Refusal(
                ResultExtensions.Required("title", request.Title),
                TextRule.Line("title", request.Title),
                TextRule.Line("type", request.Type, TextRule.MaxIdentifier),
                DocType.Check(request.Type)) is { } refusal)
            return refusal;

        // A retry in the same project gets the document back. A document of another project is
        // not shown: the caller learns only that the ID is taken.
        var existing = await db.Documents.FindAsync([request.Id], ct);
        if (existing is not null)
            return existing.ProjectId == projectId ? Results.Ok(ToDto(existing)) : IdInUse.Conflict();

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

/// <summary>What a document may be typed as.</summary>
internal static class DocType
{
    /// <summary>
    /// An ADR is a Review decision: it is event-sourced, an owner accepts it, and the acceptance
    /// binds its content hash. A document of type <c>adr</c> would be free text that looks like one.
    /// </summary>
    public static ValidationError? Check(string? type) =>
        string.Equals(type?.Trim(), "adr", StringComparison.OrdinalIgnoreCase)
            ? new ValidationError("type", "A document cannot be an ADR. Record a decision in Review instead (POST /api/projects/{projectId}/decisions).")
            : null;
}
