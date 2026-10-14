using Vellum.Modules.Identity.AgentTokens;
using System.Security.Claims;
using Deedbox;
using Microsoft.EntityFrameworkCore;
using Vellum.Shared;
using Vellum.Modules.Drafts;
using Vellum.Modules.Modelling.Elements;
using Vellum.Modules.Modelling.Export;
using Vellum.Modules.Modelling.Messages;
using Vellum.Modules.Modelling.Model;
using Vellum.Modules.Modelling.Relationships;
using Vellum.Modules.Schemas;
using Vellum.Modules.Workspaces;
using Vellum.Modules.Workspaces.Authorization;

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
            AppDbContext db,
            WorkspaceAuthorizationService auth,
            ICommandHandler<AddElementCommandEnvelope, CommandResult<ElementDto>> handler,
            CancellationToken ct) =>
        {
            var userId = user.UserId();
            await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Editor, ct);
            var proj = await db.Projects.AsNoTracking().FirstAsync(p => p.Id == projectId, ct);
            var (streamId, refusal) = await ModelBranches.ResolveAsync(branchId, projectId, proj.StreamId, db, forWrite: true, ct);
            if (refusal is not null)
                return refusal;
            return (await handler.HandleAsync(
                new AddElementCommandEnvelope(projectId, streamId, request), ct))
                .ToCreatedResult($"/api/projects/{projectId}/elements/{request.Id}");
        });

        elements.MapGet("/", async (
            Guid projectId,
            Guid? branchId,
            string? kind, string? status, Guid? parentId, string? cursor, int? limit,
            ClaimsPrincipal user, AppDbContext db,
            WorkspaceAuthorizationService auth, CancellationToken ct) =>
        {
            var userId = user.UserId();
            await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Viewer, ct);
            var proj = await db.Projects.AsNoTracking().FirstAsync(p => p.Id == projectId, ct);
            var (streamId, refusal) = await ModelBranches.ResolveAsync(branchId, projectId, proj.StreamId, db, forWrite: false, ct);
            if (refusal is not null)
                return refusal;
            return await ListElements.Handle(projectId, streamId, kind, status, parentId, cursor, limit, db, ct);
        }).AllowAgent(AgentScope.Read);

        elements.MapGet("/{elementId}", async (
            Guid projectId, Guid elementId, Guid? branchId,
            ClaimsPrincipal user, AppDbContext db,
            WorkspaceAuthorizationService auth, CancellationToken ct) =>
        {
            var userId = user.UserId();
            await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Viewer, ct);
            var proj = await db.Projects.AsNoTracking().FirstAsync(p => p.Id == projectId, ct);
            var (streamId, refusal) = await ModelBranches.ResolveAsync(branchId, projectId, proj.StreamId, db, forWrite: false, ct);
            if (refusal is not null)
                return refusal;
            return await GetElement.Handle(projectId, streamId, elementId, db, ct);
        }).AllowAgent(AgentScope.Read);

        elements.MapPatch("/{elementId}", async (
            Guid projectId, Guid elementId,
            Guid? branchId,
            UpdateElementRequest request,
            ClaimsPrincipal user, AppDbContext db,
            WorkspaceAuthorizationService auth,
            ICommandHandler<UpdateElementCommandEnvelope, CommandResult<ElementDto>> handler,
            CancellationToken ct) =>
        {
            var userId = user.UserId();
            await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Editor, ct);
            var proj = await db.Projects.AsNoTracking().FirstAsync(p => p.Id == projectId, ct);
            var (streamId, refusal) = await ModelBranches.ResolveAsync(branchId, projectId, proj.StreamId, db, forWrite: true, ct);
            if (refusal is not null)
                return refusal;
            return (await handler.HandleAsync(
                new UpdateElementCommandEnvelope(projectId, streamId, elementId, userId, request), ct))
                .ToHttpResult();
        });

        elements.MapDelete("/{elementId}", async (
            Guid projectId, Guid elementId,
            Guid? branchId,
            ClaimsPrincipal user, AppDbContext db,
            WorkspaceAuthorizationService auth,
            ICommandHandler<RemoveElementCommandEnvelope, CommandResult> handler,
            CancellationToken ct) =>
        {
            var userId = user.UserId();
            await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Editor, ct);
            var proj = await db.Projects.AsNoTracking().FirstAsync(p => p.Id == projectId, ct);
            var (streamId, refusal) = await ModelBranches.ResolveAsync(branchId, projectId, proj.StreamId, db, forWrite: true, ct);
            if (refusal is not null)
                return refusal;
            return (await handler.HandleAsync(
                new RemoveElementCommandEnvelope(projectId, streamId, elementId), ct))
                .ToHttpResult();
        });

        var relationships = project.MapGroup("/relationships").WithTags("Relationships");

        relationships.MapPost("/", async (
            Guid projectId,
            Guid? branchId,
            AddRelationshipRequest request,
            ClaimsPrincipal user, AppDbContext db,
            WorkspaceAuthorizationService auth,
            ICommandHandler<AddRelationshipCommandEnvelope, CommandResult<RelationshipDto>> handler,
            CancellationToken ct) =>
        {
            var userId = user.UserId();
            await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Editor, ct);
            var proj = await db.Projects.AsNoTracking().FirstAsync(p => p.Id == projectId, ct);
            var (streamId, refusal) = await ModelBranches.ResolveAsync(branchId, projectId, proj.StreamId, db, forWrite: true, ct);
            if (refusal is not null)
                return refusal;
            return (await handler.HandleAsync(
                new AddRelationshipCommandEnvelope(projectId, streamId, request), ct))
                .ToCreatedResult($"/api/projects/{projectId}/relationships/{request.Id}");
        });

        relationships.MapGet("/", async (
            Guid projectId,
            Guid? branchId,
            Guid? fromId, Guid? toId, string? cursor, int? limit,
            ClaimsPrincipal user, AppDbContext db,
            WorkspaceAuthorizationService auth, CancellationToken ct) =>
        {
            var userId = user.UserId();
            await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Viewer, ct);
            var proj = await db.Projects.AsNoTracking().FirstAsync(p => p.Id == projectId, ct);
            var (streamId, refusal) = await ModelBranches.ResolveAsync(branchId, projectId, proj.StreamId, db, forWrite: false, ct);
            if (refusal is not null)
                return refusal;
            return await ListRelationships.Handle(projectId, streamId, fromId, toId, cursor, limit, db, ct);
        }).AllowAgent(AgentScope.Read);

        relationships.MapGet("/{relationshipId}", async (
            Guid projectId, Guid relationshipId, Guid? branchId,
            ClaimsPrincipal user, AppDbContext db,
            WorkspaceAuthorizationService auth, CancellationToken ct) =>
        {
            var userId = user.UserId();
            await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Viewer, ct);
            var proj = await db.Projects.AsNoTracking().FirstAsync(p => p.Id == projectId, ct);
            var (streamId, refusal) = await ModelBranches.ResolveAsync(branchId, projectId, proj.StreamId, db, forWrite: false, ct);
            if (refusal is not null)
                return refusal;
            return await GetRelationship.Handle(projectId, streamId, relationshipId, db, ct);
        }).AllowAgent(AgentScope.Read);

        relationships.MapPatch("/{relationshipId}", async (
            Guid projectId, Guid relationshipId,
            Guid? branchId,
            UpdateRelationshipRequest request,
            ClaimsPrincipal user, AppDbContext db,
            WorkspaceAuthorizationService auth,
            ICommandHandler<UpdateRelationshipCommandEnvelope, CommandResult<RelationshipDto>> handler,
            CancellationToken ct) =>
        {
            var userId = user.UserId();
            await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Editor, ct);
            var proj = await db.Projects.AsNoTracking().FirstAsync(p => p.Id == projectId, ct);
            var (streamId, refusal) = await ModelBranches.ResolveAsync(branchId, projectId, proj.StreamId, db, forWrite: true, ct);
            if (refusal is not null)
                return refusal;
            return (await handler.HandleAsync(
                new UpdateRelationshipCommandEnvelope(projectId, streamId, relationshipId, userId, request), ct))
                .ToHttpResult();
        });

        relationships.MapDelete("/{relationshipId}", async (
            Guid projectId, Guid relationshipId,
            Guid? branchId,
            ClaimsPrincipal user, AppDbContext db,
            WorkspaceAuthorizationService auth,
            ICommandHandler<RemoveRelationshipCommandEnvelope, CommandResult> handler,
            CancellationToken ct) =>
        {
            var userId = user.UserId();
            await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Editor, ct);
            var proj = await db.Projects.AsNoTracking().FirstAsync(p => p.Id == projectId, ct);
            var (streamId, refusal) = await ModelBranches.ResolveAsync(branchId, projectId, proj.StreamId, db, forWrite: true, ct);
            if (refusal is not null)
                return refusal;
            return (await handler.HandleAsync(
                new RemoveRelationshipCommandEnvelope(projectId, streamId, relationshipId), ct))
                .ToHttpResult();
        });

        var messages = project.MapGroup("/messages").WithTags("Messages");

        messages.MapPost("/", async (
            Guid projectId, Guid? branchId,
            AddMessageRequest request,
            ClaimsPrincipal user, AppDbContext db, WorkspaceAuthorizationService auth,
            ICommandHandler<AddMessageCommandEnvelope, CommandResult<MessageDto>> handler,
            CancellationToken ct) =>
        {
            var userId = user.UserId();
            await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Editor, ct);
            var proj = await db.Projects.AsNoTracking().FirstAsync(p => p.Id == projectId, ct);
            var (streamId, refusal) = await ModelBranches.ResolveAsync(branchId, projectId, proj.StreamId, db, forWrite: true, ct);
            if (refusal is not null)
                return refusal;
            return (await handler.HandleAsync(
                new AddMessageCommandEnvelope(projectId, streamId, request), ct))
                .ToCreatedResult($"/api/projects/{projectId}/messages/{request.Id}");
        });

        messages.MapGet("/", async (
            Guid projectId, Guid? branchId,
            Guid? producerId, string? cursor, int? limit,
            ClaimsPrincipal user, AppDbContext db, WorkspaceAuthorizationService auth, CancellationToken ct) =>
        {
            var userId = user.UserId();
            await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Viewer, ct);
            var proj = await db.Projects.AsNoTracking().FirstAsync(p => p.Id == projectId, ct);
            var (streamId, refusal) = await ModelBranches.ResolveAsync(branchId, projectId, proj.StreamId, db, forWrite: false, ct);
            if (refusal is not null)
                return refusal;
            return await ListMessages.Handle(projectId, streamId, producerId, cursor, limit, db, ct);
        }).AllowAgent(AgentScope.Read);

        messages.MapGet("/{messageId}", async (
            Guid projectId, Guid messageId, Guid? branchId,
            ClaimsPrincipal user, AppDbContext db, WorkspaceAuthorizationService auth, CancellationToken ct) =>
        {
            var userId = user.UserId();
            await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Viewer, ct);
            var proj = await db.Projects.AsNoTracking().FirstAsync(p => p.Id == projectId, ct);
            var (streamId, refusal) = await ModelBranches.ResolveAsync(branchId, projectId, proj.StreamId, db, forWrite: false, ct);
            if (refusal is not null)
                return refusal;
            return await GetMessage.Handle(projectId, streamId, messageId, db, ct);
        }).AllowAgent(AgentScope.Read);

        messages.MapPatch("/{messageId}", async (
            Guid projectId, Guid messageId, Guid? branchId,
            UpdateMessageRequest request,
            ClaimsPrincipal user, AppDbContext db, WorkspaceAuthorizationService auth,
            ICommandHandler<UpdateMessageCommandEnvelope, CommandResult<MessageDto>> handler,
            CancellationToken ct) =>
        {
            var userId = user.UserId();
            await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Editor, ct);
            var proj = await db.Projects.AsNoTracking().FirstAsync(p => p.Id == projectId, ct);
            var (streamId, refusal) = await ModelBranches.ResolveAsync(branchId, projectId, proj.StreamId, db, forWrite: true, ct);
            if (refusal is not null)
                return refusal;
            return (await handler.HandleAsync(
                new UpdateMessageCommandEnvelope(projectId, streamId, messageId, userId, request), ct))
                .ToHttpResult();
        });

        messages.MapDelete("/{messageId}", async (
            Guid projectId, Guid messageId, Guid? branchId,
            ClaimsPrincipal user, AppDbContext db, WorkspaceAuthorizationService auth,
            ICommandHandler<RemoveMessageCommandEnvelope, CommandResult> handler,
            CancellationToken ct) =>
        {
            var userId = user.UserId();
            await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Editor, ct);
            var proj = await db.Projects.AsNoTracking().FirstAsync(p => p.Id == projectId, ct);
            var (streamId, refusal) = await ModelBranches.ResolveAsync(branchId, projectId, proj.StreamId, db, forWrite: true, ct);
            if (refusal is not null)
                return refusal;
            return (await handler.HandleAsync(
                new RemoveMessageCommandEnvelope(projectId, streamId, messageId), ct))
                .ToHttpResult();
        });

        project.MapGet("/export", async (
            Guid projectId,
            string? format,
            ClaimsPrincipal user,
            WorkspaceAuthorizationService auth,
            AppDbContext db,
            IEventStore store,
            CancellationToken ct) =>
        {
            var userId = user.UserId();
            await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Viewer, ct);
            return await ExportModel.Handle(projectId, format ?? "json", db, store, ct);
        }).WithTags("Export");

        project.MapPost("/import", async (
            Guid projectId,
            bool? dryRun,
            long? expectedVersion,
            HttpRequest request,
            ClaimsPrincipal user,
            WorkspaceAuthorizationService auth,
            AppDbContext db,
            CancellationToken ct) =>
        {
            var userId = user.UserId();
            await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Editor, ct);
            return await ImportModel.Handle(projectId, userId, request, dryRun == true, expectedVersion, db, ct);
        })
        .Produces<ImportResult>()
        .WithMetadata(new RequestBodyLimit(
            ImportModel.MaxBytes, "import_too_large", $"An import document holds at most {ImportModel.MaxBytes} bytes."))
        .WithTags("Import");

        return app;
    }
}
