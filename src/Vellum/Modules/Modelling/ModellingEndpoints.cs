using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Vellum.Kernel.Aggregates;
using Vellum.Kernel.CommandHandling;
using Vellum.Kernel.EventStore;
using Vellum.Kernel.Projections;
using Vellum.Kernel.Results;
using Vellum.Modules.Drafts;
using Vellum.Modules.Modelling.Elements;
using Vellum.Modules.Modelling.Export;
using Vellum.Modules.Modelling.Messages;
using Vellum.Modules.Modelling.Model;
using Vellum.Modules.Modelling.Relationships;
using Vellum.Modules.Schemas;
using Vellum.Modules.Workspaces;
using Vellum.Modules.Workspaces.Authorization;
using Vellum.Shared;

namespace Vellum.Modules.Modelling;

public static class ModellingEndpoints
{
    /// <summary>
    /// Resolves the event stream ID for a branchId query parameter.
    /// For write operations, the draft must be open.
    /// For read operations, any draft belonging to the project is allowed.
    /// Returns null if the branchId is invalid (not found or wrong project/status).
    /// </summary>
    private static async Task<Guid?> ResolveBranchStreamIdAsync(
        Guid? branchId, Guid projectId, Guid fallbackStreamId,
        DraftsDbContext draftsDb, bool requireOpen, CancellationToken ct)
    {
        if (branchId is null)
            return fallbackStreamId;

        var draft = await draftsDb.Drafts.AsNoTracking()
            .Where(d => d.StreamId == branchId.Value && d.ProjectId == projectId)
            .Select(d => new { d.StreamId, d.Status })
            .FirstOrDefaultAsync(ct);

        if (draft is null)
            return null;

        if (requireOpen && draft.Status != "open")
            return null;

        return draft.StreamId;
    }

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
            DraftsDbContext draftsDb,
            WorkspaceAuthorizationService auth,
            ICommandHandler<AddElementCommandEnvelope, CommandResult<ElementDto>> handler,
            CancellationToken ct) =>
        {
            var userId = user.FindFirstValue(ClaimTypes.NameIdentifier)!;
            await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Editor, ct);
            var proj = await workspacesDb.Projects.AsNoTracking().FirstAsync(p => p.Id == projectId, ct);
            var streamId = await ResolveBranchStreamIdAsync(branchId, projectId, proj.StreamId, draftsDb, requireOpen: true, ct);
            if (streamId is null)
                return Results.BadRequest(new ErrorResponse("invalid_branch", "Branch not found or not open for this project"));
            return (await handler.HandleAsync(
                new AddElementCommandEnvelope(projectId, streamId.Value, userId, request), ct))
                .ToCreatedResult($"/api/projects/{projectId}/elements/{request.Id}");
        });

        elements.MapGet("/", async (
            Guid projectId,
            Guid? branchId,
            string? kind, string? status, Guid? parentId, string? cursor, int? limit,
            ClaimsPrincipal user, WorkspacesDbContext workspacesDb,
            DraftsDbContext draftsDb,
            WorkspaceAuthorizationService auth,
            ModellingDbContext db, CancellationToken ct) =>
        {
            var userId = user.FindFirstValue(ClaimTypes.NameIdentifier)!;
            await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Viewer, ct);
            var proj = await workspacesDb.Projects.AsNoTracking().FirstAsync(p => p.Id == projectId, ct);
            var streamId = await ResolveBranchStreamIdAsync(branchId, projectId, proj.StreamId, draftsDb, requireOpen: false, ct);
            if (streamId is null)
                return Results.BadRequest(new ErrorResponse("invalid_branch", "Branch not found for this project"));
            return await ListElements.Handle(projectId, streamId.Value, kind, status, parentId, cursor, limit, db, ct);
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
            DraftsDbContext draftsDb,
            WorkspaceAuthorizationService auth,
            ICommandHandler<UpdateElementCommandEnvelope, CommandResult<ElementDto>> handler,
            CancellationToken ct) =>
        {
            var userId = user.FindFirstValue(ClaimTypes.NameIdentifier)!;
            await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Editor, ct);
            var proj = await workspacesDb.Projects.AsNoTracking().FirstAsync(p => p.Id == projectId, ct);
            var streamId = await ResolveBranchStreamIdAsync(branchId, projectId, proj.StreamId, draftsDb, requireOpen: true, ct);
            if (streamId is null)
                return Results.BadRequest(new ErrorResponse("invalid_branch", "Branch not found or not open for this project"));
            return (await handler.HandleAsync(
                new UpdateElementCommandEnvelope(projectId, streamId.Value, elementId, userId, request), ct))
                .ToHttpResult();
        });

        elements.MapDelete("/{elementId}", async (
            Guid projectId, Guid elementId,
            Guid? branchId,
            ClaimsPrincipal user, WorkspacesDbContext workspacesDb,
            DraftsDbContext draftsDb,
            WorkspaceAuthorizationService auth,
            ICommandHandler<RemoveElementCommandEnvelope, CommandResult> handler,
            CancellationToken ct) =>
        {
            var userId = user.FindFirstValue(ClaimTypes.NameIdentifier)!;
            await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Editor, ct);
            var proj = await workspacesDb.Projects.AsNoTracking().FirstAsync(p => p.Id == projectId, ct);
            var streamId = await ResolveBranchStreamIdAsync(branchId, projectId, proj.StreamId, draftsDb, requireOpen: true, ct);
            if (streamId is null)
                return Results.BadRequest(new ErrorResponse("invalid_branch", "Branch not found or not open for this project"));
            return (await handler.HandleAsync(
                new RemoveElementCommandEnvelope(projectId, streamId.Value, elementId, userId), ct))
                .ToHttpResult();
        });

        var relationships = project.MapGroup("/relationships").WithTags("Relationships");

        relationships.MapPost("/", async (
            Guid projectId,
            Guid? branchId,
            AddRelationshipRequest request,
            ClaimsPrincipal user, WorkspacesDbContext workspacesDb,
            DraftsDbContext draftsDb,
            WorkspaceAuthorizationService auth,
            ICommandHandler<AddRelationshipCommandEnvelope, CommandResult<RelationshipDto>> handler,
            CancellationToken ct) =>
        {
            var userId = user.FindFirstValue(ClaimTypes.NameIdentifier)!;
            await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Editor, ct);
            var proj = await workspacesDb.Projects.AsNoTracking().FirstAsync(p => p.Id == projectId, ct);
            var streamId = await ResolveBranchStreamIdAsync(branchId, projectId, proj.StreamId, draftsDb, requireOpen: true, ct);
            if (streamId is null)
                return Results.BadRequest(new ErrorResponse("invalid_branch", "Branch not found or not open for this project"));
            return (await handler.HandleAsync(
                new AddRelationshipCommandEnvelope(projectId, streamId.Value, userId, request), ct))
                .ToCreatedResult($"/api/projects/{projectId}/relationships/{request.Id}");
        });

        relationships.MapGet("/", async (
            Guid projectId,
            Guid? branchId,
            Guid? fromId, Guid? toId, string? cursor, int? limit,
            ClaimsPrincipal user, WorkspacesDbContext workspacesDb,
            DraftsDbContext draftsDb,
            WorkspaceAuthorizationService auth,
            ModellingDbContext db, CancellationToken ct) =>
        {
            var userId = user.FindFirstValue(ClaimTypes.NameIdentifier)!;
            await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Viewer, ct);
            var proj = await workspacesDb.Projects.AsNoTracking().FirstAsync(p => p.Id == projectId, ct);
            var streamId = await ResolveBranchStreamIdAsync(branchId, projectId, proj.StreamId, draftsDb, requireOpen: false, ct);
            if (streamId is null)
                return Results.BadRequest(new ErrorResponse("invalid_branch", "Branch not found for this project"));
            return await ListRelationships.Handle(projectId, streamId.Value, fromId, toId, cursor, limit, db, ct);
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
            DraftsDbContext draftsDb,
            WorkspaceAuthorizationService auth,
            ICommandHandler<UpdateRelationshipCommandEnvelope, CommandResult<RelationshipDto>> handler,
            CancellationToken ct) =>
        {
            var userId = user.FindFirstValue(ClaimTypes.NameIdentifier)!;
            await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Editor, ct);
            var proj = await workspacesDb.Projects.AsNoTracking().FirstAsync(p => p.Id == projectId, ct);
            var streamId = await ResolveBranchStreamIdAsync(branchId, projectId, proj.StreamId, draftsDb, requireOpen: true, ct);
            if (streamId is null)
                return Results.BadRequest(new ErrorResponse("invalid_branch", "Branch not found or not open for this project"));
            return (await handler.HandleAsync(
                new UpdateRelationshipCommandEnvelope(projectId, streamId.Value, relationshipId, userId, request), ct))
                .ToHttpResult();
        });

        relationships.MapDelete("/{relationshipId}", async (
            Guid projectId, Guid relationshipId,
            Guid? branchId,
            ClaimsPrincipal user, WorkspacesDbContext workspacesDb,
            DraftsDbContext draftsDb,
            WorkspaceAuthorizationService auth,
            ICommandHandler<RemoveRelationshipCommandEnvelope, CommandResult> handler,
            CancellationToken ct) =>
        {
            var userId = user.FindFirstValue(ClaimTypes.NameIdentifier)!;
            await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Editor, ct);
            var proj = await workspacesDb.Projects.AsNoTracking().FirstAsync(p => p.Id == projectId, ct);
            var streamId = await ResolveBranchStreamIdAsync(branchId, projectId, proj.StreamId, draftsDb, requireOpen: true, ct);
            if (streamId is null)
                return Results.BadRequest(new ErrorResponse("invalid_branch", "Branch not found or not open for this project"));
            return (await handler.HandleAsync(
                new RemoveRelationshipCommandEnvelope(projectId, streamId.Value, relationshipId, userId), ct))
                .ToHttpResult();
        });

        var messages = project.MapGroup("/messages").WithTags("Messages");

        messages.MapPost("/", async (
            Guid projectId, Guid? branchId,
            AddMessageRequest request,
            ClaimsPrincipal user, WorkspacesDbContext workspacesDb,
            DraftsDbContext draftsDb, WorkspaceAuthorizationService auth,
            ICommandHandler<AddMessageCommandEnvelope, CommandResult<MessageDto>> handler,
            CancellationToken ct) =>
        {
            var userId = user.FindFirstValue(ClaimTypes.NameIdentifier)!;
            await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Editor, ct);
            var proj = await workspacesDb.Projects.AsNoTracking().FirstAsync(p => p.Id == projectId, ct);
            var streamId = await ResolveBranchStreamIdAsync(branchId, projectId, proj.StreamId, draftsDb, requireOpen: true, ct);
            if (streamId is null)
                return Results.BadRequest(new ErrorResponse("invalid_branch", "Branch not found or not open for this project"));
            return (await handler.HandleAsync(
                new AddMessageCommandEnvelope(projectId, streamId.Value, userId, request), ct))
                .ToCreatedResult($"/api/projects/{projectId}/messages/{request.Id}");
        });

        messages.MapGet("/", async (
            Guid projectId, Guid? branchId,
            Guid? producerId, string? cursor, int? limit,
            ClaimsPrincipal user, WorkspacesDbContext workspacesDb,
            DraftsDbContext draftsDb, WorkspaceAuthorizationService auth,
            ModellingDbContext db, CancellationToken ct) =>
        {
            var userId = user.FindFirstValue(ClaimTypes.NameIdentifier)!;
            await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Viewer, ct);
            var proj = await workspacesDb.Projects.AsNoTracking().FirstAsync(p => p.Id == projectId, ct);
            var streamId = await ResolveBranchStreamIdAsync(branchId, projectId, proj.StreamId, draftsDb, requireOpen: false, ct);
            if (streamId is null)
                return Results.BadRequest(new ErrorResponse("invalid_branch", "Branch not found for this project"));
            return await ListMessages.Handle(projectId, streamId.Value, producerId, cursor, limit, db, ct);
        });

        messages.MapGet("/{messageId}", async (
            Guid projectId, Guid messageId,
            ClaimsPrincipal user, WorkspaceAuthorizationService auth,
            ModellingDbContext db, CancellationToken ct) =>
        {
            var userId = user.FindFirstValue(ClaimTypes.NameIdentifier)!;
            await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Viewer, ct);
            return await GetMessage.Handle(projectId, messageId, db, ct);
        });

        messages.MapPatch("/{messageId}", async (
            Guid projectId, Guid messageId, Guid? branchId,
            UpdateMessageRequest request,
            ClaimsPrincipal user, WorkspacesDbContext workspacesDb,
            DraftsDbContext draftsDb, WorkspaceAuthorizationService auth,
            ICommandHandler<UpdateMessageCommandEnvelope, CommandResult<MessageDto>> handler,
            CancellationToken ct) =>
        {
            var userId = user.FindFirstValue(ClaimTypes.NameIdentifier)!;
            await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Editor, ct);
            var proj = await workspacesDb.Projects.AsNoTracking().FirstAsync(p => p.Id == projectId, ct);
            var streamId = await ResolveBranchStreamIdAsync(branchId, projectId, proj.StreamId, draftsDb, requireOpen: true, ct);
            if (streamId is null)
                return Results.BadRequest(new ErrorResponse("invalid_branch", "Branch not found or not open for this project"));
            return (await handler.HandleAsync(
                new UpdateMessageCommandEnvelope(projectId, streamId.Value, messageId, userId, request), ct))
                .ToHttpResult();
        });

        messages.MapDelete("/{messageId}", async (
            Guid projectId, Guid messageId, Guid? branchId,
            ClaimsPrincipal user, WorkspacesDbContext workspacesDb,
            DraftsDbContext draftsDb, WorkspaceAuthorizationService auth,
            ICommandHandler<RemoveMessageCommandEnvelope, CommandResult> handler,
            CancellationToken ct) =>
        {
            var userId = user.FindFirstValue(ClaimTypes.NameIdentifier)!;
            await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Editor, ct);
            var proj = await workspacesDb.Projects.AsNoTracking().FirstAsync(p => p.Id == projectId, ct);
            var streamId = await ResolveBranchStreamIdAsync(branchId, projectId, proj.StreamId, draftsDb, requireOpen: true, ct);
            if (streamId is null)
                return Results.BadRequest(new ErrorResponse("invalid_branch", "Branch not found or not open for this project"));
            return (await handler.HandleAsync(
                new RemoveMessageCommandEnvelope(projectId, streamId.Value, messageId, userId), ct))
                .ToHttpResult();
        });

        project.MapGet("/export", async (
            Guid projectId,
            string? format,
            ClaimsPrincipal user,
            WorkspaceAuthorizationService auth,
            ModellingDbContext modellingDb,
            SchemasDbContext schemasDb,
            WorkspacesDbContext workspacesDb,
            CancellationToken ct) =>
        {
            var userId = user.FindFirstValue(ClaimTypes.NameIdentifier)!;
            await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Viewer, ct);
            return await ExportModel.Handle(projectId, format ?? "json", modellingDb, schemasDb, workspacesDb, ct);
        }).WithTags("Export");

        project.MapPost("/import", async (
            Guid projectId,
            HttpRequest request,
            ClaimsPrincipal user,
            WorkspaceAuthorizationService auth,
            ModellingDbContext modellingDb,
            SchemasDbContext schemasDb,
            WorkspacesDbContext workspacesDb,
            AggregateStore store,
            EventCollector collector,
            EventStoreDbContext eventStoreDb,
            IEnumerable<IInlineProjection> projections,
            ModelProjection modelProjection,
            CancellationToken ct) =>
        {
            var userId = user.FindFirstValue(ClaimTypes.NameIdentifier)!;
            await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Editor, ct);
            return await ImportModel.Handle(projectId, request, modellingDb, schemasDb, workspacesDb,
                store, collector, eventStoreDb, projections, modelProjection, ct);
        }).WithTags("Export");

        return app;
    }
}
