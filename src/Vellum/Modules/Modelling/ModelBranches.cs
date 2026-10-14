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
    /// </summary>
    public static async Task<IResult> RunAsync(
        HttpContext http, Guid projectId, Guid? branchId, WorkspaceRole role,
        Func<Guid, string, Task<IResult>> run, CancellationToken ct)
    {
        var services = http.RequestServices;
        var userId = http.User.UserId();
        await services.GetRequiredService<WorkspaceAuthorizationService>()
            .RequireProjectRoleAsync(projectId, userId, role, ct);

        var project = await services.GetRequiredService<AppDbContext>().Projects.AsNoTracking()
            .FirstAsync(p => p.Id == projectId, ct);

        var (streamId, refusal) = await ResolveAsync(
            branchId, projectId, project.StreamId,
            services.GetRequiredService<AppDbContext>(),
            forWrite: role >= WorkspaceRole.Editor, ct);

        return refusal ?? await run(streamId, userId);
    }

    /// <summary>
    /// Resolves <c>?branchId=</c> to a model stream of the project: the main stream when there
    /// is none, else the stream of a draft or a variant. Only an open branch resolves. A merged
    /// or abandoned draft is history and has no read-model rows, so a read of it finds nothing
    /// (404) and a write to it is refused (400), as is any branch the project does not have.
    /// </summary>
    public static async Task<(Guid StreamId, IResult? Refusal)> ResolveAsync(
        Guid? branchId, Guid projectId, Guid mainStreamId,
        AppDbContext db,
        bool forWrite, CancellationToken ct)
    {
        if (branchId is null)
            return (mainStreamId, null);

        var status = await db.Drafts.AsNoTracking()
            .Where(d => d.StreamId == branchId.Value && d.ProjectId == projectId)
            .Select(d => d.Status)
            .FirstOrDefaultAsync(ct);

        status ??= await db.Variants.AsNoTracking()
            .Where(v => v.StreamId == branchId.Value && v.ProjectId == projectId)
            .Select(v => v.Status)
            .FirstOrDefaultAsync(ct);

        if (status == "open")
            return (branchId.Value, null);

        if (forWrite)
        {
            return (default, Results.BadRequest(
                new ErrorResponse("invalid_branch", "Branch not found or not open for this project")));
        }

        return (default, status is null
            ? Results.BadRequest(new ErrorResponse("invalid_branch", "Branch not found for this project"))
            : Results.NotFound(new ErrorResponse("not_found", $"Branch is {status}")));
    }

    /// <summary>
    /// The streams that still hold a live model: main, open drafts and open variants. A merged
    /// or abandoned draft is history; main carries what it contributed.
    /// </summary>
    public static async Task<IReadOnlyList<(Guid StreamId, string Name)>> OpenAsync(
        Guid projectId, AppDbContext db,
        CancellationToken ct)
    {
        var mainStreamId = await db.Projects.AsNoTracking()
            .Where(p => p.Id == projectId)
            .Select(p => p.StreamId)
            .FirstAsync(ct);

        var drafts = await db.Drafts.AsNoTracking()
            .Where(d => d.ProjectId == projectId && d.Status == "open")
            .OrderBy(d => d.Name).ThenBy(d => d.Id)
            .Select(d => new { d.StreamId, d.Name })
            .ToListAsync(ct);

        var variants = await db.Variants.AsNoTracking()
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
