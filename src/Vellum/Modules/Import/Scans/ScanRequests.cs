using System.Globalization;
using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Vellum.Modules.Import.Proposals;
using Vellum.Modules.Settings.Document;
using Vellum.Modules.Workspaces.Authorization;
using Vellum.Shared;

namespace Vellum.Modules.Import.Scans;

/// <param name="Repository"><c>owner/name</c>, or the address of the repository's page on the workspace's GitHub host.</param>
/// <param name="Ref">A branch, a tag or a commit SHA. Without it the workspace's default ref applies, else the repository's default branch.</param>
/// <param name="IncludePaths">Path prefixes to scan. Without any, the whole repository is in scope.</param>
/// <param name="ExcludePaths">Path prefixes to leave out.</param>
public sealed record StartScanRequest(string? Repository, string? Ref, string[]? IncludePaths, string[]? ExcludePaths);

public sealed record StartScanResponse(Guid Id);

public sealed record ScanProgressDto(int FilesListed, int FilesRead, int FilesSkipped, long BytesRead);

public sealed record ProposalCountsDto(int Pending, int Accepted, int Rejected);

/// <param name="IsComplete">True only for a completed scan that read everything in its scope. Any other scan is not a full result.</param>
/// <param name="RetryAt">When a scan that waits for GitHub's rate limit starts again.</param>
public sealed record ScanDto(
    Guid Id, string Repository, string? Ref, string? CommitSha, string Status, bool IsComplete,
    ScanProgressDto Progress, ProposalCountsDto ProposalCounts, ScanError? Error,
    DateTimeOffset? RetryAt, string RequestedBy, DateTimeOffset CreatedAt, DateTimeOffset? CompletedAt);

public sealed record ScanDetailDto(
    Guid Id, string Repository, string? Ref, string? CommitSha, string Status, bool IsComplete,
    ScanProgressDto Progress, ProposalCountsDto ProposalCounts, ScanError? Error,
    DateTimeOffset? RetryAt, string RequestedBy, DateTimeOffset CreatedAt, DateTimeOffset? CompletedAt,
    IReadOnlyList<ScanNote> Notes, IReadOnlyList<RuleVersion> RuleVersions);

public static partial class ScanRequests
{
    public const int MaxScopePaths = 50;
    public const int MaxScopePathLength = 400;

    [GeneratedRegex(@"^[A-Za-z0-9_.-]{1,100}$")]
    private static partial Regex RepositoryPart();

