using System.Globalization;
using System.Text;
using Deedbox;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Vellum.Modules.GitHub;
using Vellum.Modules.Import.Extraction;
using Vellum.Modules.Import.Proposals;
using Vellum.Modules.Settings;
using Vellum.Modules.Workspaces;
using Vellum.Shared.Telemetry;
using Vellum.Shared;

namespace Vellum.Modules.Import.Scans;

/// <param name="PollInterval">How long an idle worker waits before it looks for work again.</param>
/// <param name="LeaseDuration">How long a scan stays with a worker that stopped renewing its lease.</param>
/// <param name="WatchInterval">How often a running scan renews its lease and looks for a cancel request.</param>
/// <param name="MaxAttempts">How many times a scan is taken before it fails for good. Each rate-limit wait is one.</param>
/// <param name="Parallelism">How many scans one instance runs at the same time.</param>
/// <param name="MaxInternalFailures">How many runs in a row an unexpected error may stop before the scan fails with <c>internal_error</c>.</param>
/// <param name="RetentionInterval">How often the worker applies <see cref="ImportRetention"/>.</param>
public sealed record ScanWorkerOptions(
    bool Enabled, TimeSpan PollInterval, TimeSpan LeaseDuration, TimeSpan WatchInterval, int MaxAttempts = 50,
    int Parallelism = 2, int MaxInternalFailures = 5, TimeSpan? RetentionInterval = null)
{
    public static readonly ScanWorkerOptions Default = new(true, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(2));

    public TimeSpan Retention => RetentionInterval ?? TimeSpan.FromMinutes(10);
}

/// <summary>Wakes the workers of this instance when a scan is queued here, so they do not wait for their next poll.</summary>
public sealed class ScanSignal
{
    private readonly SemaphoreSlim _signal = new(0, int.MaxValue);
    private int _waiting;

    public void Notify()
    {
        // One wake-up for each loop that waits now. A loop that is busy looks again when it is done.
        var waiting = Volatile.Read(ref _waiting);
        if (waiting > 0)
            _signal.Release(waiting);
    }

    public async Task WaitAsync(TimeSpan timeout, CancellationToken ct)
    {
        Interlocked.Increment(ref _waiting);
        try
        {
            await _signal.WaitAsync(timeout, ct);
        }
        finally
        {
            Interlocked.Decrement(ref _waiting);
        }
    }
}

