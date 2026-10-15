using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Vellum.Modules.Identity.AgentTokens;
using Vellum.Modules.Import.Proposals;
using Vellum.Modules.Import.Scans;
using Vellum.Modules.Import.Sources;
using Vellum.Shared;

namespace Vellum.Modules.Import;

public static class ImportModule
{
    /// <param name="runWorker">False when the host is started only to write the OpenAPI document.</param>
    public static IServiceCollection AddImportModule(this IServiceCollection services, bool runWorker)
    {
        // The connection string is read when the context is first used, so configuration that
        // a test host overrides applies.
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<ScanSignal>();
        services.AddSingleton<ScanWorker>();
        services.AddScoped<IProjectRetention, ImportProjectRetention>();
        Vellum.Shared.Outbound.OutboundServices.AddOutboundClient<Runtime.INewRelicApi>(services);
        services.AddScoped<Runtime.NewRelicReader>();

        // Import:Worker:Enabled=false turns the worker of this instance off; scans then wait
        // for an instance that runs one. Import:Worker:Parallelism is how many scans one
        // instance runs at a time.
        services.AddSingleton(provider =>
        {
            var section = provider.GetRequiredService<IConfiguration>().GetSection("Import:Worker");
            var defaults = ScanWorkerOptions.Default;
            return defaults with
            {
                Enabled = section.GetValue("Enabled", defaults.Enabled),
                PollInterval = TimeSpan.FromSeconds(Math.Clamp(section.GetValue("PollSeconds", defaults.PollInterval.TotalSeconds), 0.05, 3600)),
                LeaseDuration = TimeSpan.FromSeconds(Math.Clamp(section.GetValue("LeaseSeconds", defaults.LeaseDuration.TotalSeconds), 5, 3600)),
                Parallelism = Math.Clamp(section.GetValue("Parallelism", defaults.Parallelism), 1, 32),
                RetentionInterval = TimeSpan.FromSeconds(Math.Clamp(section.GetValue("RetentionSeconds", defaults.Retention.TotalSeconds), 1, 86400)),
            };
        });

        if (runWorker)
            services.AddHostedService<ScanWorkerService>();
        return services;
    }

    public static WebApplication MapImportEndpoints(this WebApplication app)
    {
        var imports = app.MapGroup("/api/projects/{projectId}/imports")
            .RequireAuthorization()
            .WithTags("Imports");

        // An agent token starts a scan of a pull request and no other scan (ScanRequests.Start).
        imports.MapPost("/", ScanRequests.Start).AllowAgent(AgentScope.ScanPullRequest)
            .Produces<StartScanResponse>(StatusCodes.Status202Accepted)
            .Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces<ErrorResponse>(StatusCodes.Status403Forbidden)
            .Produces<ErrorResponse>(StatusCodes.Status409Conflict)
            .Produces<ErrorResponse>(StatusCodes.Status429TooManyRequests);

        imports.MapGet("/", ScanRequests.List)
            .Produces<Page<ScanDto>>();

        imports.MapGet("/{importId}", ScanRequests.Get).AllowAgent(AgentScope.Read)
            .Produces<ScanDetailDto>()
            .Produces<ErrorResponse>(StatusCodes.Status404NotFound);

        imports.MapPost("/{importId}/cancel", ScanRequests.Cancel)
            .Produces(StatusCodes.Status202Accepted)
            .Produces<ErrorResponse>(StatusCodes.Status409Conflict);

        imports.MapPost("/{importId}/resume", ScanRequests.Resume)
            .Produces(StatusCodes.Status202Accepted)
            .Produces<ErrorResponse>(StatusCodes.Status409Conflict)
            .Produces<ErrorResponse>(StatusCodes.Status429TooManyRequests);

        imports.MapPost("/{importId}/track", ScanRequests.Track)
            .Produces<ScanDetailDto>()
            .Produces<ErrorResponse>(StatusCodes.Status404NotFound)
            .Produces<ErrorResponse>(StatusCodes.Status409Conflict);

        imports.MapGet("/{importId}/proposals", ProposalReads.List)
            .Produces<ProposalPage>();

        imports.MapGet("/{importId}/proposals/{proposalId}", ProposalReads.Get)
            .Produces<ProposalDto>()
            .Produces<ErrorResponse>(StatusCodes.Status404NotFound);

        imports.MapPost("/{importId}/proposals/decide", DecideProposals.Handle).RequireHuman()
            .Produces<DecideProposalsResponse>()
            .Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces<ErrorResponse>(StatusCodes.Status409Conflict);

        imports.MapGet("/{importId}/evidence", ProposalReads.Evidence)
            .Produces<Page<EvidenceItemDto>>();

        imports.MapGet("/{importId}/source", SourceWindow.Handle)
            .Produces<SourceWindowDto>()
            .Produces<ErrorResponse>(StatusCodes.Status403Forbidden)
            .Produces<ErrorResponse>(StatusCodes.Status404NotFound)
            .Produces<ErrorResponse>(StatusCodes.Status409Conflict);

        var sources = app.MapGroup("/api/projects/{projectId}/sources")
            .RequireAuthorization()
            .WithTags("Sources");

        sources.MapGet("/", SourceRequests.List)
            .Produces<List<SourceDto>>();

        sources.MapPost("/", SourceRequests.Create)
            .Produces<SourceDto>(StatusCodes.Status201Created)
            .Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces<ErrorResponse>(StatusCodes.Status409Conflict);

        sources.MapGet("/{sourceId}", SourceRequests.Get)
            .Produces<SourceDto>()
            .Produces<ErrorResponse>(StatusCodes.Status404NotFound);

        sources.MapPatch("/{sourceId}", SourceRequests.Update)
            .Produces<SourceDto>()
            .Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces<ErrorResponse>(StatusCodes.Status404NotFound);

        sources.MapPost("/{sourceId}/scans", SourceRequests.StartScan)
            .Produces<StartScanResponse>(StatusCodes.Status202Accepted)
            .Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces<ErrorResponse>(StatusCodes.Status404NotFound)
            .Produces<ErrorResponse>(StatusCodes.Status409Conflict)
            .Produces<ErrorResponse>(StatusCodes.Status429TooManyRequests);

        // A trace export is read as it arrives and is never stored; its own bounds apply (TraceLimits).
        sources.MapPost("/{sourceId}/traces", Runtime.RuntimeSources.Upload).RequireHuman()
            .WithBodyLimit(Runtime.TraceLimits.Default.MaxBytes)
            .Accepts<Stream>("application/json")
            .Produces<Runtime.TraceUploadResponse>(StatusCodes.Status202Accepted)
            .Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces<ErrorResponse>(StatusCodes.Status404NotFound)
            .Produces<ErrorResponse>(StatusCodes.Status409Conflict)
            .Produces<ErrorResponse>(StatusCodes.Status413PayloadTooLarge)
            .Produces<ErrorResponse>(StatusCodes.Status429TooManyRequests);

        sources.MapPut("/{sourceId}/newrelic/key", Runtime.RuntimeSources.SetKey).RequireHuman()
            .Produces(StatusCodes.Status204NoContent)
            .Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces<ErrorResponse>(StatusCodes.Status404NotFound)
            .Produces<ErrorResponse>(StatusCodes.Status409Conflict);

        sources.MapPost("/{sourceId}/newrelic/test", Runtime.RuntimeSources.Test)
            .Produces<Runtime.NewRelicTestResponse>()
            .Produces<ErrorResponse>(StatusCodes.Status404NotFound)
            .Produces<ErrorResponse>(StatusCodes.Status409Conflict);

        sources.MapPost("/newrelic/test", Runtime.RuntimeSources.TestNew).RequireHuman()
            .Produces<Runtime.NewRelicTestResponse>()
            .Produces<ErrorResponse>(StatusCodes.Status400BadRequest);

        sources.MapGet("/{sourceId}/snapshots", SourceRequests.Snapshots)
            .Produces<List<SourceSnapshotDto>>()
            .Produces<ErrorResponse>(StatusCodes.Status404NotFound);

        sources.MapGet("/{sourceId}/snapshots/compare", SourceRequests.Compare)
            .Produces<SnapshotComparisonDto>()
            .Produces<ErrorResponse>(StatusCodes.Status404NotFound);

        sources.MapGet("/{sourceId}/bindings", SourceRequests.Bindings)
            .Produces<List<SourceBindingDto>>()
            .Produces<ErrorResponse>(StatusCodes.Status404NotFound);

        sources.MapPost("/{sourceId}/accept", SourceRequests.AcceptAll).RequireHuman()
            .Produces<DecideProposalsResponse>()
            .Produces<ErrorResponse>(StatusCodes.Status404NotFound)
            .Produces<ErrorResponse>(StatusCodes.Status409Conflict);

        return app;
    }
}

