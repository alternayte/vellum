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
        var userId = user.UserId();
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
            // The status is read again under the lock.
            if (found.Status != "open")
                return Results.Conflict(new ErrorResponse("conflict", $"Draft is already {found.Status}"));

            // A return before the commit rolls everything back.
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var store = services.GetRequiredService<IEventStore>().ForProject(projectId).UseDbContext(db);

            // The draft's model stream and then its cost sheet stay locked until the commit, so
            // a merge of this draft and this abandon take turns, and a write of the sheet that
            // waits for the lock finds the draft closed. The status is read under that lock.
            await store.Execute<ModelState>(StreamId.From(found.StreamId), _ => [], ct);
            await CostBranches.LockSheetsAsync(db, [found.StreamId], ct);

            var draft = await db.Drafts.FirstAsync(d => d.Id == draftId, ct);
            if (draft.Status != "open")
                return Results.Conflict(new ErrorResponse("conflict", $"Draft is already {draft.Status}"));

            draft.Status = "abandoned";
            draft.AbandonedAt = DateTimeOffset.UtcNow;

            // One append. Deedbox saves the status in the transaction, and the inline projection
            // drops the draft's rows.
            await store.Append(
                StreamId.From(draft.StreamId), ExpectedVersion.Any, [new ModelEvent.BranchClosed(draft.StreamId)], ct);
            await CostBranches.CloseAsync(db, projectId, draft.StreamId, ct);

            await transaction.CommitAsync(ct);
            return Results.Ok(CreateDraft.ToDto(draft));
        }, ct);
    }
}
