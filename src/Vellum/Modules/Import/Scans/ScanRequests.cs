using System.Globalization;
using System.Security.Claims;
using System.Text.RegularExpressions;
using Deedbox;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Vellum.Modules.Identity.AgentTokens;
using Vellum.Modules.Import.Proposals;
using Vellum.Modules.Import.Sources;
using Vellum.Modules.Settings;
using Vellum.Modules.Workspaces;
using Vellum.Shared;
using static Vellum.Shared.ResultExtensions;

namespace Vellum.Modules.Import.Scans;

/// <param name="Repository"><c>owner/name</c>, or the address of the repository's page on the workspace's GitHub host.</param>
/// <param name="Ref">A branch, a tag or a commit SHA. Without it the workspace's default ref applies, else the repository's default branch.</param>
/// <param name="IncludePaths">Path prefixes to scan. Without any, the whole repository is in scope.</param>
/// <param name="ExcludePaths">Path prefixes to leave out.</param>
/// <param name="PullRequest">
/// The number of a pull request of the repository, in place of a ref. The scan reads the head
/// commit of the pull request, records the pull request with both commit SHAs, and is a
/// comparison: its proposals are a preview. Nothing is written to GitHub.
/// </param>
public sealed record StartScanRequest(string? Repository, string? Ref, string[]? IncludePaths, string[]? ExcludePaths, int? PullRequest = null);

public sealed record StartScanResponse(Guid Id);

public sealed record ScanProgressDto(int FilesListed, int FilesRead, int FilesSkipped, long BytesRead);

public sealed record ProposalCountsDto(int Pending, int Accepted, int Rejected);

/// <summary>
/// The accepted facts of the repository that the scan could judge, and those it could not,
/// by reason. A fact that is not covered got no removal proposal and no stale mark, and
/// kept the mark it had.
/// </summary>
/// <param name="Scope">The scope, an exclude glob or the path filter left out a file of the fact's evidence.</param>
/// <param name="Limit">A bound of the scan left out a file of the fact's evidence.</param>
/// <param name="Withheld">A file of the fact's evidence holds a secret or is not text, and is not kept.</param>
/// <param name="Failed">A file of the fact's evidence could not be fetched, verified or parsed.</param>
public sealed record LinkedFactCoverageDto(int Covered, int NotCovered, int Scope, int Limit, int Withheld, int Failed)
{
    internal static LinkedFactCoverageDto? From(LinkCoverage? c) => c is null
        ? null
        : new(c.Covered, c.Scope + c.Limit + c.Withheld + c.Failed, c.Scope, c.Limit, c.Withheld, c.Failed);
}

/// <param name="FilesListed">Every file of the commit.</param>
/// <param name="FilesExtracted">The files whose content the extractors read. Only these were looked at.</param>
/// <param name="LinkedFacts">Null until the scan completes.</param>
public sealed record ScanCoverageDto(int FilesListed, int FilesExtracted, LinkedFactCoverageDto? LinkedFacts);

/// <param name="Repository"><c>owner/name</c> in lower case: the name the scan is filed under.</param>
/// <param name="RepositoryName">The name as GitHub writes it. Null until the scan asked GitHub.</param>
/// <param name="Ref">The ref the caller asked for, or null.</param>
/// <param name="ResolvedRef">The name of the ref the scan read. Null for a scan of a bare commit.</param>
/// <param name="IsComplete">True only for a completed scan that no limit and no failure cut short. It says nothing about the scope: see <paramref name="Coverage"/>.</param>
/// <param name="Kind"><c>tracking</c> or <c>comparison</c>.</param>
/// <param name="TrackedRef">The ref that the accepted facts of the repository follow in this project. Null when there is none yet. Empty when it is the default ref and no scan named it yet.</param>
/// <param name="CanDecide">True when the proposals of the scan can be decided: the scan is completed and is a tracking scan.</param>
/// <param name="CanDecideReason">Why not, when <paramref name="CanDecide"/> is false and the scan is completed.</param>
/// <param name="Relinked">Links that the scan moved to a new fact key after the extraction rules changed.</param>
/// <param name="NeedsReview">Links that it could not move by the exact rule. Each has a <c>relink</c> proposal.</param>
/// <param name="RetryAt">When a scan that waits starts again.</param>
/// <param name="PullRequestNumber">The pull request the caller asked for, or null for a scan of a ref.</param>
/// <param name="PullRequest">The pull request as GitHub described it when the scan pinned the head commit. Null until then.</param>
/// <param name="CanResume">True for a failed or cancelled scan that a resume can carry on. False when the failure is one that a resume meets again: start a new scan.</param>
/// <param name="SourceId">The Source the scan reads.</param>
/// <param name="ContentHeld">False for a push of <c>vellum scan</c>: the instance holds the manifest, the facts and the citations, and no file content. The source view has nothing to show.</param>
/// <param name="ReportedCommitSha">For a push: the commit that <c>.git/HEAD</c> named on the machine that pushed, as reported. Not verified.</param>
public sealed record ScanDto(
    Guid Id, string Repository, string? RepositoryName, string? Ref, string? ResolvedRef, string? CommitSha, string Status, bool IsComplete,
    string Kind, string? TrackedRef, bool CanDecide, string? CanDecideReason,
    ScanCoverageDto Coverage, int Relinked, int NeedsReview,
    ScanProgressDto Progress, ProposalCountsDto ProposalCounts, ScanError? Error,
    DateTimeOffset? RetryAt, string RequestedBy, DateTimeOffset CreatedAt, DateTimeOffset? CompletedAt,
    int? PullRequestNumber, ScanPullRequest? PullRequest, bool CanResume, Guid SourceId, bool ContentHeld, string? ReportedCommitSha);

