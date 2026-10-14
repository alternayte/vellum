using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Vellum.Modules.GitHosts;
using Vellum.Modules.GitHub;
using Vellum.Modules.Import.Scans;
using Vellum.Modules.Settings;
using Vellum.Modules.Workspaces;
using Vellum.Shared;
using static Vellum.Shared.ResultExtensions;

namespace Vellum.Modules.Import.Sources;

/// <param name="Repository">The repository as a Source of the kind keeps it, such as <c>acme/orders</c>.</param>
/// <param name="Name">The name as the host writes it, with its case.</param>
/// <param name="SourceId">The Source this project already has for the repository, or null.</param>
public sealed record RepositoryChoiceDto(string Repository, string Name, string DefaultBranch, bool IsPrivate, Guid? SourceId);

/// <param name="HasMore">True when the host has another page.</param>
public sealed record RepositoryListDto(string Kind, string Owner, int Page, IReadOnlyList<RepositoryChoiceDto> Repositories, bool HasMore);

/// <param name="Kind"><c>github</c>, <c>gitlab</c>, <c>bitbucket</c> or <c>azure-devops</c>.</param>
/// <param name="Repositories">Up to <see cref="BulkAdd.MaxRepositories"/> repositories in the form of the kind.</param>
/// <param name="Ref">The ref every scan reads. Without it each repository's default ref applies.</param>
public sealed record BulkAddRequest(string? Kind, string[]? Repositories, string? Ref = null);

/// <summary>What happened to one repository of a bulk add. One failure does not stop the others.</summary>
/// <param name="Created">True when the add made the Source; false when the project had it.</param>
/// <param name="ScanId">The queued scan. Null when the Source has no new scan: see <paramref name="Error"/>.</param>
public sealed record BulkAddItemDto(string Repository, Guid? SourceId, bool Created, Guid? ScanId, ErrorResponse? Error);

public sealed record BulkAddResponse(IReadOnlyList<BulkAddItemDto> Items);

/// <summary>
/// Onboards many repositories of one organization, group or workspace into one project: one
/// Source per repository and one scan each. The scans wait in the one scan queue, which runs a
/// bounded number at a time per host (<see cref="ScanWorkerOptions.MaxRunningPerHost"/>), and
/// each Source reports its own progress and failure. A repository that fails to be added or
/// queued is reported and the others go on.
/// </summary>
public static class BulkAdd
{
    public const int MaxRepositories = 100;

    /// <summary>One page of the repositories of an owner on a git host, with the Source the project already has for each.</summary>
    public static async Task<IResult> Repositories(
        Guid projectId, string? kind, string? owner, int? page, ClaimsPrincipal user,
        WorkspaceAuthorizationService auth, SettingsResolver settings, GitHubReader github, GitHostReader hosts, AppDbContext db, CancellationToken ct)
    {
        await auth.RequireProjectRoleAsync(projectId, user.UserId(), WorkspaceRole.Editor, ct);
        var pageNumber = page ?? 1;
        if (pageNumber is < 1 or > 100)
            return Invalid("page", "A page is a number from 1 to 100.");
        var workspaceId = await auth.GetProjectWorkspaceIdAsync(projectId, ct);
        var workspace = await settings.GetWorkspaceAsync(workspaceId, ct);

        string host;
        List<RepositoryChoiceDto> choices;
        bool hasMore;
        if (kind == "github")
        {
            if (workspace.GithubApiBaseUrl is not SettingsUrl.Usable api)
                return Results.Conflict(new ErrorResponse("github_not_configured", "The workspace has no usable GitHub API base URL. Set github.apiBaseUrl."));
            if (string.IsNullOrWhiteSpace(owner) || !ScanRequests.IsRepositoryName($"{owner.Trim()}/x"))
                return Invalid("owner", "Give the organization or user, such as acme.");
            host = ScanRequests.WebHost(api.Url);
            var connected = await github.ConnectAsync(workspaceId, ct);
            if (connected.Failure is { } notConnected)
                return Failed(notConnected, false, null);
            var listed = await connected.Value!.ListRepositoriesAsync(owner.Trim(), pageNumber, ct);
            if (listed.Failure is { } failure)
                return Failed(failure, connected.Value.HasToken, null);
            choices = [.. listed.Value.Repositories.Select(r => new RepositoryChoiceDto(r.FullName.ToLowerInvariant(), r.FullName, r.DefaultBranch, r.IsPrivate, null))];
            hasMore = listed.Value.HasMore;
        }
        else if (GitHostInfo.Of(kind) is { } gitHost)
        {
            if (gitHost.ApiBaseUrl(workspace) is not SettingsUrl.Usable api)
                return SourceRequests.HostNotConfigured(gitHost);
            if (!gitHost.TryReadOwner(owner, out var checkedOwner))
                return Invalid("owner", $"Give the {gitHost.OwnerForm}.");
            host = gitHost.WebHost(api.Url);
            var words = GitHostFetch.Words(gitHost);
            var connected = await hosts.ConnectAsync(gitHost, workspaceId, ct);
            if (connected.Failure is { } notConnected)
                return Failed(notConnected, false, words);
            var listed = await connected.Value!.ListRepositoriesAsync(checkedOwner, pageNumber, ct);
            if (listed.Failure is { } failure)
                return Failed(failure, connected.Value.HasToken, words);
            choices = [.. listed.Value!.Repositories.Select(r => new RepositoryChoiceDto(r.Locator, r.Name, r.DefaultBranch, r.IsPrivate, null))];
            hasMore = listed.Value.HasMore;
        }
        else
        {
            return Invalid("kind", "Bulk add reads github, gitlab, bitbucket or azure-devops.");
        }

        var locators = choices.Select(c => c.Repository).ToList();
        var existing = await db.Sources.AsNoTracking()
            .Where(s => s.ProjectId == projectId && s.Host == host && locators.Contains(s.Locator))
            .ToDictionaryAsync(s => s.Locator, s => s.Id, ct);
        return Results.Ok(new RepositoryListDto(kind!, owner!.Trim(), pageNumber,
            [.. choices.Select(c => c with { SourceId = existing.TryGetValue(c.Repository, out var id) ? id : null })], hasMore));
    }