/// <summary>
/// Runs scans. The table is the queue. A worker takes a scan that is queued, whose wait is
/// over, or whose worker stopped renewing its lease, with <c>FOR UPDATE SKIP LOCKED</c>, so
/// several instances never take the same scan.
/// <para>
/// Fairness: the next scan is the oldest one of the workspace that was served least
/// recently, so one workspace with many scans does not hold up the others.
/// </para>
/// <para>
/// The lease. Its expiry is written and compared on the clock of the database
/// (<c>now()</c>), which every worker shares; no application clock decides who has a scan.
/// Each claim raises the scan's fencing token, and every write of a run names the token it
/// was given. A run that lost the scan writes nothing.
/// </para>
/// <para>
/// Poison scans. A run that an unexpected error stops is tried again after a wait that
/// doubles, up to <see cref="MaxBackoff"/>; after <see cref="ScanWorkerOptions.MaxInternalFailures"/>
/// such runs in a row the scan fails with <c>internal_error</c>. A scan whose worker stopped
/// without a word twice in a row is not taken again: it fails with <c>worker_crashed</c>.
/// </para>
/// <para>
/// A scan is resumable at every step. The commit SHA is stored when the ref is resolved and
/// is never resolved again; the file list is stored when it is read; each blob is checked
/// against its SHA, cleared and stored when it is fetched. A worker that takes over a scan
/// fetches only what is not stored, all at the same commit, and a file whose stored content
/// is gone is fetched again.
/// </para>
/// <para>
/// A scan writes to the model in one case only: a stale mark on an accepted fact whose
/// evidence a tracking scan looked for and did not find, or its removal when the evidence
/// is back. It never accepts a proposal and never changes a value of a fact.
/// </para>
/// </summary>
public sealed class ScanWorker(
    IServiceScopeFactory scopes, TimeProvider time, ScanWorkerOptions options, ILogger<ScanWorker> logger)
{
    private static readonly TimeSpan DefaultRateLimitWait = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan MaxRateLimitWait = TimeSpan.FromMinutes(65);
    /// <summary>The wait after the first unexpected error. Each further one doubles it.</summary>
    public static readonly TimeSpan FirstBackoff = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(15);

    /// <summary>The wait before run <paramref name="failures"/> + 1, after that many unexpected errors in a row.</summary>
    public static TimeSpan Backoff(int failures) =>
        failures >= 16 ? MaxBackoff : TimeSpan.FromTicks(Math.Min(MaxBackoff.Ticks, FirstBackoff.Ticks << Math.Max(0, failures - 1)));

    /// <summary>Takes one scan and runs it until it stops. False when there was no scan to take.</summary>
    public async Task<bool> RunOnceAsync(string workerId, CancellationToken stopping)
    {
        if (await ClaimAsync(workerId, stopping) is not { } claim)
            return false;

        await RunAsync(claim.ScanId, claim.LeaseToken, stopping);
        return true;
    }

    /// <summary>Applies <see cref="ImportRetention"/> to every workspace: the data of deleted projects, then the blobs nothing needs.</summary>
    public async Task RunRetentionAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = time.GetUtcNow();
        await ImportRetention.DeleteOrphansAsync(db, now, ct);
        await ImportRetention.DropUnusedBlobsAsync(db, null, now, ct);
    }

    private readonly record struct Claim(Guid ScanId, long LeaseToken);

    private async Task<Claim?> ClaimAsync(string workerId, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = time.GetUtcNow();
        var lease = options.LeaseDuration.TotalSeconds;

        // A scan whose worker stopped renewing the lease twice in a row is not run a third time.
        await db.Database.ExecuteSqlAsync($"""
            UPDATE import.scans
            SET status = 'failed', error_code = {ScanFailures.WorkerCrashed.Code}, error_message = {ScanFailures.WorkerCrashed.Message},
                completed_at = {now}, updated_at = {now}, lease_owner = NULL, lease_expires_at = NULL, lease_token = lease_token + 1
            WHERE status = 'running' AND lease_expires_at < now() AND crashes >= 1
            """, ct);

        var claimed = await db.Database.SqlQuery<string>($"""
            UPDATE import.scans s
            SET status = 'running', lease_owner = {workerId}, lease_expires_at = now() + make_interval(secs => {lease}),
                lease_token = s.lease_token + 1, attempts = s.attempts + 1,
                crashes = CASE WHEN s.status = 'running' THEN s.crashes + 1 ELSE s.crashes END,
                started_at = COALESCE(s.started_at, {now}), claimed_at = clock_timestamp(), updated_at = {now}, retry_at = NULL
            WHERE s.id = (
                SELECT c.id
                FROM import.scans c
                LEFT JOIN LATERAL (
                    SELECT max(w.claimed_at) AS last FROM import.scans w WHERE w.workspace_id = c.workspace_id) served ON true
                WHERE c.status = 'queued'
                   OR (c.status = 'waiting' AND c.retry_at <= {now})
                   OR (c.status = 'running' AND c.lease_expires_at < now())
                ORDER BY served.last NULLS FIRST, c.created_at, c.id
                FOR UPDATE OF c SKIP LOCKED
                LIMIT 1)
            RETURNING s.id::text || ':' || s.lease_token::text AS "Value"
            """).ToListAsync(ct);
        if (claimed.Count == 0)
            return null;

        var parts = claimed[0].Split(':');
        return new Claim(Guid.Parse(parts[0]), long.Parse(parts[1], CultureInfo.InvariantCulture));
    }

    private async Task RunAsync(Guid scanId, long leaseToken, CancellationToken stopping)
    {
        using var run = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        var watch = new Watch();
        var watching = WatchAsync(scanId, leaseToken, watch, run);

        try
        {
            await ExecuteAsync(scanId, leaseToken, run.Token);
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
                // Another worker has the scan, or its row is gone. This one stops and writes nothing.
                logger.LogInformation("Scan {ScanId}: the lease went to another worker.", scanId);
            }
            else if (watch.CancelRequested)
            {
                // A call that the cancel request cut short can surface as any failure.
                await FinishAsync(scanId, leaseToken, ScanStop.Cancelled(), internalError: false);
            }
            else if (ex is ScanStop stop)
            {
                await FinishAsync(scanId, leaseToken, stop, internalError: false);
            }
            else
            {
                // The type only: a message can hold text of the repository.
                logger.LogError("Scan {ScanId} stopped on {ExceptionType}.", scanId, ex.GetType().FullName);
                await FinishAsync(scanId, leaseToken, ScanStop.Failed(ScanFailures.Internal), internalError: true);
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
    /// it or when the lease is no longer this run's. It runs next to the scan, so a cancel
    /// request ends a call to GitHub that is in flight.
    /// </summary>
    private async Task WatchAsync(Guid scanId, long leaseToken, Watch watch, CancellationTokenSource run)
    {
        try
        {
            var lease = options.LeaseDuration.TotalSeconds;
            while (!run.IsCancellationRequested)
            {
                await Task.Delay(options.WatchInterval, time, run.Token);

                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var renewed = await db.Database.ExecuteSqlAsync($"""
                    UPDATE import.scans SET lease_expires_at = now() + make_interval(secs => {lease})
                    WHERE id = {scanId} AND lease_token = {leaseToken} AND status = 'running'
                    """, run.Token);
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

    private async Task ExecuteAsync(Guid scanId, long leaseToken, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<AppDbContext>();
        var scan = await db.Scans.FirstOrDefaultAsync(s => s.Id == scanId, ct);
        if (scan is null || scan.LeaseToken != leaseToken)
            return;
        if (scan.CancelRequested)
            throw ScanStop.Cancelled();
        if (scan.Attempts > options.MaxAttempts)
            throw ScanStop.Failed(ScanFailures.RateLimitNeverCleared);

        // A scan of a project that was deleted ends here, before anything is asked of
        // GitHub, and takes the rest of the project's import data with it.
        if (!await services.GetRequiredService<AppDbContext>().Projects.AnyAsync(p => p.Id == scan.ProjectId, ct))
        {
            db.ChangeTracker.Clear();
            await ImportRetention.DeleteProjectAsync(db, scan.ProjectId, time.GetUtcNow(), ct);
            return;
        }

        var limits = ScanLimits.FromJson(scan.Limits);

        var connected = await services.GetRequiredService<GitHubReader>().ConnectAsync(scan.WorkspaceId, ct);
        if (connected.Failure is { } notConnected)
            throw ScanStop.Failed(ScanFailures.NotConnected(notConnected));
        var github = connected.Value!;

        // Step 1: pin the commit. A resumed scan keeps the SHA it has, so a branch that
        // moved in the meantime cannot give it files of two revisions.
        if (scan.CommitSha is null)
        {
            var asked = scan.Repository.Split('/');
            var repository = Read(await github.GetRepositoryAsync(asked[0], asked[1], ct), github, "repository", null);
            if (scan.PullRequestNumber is { } number)
            {
                var pull = await PinPullRequestAsync(github, asked[0], asked[1], repository, number, ct);
                scan.CommitSha = pull.HeadSha;
                scan.ResolvedRef = null;
                scan.PullRequest = pull.ToJson();
                var pullNotes = ScanNotes.Read(scan.Notes);
                if (pull.FromFork)
                    pullNotes.Add(ScanNotes.Of("pull-request-fork", ""));
                if (pull.Merged)
                    pullNotes.Add(ScanNotes.Of("pull-request-merged", ""));
                else if (pull.State == "closed")
                    pullNotes.Add(ScanNotes.Of("pull-request-closed", ""));
                scan.Notes = ScanNotes.Write(pullNotes);
            }
            else
            {
                var requested = scan.RequestedRef;
                if (requested is null)
                {
                    var settings = await services.GetRequiredService<SettingsResolver>().GetWorkspaceAsync(scan.WorkspaceId, ct);
                    requested = settings.Effective.Github.DefaultRef is { Length: > 0 } configured ? configured : null;
                }
                var reference = requested ?? repository.DefaultBranch;
                scan.CommitSha = Read(await github.ResolveRefAsync(asked[0], asked[1], reference, ct), github, "ref", requested);
                scan.ResolvedRef = scan.IsBareCommit() ? null : reference;
            }
            scan.RepositoryIsPrivate = repository.IsPrivate;
            scan.RepositoryId = repository.Id;
            scan.RepositoryName = repository.FullName;
            await IdentifyRepositoryAsync(db, scan, repository, ct);
            await SaveAsync(db, scan, ct);
        }
        var names = scan.Repository.Split('/');
        var (owner, name) = (names[0], names[1]);

        // Step 2: the file list of that commit. Every file gets a row; which of them the
        // scan reads is decided from the list alone, before anything is fetched.
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
            var rows = selection.Files.Select(file => new ScanFileEntity
            {
                ScanId = scan.Id, Path = file.Entry.Path, PathHash = KeyHash.Of(file.Entry.Path),
                BlobSha = file.Entry.Sha, Size = file.Entry.Size ?? 0,
                State = file.Reason is null ? ScanFileState.Pending : ScanFileState.Skipped, Reason = file.Reason,
            }).ToList();
            db.ScanFiles.AddRange(rows);
            scan.TreeListed = true;
            scan.FilesListed = selection.Listed;
            scan.FilesSkipped = selection.Skipped;
            scan.Partial |= selection.Partial;
            scan.Notes = ScanNotes.Write(ScanNotes.Read(scan.Notes).Concat(selection.Notes));
            await SaveAsync(db, scan, ct);
            foreach (var row in rows)
                db.Entry(row).State = EntityState.Detached;
        }

        // Step 3: a file counts as read only while its content is stored. One whose blob
        // is gone goes back to the list of files to fetch.
        var workspaceId = scan.WorkspaceId;
        var lost = await db.ScanFiles
            .Where(f => f.ScanId == scanId && f.State == ScanFileState.Read
                && !db.Blobs.Any(b => b.WorkspaceId == workspaceId && b.BlobSha == f.BlobSha && b.Content != null))
            .ToListAsync(ct);
        if (lost.Count > 0)
        {
            foreach (var file in lost)
            {
                file.State = ScanFileState.Pending;
                scan.FilesRead--;
                scan.BytesRead -= file.Size;
            }
            await SaveAsync(db, scan, ct);
        }

        // Step 4: the blobs, in path order. One that is stored already is not asked for.
        var pending = (await db.ScanFiles.Where(f => f.ScanId == scan.Id && f.State == ScanFileState.Pending).ToListAsync(ct))
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
                    {
                        var content = blob.Value!.Content;
                        // The bytes must be the file of the pinned commit, and they must be
                        // cleared before they are stored anywhere.
                        if (!ContentClearance.IsBlob(content, file.BlobSha))
                            Skip(scan, file, CoverageGap.ShaMismatch, partial: true);
                        else if (ContentClearance.Withhold(content) is { } withheld)
                        {
                            await StoreBlobAsync(db, scan.WorkspaceId, file.BlobSha, null, content.LongLength, withheld, ct);
                            Skip(scan, file, withheld, partial: false);
                        }
                        else
                        {
                            await StoreBlobAsync(db, scan.WorkspaceId, file.BlobSha, content, content.LongLength, null, ct);
                            MarkRead(scan, file, content.LongLength);
                        }
                        break;
                    }
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

        // Step 5: extract. The extractors read text and nothing else; they run nothing.
        // They get exactly the files that count as read. One file less means stored
        // content went missing, and the scan has no result.
        var files = await db.ScanFiles.Where(f => f.ScanId == scanId).ToListAsync(ct);
        var readFiles = files.Where(f => f.State == ScanFileState.Read).ToList();
        var shas = readFiles.Select(f => f.BlobSha).Distinct().ToList();
        var contents = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var chunk in shas.Chunk(500))
        {
            foreach (var blob in await db.Blobs.AsNoTracking()
                         .Where(b => b.WorkspaceId == workspaceId && chunk.Contains(b.BlobSha) && b.Content != null)
                         .Select(b => new { b.BlobSha, b.Content }).ToListAsync(ct))
                contents[blob.BlobSha] = blob.Content!;
        }
        if (readFiles.Any(f => !contents.ContainsKey(f.BlobSha)))
            throw ScanStop.Failed(ScanFailures.StoredContentMissing);

        var repositoryName = $"{scan.Host}/{scan.Repository}";
        var input = new ExtractionInput(repositoryName, scan.CommitSha,
            [.. readFiles.Select(f => new SourceFile(f.Path, Encoding.UTF8.GetString(contents[f.BlobSha])))]);
        var result = await Task.Run(() => EvidenceExtractor.Extract(input, new ExtractionLimits(
            MaxFiles: limits.MaxFiles, MaxFileBytes: limits.MaxFileBytes, MaxTotalBytes: limits.MaxTotalBytes)), ct);

        // What the extraction says about single files. A file that it withheld is not kept;
        // a file that it read in part does not count as looked at.
        var byPath = readFiles.ToDictionary(f => f.Path, StringComparer.Ordinal);
        foreach (var note in result.Notes)
        {
            if (FileGap(note) is not { } reason || !byPath.TryGetValue(note.Path, out var file) || file.State != ScanFileState.Read)
                continue;
            file.State = ScanFileState.Skipped;
            file.Reason = reason;
            if (CoverageGap.Of(reason) == CoverageGap.Withheld)
            {
                await db.Blobs.Where(b => b.WorkspaceId == scan.WorkspaceId && b.BlobSha == file.BlobSha)
                    .ExecuteUpdateAsync(set => set.SetProperty(b => b.Content, (byte[]?)null).SetProperty(b => b.WithheldReason, reason), ct);
            }
        }
        await SaveAsync(db, scan, ct);

        var gap = ExtractionGap(result);
        var notes = ScanNotes.Read(scan.Notes)
            .Concat(result.Notes.Select(n => new ScanNote(n.Reason, n.Path, n.Line, NoteMessage(n))))
            .ToList();
        var isComplete = !scan.Partial && result.IsComplete;

        var coverageFiles = files.ToDictionary(f => f.Path, f => f.State == ScanFileState.Read ? null : f.Reason ?? CoverageGap.ParseFailure, StringComparer.Ordinal);
        var absent = ScanScope.AbsentGap(limits, scan.IncludePaths, scan.ExcludePaths);

        // Step 6: the snapshot, the proposals and the stale marks, in one transaction with
        // the status of the scan. A run that lost its lease writes none of it.
        services.GetRequiredService<DeedboxContext>().Metadata = new EventMetadata
        {
            Actor = "system:import-scan",
            CorrelationId = scan.Id.ToString(),
        };
        db.ChangeTracker.Clear();
        var written = await SnapshotWriter.WriteAsync(
            services, scan, WriteMode.Complete(leaseToken),
            new SnapshotInput(result.Facts, isComplete, gap, ScanNotes.Write(notes),
                () => new ScanCoverage(coverageFiles, absent, gap), coverageFiles.Count(f => f.Value is null)),
            time.GetUtcNow(), ct);
        switch (written)
        {
            case SnapshotOutcome.ProjectGone:
                throw ScanStop.Failed(ScanFailures.ProjectGone);
            case SnapshotOutcome.ModelBusy:
                throw ScanStop.Failed(ScanFailures.ModelBusy);
            case SnapshotOutcome.Written:
                await DropUnusedBlobsAsync(scan.WorkspaceId, CancellationToken.None);
                break;
        }
    }

    /// <summary>
    /// GitHub's ID says which repository this is; its name can change. The scan and every
    /// row of the repository in this project take the name GitHub gives now, so a renamed
    /// repository keeps its accepted facts. Rows written before IDs were recorded are
    /// claimed by the name they have.
    /// </summary>
    private static async Task IdentifyRepositoryAsync(AppDbContext db, ScanEntity scan, GitHubRepository repository, CancellationToken ct)
    {
        var canonical = repository.FullName.ToLowerInvariant();
        // Without an ID nothing says that another name is the same repository.
        if (repository.Id is null || !ScanRequests.IsRepositoryName(canonical))
            canonical = scan.Repository;
        var (projectId, host, asked) = (scan.ProjectId, scan.Host, scan.Repository);

        if (repository.Id is { } id)
        {
            foreach (var known in new[] { asked, canonical }.Distinct())
            {
                await db.FactLinks.Where(l => l.ProjectId == projectId && l.Host == host && l.Repository == known && l.RepositoryId == null)
                    .ExecuteUpdateAsync(set => set.SetProperty(l => l.RepositoryId, id), ct);
                await db.Rejections.Where(r => r.ProjectId == projectId && r.Host == host && r.Repository == known && r.RepositoryId == null)
                    .ExecuteUpdateAsync(set => set.SetProperty(r => r.RepositoryId, id), ct);
                await db.TrackedRefs.Where(t => t.ProjectId == projectId && t.Host == host && t.Repository == known && t.RepositoryId == null)
                    .ExecuteUpdateAsync(set => set.SetProperty(t => t.RepositoryId, id), ct);
            }

            // A row moves to the new name unless the new name already has that row.
            await db.FactLinks
                .Where(l => l.ProjectId == projectId && l.Host == host && l.RepositoryId == id && l.Repository != canonical
                    && !db.FactLinks.Any(o => o.ProjectId == projectId && o.Host == host && o.Repository == canonical
                        && o.Subject == l.Subject && o.FactKeyHash == l.FactKeyHash))
                .ExecuteUpdateAsync(set => set.SetProperty(l => l.Repository, canonical), ct);
            await db.Rejections
                .Where(r => r.ProjectId == projectId && r.Host == host && r.RepositoryId == id && r.Repository != canonical
                    && !db.Rejections.Any(o => o.ProjectId == projectId && o.Host == host && o.Repository == canonical
                        && o.Subject == r.Subject && o.FactKeyHash == r.FactKeyHash && o.Fingerprint == r.Fingerprint))
                .ExecuteUpdateAsync(set => set.SetProperty(r => r.Repository, canonical), ct);
            await db.TrackedRefs
                .Where(t => t.ProjectId == projectId && t.Host == host && t.RepositoryId == id && t.Repository != canonical
                    && !db.TrackedRefs.Any(o => o.ProjectId == projectId && o.Host == host && o.Repository == canonical))
                .ExecuteUpdateAsync(set => set.SetProperty(t => t.Repository, canonical), ct);
        }

        // Links accepted before refs were recorded follow the default ref. This scan names it.
        if (scan.RequestedRef is null && scan.ResolvedRef is { } resolved)
        {
            await db.TrackedRefs
                .Where(t => t.ProjectId == projectId && t.Host == host && t.Repository == canonical && t.RefName == "")
                .ExecuteUpdateAsync(set => set.SetProperty(t => t.RefName, resolved), ct);
        }

        if (canonical == asked)
            return;
        scan.Repository = canonical;
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // The index that allows one active scan per repository refused the new name.
            throw ScanStop.Failed(ScanFailures.RepositoryRenamed);
        }
    }

    /// <summary>The reason a note takes a file out of what the scan looked at, or null when it does not.</summary>
    private static string? FileGap(ExtractionNote note)
    {
        if (note.Path.Length == 0)
            return null;
        return note.Kind switch
        {
            NoteKind.Skipped => note.Reason,
            NoteKind.ParseFailure => CoverageGap.ParseFailure,
            NoteKind.LimitReached => note.Reason.StartsWith("limit:", StringComparison.Ordinal) ? note.Reason : "limit:" + note.Reason,
            _ => null,
        };
    }

    /// <summary>
    /// Null when every fact that is missing is explained by a file that <see cref="FileGap"/>
    /// takes out. Else the extraction left out facts across files (a bound on facts, on files
    /// or on notes, or a failure with no file), and no absence can be judged.
    /// </summary>
    private static string? ExtractionGap(ExtractionResult result)
    {
        if (result.IsComplete)
            return null;
        var cuts = result.Notes.Where(n => n.Kind is NoteKind.ParseFailure or NoteKind.LimitReached).ToList();
        if (cuts.Any(n => n.Kind == NoteKind.ParseFailure && n.Path.Length == 0))
            return CoverageGap.Failed;
        var acrossFiles = cuts.Count == 0 || cuts.Any(n =>
            n.Path.Length == 0 || n.Reason is "limit:facts" or "limit:files" or "limit:total-bytes" or "limit:notes");
        return acrossFiles ? CoverageGap.Limit : null;
    }

    /// <summary>
    /// Reads the pull request and returns its provenance. The scan reads the head commit
    /// through the repository of the scan, as every other read of it does. For a pull request
    /// from another repository (a fork, or a fork that was deleted) that works only when
    /// GitHub serves the head commit here, which it does for the pull request refs it keeps.
    /// When it does not, the scan stops: Vellum reads no other repository.
    /// </summary>
    private static async Task<ScanPullRequest> PinPullRequestAsync(
        GitHubConnection github, string owner, string name, GitHubRepository repository, int number, CancellationToken ct)
    {
        var read = await github.GetPullRequestAsync(owner, name, number, ct);
        if (read.Failure is GitHubFailure.NotFound)
            throw ScanStop.Failed(ScanFailures.PullRequestNotFound(number, github.HasToken));
        var pull = Read(read, github, "pull-request", null);

        var fromFork = pull.HeadRepository is null || !string.Equals(pull.HeadRepository, repository.FullName, StringComparison.OrdinalIgnoreCase);
        if (fromFork)
        {
            var served = await github.ResolveRefAsync(owner, name, pull.HeadSha, ct);
            if (served.Failure is GitHubFailure.RateLimited)
                Read(served, github, "ref", null);
            if (served.Failure is not null || served.Value != pull.HeadSha)
                throw ScanStop.Failed(ScanFailures.PullRequestHeadUnreadable(number));
        }

        return new ScanPullRequest(
            pull.Number, pull.Title, pull.State, pull.Merged, pull.BaseRef, pull.BaseSha, pull.HeadRef, pull.HeadSha,
            pull.HeadRepository, fromFork);
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
        file.State = ScanFileState.Read;
        scan.FilesRead++;
        scan.BytesRead += bytes;
    }

    private static void Skip(ScanEntity scan, ScanFileEntity file, string reason, bool partial)
    {
        file.State = ScanFileState.Skipped;
        file.Reason = reason;
        scan.FilesSkipped++;
        scan.Partial |= partial;
        scan.Notes = ScanNotes.Write(ScanNotes.Read(scan.Notes).Append(ScanNotes.Of(reason, file.Path)));
    }

    /// <summary>
    /// The checkpoint. The scan row is written with every save, and its fencing token is a
    /// concurrency token: a run that lost the scan fails here, and its file rows with it.
    /// </summary>
    private async Task SaveAsync(AppDbContext db, ScanEntity scan, CancellationToken ct)
    {
        scan.UpdatedAt = time.GetUtcNow();
        await db.SaveChangesAsync(ct);
    }

    private async Task StoreBlobAsync(
        AppDbContext db, Guid workspaceId, string blobSha, byte[]? content, long size, string? withheld, CancellationToken ct)
    {
        // Two scans of one workspace can fetch the same blob at once. The content is the
        // same, and a row that says "withheld" is never given content.
        var now = time.GetUtcNow();
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO import.blobs (workspace_id, blob_sha, content, size, withheld_reason, created_at)
            VALUES ({workspaceId}, {blobSha}, {content}, {size}, {withheld}, {now})
            ON CONFLICT (workspace_id, blob_sha) DO NOTHING
            """, ct);
    }

    /// <summary>
    /// Writes how the run ended, when this run still has the scan. A scan that waits keeps
    /// its checkpoint and is taken again at <c>RetryAt</c>. An unexpected error is such a
    /// wait, with a longer one each time, until the scan has failed that way
    /// <see cref="ScanWorkerOptions.MaxInternalFailures"/> times in a row.
    /// </summary>
    private async Task FinishAsync(Guid scanId, long leaseToken, ScanStop stop, bool internalError)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = time.GetUtcNow();

        var failures = 0;
        if (internalError)
        {
            failures = await db.Scans.Where(s => s.Id == scanId).Select(s => s.InternalFailures).FirstOrDefaultAsync(CancellationToken.None) + 1;
            if (failures < options.MaxInternalFailures)
                stop = ScanStop.Retry(ScanFailures.InternalRetry, now + Backoff(failures));
        }

        var waiting = stop.Status == ScanStatus.Waiting;
        VellumMetrics.ImportScanEnded(EnumText.Of(stop.Status));
        DateTimeOffset? retryAt = !waiting ? null : stop.Error is null ? RetryAt(stop.RetryAt) : stop.RetryAt;
        DateTimeOffset? completedAt = waiting ? null : now;

        await db.Scans
            .Where(s => s.Id == scanId && s.LeaseToken == leaseToken && s.Status == ScanStatus.Running)
            .ExecuteUpdateAsync(set => set
                .SetProperty(s => s.Status, stop.Status)
                .SetProperty(s => s.ErrorCode, stop.Error == null ? null : stop.Error.Code)
                .SetProperty(s => s.ErrorMessage, stop.Error == null ? null : stop.Error.Message)
                .SetProperty(s => s.RetryAt, retryAt)
                .SetProperty(s => s.CompletedAt, completedAt)
                .SetProperty(s => s.UpdatedAt, now)
                .SetProperty(s => s.InternalFailures, failures)
                .SetProperty(s => s.Crashes, 0)
                .SetProperty(s => s.LeaseOwner, (string?)null)
                .SetProperty(s => s.LeaseExpiresAt, (DateTimeOffset?)null),
                CancellationToken.None);
    }

    private async Task DropUnusedBlobsAsync(Guid workspaceId, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await ImportRetention.DropUnusedBlobsAsync(db, workspaceId, time.GetUtcNow(), ct);
    }

    private static string NoteMessage(ExtractionNote note) => note.Kind switch
    {
        NoteKind.Undecided => "A rule found a site it could not decide from the text. No fact was made for it.",
        NoteKind.Redacted or NoteKind.Conflict or NoteKind.LimitReached or NoteKind.Skipped => ScanNotes.Message(note.Reason),
        _ => "The file could not be parsed. Its facts are missing from this scan.",
    };
}

/// <summary>
/// The hosted service: <see cref="ScanWorkerOptions.Parallelism"/> worker loops per app
/// instance, and one timer that applies the retention rules whether or not a scan runs.
/// </summary>
public sealed class ScanWorkerService(ScanWorker worker, ScanSignal signal, ScanWorkerOptions options, ILogger<ScanWorkerService> logger)
    : BackgroundService
{
    private readonly string _instance = $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled)
            return;

        var loops = Enumerable.Range(1, Math.Clamp(options.Parallelism, 1, 32))
            .Select(n => WorkAsync($"{_instance}:{n}", stoppingToken))
            .Append(RetainAsync(stoppingToken));
        await Task.WhenAll(loops);
    }

    private async Task WorkAsync(string workerId, CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // While there is work, take the next scan at once.
                if (await worker.RunOnceAsync(workerId, stoppingToken))
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

    private async Task RetainAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.Retention);
        try
        {
            do
            {
                try
                {
                    await worker.RunRetentionAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // The next tick tries again: every retention statement is safe to repeat.
                    logger.LogError("The import retention run failed ({ExceptionType}). It runs again at the next interval.", ex.GetType().FullName);
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
            // The host is stopping.
        }
    }
}
