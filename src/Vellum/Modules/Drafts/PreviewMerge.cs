// src/Vellum/Modules/Drafts/PreviewMerge.cs
using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Vellum.Kernel.Aggregates;
using Vellum.Modules.Drafts.Merge;
using Vellum.Modules.Modelling.Model;
using Vellum.Modules.Workspaces.Authorization;
using Vellum.Shared;

namespace Vellum.Modules.Drafts;

public sealed record MergePreviewResponse(
    IReadOnlyList<MergeChange> AutoResolved,
    IReadOnlyList<MergeConflict> Conflicts,
    int MainVersion);

public static class PreviewMerge
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static async Task<IResult> Handle(
        Guid projectId, Guid draftId,
        ClaimsPrincipal user,
        DraftsDbContext draftsDb,
        AggregateStore store,
        WorkspaceAuthorizationService auth,
        CancellationToken ct)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier)!;
        await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Viewer, ct);

        var draft = await draftsDb.Drafts.AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == draftId && d.ProjectId == projectId, ct);
        if (draft is null)
            return Results.NotFound(new ErrorResponse("not_found", "Draft not found"));
        if (draft.Status != "open")
            return Results.Conflict(new ErrorResponse("conflict", $"Draft is {draft.Status}"));

        var baseState = JsonSerializer.Deserialize<ModelState>(draft.BaseSnapshot, JsonOpts)!;
        var (oursState, mainVersion) = await store.LoadAsync<ModelState, ModelEvent>(draft.BaseStreamId, ct);
        var (theirsState, _) = await store.LoadAsync<ModelState, ModelEvent>(draft.StreamId, ct);

        var result = ThreeWayMerge.Compute(baseState, oursState, theirsState);

        return Results.Ok(new MergePreviewResponse(result.AutoResolved, result.Conflicts, mainVersion));
    }
}
