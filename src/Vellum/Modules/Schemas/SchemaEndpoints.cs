using Vellum.Modules.Identity.AgentTokens;
using System.Security.Claims;
using Vellum.Shared;
using Vellum.Modules.Workspaces.Authorization;

namespace Vellum.Modules.Schemas;

public static class SchemaEndpoints
{
    public static WebApplication MapSchemaEndpoints(this WebApplication app)
    {
        var schemas = app.MapGroup("/api/projects/{projectId}/schemas")
            .RequireAuthorization()
            .WithBodyLimit(RequestBodyLimit.Document)
            .WithTags("Schemas");

        schemas.MapPost("/", async (
            Guid projectId,
            CreateSchemaRequest request,
            ClaimsPrincipal user,
            WorkspaceAuthorizationService auth,
            ICommandHandler<CreateSchemaCommandEnvelope, CommandResult<SchemaDto>> handler,
            CancellationToken ct) =>
        {
            var userId = user.UserId();
            await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Editor, ct);
            return (await handler.HandleAsync(
                new CreateSchemaCommandEnvelope(projectId, request), ct))
                .ToCreatedResult($"/api/projects/{projectId}/schemas/{request.Id}");
        });

        schemas.MapGet("/", async (
            Guid projectId,
            string? cursor, int? limit,
            ClaimsPrincipal user,
            WorkspaceAuthorizationService auth,
            AppDbContext db, CancellationToken ct) =>
        {
            var userId = user.UserId();
            await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Viewer, ct);
            return await ListSchemas.Handle(projectId, cursor, limit, db, ct);
        }).AllowAgent(AgentScope.Read);

        schemas.MapGet("/{schemaId}", async (
            Guid projectId, Guid schemaId,
            ClaimsPrincipal user,
            WorkspaceAuthorizationService auth,
            AppDbContext db, CancellationToken ct) =>
        {
            var userId = user.UserId();
            await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Viewer, ct);
            return await GetSchema.Handle(projectId, schemaId, db, ct);
        }).AllowAgent(AgentScope.Read);

        schemas.MapPatch("/{schemaId}", async (
            Guid projectId, Guid schemaId,
            UpdateSchemaRequest request,
            ClaimsPrincipal user,
            WorkspaceAuthorizationService auth,
            ICommandHandler<UpdateSchemaCommandEnvelope, CommandResult<SchemaDto>> handler,
            CancellationToken ct) =>
        {
            var userId = user.UserId();
            await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Editor, ct);
            return (await handler.HandleAsync(
                new UpdateSchemaCommandEnvelope(projectId, schemaId, request), ct))
                .ToHttpResult();
        });

        schemas.MapDelete("/{schemaId}", async (
            Guid projectId, Guid schemaId,
            ClaimsPrincipal user,
            WorkspaceAuthorizationService auth,
            ICommandHandler<DeleteSchemaCommandEnvelope, CommandResult> handler,
            CancellationToken ct) =>
        {
            var userId = user.UserId();
            await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Editor, ct);
            return (await handler.HandleAsync(
                new DeleteSchemaCommandEnvelope(projectId, schemaId), ct))
                .ToHttpResult();
        });

        return app;
    }
}
