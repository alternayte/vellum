using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Vellum.Modules.Drafts;
using Vellum.Modules.Workspaces;
using Vellum.Modules.Workspaces.Authorization;
using Vellum.Shared;

namespace Vellum.Modules.Modelling;

/// <summary>The model branches of a project: its main stream, its drafts and its variants.</summary>
public static class ModelBranches
{
    /// <summary>
    /// Authorizes the caller for <paramref name="role"/>, resolves <c>?branchId=</c> to a
    /// model stream and runs <paramref name="run"/> with the stream ID and the user ID.
    /// A write needs an open branch; a read accepts any branch of the project.
    /// </summary>
    public static async Task<IResult> RunAsync(
        HttpContext http, Guid projectId, Guid? branchId, WorkspaceRole role,
        Func<Guid, string, Task<IResult>> run, CancellationToken ct)
    {
        var services = http.RequestServices;
        var userId = http.User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        await services.GetRequiredService<WorkspaceAuthorizationService>()
            .RequireProjectRoleAsync(projectId, userId, role, ct);

        var project = await services.GetRequiredService<WorkspacesDbContext>().Projects.AsNoTracking()
            .FirstAsync(p => p.Id == projectId, ct);

        var isWrite = role >= WorkspaceRole.Editor;
        var streamId = await ModellingEndpoints.ResolveBranchStreamIdAsync(
            branchId, projectId, project.StreamId,
            services.GetRequiredService<DraftsDbContext>(),
            services.GetRequiredService<ModellingDbContext>(),
            requireOpen: isWrite, ct);
        if (streamId is null)
        {
            return Results.BadRequest(new ErrorResponse("invalid_branch", isWrite
                ? "Branch not found or not open for this project"
                : "Branch not found for this project"));
        }

        return await run(streamId.Value, userId);
    }

    /// <summary>
    /// The streams that still hold a live model: main, open drafts and open variants. A merged
    /// or abandoned draft is history; main carries what it contributed.
    /// </summary>
    public static async Task<IReadOnlyList<(Guid StreamId, string Name)>> OpenAsync(
        Guid projectId, WorkspacesDbContext workspacesDb, DraftsDbContext draftsDb, ModellingDbContext modellingDb,
        CancellationToken ct)
    {
        var mainStreamId = await workspacesDb.Projects.AsNoTracking()
            .Where(p => p.Id == projectId)
            .Select(p => p.StreamId)
            .FirstAsync(ct);

        var drafts = await draftsDb.Drafts.AsNoTracking()
            .Where(d => d.ProjectId == projectId && d.Status == "open")
            .OrderBy(d => d.Name).ThenBy(d => d.Id)
            .Select(d => new { d.StreamId, d.Name })
            .ToListAsync(ct);

        var variants = await modellingDb.Variants.AsNoTracking()
            .Where(v => v.ProjectId == projectId && v.Status == "open")
            .OrderBy(v => v.Name).ThenBy(v => v.Id)
            .Select(v => new { v.StreamId, v.Name })
            .ToListAsync(ct);

        return
        [
            (mainStreamId, "main"),
            .. drafts.Select(d => (d.StreamId, $"draft '{d.Name}'")),
            .. variants.Select(v => (v.StreamId, $"variant '{v.Name}'")),
        ];
    }
}
