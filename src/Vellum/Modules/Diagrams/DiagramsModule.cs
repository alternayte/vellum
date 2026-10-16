using System.Security.Claims;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Vellum.Modules.Identity.AgentTokens;
using Vellum.Modules.Promises;
using Vellum.Modules.Views;
using Vellum.Modules.Workspaces;
using Vellum.Shared;
using static Vellum.Shared.ResultExtensions;

namespace Vellum.Modules.Diagrams;

/// <summary>
/// Diagram export: a view as SVG or Mermaid, the model as Structurizr DSL. Each
/// reads the main model, or a named revision with <c>revisionId</c>, and the saved layout of the
/// view. The same revision and the same layout give the same bytes (guarantee 4). Every export
/// reads and none writes, so a Viewer may call them all. PNG is rasterised in the browser from
/// the SVG, so it has no endpoint.
/// </summary>
public static class DiagramsModule
{
    public static WebApplication MapDiagramEndpoints(this WebApplication app)
    {
        var views = app.MapGroup("/api/projects/{projectId}/views/{viewId}/export")
            .RequireAuthorization()
            .WithTags("Export");
        views.MapGet("/svg", Svg).AllowAgent(AgentScope.Read)
            .Produces<string>(contentType: "image/svg+xml")
            .Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces<ErrorResponse>(StatusCodes.Status404NotFound);
        views.MapGet("/mermaid", Mermaid).AllowAgent(AgentScope.Read)
            .Produces<string>(contentType: "text/plain")
            .Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces<ErrorResponse>(StatusCodes.Status404NotFound);

        app.MapGet("/api/projects/{projectId}/export/structurizr", Structurizr)
            .RequireAuthorization()
            .WithTags("Export")
            .AllowAgent(AgentScope.Read)
            .Produces<string>(contentType: "text/plain")
            .Produces<ErrorResponse>(StatusCodes.Status400BadRequest);

        return app;
    }

    /// <summary>The view as an SVG picture. <c>revisionId</c> names a revision; without it, the main model.</summary>
    private static async Task<IResult> Svg(
        Guid projectId, Guid viewId, Guid? revisionId, ClaimsPrincipal user, HttpContext http,
        WorkspaceAuthorizationService auth, CancellationToken ct)
    {
        var (diagram, refusal) = await LoadAsync(projectId, viewId, revisionId, user, http, auth, ct);
        return diagram is null ? refusal! : Results.Text(DiagramSvg.Write(diagram), "image/svg+xml", Encoding.UTF8);
    }

    /// <summary>The view as a Mermaid flowchart. <c>revisionId</c> names a revision; without it, the main model.</summary>
    private static async Task<IResult> Mermaid(
        Guid projectId, Guid viewId, Guid? revisionId, ClaimsPrincipal user, HttpContext http,
        WorkspaceAuthorizationService auth, CancellationToken ct)
    {
        var (diagram, refusal) = await LoadAsync(projectId, viewId, revisionId, user, http, auth, ct);
        return diagram is null ? refusal! : Results.Text(MermaidView.Write(diagram), "text/plain", Encoding.UTF8);
    }

    /// <summary>The whole model and its views as a Structurizr DSL workspace. <c>revisionId</c> names a revision; without it, the main model.</summary>
    private static async Task<IResult> Structurizr(
        Guid projectId, Guid? revisionId, ClaimsPrincipal user, HttpContext http,
        AppDbContext db, WorkspaceAuthorizationService auth, CancellationToken ct)
    {
        await auth.RequireProjectRoleAsync(projectId, user.UserId(), WorkspaceRole.Viewer, ct);
        var (model, refusal) = await PinnedModel.ResolveAsync(http.RequestServices, projectId, revisionId, null, ct);
        if (model is null) return refusal!;

        var projectName = await db.Projects.AsNoTracking().Where(p => p.Id == projectId).Select(p => p.Name).FirstAsync(ct);
        var views = await db.Views.AsNoTracking().Where(v => v.ProjectId == projectId).ToListAsync(ct);
        var viewIds = views.Select(v => v.Id).ToList();
        var positions = (await db.LayoutPositions.AsNoTracking().Where(p => viewIds.Contains(p.ViewId)).ToListAsync(ct))
            .GroupBy(p => p.ViewId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<LayoutPositionEntity>)[.. g]);
        var edges = (await db.LayoutEdges.AsNoTracking().Where(e => viewIds.Contains(e.ViewId)).ToListAsync(ct))
            .GroupBy(e => e.ViewId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<LayoutEdgeEntity>)[.. g]);
        return Results.Text(StructurizrDsl.Write(projectName, model, views, positions, edges), "text/plain", Encoding.UTF8);
    }

    private static async Task<(ViewDiagram? Diagram, IResult? Refusal)> LoadAsync(
        Guid projectId, Guid viewId, Guid? revisionId, ClaimsPrincipal user, HttpContext http,
        WorkspaceAuthorizationService auth, CancellationToken ct)
    {
        await auth.RequireProjectRoleAsync(projectId, user.UserId(), WorkspaceRole.Viewer, ct);
        var db = http.RequestServices.GetRequiredService<AppDbContext>();

        var view = await db.Views.AsNoTracking().FirstOrDefaultAsync(v => v.Id == viewId && v.ProjectId == projectId, ct);
        if (view is null) return (null, NotFound("View"));

        var (model, refusal) = await PinnedModel.ResolveAsync(http.RequestServices, projectId, revisionId, null, ct);
        if (model is null) return (null, refusal);

        var positions = await db.LayoutPositions.AsNoTracking().Where(p => p.ViewId == viewId).ToListAsync(ct);
        var edges = await db.LayoutEdges.AsNoTracking().Where(e => e.ViewId == viewId).ToListAsync(ct);
        return (ViewDiagram.Project(view, positions, model, edges), null);
    }
}
