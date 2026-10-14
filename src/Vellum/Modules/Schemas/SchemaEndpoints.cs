using Deedbox;
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
            IEventStore store,
            CancellationToken ct) =>
        {
            var userId = user.UserId();
            await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Editor, ct);
            return (await CreateSchema.HandleAsync(store, projectId, request, ct))
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
            IEventStore store,
            AppDbContext db,
            CancellationToken ct) =>
        {
            var userId = user.UserId();
            await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Editor, ct);
            return (await UpdateSchema.HandleAsync(store, db, projectId, schemaId, request, ct))
                .ToHttpResult();
        });

        schemas.MapDelete("/{schemaId}", async (
            Guid projectId, Guid schemaId,
            ClaimsPrincipal user,
            WorkspaceAuthorizationService auth,
            HttpContext http,
            CancellationToken ct) =>
        {
            var userId = user.UserId();
            await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Editor, ct);
            return (await DeleteSchema.HandleAsync(http.RequestServices, projectId, schemaId, ct))
                .ToHttpResult();
        });

        return app;
    }
}
