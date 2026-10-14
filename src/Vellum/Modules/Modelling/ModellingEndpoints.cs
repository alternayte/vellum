using Vellum.Modules.Identity.AgentTokens;
using System.Security.Claims;
using Deedbox;
using Vellum.Shared;
using Vellum.Modules.Modelling.Export;
using Vellum.Modules.Workspaces;

namespace Vellum.Modules.Modelling;

public static class ModellingEndpoints
{
    public static WebApplication MapModellingEndpoints(this WebApplication app)
    {
        var project = app.MapGroup("/api/projects/{projectId}")
            .RequireAuthorization();

        FactLabels.Map(project);

        var elements = project.MapGroup("/elements").WithTags("Elements");

        elements.MapPost("/", (
            Guid projectId, Guid? branchId, AddElementRequest request,
            HttpContext http, IEventStore store, CancellationToken ct) =>
            ModelBranches.RunAsync(http, projectId, branchId, WorkspaceRole.Editor,
                async (streamId, _) => (await AddElement.HandleAsync(store, projectId, streamId, request, ct))
                    .ToCreatedResult($"/api/projects/{projectId}/elements/{request.Id}"), ct));

        elements.MapGet("/", (
            Guid projectId, Guid? branchId,
            string? kind, string? status, Guid? parentId, string? cursor, int? limit,
            HttpContext http, AppDbContext db, CancellationToken ct) =>
            ModelBranches.RunAsync(http, projectId, branchId, WorkspaceRole.Viewer,
                (streamId, _) => ListElements.Handle(projectId, streamId, kind, status, parentId, cursor, limit, db, ct), ct))
            .AllowAgent(AgentScope.Read);

        elements.MapGet("/{elementId}", (
            Guid projectId, Guid elementId, Guid? branchId,
            HttpContext http, AppDbContext db, CancellationToken ct) =>
            ModelBranches.RunAsync(http, projectId, branchId, WorkspaceRole.Viewer,
                (streamId, _) => GetElement.Handle(projectId, streamId, elementId, db, ct), ct))
            .AllowAgent(AgentScope.Read);

        elements.MapPatch("/{elementId}", (
            Guid projectId, Guid elementId, Guid? branchId, UpdateElementRequest request,
            HttpContext http, IEventStore store, TimeProvider time, CancellationToken ct) =>
            ModelBranches.RunAsync(http, projectId, branchId, WorkspaceRole.Editor,
                async (streamId, userId) => (await UpdateElement.HandleAsync(
                    store, projectId, streamId, elementId, userId, request, time, ct)).ToHttpResult(), ct));

        elements.MapDelete("/{elementId}", (
            Guid projectId, Guid elementId, Guid? branchId,
            HttpContext http, IEventStore store, CancellationToken ct) =>
            ModelBranches.RunAsync(http, projectId, branchId, WorkspaceRole.Editor,
                async (streamId, _) => (await RemoveElement.HandleAsync(
                    store, projectId, streamId, elementId, ct)).ToHttpResult(), ct));

        var relationships = project.MapGroup("/relationships").WithTags("Relationships");

        relationships.MapPost("/", (
            Guid projectId, Guid? branchId, AddRelationshipRequest request,
            HttpContext http, IEventStore store, CancellationToken ct) =>
            ModelBranches.RunAsync(http, projectId, branchId, WorkspaceRole.Editor,
                async (streamId, _) => (await AddRelationship.HandleAsync(store, projectId, streamId, request, ct))
                    .ToCreatedResult($"/api/projects/{projectId}/relationships/{request.Id}"), ct));

        relationships.MapGet("/", (
            Guid projectId, Guid? branchId,
            Guid? fromId, Guid? toId, string? cursor, int? limit,
            HttpContext http, AppDbContext db, CancellationToken ct) =>
            ModelBranches.RunAsync(http, projectId, branchId, WorkspaceRole.Viewer,
                (streamId, _) => ListRelationships.Handle(projectId, streamId, fromId, toId, cursor, limit, db, ct), ct))
            .AllowAgent(AgentScope.Read);

        relationships.MapGet("/{relationshipId}", (
            Guid projectId, Guid relationshipId, Guid? branchId,
            HttpContext http, AppDbContext db, CancellationToken ct) =>
            ModelBranches.RunAsync(http, projectId, branchId, WorkspaceRole.Viewer,
                (streamId, _) => GetRelationship.Handle(projectId, streamId, relationshipId, db, ct), ct))
            .AllowAgent(AgentScope.Read);

        relationships.MapPatch("/{relationshipId}", (
            Guid projectId, Guid relationshipId, Guid? branchId, UpdateRelationshipRequest request,
            HttpContext http, IEventStore store, TimeProvider time, CancellationToken ct) =>
            ModelBranches.RunAsync(http, projectId, branchId, WorkspaceRole.Editor,
                async (streamId, userId) => (await UpdateRelationship.HandleAsync(
                    store, projectId, streamId, relationshipId, userId, request, time, ct)).ToHttpResult(), ct));

        relationships.MapDelete("/{relationshipId}", (
            Guid projectId, Guid relationshipId, Guid? branchId,
            HttpContext http, IEventStore store, CancellationToken ct) =>
            ModelBranches.RunAsync(http, projectId, branchId, WorkspaceRole.Editor,
                async (streamId, _) => (await RemoveRelationship.HandleAsync(
                    store, projectId, streamId, relationshipId, ct)).ToHttpResult(), ct));

        var messages = project.MapGroup("/messages").WithTags("Messages");

        messages.MapPost("/", (
            Guid projectId, Guid? branchId, AddMessageRequest request,
            HttpContext http, CancellationToken ct) =>
            ModelBranches.RunAsync(http, projectId, branchId, WorkspaceRole.Editor,
                async (streamId, _) => (await AddMessage.HandleAsync(http.RequestServices, projectId, streamId, request, ct))
                    .ToCreatedResult($"/api/projects/{projectId}/messages/{request.Id}"), ct));

        messages.MapGet("/", (
            Guid projectId, Guid? branchId,
            Guid? producerId, string? cursor, int? limit,
            HttpContext http, AppDbContext db, CancellationToken ct) =>
            ModelBranches.RunAsync(http, projectId, branchId, WorkspaceRole.Viewer,
                (streamId, _) => ListMessages.Handle(projectId, streamId, producerId, cursor, limit, db, ct), ct))
            .AllowAgent(AgentScope.Read);

        messages.MapGet("/{messageId}", (
            Guid projectId, Guid messageId, Guid? branchId,
            HttpContext http, AppDbContext db, CancellationToken ct) =>
            ModelBranches.RunAsync(http, projectId, branchId, WorkspaceRole.Viewer,
                (streamId, _) => GetMessage.Handle(projectId, streamId, messageId, db, ct), ct))
            .AllowAgent(AgentScope.Read);

        messages.MapPatch("/{messageId}", (
            Guid projectId, Guid messageId, Guid? branchId, UpdateMessageRequest request,
            HttpContext http, TimeProvider time, CancellationToken ct) =>
            ModelBranches.RunAsync(http, projectId, branchId, WorkspaceRole.Editor,
                async (streamId, userId) => (await UpdateMessage.HandleAsync(
                    http.RequestServices, projectId, streamId, messageId, userId, request, time, ct)).ToHttpResult(), ct));

        messages.MapDelete("/{messageId}", (
            Guid projectId, Guid messageId, Guid? branchId,
            HttpContext http, IEventStore store, CancellationToken ct) =>
            ModelBranches.RunAsync(http, projectId, branchId, WorkspaceRole.Editor,
                async (streamId, _) => (await RemoveMessage.HandleAsync(
                    store, projectId, streamId, messageId, ct)).ToHttpResult(), ct));

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
