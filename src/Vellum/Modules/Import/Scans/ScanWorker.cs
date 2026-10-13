using System.Text;
using Deedbox;
using Microsoft.EntityFrameworkCore;
using Vellum.Modules.GitHub;
using Vellum.Modules.Import.Extraction;
using Vellum.Modules.Import.Proposals;
using Vellum.Modules.Settings.Document;

namespace Vellum.Modules.Import.Scans;

/// <param name="PollInterval">How long an idle worker waits before it looks for work again.</param>
/// <param name="LeaseDuration">How long a scan stays with a worker that stopped renewing its lease.</param>
/// <param name="WatchInterval">How often a running scan renews its lease and looks for a cancel request.</param>
/// <param name="MaxAttempts">How many times a scan is taken before it fails for good. Each rate-limit wait is one.</param>
public sealed record ScanWorkerOptions(
    bool Enabled, TimeSpan PollInterval, TimeSpan LeaseDuration, TimeSpan WatchInterval, int MaxAttempts = 50)
{
    public static readonly ScanWorkerOptions Default = new(true, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(2));
}

/// <summary>Wakes the worker of this instance when a scan is queued here, so it does not wait for its next poll.</summary>
public sealed class ScanSignal
{
    private readonly SemaphoreSlim _signal = new(0, 1);

    public void Notify()
    {
        try
        {
            _signal.Release();
        }
        catch (SemaphoreFullException)
        {
            // A wake-up is already pending.
        }
    }

    public async Task WaitAsync(TimeSpan timeout, CancellationToken ct) => await _signal.WaitAsync(timeout, ct);
}

