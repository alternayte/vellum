using System.Security.Claims;
using System.Text.Json;
using Deedbox;
using Microsoft.EntityFrameworkCore;
using Vellum.Modules.Modelling.Entities;
using Vellum.Modules.Modelling.Model;
using Vellum.Modules.Schemas;
using Vellum.Modules.Workspaces;
using Vellum.Modules.Workspaces.Authorization;
using Vellum.Shared;

namespace Vellum.Modules.Modelling.Revisions;

public sealed record CreateRevisionRequest(Guid Id, string Name, string? Note);

public sealed record RevisionDto(
    Guid Id, Guid ProjectId, string Name, string? Note,
    Guid StreamId, int Version, string CreatedBy, DateTimeOffset CreatedAt);

public static class CreateRevision
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static async Task<IResult> Handle(
        Guid projectId,
        CreateRevisionRequest request,
        ClaimsPrincipal user,
        HttpContext http,
        WorkspaceAuthorizationService auth,
        CancellationToken ct)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier)!;
        await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Editor, ct);

        if (request.Id == Guid.Empty)
            return Invalid("id", "An ID is required");
        if (string.IsNullOrWhiteSpace(request.Name))
            return Results.BadRequest(new ErrorResponse("validation_error", "Revision name is required"));
        if ((TextRule.Line("name", request.Name) ?? TextRule.Lines("note", request.Note)) is { } textError)
            return Invalid(textError.Field, textError.Message);

        // The snapshot and the hold on the schemas it names are one transaction. A unit that
        // fails with a deadlock wrote nothing and runs again.
        return await SharedTransaction.RunAsync(http.RequestServices, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();

            var project = await db.Projects.AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == projectId, ct);
            if (project is null) return Results.NotFound(new ErrorResponse("not_found", "Project not found"));

            var existingById = await db.Revisions.AsNoTracking()
                .FirstOrDefaultAsync(r => r.Id == request.Id, ct);
            if (existingById is not null)
            {
                return existingById.ProjectId == projectId
                    ? Results.Ok(ToDto(existingById))
                    : Results.Conflict(new ErrorResponse("conflict", "Revision id is already in use"));
            }

            if (await db.Revisions.AnyAsync(
                    r => r.ProjectId == projectId && r.Name == request.Name, ct))
            {
                return Results.Conflict(new ErrorResponse("conflict", "A revision with this name already exists"));
            }

            // The stream lock gives the newest model, in which every schema reference points at
            // a schema that exists. Holding those schema rows until the commit keeps a delete
            // from running between this read and the stored snapshot (see SchemaReferences).
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var store = services.GetRequiredService<IEventStore>().UseDbContext(db);
            var pinned = await store.Execute<ModelState>(StreamId.From(project.StreamId), _ => [], ct);
            await SchemaReferences.HoldAsync(
                db, projectId, ContractDecider.ReferencedSchemaIds(pinned.State), ct);

            var revision = new RevisionEntity
            {
                Id = request.Id,
                ProjectId = projectId,
                Name = request.Name,
                Note = request.Note,
                StreamId = project.StreamId,
                Version = checked((int)pinned.Version),
                State = JsonSerializer.Serialize(pinned.State, JsonOpts),
                CreatedBy = userId,
                CreatedAt = DateTimeOffset.UtcNow,
            };

            db.Revisions.Add(revision);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);

            return Results.Created(
                $"/api/projects/{projectId}/revisions/{revision.Id}",
                ToDto(revision));
        }, ct);
    }

    /// <summary>The model a revision stores.</summary>
    internal static ModelState ReadState(string state) => JsonSerializer.Deserialize<ModelState>(state, JsonOpts)!;

    internal static RevisionDto ToDto(RevisionEntity revision) => new(
        revision.Id, revision.ProjectId, revision.Name, revision.Note,
        revision.StreamId, revision.Version, revision.CreatedBy, revision.CreatedAt);

    private static IResult Invalid(string field, string message) =>
        Results.BadRequest(new ErrorResponse("validation_error", "Validation failed", Errors: [new FieldError(field, message)]));
}
