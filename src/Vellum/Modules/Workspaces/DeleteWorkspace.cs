using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Vellum.Modules.Settings.Audit;
using Vellum.Modules.Workspaces.Authorization;
using Vellum.Shared;

namespace Vellum.Modules.Workspaces;

/// <summary>
/// Deletes a workspace: the tenant boundary. Every project goes as a project delete does
/// (<see cref="IProjectRetention"/>), then the rows that belong to the workspace itself: its
/// settings and encrypted secrets, its price book, its agent tokens, its AI usage records and
/// its import blobs and scan charges.
/// <para>
/// The audit log stays. A database trigger keeps it append-only, so the entries of the
/// workspace remain and no request can list them once the workspace is gone; the delete itself
/// is written as an instance-level entry, which the instance admin reads.
/// </para>
/// </summary>
public static class DeleteWorkspace
{
    public const string AuditAction = "workspace.deleted";

    public static async Task<IResult> Handle(
        Guid id,
        ClaimsPrincipal user,
        AppDbContext db,
        [FromServices] IEnumerable<IProjectRetention> retentions,
        ILoggerFactory loggers,
        WorkspaceAuthorizationService auth,
        CancellationToken ct)
    {
        var userId = user.UserId();
        await auth.RequireRoleAsync(id, userId, WorkspaceRole.Owner, ct);

        var workspace = await db.Workspaces.FindAsync([id], ct);
        if (workspace is null)
            return Results.NotFound(new ErrorResponse("not_found", "Workspace not found"));

        // The workspace, its members and its invitations go first, in one transaction with the
        // audit entry: every endpoint checks a membership, so no request reaches the data of
        // the workspace while it goes.
        var projectCount = await db.Projects.CountAsync(p => p.WorkspaceId == id, ct);
        await using (var transaction = await db.Database.BeginTransactionAsync(ct))
        {
            await db.Memberships.Where(m => m.WorkspaceId == id).ExecuteDeleteAsync(ct);
            await db.Invitations.Where(i => i.WorkspaceId == id).ExecuteDeleteAsync(ct);
            await db.Workspaces.Where(w => w.Id == id).ExecuteDeleteAsync(ct);
            await AuditLog.AppendAsync(db, null, userId, AuditAction, $"workspace:{id} projects:{projectCount}", ct);
            await transaction.CommitAsync(ct);
        }

        // The cleanup does not stop when the caller goes away. A project that a request created
        // while the members went is found by the next pass.
        var failures = new List<Exception>();
        while (true)
        {
            var projects = await db.Projects.AsNoTracking().Where(p => p.WorkspaceId == id).ToListAsync(CancellationToken.None);
            if (projects.Count == 0)
                break;
            foreach (var project in projects)
            {
                await db.Projects.Where(p => p.Id == project.Id).ExecuteDeleteAsync(CancellationToken.None);
                failures.AddRange(await DeleteProject.RunRetentionsAsync(
                    new DeletedProject(project.Id, id, project.StreamId, userId), retentions, loggers));
            }
        }

        try
        {
            await DeleteWorkspaceRowsAsync(db, id, CancellationToken.None);
        }
        catch (Exception ex)
        {
            loggers.CreateLogger(typeof(DeleteWorkspace)).LogError(ex, "The rows of workspace {WorkspaceId} were not deleted", id);
            failures.Add(ex);
        }

        if (failures.Count > 0)
            throw new AggregateException($"The data of workspace {id} was not deleted in full.", failures);
        return Results.NoContent();
    }

    /// <summary>The rows that name the workspace and no project. It runs after every project retention, which deletes the pins of the price book.</summary>
    private static async Task DeleteWorkspaceRowsAsync(AppDbContext db, Guid id, CancellationToken ct)
    {
        await db.WorkspaceSettings.Where(s => s.WorkspaceId == id).ExecuteDeleteAsync(ct);
        await db.Secrets.Where(s => s.WorkspaceId == id).ExecuteDeleteAsync(ct);
        await db.PriceEntries.Where(p => p.WorkspaceId == id).ExecuteDeleteAsync(ct);
        await db.AgentTokens.Where(t => t.WorkspaceId == id).ExecuteDeleteAsync(ct);
        await db.UsageRecords.Where(r => r.WorkspaceId == id).ExecuteDeleteAsync(ct);
        await db.ScanCharges.Where(c => c.WorkspaceId == id).ExecuteDeleteAsync(ct);
        await db.Blobs.Where(b => b.WorkspaceId == id).ExecuteDeleteAsync(ct);
    }
}
