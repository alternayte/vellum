using Microsoft.EntityFrameworkCore;

namespace Vellum.Modules.Import.Scans;

/// <summary>
/// What the import tables keep, and for how long. Text of a repository is kept only while
/// something needs it. None of this asks GitHub for anything, so it works when the
/// workspace's token is gone.
/// <list type="bullet">
/// <item>A project that is deleted takes its scans, snapshots, proposals, links, rejections
/// and tracked refs with it, at once (<see cref="DeleteProjectAsync"/>) and again on the
/// worker's timer for a project whose delete was cut short (<see cref="DeleteOrphansAsync"/>).</item>
/// <item>A blob stays while a scan that needs it can still use it: a scan that is not
/// finished, a failed or cancelled scan that can still be resumed (<see cref="ResumableFor"/>),
/// and the two newest completed scans of each repository of each project, which are the
/// ones a person reviews and compares. Every other blob is deleted.</item>
/// </list>
/// Every statement is safe to run again.
/// </summary>
public static class ImportRetention
{
    /// <summary>
    /// How long after its last change a failed or cancelled scan can be resumed. Its blobs
    /// are kept for that long; after it the scan is expired and a new scan is needed.
    /// </summary>
    public static readonly TimeSpan ResumableFor = TimeSpan.FromDays(7);

    /// <summary>True when the scan stopped too long ago to be resumed: its blobs may be gone.</summary>
    public static bool IsExpired(ScanEntity scan, DateTimeOffset now) => scan.UpdatedAt <= now - ResumableFor;

    /// <summary>Deletes everything the importer holds for one project, and the blobs that nothing else of its workspaces needs.</summary>
    public static async Task DeleteProjectAsync(ImportDbContext db, Guid projectId, DateTimeOffset now, CancellationToken ct)
    {
        var workspaces = await db.Scans.Where(s => s.ProjectId == projectId).Select(s => s.WorkspaceId).Distinct().ToListAsync(ct);

        await db.FactLinks.Where(l => l.ProjectId == projectId).ExecuteDeleteAsync(ct);
        await db.Rejections.Where(r => r.ProjectId == projectId).ExecuteDeleteAsync(ct);
        await db.TrackedRefs.Where(t => t.ProjectId == projectId).ExecuteDeleteAsync(ct);
        // The files, the snapshot, its facts and the proposals of a scan go with its row.
        // A worker that runs one of these scans finds no row to write to and stops.
        await db.Scans.Where(s => s.ProjectId == projectId).ExecuteDeleteAsync(ct);

        foreach (var workspaceId in workspaces)
            await DropUnusedBlobsAsync(db, workspaceId, now, ct);
    }

    /// <summary>Runs <see cref="DeleteProjectAsync"/> for every project that the import tables name and that no longer exists.</summary>
    public static async Task<int> DeleteOrphansAsync(ImportDbContext db, DateTimeOffset now, CancellationToken ct)
    {
        var gone = await db.Database.SqlQuery<Guid>($"""
            SELECT i.project_id AS "Value"
            FROM (SELECT project_id FROM import.scans
                  UNION SELECT project_id FROM import.fact_links
                  UNION SELECT project_id FROM import.rejections
                  UNION SELECT project_id FROM import.tracked_refs) i
            WHERE NOT EXISTS (SELECT 1 FROM workspaces.projects p WHERE p.id = i.project_id)
            """).ToListAsync(ct);
        foreach (var projectId in gone)
            await DeleteProjectAsync(db, projectId, now, ct);
        return gone.Count;
    }

    /// <summary>Applies the blob rule to one workspace, or to all of them when <paramref name="workspaceId"/> is null.</summary>
    public static async Task<int> DropUnusedBlobsAsync(ImportDbContext db, Guid? workspaceId, DateTimeOffset now, CancellationToken ct)
    {
        var resumableSince = now - ResumableFor;
        return await db.Database.ExecuteSqlAsync($"""
            DELETE FROM import.blobs b
            WHERE ({workspaceId}::uuid IS NULL OR b.workspace_id = {workspaceId}::uuid)
              AND NOT EXISTS (
                SELECT 1
                FROM import.scan_files f
                JOIN import.scans s ON s.id = f.scan_id
                WHERE f.blob_sha = b.blob_sha AND s.workspace_id = b.workspace_id
                  AND (s.status IN ('queued', 'running', 'waiting')
                       OR (s.status IN ('failed', 'cancelled') AND s.updated_at > {resumableSince})
                       OR s.id IN (
                            SELECT ranked.id
                            FROM (SELECT c.id, row_number() OVER (
                                      PARTITION BY c.project_id, c.host, c.repository ORDER BY c.completed_at DESC, c.created_at DESC, c.id) AS n
                                  FROM import.scans c
                                  WHERE c.workspace_id = b.workspace_id AND c.status = 'completed') ranked
                            WHERE ranked.n <= 2)))
            """, ct);
    }
}
