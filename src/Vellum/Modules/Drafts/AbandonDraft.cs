// src/Vellum/Modules/Drafts/AbandonDraft.cs
using System.Security.Claims;
using Deedbox;
using Microsoft.EntityFrameworkCore;
using Vellum.Modules.Costs;
using Vellum.Modules.Modelling.Model;
using Vellum.Modules.Workspaces.Authorization;
using Vellum.Shared;

namespace Vellum.Modules.Drafts;

public static class AbandonDraft
{
    public static async Task<IResult> Handle(
        Guid projectId, Guid draftId,
        ClaimsPrincipal user,
        HttpContext http,
        WorkspaceAuthorizationService auth,
        CancellationToken ct)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier)!;
        await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Editor, ct);

        // The draft status, the closing event, the removal of the draft's read-model rows and
        // the removal of its cost sheet and saved estimates commit together.
        return await SharedTransaction.RunAsync(http.RequestServices, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();

            var found = await db.Drafts.AsNoTracking()
                .Where(d => d.Id == draftId && d.ProjectId == projectId)
                .Select(d => new { d.StreamId, d.Status })
                .FirstOrDefaultAsync(ct);
            if (found is null)
                return Results.NotFound(new ErrorResponse("not_found", "Draft not found"));
            // A closed draft has no cost sheet stream to lock. The status is read again under the lock.
            if (found.Status != "open")
                return Results.Conflict(new ErrorResponse("conflict", $"Draft is already {found.Status}"));

            // A return before the commit rolls everything back.
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var store = services.GetRequiredService<IEventStore>().ForProject(projectId).UseDbContext(db);

            // The draft's model stream and its cost sheet stream stay locked until the commit,
            // in ID order, so a merge of this draft and this abandon take turns. The status is
            // read under that lock.
            var sheetId = CostAccess.SheetStreamId(found.StreamId);
            foreach (var streamId in new[] { found.StreamId, sheetId }.Order())
            {
                if (streamId == sheetId)
                    await CostBranches.LockSheetAsync(store, streamId, ct);
                else
                    await store.Execute<ModelState>(StreamId.From(streamId), _ => [], ct);
            }

            var draft = await db.Drafts.FirstAsync(d => d.Id == draftId, ct);
            if (draft.Status != "open")
                return Results.Conflict(new ErrorResponse("conflict", $"Draft is already {draft.Status}"));

            draft.Status = "abandoned";
            draft.AbandonedAt = DateTimeOffset.UtcNow;

            // One append. Deedbox saves the status in the transaction, and the inline projection
            // drops the draft's rows.
            await store.Append(
                StreamId.From(draft.StreamId), ExpectedVersion.Any, [new ModelEvent.BranchClosed(draft.StreamId)], ct);
            await CostBranches.CloseAsync(db, store, projectId, draft.StreamId, ct);

            await transaction.CommitAsync(ct);
            return Results.Ok(CreateDraft.ToDto(draft));
        }, ct);
    }
}