/// <summary>
/// What the import tables keep, and for how long. Text of a repository is kept only while
/// something needs it. None of this asks GitHub for anything, so it works when the
/// workspace's token is gone.
/// <list type="bullet">
/// <item>A project that is deleted takes its Sources, scans, snapshots, proposals, links, rejections
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
    public static async Task DeleteProjectAsync(AppDbContext db, Guid projectId, DateTimeOffset now, CancellationToken ct)
    {
        var workspaces = await db.Scans.Where(s => s.ProjectId == projectId).Select(s => s.WorkspaceId)
            .Union(db.Sources.Where(s => s.ProjectId == projectId).Select(s => s.WorkspaceId)).Distinct().ToListAsync(ct);

        await db.FactLinks.Where(l => l.ProjectId == projectId).ExecuteDeleteAsync(ct);
        await db.Rejections.Where(r => r.ProjectId == projectId).ExecuteDeleteAsync(ct);
        await db.TrackedRefs.Where(t => t.ProjectId == projectId).ExecuteDeleteAsync(ct);
        // The files, the snapshot, its facts and the proposals of a scan go with its row.
        // A worker that runs one of these scans finds no row to write to and stops.
        await db.Scans.Where(s => s.ProjectId == projectId).ExecuteDeleteAsync(ct);
        await db.Sources.Where(s => s.ProjectId == projectId).ExecuteDeleteAsync(ct);

        foreach (var workspaceId in workspaces)
            await DropUnusedBlobsAsync(db, workspaceId, now, ct);
    }

    /// <summary>Runs <see cref="DeleteProjectAsync"/> for every project that the import tables name and that no longer exists.</summary>
    public static async Task<int> DeleteOrphansAsync(AppDbContext db, DateTimeOffset now, CancellationToken ct)
    {
        var gone = await db.Database.SqlQuery<Guid>($"""
            SELECT i.project_id AS "Value"
            FROM (SELECT project_id FROM import.scans
                  UNION SELECT project_id FROM import.sources
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
    public static async Task<int> DropUnusedBlobsAsync(AppDbContext db, Guid? workspaceId, DateTimeOffset now, CancellationToken ct)
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

/// <summary>Runs <see cref="ImportRetention.DeleteProjectAsync"/> when a project is deleted.</summary>
public sealed class ImportProjectRetention(AppDbContext db, TimeProvider time) : IProjectRetention
{
    public Task DeleteProjectAsync(DeletedProject project, CancellationToken ct) =>
        ImportRetention.DeleteProjectAsync(db, project.Id, time.GetUtcNow(), ct);
}