    /// <summary>
    /// Adds a Source for each repository the project does not have yet and queues one scan of
    /// each. Each repository is its own step: a refusal (a bad name, the day's scan limit, a scan
    /// already running) is reported for it, and the next one goes on.
    /// </summary>
    public static async Task<IResult> Handle(
        Guid projectId, BulkAddRequest request, ClaimsPrincipal user,
        WorkspaceAuthorizationService auth, SettingsResolver settings, SourcesRoot roots, AppDbContext db,
        ScanSignal signal, TimeProvider time, CancellationToken ct)
    {
        var userId = user.UserId();
        await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Editor, ct);
        if (!EnumText.TryParse(request.Kind, out SourceKind kind) || !SourceEntity.IsGit(kind))
            return Invalid("kind", "Bulk add reads github, gitlab, bitbucket or azure-devops.");
        if (request.Repositories is not { Length: > 0 } repositories)
            return Invalid("repositories", "Choose at least one repository.");
        if (repositories.Length > MaxRepositories)
            return Invalid("repositories", $"Choose at most {MaxRepositories} repositories at a time.");

        var workspaceId = await auth.GetProjectWorkspaceIdAsync(projectId, ct);
        var workspace = await settings.GetWorkspaceAsync(workspaceId, ct);
        var items = new List<BulkAddItemDto>();
        foreach (var repository in repositories.Distinct(StringComparer.Ordinal))
        {
            db.ChangeTracker.Clear();
            var place = SourceRequests.Place(kind, new CreateSourceRequest(request.Kind, null, repository), null, workspace, roots);
            if (place.Error is { } refused)
            {
                items.Add(new BulkAddItemDto(repository, null, false, null, ErrorOf(refused)));
                continue;
            }

            var (source, created) = await EnsureSourceAsync(db, kind, projectId, workspaceId, place.Host!, place.Locator!, userId, time.GetUtcNow(), ct);
            var queued = await ScanRequests.QueueAsync(projectId, source, new ScanRequests.ScanOptions(request.Ref, null, null, null),
                user, workspace, db, signal, time.GetUtcNow(), ct);
            items.Add(queued is IValueHttpResult { Value: StartScanResponse started }
                ? new BulkAddItemDto(source.Locator, source.Id, created, started.Id, null)
                : new BulkAddItemDto(source.Locator, source.Id, created, null, ErrorOf(queued)));
        }
        db.ChangeTracker.Clear();
        return Results.Ok(new BulkAddResponse(items));
    }

    /// <summary>The Source of a place in a project, created with the place's name when the project has none yet. Two requests at once make one.</summary>
    internal static async Task<(SourceEntity Source, bool Created)> EnsureSourceAsync(
        AppDbContext db, SourceKind kind, Guid projectId, Guid workspaceId, string host, string locator, string createdBy, DateTimeOffset now, CancellationToken ct)
    {
        if (await db.Sources.AsNoTracking().FirstOrDefaultAsync(s => s.ProjectId == projectId && s.Host == host && s.Locator == locator, ct) is { } existing)
            return (existing, false);
        var source = new SourceEntity
        {
            Id = Guid.NewGuid(), ProjectId = projectId, WorkspaceId = workspaceId, Kind = kind, Name = locator,
            Host = host, Locator = locator, CreatedBy = createdBy, CreatedAt = now,
        };
        db.Sources.Add(source);
        try
        {
            await db.SaveChangesAsync(ct);
            db.Entry(source).State = EntityState.Detached;
            return (source, true);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            db.ChangeTracker.Clear();
            return (await db.Sources.AsNoTracking().FirstAsync(s => s.ProjectId == projectId && s.Host == host && s.Locator == locator, ct), false);
        }
    }

    private static ErrorResponse ErrorOf(IResult result) => result is IValueHttpResult { Value: ErrorResponse error }
        ? error
        : new ErrorResponse("not_added", "The repository could not be added.");

    private static IResult Failed(GitHubFailure failure, bool hasToken, ScanFailures.HostWords? words)
    {
        var error = ScanFailures.Describe(failure, hasToken, "repository", null, words);
        var body = new ErrorResponse(error.Code, error.Message.Replace(ScanFailures.ResumeHint, "", StringComparison.Ordinal));
        return failure switch
        {
            GitHubFailure.NotFound => Results.NotFound(body with
            {
                Title = hasToken
                    ? "The host has no organization, group or workspace of this name that the workspace's token can see."
                    : "The host has no public organization, group or workspace of this name. Save a token to list private ones.",
            }),
            GitHubFailure.RateLimited => Results.Json(body, statusCode: StatusCodes.Status429TooManyRequests),
            _ => Results.Json(body, statusCode: StatusCodes.Status502BadGateway),
        };
    }
}
