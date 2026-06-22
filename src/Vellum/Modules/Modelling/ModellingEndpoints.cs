using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Vellum.Kernel.CommandHandling;
using Vellum.Kernel.Results;
using Vellum.Modules.Modelling.Elements;
using Vellum.Modules.Modelling.Relationships;
using Vellum.Modules.Workspaces;
using Vellum.Modules.Workspaces.Authorization;
using Vellum.Shared;

namespace Vellum.Modules.Modelling;

public static class ModellingEndpoints
{
    public static WebApplication MapModellingEndpoints(this WebApplication app)
    {
        var project = app.MapGroup("/api/projects/{projectId}")
            .RequireAuthorization();

        var elements = project.MapGroup("/elements").WithTags("Elements");

        elements.MapPost("/", async (
            Guid projectId,
            Guid? branchId,
            AddElementRequest request,
            ClaimsPrincipal user,
            WorkspacesDbContext workspacesDb,
            WorkspaceAuthorizationService auth,
            ICommandHandler<AddElementCommandEnvelope, CommandResult<ElementDto>> handler,
            CancellationToken ct) =>
        {
            var userId = user.FindFirstValue(ClaimTypes.NameIdentifier)!;
            await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Editor, ct);
            var proj = await workspacesDb.Projects.AsNoTracking().FirstAsync(p => p.Id == projectId, ct);
            var streamId = branchId ?? proj.StreamId;
            return (await handler.HandleAsync(
                new AddElementCommandEnvelope(projectId, streamId, userId, request), ct))
                .ToCreatedResult($"/api/projects/{projectId}/elements/{request.Id}");
        });

