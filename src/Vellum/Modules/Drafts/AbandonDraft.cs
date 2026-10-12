// src/Vellum/Modules/Drafts/AbandonDraft.cs
using System.Security.Claims;
using Deedbox;
using Microsoft.EntityFrameworkCore;
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

        // The draft status, the closing event and the removal of the draft's read-model rows
        // commit together.
        return await SharedTransaction.RunAsync(http.RequestServices, async services =>
        {
            var db = services.GetRequiredService<DraftsDbContext>();

            var streamId = await db.Drafts.AsNoTracking()
                .Where(d => d.Id == draftId && d.ProjectId == projectId)
                .Select(d => (Guid?)d.StreamId)
                .FirstOrDefaultAsync(ct);
            if (streamId is null)
                return Results.NotFound(new ErrorResponse("not_found", "Draft not found"));

            // A return before the commit rolls everything back.
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var store = services.GetRequiredService<IEventStore>().ForProject(projectId).UseDbContext(db);

            // The draft's stream stays locked until the commit, so a merge of this draft and
            // this abandon take turns. The status is read under that lock.
            await store.Execute<ModelState>(StreamId.From(streamId.Value), _ => [], ct);

            var draft = await db.Drafts.FirstAsync(d => d.Id == draftId, ct);
            if (draft.Status != "open")
                return Results.Conflict(new ErrorResponse("conflict", $"Draft is already {draft.Status}"));

            draft.Status = "abandoned";
            draft.AbandonedAt = DateTimeOffset.UtcNow;

            // One append. Deedbox saves the status in the transaction, and the inline projection
            // drops the draft's rows.
            await store.Append(
                StreamId.From(draft.StreamId), ExpectedVersion.Any, [new ModelEvent.BranchClosed(draft.StreamId)], ct);

            await transaction.CommitAsync(ct);
            return Results.Ok(CreateDraft.ToDto(draft));
        }, ct);
    }
}