public sealed record ScanDetailDto(
    Guid Id, string Repository, string? RepositoryName, string? Ref, string? ResolvedRef, string? CommitSha, string Status, bool IsComplete,
    string Kind, string? TrackedRef, bool CanDecide, string? CanDecideReason,
    ScanCoverageDto Coverage, int Relinked, int NeedsReview,
    ScanProgressDto Progress, ProposalCountsDto ProposalCounts, ScanError? Error,
    DateTimeOffset? RetryAt, string RequestedBy, DateTimeOffset CreatedAt, DateTimeOffset? CompletedAt,
    int? PullRequestNumber, ScanPullRequest? PullRequest, bool CanResume,
    IReadOnlyList<ScanNote> Notes, IReadOnlyList<RuleVersion> RuleVersions,
    string? TrackedBy, DateTimeOffset? TrackedAt, Guid SourceId, string? SnapshotId, bool ContentHeld, string? ReportedCommitSha);

public static class ScanSnapshots
{
    /// <summary>
    /// The snapshots of a project whose scans read a ref: no pull request and no bare commit.
    /// Only such a snapshot can stand for the newest state of a repository; a pull request or
    /// a commit follows no line of history. The database filters them.
    /// </summary>
    public static IQueryable<SnapshotEntity> OfRefs(AppDbContext db, Guid projectId) =>
        db.ImportSnapshots.Where(s => s.ProjectId == projectId && db.Scans.Any(c => c.Id == s.ScanId && c.PullRequestNumber == null
            && !(c.RequestedRef != null && Regex.IsMatch(c.RequestedRef, "^[0-9A-Fa-f]{40}$"))));
}

public static partial class ScanRequests
{
    public const int MaxScopePaths = 50;
    public const int MaxScopePathLength = 400;
    /// <summary>How many times a person can resume one scan. Each resume gives the scan its automatic attempts back.</summary>
    public const int MaxManualResumes = 3;
    /// <summary>The largest pull request number a scan accepts. GitHub's numbers are far below it.</summary>
    public const int MaxPullRequestNumber = 100_000_000;

    [GeneratedRegex(@"^[A-Za-z0-9_.-]{1,100}$")]
    private static partial Regex RepositoryPart();

    public static async Task<IResult> Start(
        Guid projectId, StartScanRequest request, ClaimsPrincipal user,
        WorkspaceAuthorizationService auth, SettingsResolver settings, AppDbContext db,
        ScanSignal signal, TimeProvider time, CancellationToken ct)
    {
        var userId = user.UserId();
        await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Editor, ct);
        // A token scans a pull request, which is always a comparison: it writes nothing to the
        // model, marks no fact stale and cannot become the tracked ref. A scan of a ref can.
        if (AgentPrincipal.IsAgent(user) && (request.PullRequest is null || request.Ref is not null))
        {
            return Results.Json(new ErrorResponse(AgentBoundary.AgentNotAllowedError, "This agent token cannot do this",
                "An agent token starts only a scan of a pull request: give pullRequest and no ref. A person scans a branch, a tag or a commit."),
                statusCode: StatusCodes.Status403Forbidden);
        }
        var workspaceId = await auth.GetProjectWorkspaceIdAsync(projectId, ct);

        var workspace = await settings.GetWorkspaceAsync(workspaceId, ct);
        if (workspace.GithubApiBaseUrl is not SettingsUrl.Usable github)
        {
            return Results.Conflict(new ErrorResponse("github_not_configured",
                "The workspace has no usable GitHub API base URL. Set github.apiBaseUrl in the workspace settings to an address the instance policy allows."));
        }
        var host = WebHost(github.Url);

