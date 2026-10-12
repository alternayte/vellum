using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Vellum.Kernel.Aggregates;
using Vellum.Modules.Modelling.Entities;
using Vellum.Modules.Modelling.Model;
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
        WorkspacesDbContext workspacesDb,
        ModellingDbContext modellingDb,
        AggregateStore store,
        WorkspaceAuthorizationService auth,
        CancellationToken ct)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier)!;
        await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Editor, ct);

        if (string.IsNullOrWhiteSpace(request.Name))
            return Results.BadRequest(new ErrorResponse("validation_error", "Revision name is required"));

        var project = await workspacesDb.Projects.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == projectId, ct);
        if (project is null) return Results.NotFound(new ErrorResponse("not_found", "Project not found"));

        var existingById = await modellingDb.Revisions.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == request.Id, ct);
        if (existingById is not null)
        {
            return existingById.ProjectId == projectId
                ? Results.Ok(ToDto(existingById))
                : Results.Conflict(new ErrorResponse("conflict", "Revision id is already in use"));
        }

        if (await modellingDb.Revisions.AnyAsync(
                r => r.ProjectId == projectId && r.Name == request.Name, ct))
        {
            return Results.Conflict(new ErrorResponse("conflict", "A revision with this name already exists"));
        }

        var (state, version) = await store.LoadAsync<ModelState, ModelEvent>(project.StreamId, ct);

        var revision = new RevisionEntity
        {
            Id = request.Id,
            ProjectId = projectId,
            Name = request.Name,
            Note = request.Note,
            StreamId = project.StreamId,
            Version = version,
            State = JsonSerializer.Serialize(state, JsonOpts),
            CreatedBy = userId,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        modellingDb.Revisions.Add(revision);
        await modellingDb.SaveChangesAsync(ct);

        return Results.Created(
            $"/api/projects/{projectId}/revisions/{revision.Id}",
            ToDto(revision));
    }

    internal static RevisionDto ToDto(RevisionEntity revision) => new(
        revision.Id, revision.ProjectId, revision.Name, revision.Note,
        revision.StreamId, revision.Version, revision.CreatedBy, revision.CreatedAt);
}
