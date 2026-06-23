using System.Security.Claims;
using Vellum.Kernel.CommandHandling;
using Vellum.Kernel.Results;
using Vellum.Modules.Workspaces.Authorization;
using Vellum.Shared;

namespace Vellum.Modules.Schemas;

public static class SchemaEndpoints
{
    public static WebApplication MapSchemaEndpoints(this WebApplication app)
    {
        var schemas = app.MapGroup("/api/projects/{projectId}/schemas")
            .RequireAuthorization()
            .WithTags("Schemas");

        schemas.MapPost("/", async (
            Guid projectId,
            CreateSchemaRequest request,
            ClaimsPrincipal user,
            WorkspaceAuthorizationService auth,
            ICommandHandler<CreateSchemaCommandEnvelope, CommandResult<SchemaDto>> handler,
            CancellationToken ct) =>
        {
            var userId = user.FindFirstValue(ClaimTypes.NameIdentifier)!;
            await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Editor, ct);
            return (await handler.HandleAsync(
                new CreateSchemaCommandEnvelope(projectId, userId, request), ct))
                .ToCreatedResult($"/api/projects/{projectId}/schemas/{request.Id}");
        });

        schemas.MapGet("/", async (
            Guid projectId,
            string? cursor, int? limit,
            ClaimsPrincipal user,
            WorkspaceAuthorizationService auth,
            SchemasDbContext db, CancellationToken ct) =>
        {
            var userId = user.FindFirstValue(ClaimTypes.NameIdentifier)!;
            await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Viewer, ct);
            return await ListSchemas.Handle(projectId, cursor, limit, db, ct);
        });

        schemas.MapGet("/{schemaId}", async (
            Guid projectId, Guid schemaId,
            ClaimsPrincipal user,
            WorkspaceAuthorizationService auth,
            SchemasDbContext db, CancellationToken ct) =>
        {
            var userId = user.FindFirstValue(ClaimTypes.NameIdentifier)!;
            await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Viewer, ct);
            return await GetSchema.Handle(projectId, schemaId, db, ct);
        });

        schemas.MapPatch("/{schemaId}", async (
            Guid projectId, Guid schemaId,
            UpdateSchemaRequest request,
            ClaimsPrincipal user,
            WorkspaceAuthorizationService auth,
            ICommandHandler<UpdateSchemaCommandEnvelope, CommandResult<SchemaDto>> handler,
            CancellationToken ct) =>
        {
            var userId = user.FindFirstValue(ClaimTypes.NameIdentifier)!;
            await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Editor, ct);
            return (await handler.HandleAsync(
                new UpdateSchemaCommandEnvelope(projectId, schemaId, userId, request), ct))
                .ToHttpResult();
        });

        schemas.MapDelete("/{schemaId}", async (
            Guid projectId, Guid schemaId,
            ClaimsPrincipal user,
            WorkspaceAuthorizationService auth,
            ICommandHandler<DeleteSchemaCommandEnvelope, CommandResult> handler,
            CancellationToken ct) =>
        {
            var userId = user.FindFirstValue(ClaimTypes.NameIdentifier)!;
            await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Editor, ct);
            return (await handler.HandleAsync(
                new DeleteSchemaCommandEnvelope(projectId, schemaId, userId), ct))
                .ToHttpResult();
        });

        return app;
    }
}