        if (!TryReadRepository(request.Repository, host, out var repository, out var repositoryError))
            return Invalid("repository", repositoryError);
        // A token reads only a repository that the project already follows or holds facts of,
        // not every repository that the GitHub credential of the workspace can read.
        if (AgentPrincipal.IsAgent(user)
            && !await db.TrackedRefs.AnyAsync(t => t.ProjectId == projectId && t.Host == host && t.Repository == repository, ct)
            && !await db.FactLinks.AnyAsync(l => l.ProjectId == projectId && l.Host == host && l.Repository == repository, ct))
        {
            return Results.Json(new ErrorResponse(AgentBoundary.AgentNotAllowedError, "This agent token cannot do this",
                "An agent token scans only a repository that the project follows. A person imports the repository first."),
                statusCode: StatusCodes.Status403Forbidden);
        }
        var now = time.GetUtcNow();
        // A scan of a repository is a scan of the project's github Source for it, which the
        // first scan creates.
        var source = await SourceRequests.EnsureGitHubSourceAsync(db, projectId, workspaceId, host, repository, AgentPrincipal.IsAgent(user) ? AgentPrincipal.Actor(user)! : userId, now, ct);
        return await QueueAsync(projectId, source, new ScanOptions(request.Ref, request.IncludePaths, request.ExcludePaths, request.PullRequest),
            user, workspace, db, signal, now, ct);
    }

    /// <summary>What a scan of a Source reads: a ref or a pull request, and the paths in scope.</summary>
    public sealed record ScanOptions(string? Ref, string[]? IncludePaths, string[]? ExcludePaths, int? PullRequest);

    /// <summary>Checks the options, takes one unit of the day's scans and queues a scan of <paramref name="source"/>.</summary>
    /// <param name="prepare">Adds what the scan starts with (a runtime upload's document) in the same transaction as the scan row.</param>
    /// <param name="fill">
    /// Writes what the scan starts with after its row is saved (an archive upload's file rows and
    /// blobs) in the same transaction: the scan is queued with all of it or not at all.
    /// </param>
    internal static async Task<IResult> QueueAsync(
        Guid projectId, SourceEntity source, ScanOptions request, ClaimsPrincipal user, WorkspaceSettings workspace,
        AppDbContext db, ScanSignal signal, DateTimeOffset now, CancellationToken ct,
        Func<ScanEntity, CancellationToken, Task>? prepare = null, Func<ScanEntity, CancellationToken, Task>? fill = null)
    {
        var userId = user.UserId();
        var reference = string.IsNullOrWhiteSpace(request.Ref) ? null : request.Ref;
        if (reference is not null && (TextRule.Line("ref", reference, 255) is not null || !IsRef(reference)))
            return Invalid("ref", "A ref is a branch, a tag or a commit SHA of at most 255 characters, with no white space.");
        if (request.PullRequest is { } number)
        {
            if (reference is not null)
                return Invalid("pullRequest", "Give a branch, a tag, a commit or a pull request number. Not two of them.");
            if (number is < 1 or > MaxPullRequestNumber)
                return Invalid("pullRequest", "A pull request number is a whole number from 1.");
        }
        if (!TryReadPaths(request.IncludePaths, out var include, out var pathError))
            return Invalid("includePaths", pathError);
        if (!TryReadPaths(request.ExcludePaths, out var exclude, out pathError))
            return Invalid("excludePaths", pathError);

        var workspaceId = source.WorkspaceId;
        var import = workspace.Effective.Import;
        var scan = new ScanEntity
        {
            Id = Guid.NewGuid(), ProjectId = projectId, WorkspaceId = workspaceId, SourceId = source.Id,
            Host = source.Host, Repository = source.Locator, RequestedRef = reference, PullRequestNumber = request.PullRequest,
            IncludePaths = include, ExcludePaths = exclude,
            Limits = new ScanLimits(import.MaxFiles, import.MaxFileBytes, import.MaxTotalBytes, [.. import.ExcludeGlobs]).ToJson(),
            // A token's scan names the token, so a person can tell it from their own.
            Status = ScanStatus.Queued, RequestedBy = AgentPrincipal.IsAgent(user) ? AgentPrincipal.Actor(user)! : userId, CreatedAt = now, UpdatedAt = now,
        };

        // The count and the insert are one transaction under a lock for the workspace, so
        // two requests at once cannot both take the last scan of the day.
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (await ChargeAsync(db, workspaceId, scan.Id, workspace.Effective.Usage.MaxScansPerDay, now, ct) is { } refused)
            return refused;

        db.Scans.Add(scan);
        if (prepare is not null)
            await prepare(scan, ct);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (IsUniqueViolation(ex))
        {
            return Results.Conflict(new ErrorResponse("scan_in_progress",
                "A scan of this Source is already queued or running for this project. Wait for it, or cancel it."));
        }
        if (fill is not null)
            await fill(scan, ct);
        await transaction.CommitAsync(ct);

        signal.Notify();
        return Results.Accepted($"/api/projects/{projectId}/imports/{scan.Id}", new StartScanResponse(scan.Id));
    }

    /// <summary>
    /// Takes one unit of <c>usage.maxScansPerDay</c> for the workspace, inside the caller's
    /// transaction and under a lock for the workspace. Returns the refusal when the day's
    /// units are used up. A started scan is one unit; so is a resume that asks GitHub again.
    /// </summary>
    internal static async Task<IResult?> ChargeAsync(
        AppDbContext db, Guid workspaceId, Guid scanId, int limit, DateTimeOffset now, CancellationToken ct)
    {
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtextextended({"import-scans:" + workspaceId}, 0))", ct);

        var dayStart = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);
        var today = await db.ScanCharges.CountAsync(c => c.WorkspaceId == workspaceId && c.ChargedAt >= dayStart, ct);
        if (today >= limit)
        {
            return Results.Json(new ErrorResponse("scan_limit_reached",
                string.Create(CultureInfo.InvariantCulture,
                    $"This workspace started {today} scans today (UTC). The limit usage.maxScansPerDay is {limit}. Start the scan tomorrow, or raise the limit in the workspace settings.")),
                statusCode: StatusCodes.Status429TooManyRequests);
        }

        db.ScanCharges.Add(new ScanChargeEntity { Id = Guid.NewGuid(), WorkspaceId = workspaceId, ScanId = scanId, ChargedAt = now });
        return null;
    }

    public static async Task<IResult> List(
        Guid projectId, string? cursor, int? limit, ClaimsPrincipal user,
        WorkspaceAuthorizationService auth, AppDbContext db, CancellationToken ct)
    {
        await auth.RequireProjectRoleAsync(projectId, user.UserId(), WorkspaceRole.Viewer, ct);
        var pageSize = Math.Clamp(limit ?? 50, 1, 200);

        // Newest first. The cursor holds the creation time of the last row as ticks.
        var query = db.Scans.AsNoTracking().Where(s => s.ProjectId == projectId);
        if (CursorEncoder.DecodeNumber(cursor) is { } after)
        {
            if (after.Number > DateTimeOffset.MaxValue.UtcTicks)
                throw new InvalidCursorException();
            var createdAt = new DateTimeOffset(after.Number, TimeSpan.Zero);
            query = query.Where(s => s.CreatedAt < createdAt || (s.CreatedAt == createdAt && s.Id.CompareTo(after.Id) < 0));
        }

        var rows = await query.OrderByDescending(s => s.CreatedAt).ThenByDescending(s => s.Id).Take(pageSize + 1).ToListAsync(ct);
        string? next = null;
        if (rows.Count > pageSize)
        {
            rows.RemoveAt(pageSize);
            next = CursorEncoder.Encode(rows[^1].CreatedAt.UtcTicks.ToString(CultureInfo.InvariantCulture), rows[^1].Id);
        }

        var counts = await CountsAsync(db, [.. rows.Select(r => r.Id)], ct);
        var tracked = await db.TrackedRefs.AsNoTracking().Where(t => t.ProjectId == projectId).ToListAsync(ct);
        return Results.Ok(new Page<ScanDto>([.. rows.Select(r => ToDto(r, counts.GetValueOrDefault(r.Id), TrackedOf(tracked, r)))], next));
    }

    public static async Task<IResult> Get(
        Guid projectId, Guid importId, ClaimsPrincipal user,
        WorkspaceAuthorizationService auth, AppDbContext db, CancellationToken ct)
    {
        await auth.RequireProjectRoleAsync(projectId, user.UserId(), WorkspaceRole.Viewer, ct);
        var scan = await db.Scans.AsNoTracking().FirstOrDefaultAsync(s => s.Id == importId && s.ProjectId == projectId, ct);
        return scan is null ? NotFound("Import") : Results.Ok(await DetailAsync(db, scan, ct));
    }

    private static async Task<ScanDetailDto> DetailAsync(AppDbContext db, ScanEntity scan, CancellationToken ct)
    {
        var counts = await CountsAsync(db, [scan.Id], ct);
        var snapshot = await db.ImportSnapshots.AsNoTracking().Where(s => s.ScanId == scan.Id)
            .Select(s => new { s.RuleVersions, s.SnapshotId }).FirstOrDefaultAsync(ct);
        var rules = snapshot?.RuleVersions;
        var tracked = await db.TrackedRefs.AsNoTracking()
            .FirstOrDefaultAsync(t => t.ProjectId == scan.ProjectId && t.Host == scan.Host && t.Repository == scan.Repository, ct);
        var dto = ToDto(scan, counts.GetValueOrDefault(scan.Id), tracked);
        return new ScanDetailDto(
            dto.Id, dto.Repository, dto.RepositoryName, dto.Ref, dto.ResolvedRef, dto.CommitSha, dto.Status, dto.IsComplete,
            dto.Kind, dto.TrackedRef, dto.CanDecide, dto.CanDecideReason, dto.Coverage, dto.Relinked, dto.NeedsReview,
            dto.Progress, dto.ProposalCounts, dto.Error, dto.RetryAt, dto.RequestedBy, dto.CreatedAt, dto.CompletedAt,
            dto.PullRequestNumber, dto.PullRequest, dto.CanResume,
            ScanNotes.Read(scan.Notes),
            rules is null ? [] : System.Text.Json.JsonSerializer.Deserialize<List<RuleVersion>>(rules, System.Text.Json.JsonSerializerOptions.Web) ?? [],
            scan.TrackedBy, scan.TrackedAt, scan.SourceId, snapshot?.SnapshotId, dto.ContentHeld, dto.ReportedCommitSha);
    }

    /// <summary>
    /// A queued or waiting scan is cancelled at once. A running scan is asked to stop: its
    /// worker sees the request within its watch interval and ends the call in flight.
    /// </summary>
    public static async Task<IResult> Cancel(
        Guid projectId, Guid importId, ClaimsPrincipal user,
        WorkspaceAuthorizationService auth, AppDbContext db, TimeProvider time, CancellationToken ct)
    {
        await auth.RequireProjectRoleAsync(projectId, user.UserId(), WorkspaceRole.Editor, ct);
        var now = time.GetUtcNow();

        var idle = await db.Scans
            .Where(s => s.Id == importId && s.ProjectId == projectId && (s.Status == ScanStatus.Queued || s.Status == ScanStatus.Waiting))
            .ExecuteUpdateAsync(set => set
                .SetProperty(s => s.Status, ScanStatus.Cancelled)
                .SetProperty(s => s.RetryAt, (DateTimeOffset?)null)
                .SetProperty(s => s.CompletedAt, now)
                .SetProperty(s => s.UpdatedAt, now), ct);
        if (idle == 1)
            return Results.Accepted();

        var running = await db.Scans
            .Where(s => s.Id == importId && s.ProjectId == projectId && s.Status == ScanStatus.Running)
            .ExecuteUpdateAsync(set => set.SetProperty(s => s.CancelRequested, true).SetProperty(s => s.UpdatedAt, now), ct);
        if (running == 1)
            return Results.Accepted();

        return await db.Scans.AnyAsync(s => s.Id == importId && s.ProjectId == projectId, ct)
            ? Results.Conflict(new ErrorResponse("scan_finished", "The scan already ended. There is nothing to cancel."))
            : NotFound("Import");
    }

    /// <summary>
    /// A failed or cancelled scan goes back to the queue with its checkpoint: the same
    /// commit, the blobs it has. These rules bound it:
    /// <list type="bullet">
    /// <item>A scan whose failure is in <see cref="ScanFailures.Final"/> is not resumed: a
    /// resume would meet the same failure.</item>
    /// <item>A scan that stopped longer ago than <see cref="ImportRetention.ResumableFor"/>
    /// is expired: its blobs are no longer kept, so it is not resumed.</item>
    /// <item>A scan is resumed by hand at most <see cref="MaxManualResumes"/> times. Each
    /// resume gives it its automatic attempts back.</item>
    /// <item>A resume that has to ask GitHub again is one unit of <c>usage.maxScansPerDay</c>,
    /// like a new scan. A resume that only has to extract what is stored is free.</item>
    /// </list>
    /// </summary>
    public static async Task<IResult> Resume(
        Guid projectId, Guid importId, ClaimsPrincipal user,
        WorkspaceAuthorizationService auth, SettingsResolver settings, AppDbContext db,
        ScanSignal signal, TimeProvider time, CancellationToken ct)
    {
        await auth.RequireProjectRoleAsync(projectId, user.UserId(), WorkspaceRole.Editor, ct);
        var now = time.GetUtcNow();

        var scan = await db.Scans.AsNoTracking().FirstOrDefaultAsync(s => s.Id == importId && s.ProjectId == projectId, ct);
        if (scan is null)
            return NotFound("Import");
        if (scan.Status is not (ScanStatus.Failed or ScanStatus.Cancelled))
            return Results.Conflict(new ErrorResponse("scan_not_resumable", "Only a failed or cancelled scan can be resumed."));
        if (scan.ContentNotHeld)
            return Results.Conflict(new ErrorResponse("scan_not_resumable", "A push is not resumed: Vellum holds none of its files. Run vellum scan again."));
        if (!ScanFailures.CanResume(scan.Status, scan.ErrorCode))
        {
            return Results.Conflict(new ErrorResponse("scan_not_resumable",
                "A resume meets the same failure again: it asks GitHub and the project the same questions. Start a new scan."));
        }
        if (ImportRetention.IsExpired(scan, now))
        {
            return Results.Conflict(new ErrorResponse("scan_expired", string.Create(CultureInfo.InvariantCulture,
                $"The scan stopped more than {ImportRetention.ResumableFor.TotalDays:0} days ago, and the files it read are no longer kept. Start a new scan.")));
        }
        if (scan.ManualResumes >= MaxManualResumes)
        {
            return Results.Conflict(new ErrorResponse("resume_limit_reached",
                $"The scan was resumed {MaxManualResumes} times and stopped each time. Start a new scan. If it stops in the same way, report the scan ID to the operator of this instance."));
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (await HasToFetchAsync(db, scan, ct))
        {
            var workspace = await settings.GetWorkspaceAsync(scan.WorkspaceId, ct);
            if (await ChargeAsync(db, scan.WorkspaceId, scan.Id, workspace.Effective.Usage.MaxScansPerDay, now, ct) is { } refused)
                return refused;
            await db.SaveChangesAsync(ct);
        }

        int resumed;
        try
        {
            resumed = await db.Scans
                .Where(s => s.Id == importId && s.ProjectId == projectId
                    && (s.Status == ScanStatus.Failed || s.Status == ScanStatus.Cancelled) && s.ManualResumes == scan.ManualResumes)
                .ExecuteUpdateAsync(set => set
                    .SetProperty(s => s.Status, ScanStatus.Queued)
                    .SetProperty(s => s.CancelRequested, false)
                    .SetProperty(s => s.ErrorCode, (string?)null)
                    .SetProperty(s => s.ErrorMessage, (string?)null)
                    .SetProperty(s => s.CompletedAt, (DateTimeOffset?)null)
                    .SetProperty(s => s.Attempts, 0)
                    .SetProperty(s => s.InternalFailures, 0)
                    .SetProperty(s => s.Crashes, 0)
                    .SetProperty(s => s.ManualResumes, s => s.ManualResumes + 1)
                    .SetProperty(s => s.UpdatedAt, now), ct);
        }
        catch (Exception ex) when (IsUniqueViolation(ex))
        {
            return Results.Conflict(new ErrorResponse("scan_in_progress",
                "Another scan of this repository is already queued or running for this project. Wait for it, or cancel it."));
        }
        if (resumed == 0)
            return Results.Conflict(new ErrorResponse("scan_not_resumable", "Only a failed or cancelled scan can be resumed."));
        if (!ScanFailures.CanResume(scan.Status, scan.ErrorCode))
        {
            return Results.Conflict(new ErrorResponse("scan_not_resumable",
                "A resume meets the same failure again: it asks GitHub and the project the same questions. Start a new scan."));
        }

        await transaction.CommitAsync(ct);
        signal.Notify();
        return Results.Accepted();
    }

    /// <summary>True when the scan cannot finish from what is stored: no commit, no file list, a file not fetched yet, or a fetched file whose content is gone.</summary>
    private static async Task<bool> HasToFetchAsync(AppDbContext db, ScanEntity scan, CancellationToken ct)
    {
        if (scan.CommitSha is null || !scan.TreeListed)
            return true;
        var workspaceId = scan.WorkspaceId;
        return await db.ScanFiles.AnyAsync(f => f.ScanId == scan.Id
            && ((f.State == ScanFileState.Pending && !db.Blobs.Any(b => b.WorkspaceId == workspaceId && b.BlobSha == f.BlobSha))
                || (f.State == ScanFileState.Read && !db.Blobs.Any(b => b.WorkspaceId == workspaceId && b.BlobSha == f.BlobSha && b.Content != null))), ct);
    }

    /// <summary>
    /// Makes the ref of a completed comparison scan the tracked ref of its repository in this
    /// project, and plans the scan again as a tracking scan: its proposals can then be
    /// decided, and it may mark facts stale. The scan row records who did it and when. Only
    /// the newest completed scan of a ref can be tracked, and a scan of a bare commit cannot.
    /// </summary>
    public static async Task<IResult> Track(
        Guid projectId, Guid importId, ClaimsPrincipal user, HttpContext http,
        WorkspaceAuthorizationService auth, AppDbContext db, ScanWorkerOptions worker, TimeProvider time, CancellationToken ct)
    {
        var userId = user.UserId();
        await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Editor, ct);

        var scan = await db.Scans.AsNoTracking().FirstOrDefaultAsync(s => s.Id == importId && s.ProjectId == projectId, ct);
        if (scan is null)
            return NotFound("Import");
        if (scan.Status != ScanStatus.Completed || scan.CommitSha is null)
            return Results.Conflict(new ErrorResponse("import_not_completed", "Only a completed scan can be made the tracked one."));
        if (scan.IsPullRequest())
        {
            return Results.Conflict(new ErrorResponse("ref_not_trackable",
                "This scan read the head commit of a pull request, which is no line of history to follow. Scan a branch or a tag, then track that scan."));
        }
        if (scan.IsBareCommit() || scan.ResolvedRef is null)
        {
            return Results.Conflict(new ErrorResponse("ref_not_trackable",
                "This scan read a commit and no ref, so there is no line of history to follow. Scan a branch or a tag, then track that scan."));
        }

        var tracked = await db.TrackedRefs.AsNoTracking()
            .FirstOrDefaultAsync(t => t.ProjectId == projectId && t.Host == scan.Host && t.Repository == scan.Repository, ct);
        if (tracked is not null && tracked.RefName == scan.ResolvedRef)
            return Results.Conflict(new ErrorResponse("already_tracked", $"The accepted facts of this repository already follow '{tracked.RefName}'."));

        var newer = await db.Scans.AnyAsync(s =>
            s.ProjectId == projectId && s.Host == scan.Host && s.Repository == scan.Repository && s.Id != scan.Id
            && s.Status == ScanStatus.Completed && s.ResolvedRef == scan.ResolvedRef && s.CompletedAt > scan.CompletedAt, ct);
        if (newer)
            return Results.Conflict(new ErrorResponse("newer_scan_exists", "A newer scan of this ref is completed. Track that scan."));

        // The marks that follow are conclusions of the scan. The scan row says who asked.
        http.RequestServices.GetRequiredService<DeedboxContext>().Metadata = new EventMetadata
        {
            Actor = "system:import-scan",
            CorrelationId = scan.Id.ToString(),
        };
        var now = time.GetUtcNow();
        var outcome = await SnapshotWriter.WriteAsync(
            http.RequestServices, scan, WriteMode.Track(userId), await StoredInputAsync(db, scan, ct), now, ct);
        switch (outcome)
        {
            case SnapshotOutcome.ProjectGone:
                return NotFound("Import");
            case SnapshotOutcome.ModelBusy:
                return Results.Conflict(new ErrorResponse("model_busy",
                    "The project's model changed each time the scan was compared with it. Nothing was written. Try again."));
            case SnapshotOutcome.LeaseLost:
                return Results.Conflict(new ErrorResponse("scan_changed", "The scan changed while it was planned again. Nothing was written. Try again."));
        }

        // The ref a person follows now is the line the model takes, as a first scan of it is.
        if (worker.AutoApply)
            await AutoApply.RunAsync(http.RequestServices, scan.Id, userId, time, ct);
        var after = await db.Scans.AsNoTracking().FirstAsync(s => s.Id == importId, ct);
        return Results.Ok(await DetailAsync(db, after, ct));
    }

    /// <summary>A completed scan as a plan reads it again: its stored facts, and where it looked, from its file rows.</summary>
    internal static async Task<SnapshotInput> StoredInputAsync(AppDbContext db, ScanEntity scan, CancellationToken ct)
    {
        var kind = await db.Sources.AsNoTracking().Where(x => x.Id == scan.SourceId).Select(x => x.Kind).FirstOrDefaultAsync(ct);
        if (Runtime.RuntimeFetch.IsRuntime(kind))
        {
            var path = await db.ScanFiles.AsNoTracking().Where(f => f.ScanId == scan.Id).Select(f => f.Path).FirstOrDefaultAsync(ct) ?? "";
            var stored = await SnapshotWriter.StoredFactsAsync(db, scan.Id, ct);
            return new SnapshotInput(stored, scan.IsComplete, scan.ExtractionGapText(), scan.Notes,
                () => ScanWorker.RuntimeCoverage(path, scan.ExtractionGapText()), scan.FilesExtracted);
        }

        var files = await db.ScanFiles.AsNoTracking().Where(f => f.ScanId == scan.Id)
            .Select(f => new { f.Path, f.State, f.Reason }).ToListAsync(ct);
        var coverageFiles = files.ToDictionary(
            f => f.Path, f => f.State == ScanFileState.Read ? null : f.Reason ?? CoverageGap.ParseFailure, StringComparer.Ordinal);
        var absent = ScanScope.AbsentGap(ScanLimits.FromJson(scan.Limits), scan.IncludePaths, scan.ExcludePaths);
        var facts = await SnapshotWriter.StoredFactsAsync(db, scan.Id, ct);
        return new SnapshotInput(facts, scan.IsComplete, scan.ExtractionGapText(), scan.Notes,
            () => new ScanCoverage(coverageFiles, absent, scan.ExtractionGapText()), scan.FilesExtracted);
    }

    /// <summary>The index <c>ix_scans_one_active_per_repository</c> refused a second active scan.</summary>
    private static bool IsUniqueViolation(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
                return true;
        }
        return false;
    }

    /// <summary>
    /// The host of the repository pages for an API address: <c>github.com</c> for
    /// <c>api.github.com</c>, and the API host itself for GitHub Enterprise Server.
    /// </summary>
    internal static string WebHost(Uri apiBaseUrl)
    {
        var host = apiBaseUrl.IdnHost.ToLowerInvariant();
        if (host == "api.github.com")
            return "github.com";
        return apiBaseUrl.IsDefaultPort ? host : string.Create(CultureInfo.InvariantCulture, $"{host}:{apiBaseUrl.Port}");
    }

    /// <summary>
    /// Reads <c>owner/name</c> or a page address on <paramref name="host"/>. The result is in
    /// lower case: GitHub names do not depend on case, and the name is part of every
    /// evidence pointer. What identifies the repository across renames is its GitHub ID,
    /// which the scan reads.
    /// </summary>
    internal static bool TryReadRepository(string? value, string host, out string repository, out string error)
    {
        repository = "";
        error = $"Give the repository as owner/name or as https://{host}/owner/name.";
        if (string.IsNullOrWhiteSpace(value) || value.Length > 400 || value.Any(char.IsControl))
            return false;

        var text = value.Trim();
        if (text.Contains("://", StringComparison.Ordinal))
        {
            if (!Uri.TryCreate(text, UriKind.Absolute, out var url) || url.Scheme is not ("https" or "http")
                || url.UserInfo.Length > 0 || url.Query.Length > 0 || url.Fragment.Length > 0)
                return false;
            var urlHost = url.IsDefaultPort ? url.IdnHost : string.Create(CultureInfo.InvariantCulture, $"{url.IdnHost}:{url.Port}");
            if (!string.Equals(urlHost, host, StringComparison.OrdinalIgnoreCase))
            {
                error = $"The address is not on this workspace's GitHub host ({host}).";
                return false;
            }
            text = url.AbsolutePath.Trim('/');
        }

        var parts = text.Split('/');
        if (parts.Length != 2)
            return false;
        var name = parts[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? parts[1][..^4] : parts[1];
        if (!IsPart(parts[0]) || !IsPart(name))
            return false;

        repository = $"{parts[0]}/{name}".ToLowerInvariant();
        return true;
    }

    /// <summary>True for <c>owner/name</c> in the form a scan is filed under.</summary>
    internal static bool IsRepositoryName(string value)
    {
        var parts = value.Split('/');
        return parts.Length == 2 && IsPart(parts[0]) && IsPart(parts[1]);
    }

    private static bool IsPart(string value) => value is not ("." or "..") && RepositoryPart().IsMatch(value);

    private static bool IsRef(string value) =>
        !value.StartsWith('/') && !value.EndsWith('/')
        && !value.Contains("..", StringComparison.Ordinal) && !value.Contains("//", StringComparison.Ordinal)
        && !value.Any(c => char.IsWhiteSpace(c) || c is '?' or '#' or '\\');

    private static bool TryReadPaths(string[]? values, out string[] paths, out string error)
    {
        paths = [];
        error = "";
        if (values is null)
            return true;
        if (values.Length > MaxScopePaths)
        {
            error = $"At most {MaxScopePaths} paths are allowed.";
            return false;
        }

        var result = new List<string>();
        foreach (var value in values)
        {
            var path = value?.Trim().Trim('/') ?? "";
            if (path.Length is 0 or > MaxScopePathLength || TextRule.Line("path", path, MaxScopePathLength) is not null
                || path.Contains('\\') || path.Split('/').Any(segment => segment is "" or "." or ".."))
            {
                error = $"A path is a directory or a file of the repository, such as src/Api, of at most {MaxScopePathLength} characters.";
                return false;
            }
            result.Add(path);
        }
        paths = [.. result.Distinct(StringComparer.Ordinal)];
        return true;
    }

    internal static async Task<Dictionary<Guid, ProposalCountsDto>> CountsAsync(AppDbContext db, Guid[] scanIds, CancellationToken ct)
    {
        var rows = await db.ImportProposals.AsNoTracking()
            .Where(p => scanIds.Contains(p.ScanId))
            .GroupBy(p => new { p.ScanId, p.Status })
            .Select(g => new { g.Key.ScanId, g.Key.Status, Count = g.Count() })
            .ToListAsync(ct);
        return rows.GroupBy(r => r.ScanId).ToDictionary(g => g.Key, g => new ProposalCountsDto(
            g.Where(r => r.Status == ProposalStatus.Pending).Sum(r => r.Count),
            g.Where(r => r.Status == ProposalStatus.Accepted).Sum(r => r.Count),
            g.Where(r => r.Status == ProposalStatus.Rejected).Sum(r => r.Count)));
    }

    private static TrackedRefEntity? TrackedOf(List<TrackedRefEntity> tracked, ScanEntity scan) =>
        tracked.FirstOrDefault(t => t.Host == scan.Host && t.Repository == scan.Repository);

    internal static ScanDto ToDto(ScanEntity scan, ProposalCountsDto? counts, TrackedRefEntity? tracked)
    {
        var kind = ScanKindInfo.Of(scan, tracked);
        var completed = scan.Status == ScanStatus.Completed;
        return new ScanDto(
            scan.Id, scan.Repository, scan.RepositoryName, scan.RequestedRef, scan.ResolvedRef, scan.CommitSha, EnumText.Of(scan.Status), scan.IsComplete,
            kind.Kind, tracked?.RefName, completed && kind.Tracking, completed ? ScanKindInfo.Describe(kind, tracked) : null,
            new ScanCoverageDto(scan.FilesListed, scan.FilesExtracted, LinkedFactCoverageDto.From(LinkCoverage.FromJson(scan.Coverage))),
            scan.Relinked, scan.NeedsReview,
            new ScanProgressDto(scan.FilesListed, scan.FilesRead, scan.FilesSkipped, scan.BytesRead),
            counts ?? new ProposalCountsDto(0, 0, 0),
            scan.ErrorCode is null ? null : new ScanError(scan.ErrorCode, scan.ErrorMessage ?? ""),
            scan.RetryAt, scan.RequestedBy, scan.CreatedAt, scan.CompletedAt,
            scan.PullRequestNumber, ScanPullRequest.FromJson(scan.PullRequest),
            !scan.ContentNotHeld && ScanFailures.CanResume(scan.Status, scan.ErrorCode), scan.SourceId,
            !scan.ContentNotHeld, scan.ReportedCommitSha);
    }

}