/// <summary>
/// Runs scans. The table is the queue: a worker takes the oldest scan that is queued, whose
/// rate-limit wait is over, or whose worker stopped renewing its lease, with
/// <c>FOR UPDATE SKIP LOCKED</c>, so several instances never take the same scan.
/// <para>
/// A scan is resumable at every step. The commit SHA is stored when the ref is resolved and
/// is never resolved again; the file list is stored when it is read; each blob is stored
/// under its SHA when it is fetched. A worker that takes over a scan fetches only the blobs
/// that are not stored yet, all at the same commit.
/// </para>
/// <para>
/// A scan writes to the model in one case only: a stale mark on an accepted fact whose
/// evidence a complete scan no longer finds, or its removal when the evidence is back. It
/// never accepts a proposal and never changes a value of a fact.
/// </para>
/// </summary>
public sealed class ScanWorker(
    IServiceScopeFactory scopes, TimeProvider time, ScanWorkerOptions options, ILogger<ScanWorker> logger)
{
    private static readonly TimeSpan DefaultRateLimitWait = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan MaxRateLimitWait = TimeSpan.FromMinutes(65);
    private static readonly TimeSpan ResumableFor = TimeSpan.FromDays(7);

    /// <summary>Takes one scan and runs it until it stops. False when there was no scan to take.</summary>
    public async Task<bool> RunOnceAsync(string workerId, CancellationToken stopping)
    {
        if (await ClaimAsync(workerId, stopping) is not { } scanId)
            return false;

        await RunAsync(scanId, workerId, stopping);
        return true;
    }

    private async Task<Guid?> ClaimAsync(string workerId, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ImportDbContext>();
        var now = time.GetUtcNow();
        var expires = now + options.LeaseDuration;

        var claimed = await db.Database.SqlQuery<Guid>($"""
            UPDATE import.scans
            SET status = 'running', lease_owner = {workerId}, lease_expires_at = {expires},
                attempts = attempts + 1, started_at = COALESCE(started_at, {now}), updated_at = {now}, retry_at = NULL
            WHERE id = (
                SELECT id FROM import.scans
                WHERE status = 'queued'
                   OR (status = 'waiting' AND retry_at <= {now})
                   OR (status = 'running' AND lease_expires_at < {now})
                ORDER BY created_at, id
                FOR UPDATE SKIP LOCKED
                LIMIT 1)
            RETURNING id AS "Value"
            """).ToListAsync(ct);
        return claimed.Count == 0 ? null : claimed[0];
    }

    private async Task RunAsync(Guid scanId, string workerId, CancellationToken stopping)
    {
        using var run = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        var watch = new Watch();
        var watching = WatchAsync(scanId, workerId, watch, run);

        try
        {
            await ExecuteAsync(scanId, workerId, run.Token);
        }
        catch (Exception ex)
        {
            if (stopping.IsCancellationRequested)
            {
                // This instance is stopping, or died as far as anyone can tell. Nothing is
                // written: the lease runs out, and another worker goes on from the checkpoint.
            }
            else if (watch.LeaseLost || ex is DbUpdateConcurrencyException)
            {
                // Another worker has the scan. This one stops and writes nothing.
                logger.LogInformation("Scan {ScanId}: the lease went to another worker.", scanId);
            }
            else if (watch.CancelRequested)
            {
                // A call that the cancel request cut short can surface as any failure.
                await FinishAsync(scanId, workerId, ScanStop.Cancelled());
            }
            else if (ex is ScanStop stop)
            {
                await FinishAsync(scanId, workerId, stop);
            }
            else
            {
                // The type only: a message can hold text of the repository.
                logger.LogError("Scan {ScanId} stopped on {ExceptionType}.", scanId, ex.GetType().FullName);
                await FinishAsync(scanId, workerId, ScanStop.Failed(ScanFailures.Internal));
            }
        }
        finally
        {
            await run.CancelAsync();
            await watching;
        }
    }

    private sealed class Watch
    {
        public volatile bool CancelRequested;
        public volatile bool LeaseLost;
    }

    /// <summary>
    /// Renews the lease while the scan runs and stops the run when a person asked to cancel
    /// it or when the lease is no longer this worker's. It runs next to the scan, so a cancel
    /// request ends a call to GitHub that is in flight.
    /// </summary>
    private async Task WatchAsync(Guid scanId, string workerId, Watch watch, CancellationTokenSource run)
    {
        try
        {
            while (!run.IsCancellationRequested)
            {
                await Task.Delay(options.WatchInterval, time, run.Token);

                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<ImportDbContext>();
                var expires = time.GetUtcNow() + options.LeaseDuration;
                var renewed = await db.Scans
                    .Where(s => s.Id == scanId && s.LeaseOwner == workerId && s.Status == ScanStatus.Running)
                    .ExecuteUpdateAsync(set => set.SetProperty(s => s.LeaseExpiresAt, expires), run.Token);
                if (renewed == 0)
                {
                    watch.LeaseLost = true;
                    await run.CancelAsync();
                    return;
                }

                if (await db.Scans.Where(s => s.Id == scanId).Select(s => s.CancelRequested).FirstAsync(run.Token))
                {
                    watch.CancelRequested = true;
                    await run.CancelAsync();
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The run ended.
        }
        catch (Exception ex)
        {
            // The database is unreachable. Without a renewal the lease runs out, so the run
            // must not go on as if it still had it.
            logger.LogWarning("Scan {ScanId}: the lease could not be renewed ({ExceptionType}).", scanId, ex.GetType().Name);
            watch.LeaseLost = true;
            await run.CancelAsync();
        }
    }

    private async Task ExecuteAsync(Guid scanId, string workerId, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<ImportDbContext>();
        var scan = await db.Scans.FirstAsync(s => s.Id == scanId, ct);
        if (scan.LeaseOwner != workerId)
            return;
        if (scan.CancelRequested)
            throw ScanStop.Cancelled();
        if (scan.Attempts > options.MaxAttempts)
            throw ScanStop.Failed(ScanFailures.RateLimitNeverCleared);

        var limits = ScanLimits.FromJson(scan.Limits);
        var names = scan.Repository.Split('/');
        var (owner, name) = (names[0], names[1]);

        var connected = await services.GetRequiredService<GitHubReader>().ConnectAsync(scan.WorkspaceId, ct);
        if (connected.Failure is { } notConnected)
            throw ScanStop.Failed(ScanFailures.NotConnected(notConnected));
        var github = connected.Value!;

        // Step 1: pin the commit. A resumed scan keeps the SHA it has, so a branch that
        // moved in the meantime cannot give it files of two revisions.
        if (scan.CommitSha is null)
        {
            var repository = Read(await github.GetRepositoryAsync(owner, name, ct), github, "repository", null);
            var requested = scan.RequestedRef;
            if (requested is null)
            {
                var settings = await services.GetRequiredService<SettingsResolver>().GetWorkspaceAsync(scan.WorkspaceId, ct);
                requested = settings.Effective.Github.DefaultRef is { Length: > 0 } configured ? configured : null;
            }
            scan.CommitSha = Read(
                await github.ResolveRefAsync(owner, name, requested ?? repository.DefaultBranch, ct), github, "ref", requested);
            scan.RepositoryIsPrivate = repository.IsPrivate;
            await SaveAsync(db, scan, ct);
        }

        // Step 2: the file list of that commit, filtered before anything is fetched.
        if (!scan.TreeListed)
        {
            var tree = await github.GetTreeAsync(owner, name, scan.CommitSha, ct);
            if (tree.Failure is GitHubFailure.Truncated)
            {
                scan.Partial = true;
                scan.Notes = ScanNotes.Write(ScanNotes.Read(scan.Notes).Append(ScanNotes.Of("tree-truncated", "")));
                await SaveAsync(db, scan, ct);
                throw ScanStop.Failed(ScanFailures.TreeTruncated);
            }

            var selection = ScanScope.Select(Read(tree, github, "tree", null).Entries, limits, scan.IncludePaths, scan.ExcludePaths);
            db.ScanFiles.AddRange(selection.Files.Select(file => new ScanFileEntity
            {
                ScanId = scan.Id, Path = file.Path, BlobSha = file.Sha, Size = file.Size ?? 0,
            }));
            scan.TreeListed = true;
            scan.FilesListed = selection.Listed;
            scan.FilesSkipped = selection.Skipped;
            scan.Partial |= selection.Partial;
            scan.Notes = ScanNotes.Write(ScanNotes.Read(scan.Notes).Concat(selection.Notes));
            await SaveAsync(db, scan, ct);
        }

        // Step 3: the blobs, in path order. One that is stored already is not asked for.
        var pending = (await db.ScanFiles.Where(f => f.ScanId == scan.Id && f.State == ScanFileEntity.Pending).ToListAsync(ct))
            .OrderBy(f => f.Path, StringComparer.Ordinal).ToList();
        foreach (var file in pending)
        {
            ct.ThrowIfCancellationRequested();

            var stored = await db.Blobs.AsNoTracking()
                .Where(b => b.WorkspaceId == scan.WorkspaceId && b.BlobSha == file.BlobSha)
                .Select(b => new { b.WithheldReason, HasContent = b.Content != null })
                .FirstOrDefaultAsync(ct);
            if (stored is null)
            {
                var blob = await github.GetBlobAsync(owner, name, file.BlobSha, limits.MaxFileBytes, ct);
                switch (blob.Failure)
                {
                    case null:
                        await StoreBlobAsync(db, scan.WorkspaceId, file.BlobSha, blob.Value!.Content, ct);
                        MarkRead(scan, file, blob.Value.Content.LongLength);
                        break;
                    case GitHubFailure.RateLimited limited:
                        throw ScanStop.Waiting(RetryAt(limited.ResetAt));
                    case GitHubFailure.TooLarge:
                        Skip(scan, file, "limit:file-bytes", partial: true);
                        break;
                    case GitHubFailure.NotFound:
                        Skip(scan, file, "blob-missing", partial: true);
                        break;
                    default:
                        throw ScanStop.Failed(ScanFailures.Describe(blob.Failure, github.HasToken, "blob", null));
                }
            }
            else if (stored.HasContent)
                MarkRead(scan, file, file.Size);
            else
                Skip(scan, file, stored.WithheldReason ?? "secret-file", partial: false);

            await SaveAsync(db, scan, ct);
        }

        // Step 4: extract. The extractors read text and nothing else; they run nothing.
        var workspaceId = scan.WorkspaceId;
        var read = await (
            from file in db.ScanFiles.AsNoTracking()
            join blob in db.Blobs.AsNoTracking().Where(b => b.WorkspaceId == workspaceId) on file.BlobSha equals blob.BlobSha
            where file.ScanId == scanId && file.State == ScanFileEntity.Read && blob.Content != null
            select new { file.Path, file.BlobSha, blob.Content }).ToListAsync(ct);
        var repositoryName = $"{scan.Host}/{scan.Repository}";
        var input = new ExtractionInput(repositoryName, scan.CommitSha,
            [.. read.Select(f => new SourceFile(f.Path, Encoding.UTF8.GetString(f.Content!)))]);
        var result = await Task.Run(() => EvidenceExtractor.Extract(input, new ExtractionLimits(
            MaxFiles: limits.MaxFiles, MaxFileBytes: limits.MaxFileBytes, MaxTotalBytes: limits.MaxTotalBytes)), ct);

        // A file that turned out to declare a secret, or not to be text, is not kept.
        var blobOf = read.ToDictionary(f => f.Path, f => f.BlobSha, StringComparer.Ordinal);
        foreach (var note in result.Notes.Where(n => n.Kind == NoteKind.Skipped && n.Reason is "secret-file" or "binary-content"))
        {
            if (!blobOf.TryGetValue(note.Path, out var sha))
                continue;
            await db.Blobs.Where(b => b.WorkspaceId == scan.WorkspaceId && b.BlobSha == sha)
                .ExecuteUpdateAsync(set => set.SetProperty(b => b.Content, (byte[]?)null).SetProperty(b => b.WithheldReason, note.Reason), ct);
            await db.ScanFiles.Where(f => f.ScanId == scan.Id && f.Path == note.Path)
                .ExecuteUpdateAsync(set => set.SetProperty(f => f.State, ScanFileEntity.Skipped).SetProperty(f => f.Reason, note.Reason), ct);
        }

        var notes = ScanNotes.Read(scan.Notes)
            .Concat(result.Notes.Select(n => new ScanNote(n.Reason, n.Path, n.Line, NoteMessage(n))))
            .ToList();
        var isComplete = !scan.Partial && result.IsComplete;

        // Step 5: the snapshot, the proposals and the stale marks, in one transaction with
        // the status of the scan. A worker that lost its lease writes none of it.
        services.GetRequiredService<DeedboxContext>().Metadata = new EventMetadata
        {
            Actor = "system:import-scan",
            CorrelationId = scan.Id.ToString(),
        };
        var written = await SnapshotWriter.WriteAsync(
            services, scan, workerId, result, isComplete, ScanNotes.Write(notes), time.GetUtcNow(), ct);
        if (written == SnapshotOutcome.ProjectGone)
            throw ScanStop.Failed(ScanFailures.ProjectGone);
        if (written == SnapshotOutcome.Written)
            await DropUnusedBlobsAsync(scan.WorkspaceId, CancellationToken.None);
    }

    private static T Read<T>(GitHubResult<T> result, GitHubConnection github, string stage, string? reference)
    {
        if (result.Failure is GitHubFailure.RateLimited limited)
            throw ScanStop.Waiting(limited.ResetAt ?? DateTimeOffset.MinValue);
        if (result.Failure is { } failure)
            throw ScanStop.Failed(ScanFailures.Describe(failure, github.HasToken, stage, reference));
        return result.Value!;
    }

    private DateTimeOffset RetryAt(DateTimeOffset? resetAt)
    {
        var now = time.GetUtcNow();
        var at = resetAt is null || resetAt == DateTimeOffset.MinValue ? now + DefaultRateLimitWait : resetAt.Value;
        // GitHub's clock and this one differ, and a reset time can be far away or in the past.
        return at < now + TimeSpan.FromSeconds(1) ? now + TimeSpan.FromSeconds(1)
            : at > now + MaxRateLimitWait ? now + MaxRateLimitWait
            : at;
    }

    private static void MarkRead(ScanEntity scan, ScanFileEntity file, long bytes)
    {
        file.State = ScanFileEntity.Read;
        scan.FilesRead++;
        scan.BytesRead += bytes;
    }

    private static void Skip(ScanEntity scan, ScanFileEntity file, string reason, bool partial)
    {
        file.State = ScanFileEntity.Skipped;
        file.Reason = reason;
        scan.FilesSkipped++;
        scan.Partial |= partial;
        scan.Notes = ScanNotes.Write(ScanNotes.Read(scan.Notes).Append(ScanNotes.Of(reason, file.Path)));
    }

    /// <summary>The checkpoint. The lease owner is a concurrency token, so a worker that lost the scan fails here.</summary>
    private async Task SaveAsync(ImportDbContext db, ScanEntity scan, CancellationToken ct)
    {
        scan.UpdatedAt = time.GetUtcNow();
        await db.SaveChangesAsync(ct);
    }

    private async Task StoreBlobAsync(ImportDbContext db, Guid workspaceId, string blobSha, byte[] content, CancellationToken ct)
    {
        // Two scans of one workspace can fetch the same blob at once. The content is the same.
        var now = time.GetUtcNow();
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO import.blobs (workspace_id, blob_sha, content, size, created_at)
            VALUES ({workspaceId}, {blobSha}, {content}, {content.LongLength}, {now})
            ON CONFLICT (workspace_id, blob_sha) DO NOTHING
            """, ct);
    }

    /// <summary>
    /// Writes how the run ended, when this worker still has the scan. A scan that waits for
    /// GitHub's rate limit keeps its checkpoint and is taken again at <c>RetryAt</c>.
    /// </summary>
    private async Task FinishAsync(Guid scanId, string workerId, ScanStop stop)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ImportDbContext>();
        var now = time.GetUtcNow();
        DateTimeOffset? retryAt = stop.Status == ScanStatus.Waiting ? RetryAt(stop.RetryAt) : null;
        DateTimeOffset? completedAt = stop.Status == ScanStatus.Waiting ? null : now;

        await db.Scans
            .Where(s => s.Id == scanId && s.LeaseOwner == workerId && s.Status == ScanStatus.Running)
            .ExecuteUpdateAsync(set => set
                .SetProperty(s => s.Status, stop.Status)
                .SetProperty(s => s.ErrorCode, stop.Error == null ? null : stop.Error.Code)
                .SetProperty(s => s.ErrorMessage, stop.Error == null ? null : stop.Error.Message)
                .SetProperty(s => s.RetryAt, retryAt)
                .SetProperty(s => s.CompletedAt, completedAt)
                .SetProperty(s => s.UpdatedAt, now)
                .SetProperty(s => s.LeaseOwner, (string?)null)
                .SetProperty(s => s.LeaseExpiresAt, (DateTimeOffset?)null),
                CancellationToken.None);
    }

    /// <summary>
    /// The retention rule of the blob store. A blob stays while a scan that needs it can
    /// still use it: a scan that is not finished, a failed or cancelled scan that can still be
    /// resumed (<see cref="ResumableFor"/>), and the two newest completed scans of each
    /// repository of each project, which are the ones a person reviews and compares. Every
    /// other blob of the workspace is deleted when a scan completes.
    /// </summary>
    private async Task DropUnusedBlobsAsync(Guid workspaceId, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ImportDbContext>();
        var resumableSince = time.GetUtcNow() - ResumableFor;
        await db.Database.ExecuteSqlAsync($"""
            DELETE FROM import.blobs b
            WHERE b.workspace_id = {workspaceId}
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
                                  WHERE c.workspace_id = {workspaceId} AND c.status = 'completed') ranked
                            WHERE ranked.n <= 2)))
            """, ct);
    }

    private static string NoteMessage(ExtractionNote note) => note.Kind switch
    {
        NoteKind.Undecided => "A rule found a site it could not decide from the text. No fact was made for it.",
        NoteKind.Redacted or NoteKind.Conflict or NoteKind.LimitReached or NoteKind.Skipped => ScanNotes.Message(note.Reason),
        _ => "The file could not be parsed. Its facts are missing from this scan.",
    };
}

/// <summary>The hosted service: one worker loop per app instance.</summary>
public sealed class ScanWorkerService(ScanWorker worker, ScanSignal signal, ScanWorkerOptions options, ILogger<ScanWorkerService> logger)
    : BackgroundService
{
    private readonly string _workerId = $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled)
            return;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // While there is work, take the next scan at once.
                if (await worker.RunOnceAsync(_workerId, stoppingToken))
                    continue;
                await signal.WaitAsync(options.PollInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // The queue could not be read. The loop must survive a database outage.
                logger.LogError("The scan worker could not take work ({ExceptionType}). It tries again.", ex.GetType().FullName);
                try
                {
                    await Task.Delay(options.PollInterval, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }
}