        elements.MapGet("/", async (
            Guid projectId,
            Guid? branchId,
            string? kind, string? status, Guid? parentId, string? cursor, int? limit,
            ClaimsPrincipal user, WorkspacesDbContext workspacesDb,
            WorkspaceAuthorizationService auth,
            ModellingDbContext db, CancellationToken ct) =>
        {
            var userId = user.FindFirstValue(ClaimTypes.NameIdentifier)!;
            await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Viewer, ct);
            var proj = await workspacesDb.Projects.AsNoTracking().FirstAsync(p => p.Id == projectId, ct);
            var streamId = branchId ?? proj.StreamId;
            return await ListElements.Handle(projectId, streamId, kind, status, parentId, cursor, limit, db, ct);
        });

        elements.MapGet("/{elementId}", async (
            Guid projectId, Guid elementId,
            ClaimsPrincipal user,
            WorkspaceAuthorizationService auth,
            ModellingDbContext db, CancellationToken ct) =>
        {
            var userId = user.FindFirstValue(ClaimTypes.NameIdentifier)!;
            await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Viewer, ct);
            return await GetElement.Handle(projectId, elementId, db, ct);
        });

        elements.MapPatch("/{elementId}", async (
            Guid projectId, Guid elementId,
            Guid? branchId,
            UpdateElementRequest request,
            ClaimsPrincipal user, WorkspacesDbContext workspacesDb,
            WorkspaceAuthorizationService auth,
            ICommandHandler<UpdateElementCommandEnvelope, CommandResult<ElementDto>> handler,
            CancellationToken ct) =>
        {
            var userId = user.FindFirstValue(ClaimTypes.NameIdentifier)!;
            await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Editor, ct);
            var proj = await workspacesDb.Projects.AsNoTracking().FirstAsync(p => p.Id == projectId, ct);
            var streamId = branchId ?? proj.StreamId;
            return (await handler.HandleAsync(
                new UpdateElementCommandEnvelope(projectId, streamId, elementId, userId, request), ct))
                .ToHttpResult();
        });

        elements.MapDelete("/{elementId}", async (
            Guid projectId, Guid elementId,
            Guid? branchId,
            ClaimsPrincipal user, WorkspacesDbContext workspacesDb,
            WorkspaceAuthorizationService auth,
            ICommandHandler<RemoveElementCommandEnvelope, CommandResult> handler,
            CancellationToken ct) =>
        {
            var userId = user.FindFirstValue(ClaimTypes.NameIdentifier)!;
            await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Editor, ct);
            var proj = await workspacesDb.Projects.AsNoTracking().FirstAsync(p => p.Id == projectId, ct);
            var streamId = branchId ?? proj.StreamId;
            return (await handler.HandleAsync(
                new RemoveElementCommandEnvelope(projectId, streamId, elementId, userId), ct))
                .ToHttpResult();
        });

        var relationships = project.MapGroup("/relationships").WithTags("Relationships");

        relationships.MapPost("/", async (
            Guid projectId,
            Guid? branchId,
            AddRelationshipRequest request,
            ClaimsPrincipal user, WorkspacesDbContext workspacesDb,
            WorkspaceAuthorizationService auth,
            ICommandHandler<AddRelationshipCommandEnvelope, CommandResult<RelationshipDto>> handler,
            CancellationToken ct) =>
        {
            var userId = user.FindFirstValue(ClaimTypes.NameIdentifier)!;
            await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Editor, ct);
            var proj = await workspacesDb.Projects.AsNoTracking().FirstAsync(p => p.Id == projectId, ct);
            var streamId = branchId ?? proj.StreamId;
            return (await handler.HandleAsync(
                new AddRelationshipCommandEnvelope(projectId, streamId, userId, request), ct))
                .ToCreatedResult($"/api/projects/{projectId}/relationships/{request.Id}");
        });

        relationships.MapGet("/", async (
            Guid projectId,
            Guid? branchId,
            Guid? fromId, Guid? toId, string? cursor, int? limit,
            ClaimsPrincipal user, WorkspacesDbContext workspacesDb,
            WorkspaceAuthorizationService auth,
            ModellingDbContext db, CancellationToken ct) =>
        {
            var userId = user.FindFirstValue(ClaimTypes.NameIdentifier)!;
            await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Viewer, ct);
            var proj = await workspacesDb.Projects.AsNoTracking().FirstAsync(p => p.Id == projectId, ct);
            var streamId = branchId ?? proj.StreamId;
            return await ListRelationships.Handle(projectId, streamId, fromId, toId, cursor, limit, db, ct);
        });

        relationships.MapGet("/{relationshipId}", async (
            Guid projectId, Guid relationshipId,
            ClaimsPrincipal user,
            WorkspaceAuthorizationService auth,
            ModellingDbContext db, CancellationToken ct) =>
        {
            var userId = user.FindFirstValue(ClaimTypes.NameIdentifier)!;
            await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Viewer, ct);
            return await GetRelationship.Handle(projectId, relationshipId, db, ct);
        });

        relationships.MapPatch("/{relationshipId}", async (
            Guid projectId, Guid relationshipId,
            Guid? branchId,
            UpdateRelationshipRequest request,
            ClaimsPrincipal user, WorkspacesDbContext workspacesDb,
            WorkspaceAuthorizationService auth,
            ICommandHandler<UpdateRelationshipCommandEnvelope, CommandResult<RelationshipDto>> handler,
            CancellationToken ct) =>
        {
            var userId = user.FindFirstValue(ClaimTypes.NameIdentifier)!;
            await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Editor, ct);
            var proj = await workspacesDb.Projects.AsNoTracking().FirstAsync(p => p.Id == projectId, ct);
            var streamId = branchId ?? proj.StreamId;
            return (await handler.HandleAsync(
                new UpdateRelationshipCommandEnvelope(projectId, streamId, relationshipId, userId, request), ct))
                .ToHttpResult();
        });

        relationships.MapDelete("/{relationshipId}", async (
            Guid projectId, Guid relationshipId,
            Guid? branchId,
            ClaimsPrincipal user, WorkspacesDbContext workspacesDb,
            WorkspaceAuthorizationService auth,
            ICommandHandler<RemoveRelationshipCommandEnvelope, CommandResult> handler,
            CancellationToken ct) =>
        {
            var userId = user.FindFirstValue(ClaimTypes.NameIdentifier)!;
            await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Editor, ct);
            var proj = await workspacesDb.Projects.AsNoTracking().FirstAsync(p => p.Id == projectId, ct);
            var streamId = branchId ?? proj.StreamId;
            return (await handler.HandleAsync(
                new RemoveRelationshipCommandEnvelope(projectId, streamId, relationshipId, userId), ct))
                .ToHttpResult();
        });

        return app;
    }
}