    public static async Task<IResult> Start(
        Guid projectId, StartScanRequest request, ClaimsPrincipal user,
        WorkspaceAuthorizationService auth, SettingsResolver settings, ImportDbContext db,
        ScanSignal signal, TimeProvider time, CancellationToken ct)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier)!;
        await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Editor, ct);
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
        var reference = string.IsNullOrWhiteSpace(request.Ref) ? null : request.Ref;
        if (reference is not null && (TextRule.Line("ref", reference, 255) is not null || !IsRef(reference)))
            return Invalid("ref", "A ref is a branch, a tag or a commit SHA of at most 255 characters, with no white space.");
        if (!TryReadPaths(request.IncludePaths, out var include, out var pathError))
            return Invalid("includePaths", pathError);
        if (!TryReadPaths(request.ExcludePaths, out var exclude, out pathError))
            return Invalid("excludePaths", pathError);

        var now = time.GetUtcNow();
        var limit = workspace.Effective.Usage.MaxScansPerDay;
        var import = workspace.Effective.Import;
        var scan = new ScanEntity
        {
            Id = Guid.NewGuid(), ProjectId = projectId, WorkspaceId = workspaceId,
            Host = host, Repository = repository, RequestedRef = reference,
            IncludePaths = include, ExcludePaths = exclude,
            Limits = new ScanLimits(import.MaxFiles, import.MaxFileBytes, import.MaxTotalBytes, [.. import.ExcludeGlobs]).ToJson(),
            Status = ScanStatus.Queued, RequestedBy = userId, CreatedAt = now, UpdatedAt = now,
        };

        // The count and the insert are one transaction under a lock for the workspace, so
        // two requests at once cannot both take the last scan of the day.
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtextextended({"import-scans:" + workspaceId}, 0))", ct);

        var dayStart = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);
        var today = await db.Scans.CountAsync(s => s.WorkspaceId == workspaceId && s.CreatedAt >= dayStart, ct);
        if (today >= limit)
        {
            return Results.Json(new ErrorResponse("scan_limit_reached",
                string.Create(CultureInfo.InvariantCulture,
                    $"This workspace started {today} scans today (UTC). The limit usage.maxScansPerDay is {limit}. Start the scan tomorrow, or raise the limit in the workspace settings.")),
                statusCode: StatusCodes.Status429TooManyRequests);
        }

        db.Scans.Add(scan);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (IsUniqueViolation(ex))
        {
            return Results.Conflict(new ErrorResponse("scan_in_progress",
                "A scan of this repository is already queued or running for this project. Wait for it, or cancel it."));
        }
        await transaction.CommitAsync(ct);

        signal.Notify();
        return Results.Accepted($"/api/projects/{projectId}/imports/{scan.Id}", new StartScanResponse(scan.Id));
    }

    public static async Task<IResult> List(
        Guid projectId, string? cursor, int? limit, ClaimsPrincipal user,
        WorkspaceAuthorizationService auth, ImportDbContext db, CancellationToken ct)
    {
        await auth.RequireProjectRoleAsync(projectId, user.FindFirstValue(ClaimTypes.NameIdentifier)!, WorkspaceRole.Viewer, ct);
        var pageSize = Math.Clamp(limit ?? 50, 1, 200);

        // Newest first. The cursor holds the creation time of the last row as ticks.
        var query = db.Scans.AsNoTracking().Where(s => s.ProjectId == projectId);
        if (CursorEncoder.Decode(cursor) is { } after
            && long.TryParse(after.SortKey, NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
            && ticks <= DateTimeOffset.MaxValue.UtcTicks)
        {
            var createdAt = new DateTimeOffset(ticks, TimeSpan.Zero);
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
        return Results.Ok(new Page<ScanDto>([.. rows.Select(r => ToDto(r, counts.GetValueOrDefault(r.Id)))], next));
    }

    public static async Task<IResult> Get(
        Guid projectId, Guid importId, ClaimsPrincipal user,
        WorkspaceAuthorizationService auth, ImportDbContext db, CancellationToken ct)
    {
        await auth.RequireProjectRoleAsync(projectId, user.FindFirstValue(ClaimTypes.NameIdentifier)!, WorkspaceRole.Viewer, ct);
        var scan = await db.Scans.AsNoTracking().FirstOrDefaultAsync(s => s.Id == importId && s.ProjectId == projectId, ct);
        if (scan is null)
            return NotFound();

        var counts = await CountsAsync(db, [scan.Id], ct);
        var rules = await db.Snapshots.AsNoTracking().Where(s => s.ScanId == scan.Id).Select(s => s.RuleVersions).FirstOrDefaultAsync(ct);
        var dto = ToDto(scan, counts.GetValueOrDefault(scan.Id));
        return Results.Ok(new ScanDetailDto(
            dto.Id, dto.Repository, dto.Ref, dto.CommitSha, dto.Status, dto.IsComplete, dto.Progress, dto.ProposalCounts, dto.Error,
            dto.RetryAt, dto.RequestedBy, dto.CreatedAt, dto.CompletedAt,
            ScanNotes.Read(scan.Notes),
            rules is null ? [] : System.Text.Json.JsonSerializer.Deserialize<List<RuleVersion>>(rules, System.Text.Json.JsonSerializerOptions.Web) ?? []));
    }

    /// <summary>
    /// A queued or waiting scan is cancelled at once. A running scan is asked to stop: its
    /// worker sees the request within its watch interval and ends the call in flight.
    /// </summary>
    public static async Task<IResult> Cancel(
        Guid projectId, Guid importId, ClaimsPrincipal user,
        WorkspaceAuthorizationService auth, ImportDbContext db, TimeProvider time, CancellationToken ct)
    {
        await auth.RequireProjectRoleAsync(projectId, user.FindFirstValue(ClaimTypes.NameIdentifier)!, WorkspaceRole.Editor, ct);
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
            : NotFound();
    }

    /// <summary>A failed or cancelled scan goes back to the queue with its checkpoint: the same commit, the blobs it has.</summary>
    public static async Task<IResult> Resume(
        Guid projectId, Guid importId, ClaimsPrincipal user,
        WorkspaceAuthorizationService auth, ImportDbContext db, ScanSignal signal, TimeProvider time, CancellationToken ct)
    {
        await auth.RequireProjectRoleAsync(projectId, user.FindFirstValue(ClaimTypes.NameIdentifier)!, WorkspaceRole.Editor, ct);
        var now = time.GetUtcNow();

        int resumed;
        try
        {
            resumed = await db.Scans
                .Where(s => s.Id == importId && s.ProjectId == projectId && (s.Status == ScanStatus.Failed || s.Status == ScanStatus.Cancelled))
                .ExecuteUpdateAsync(set => set
                    .SetProperty(s => s.Status, ScanStatus.Queued)
                    .SetProperty(s => s.CancelRequested, false)
                    .SetProperty(s => s.ErrorCode, (string?)null)
                    .SetProperty(s => s.ErrorMessage, (string?)null)
                    .SetProperty(s => s.CompletedAt, (DateTimeOffset?)null)
                    .SetProperty(s => s.Attempts, 0)
                    .SetProperty(s => s.UpdatedAt, now), ct);
        }
        catch (Exception ex) when (IsUniqueViolation(ex))
        {
            return Results.Conflict(new ErrorResponse("scan_in_progress",
                "Another scan of this repository is already queued or running for this project. Wait for it, or cancel it."));
        }

        if (resumed == 1)
        {
            signal.Notify();
            return Results.Accepted();
        }

        return await db.Scans.AnyAsync(s => s.Id == importId && s.ProjectId == projectId, ct)
            ? Results.Conflict(new ErrorResponse("scan_not_resumable", "Only a failed or cancelled scan can be resumed."))
            : NotFound();
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

    internal static IResult NotFound() => Results.NotFound(new ErrorResponse("not_found", "Import not found"));

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
    /// evidence pointer and of the key of every accepted fact.
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

    private static async Task<Dictionary<Guid, ProposalCountsDto>> CountsAsync(ImportDbContext db, Guid[] scanIds, CancellationToken ct)
    {
        var rows = await db.Proposals.AsNoTracking()
            .Where(p => scanIds.Contains(p.ScanId))
            .GroupBy(p => new { p.ScanId, p.Status })
            .Select(g => new { g.Key.ScanId, g.Key.Status, Count = g.Count() })
            .ToListAsync(ct);
        return rows.GroupBy(r => r.ScanId).ToDictionary(g => g.Key, g => new ProposalCountsDto(
            g.Where(r => r.Status == ProposalStatus.Pending).Sum(r => r.Count),
            g.Where(r => r.Status == ProposalStatus.Accepted).Sum(r => r.Count),
            g.Where(r => r.Status == ProposalStatus.Rejected).Sum(r => r.Count)));
    }

    private static ScanDto ToDto(ScanEntity scan, ProposalCountsDto? counts) => new(
        scan.Id, scan.Repository, scan.RequestedRef, scan.CommitSha, scan.Status, scan.IsComplete,
        new ScanProgressDto(scan.FilesListed, scan.FilesRead, scan.FilesSkipped, scan.BytesRead),
        counts ?? new ProposalCountsDto(0, 0, 0),
        scan.ErrorCode is null ? null : new ScanError(scan.ErrorCode, scan.ErrorMessage ?? ""),
        scan.RetryAt, scan.RequestedBy, scan.CreatedAt, scan.CompletedAt);

    private static IResult Invalid(string field, string message) =>
        Results.BadRequest(new ErrorResponse("validation_error", "Validation failed", Errors: [new FieldError(field, message)]));
}
