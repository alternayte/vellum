using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Vellum.Modules.Docs.Entities;
using Vellum.Modules.Workspaces.Authorization;
using Vellum.Shared;

namespace Vellum.Modules.Docs;

public sealed record CreateDocRequest(Guid Id, string Title, Guid? SpaceId = null, Guid? ElementId = null, Guid? DraftId = null, string? Type = null);

public sealed record UpdateDocRequest(
    string? Title, string? Content, Guid? SpaceId, Guid? ElementId,
    bool SetSpaceId = false, bool SetElementId = false,
    Guid? DraftId = null, string? Type = null,
    bool SetDraftId = false, bool SetType = false);

public sealed record DocDto(
    Guid Id, Guid ProjectId, Guid? SpaceId, Guid? ElementId,
    string Title, string Content, string CreatedBy,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    Guid? DraftId, string? Type);

/// <summary>The documents of a project: free text, optionally in a space and about an element or a draft.</summary>
public static class DocEndpoints
{
    /// <summary>A page of Markdown. The request body limit (<see cref="RequestBodyLimit.Document"/>) is the bound in bytes.</summary>
    public const int MaxContentChars = 1_000_000;

    public static WebApplication MapDocEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/projects/{projectId}/docs")
            .RequireAuthorization()
            // A page of prose is larger than a command.
            .WithBodyLimit(RequestBodyLimit.Document)
            .WithTags("Documents");

        group.MapPost("/", Create);
        group.MapGet("/", List);
        group.MapGet("/{docId}", Get);
        group.MapPatch("/{docId}", Update);
        group.MapDelete("/{docId}", Delete);

        return app;
    }

    private static async Task<IResult> Create(
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

    private static async Task<IResult> List(
        Guid projectId,
        Guid? spaceId, Guid? elementId, Guid? draftId, string? type, string? cursor, int? limit,
        ClaimsPrincipal user,
        AppDbContext db,
        WorkspaceAuthorizationService auth,
        CancellationToken ct)
    {
        await auth.RequireProjectRoleAsync(projectId, user.UserId(), WorkspaceRole.Viewer, ct);

        var pageSize = Math.Clamp(limit ?? 50, 1, 200);

        var query = db.Documents.AsNoTracking()
            .Where(d => d.ProjectId == projectId);

        if (spaceId.HasValue) query = query.Where(d => d.SpaceId == spaceId.Value);
        if (elementId.HasValue) query = query.Where(d => d.ElementId == elementId.Value);
        if (draftId.HasValue) query = query.Where(d => d.DraftId == draftId.Value);
        if (type is not null) query = query.Where(d => d.Type == type);

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
                d.Title, string.Empty, d.CreatedBy, d.CreatedAt, d.UpdatedAt, d.DraftId, d.Type))
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

    private static async Task<IResult> Get(
        Guid projectId, Guid docId,
        ClaimsPrincipal user,
        AppDbContext db,
        WorkspaceAuthorizationService auth,
        CancellationToken ct)
    {
        await auth.RequireProjectRoleAsync(projectId, user.UserId(), WorkspaceRole.Viewer, ct);

        var doc = await db.Documents.AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == docId && d.ProjectId == projectId, ct);
        return doc is null ? NotFound() : Results.Ok(ToDto(doc));
    }

    private static async Task<IResult> Update(
        Guid projectId, Guid docId,
        UpdateDocRequest request,
        ClaimsPrincipal user,
        AppDbContext db,
        WorkspaceAuthorizationService auth,
        TimeProvider time,
        CancellationToken ct)
    {
        await auth.RequireProjectRoleAsync(projectId, user.UserId(), WorkspaceRole.Editor, ct);

        if (ResultExtensions.Refusal(
                request.Title is null ? null : ResultExtensions.Required("title", request.Title),
                TextRule.Line("title", request.Title),
                TextRule.Lines("content", request.Content, MaxContentChars),
                TextRule.Line("type", request.Type, TextRule.MaxIdentifier),
                DocType.Check(request.Type)) is { } refusal)
            return refusal;

        var doc = await db.Documents.FindAsync([docId], ct);
        if (doc is null || doc.ProjectId != projectId)
            return NotFound();

        if (request.Title is not null) doc.Title = request.Title;
        if (request.Content is not null) doc.Content = request.Content;
        if (request.SetSpaceId || request.SpaceId.HasValue) doc.SpaceId = request.SpaceId;
        if (request.SetElementId || request.ElementId.HasValue) doc.ElementId = request.ElementId;
        if (request.SetDraftId || request.DraftId.HasValue) doc.DraftId = request.DraftId;
        if (request.SetType || request.Type is not null) doc.Type = request.Type;
        doc.UpdatedAt = time.GetUtcNow();

        await db.SaveChangesAsync(ct);
        return Results.Ok(ToDto(doc));
    }

    private static async Task<IResult> Delete(
        Guid projectId, Guid docId,
        ClaimsPrincipal user,
        AppDbContext db,
        WorkspaceAuthorizationService auth,
        CancellationToken ct)
    {
        await auth.RequireProjectRoleAsync(projectId, user.UserId(), WorkspaceRole.Editor, ct);

        var doc = await db.Documents.FindAsync([docId], ct);
        if (doc is null || doc.ProjectId != projectId)
            return NotFound();

        db.Documents.Remove(doc);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static IResult NotFound() => Results.NotFound(new ErrorResponse("not_found", "Document not found"));

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
