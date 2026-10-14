using System.Security.Claims;
using Deedbox;
using Microsoft.EntityFrameworkCore;
using Vellum.Modules.Costs;
using Vellum.Modules.Modelling.Model;
using Vellum.Modules.Workspaces;
using Vellum.Modules.Workspaces.Authorization;
using Vellum.Shared;
using static Vellum.Shared.ResultExtensions;

namespace Vellum.Modules.Modelling.Variants;

/// <summary>
/// Deletes a variant: its row, its read-model rows, its cost sheet and its saved estimates.
/// The model stream of the variant stays as history, like the stream of an abandoned draft:
/// a stress run, a load-test script or a promise run that was pinned to a version of it can
/// still be played again on that version (SDD guarantee 4). A project delete removes the
/// stream with every other stream of the project.
/// </summary>
public static class DeleteVariant
{
    public static async Task<IResult> Handle(
        Guid projectId, Guid variantId, ClaimsPrincipal user, HttpContext http,
        WorkspaceAuthorizationService auth, CancellationToken ct)
    {
        await auth.RequireProjectRoleAsync(projectId, user.UserId(), WorkspaceRole.Editor, ct);

        // The row, the closing event, the removal of the branch rows and of the cost sheet
        // commit together.
        return await SharedTransaction.RunAsync(http.RequestServices, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();
            var streamId = await db.Variants.AsNoTracking()
                .Where(v => v.Id == variantId && v.ProjectId == projectId)
                .Select(v => (Guid?)v.StreamId)
                .FirstOrDefaultAsync(ct);
            if (streamId is not { } branch)
                return NotFound("Variant");

            // A return before the commit rolls everything back.
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var store = services.GetRequiredService<IEventStore>().ForProject(projectId).UseDbContext(db);

            // The model stream and then the cost sheet stay locked until the commit, so a
            // write of either that waits for the lock finds the variant gone.
            await store.Execute<ModelState>(StreamId.From(branch), _ => [], ct);
            await CostBranches.LockSheetsAsync(db, [branch], ct);

            var variant = await db.Variants.FirstOrDefaultAsync(v => v.Id == variantId && v.ProjectId == projectId, ct);
            if (variant is null)
                return NotFound("Variant");
            db.Variants.Remove(variant);

            // One append. Deedbox saves the removed row in the transaction, and the inline
            // projection drops the branch rows.
            await store.Append(StreamId.From(branch), ExpectedVersion.Any, [new ModelEvent.BranchClosed(branch)], ct);
            await CostBranches.CloseAsync(db, projectId, branch, ct);

            await transaction.CommitAsync(ct);
            return Results.NoContent();
        }, ct);
    }
}
