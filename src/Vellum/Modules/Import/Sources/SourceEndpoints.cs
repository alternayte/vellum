using System.Security.Claims;
using Deedbox;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Vellum.Modules.GitHosts;
using Vellum.Modules.Import.Proposals;
using Vellum.Modules.Import.Scans;
using Vellum.Modules.Modelling;
using Vellum.Modules.Settings;
using Vellum.Modules.Workspaces;
using Vellum.Shared;
using static Vellum.Shared.ResultExtensions;

namespace Vellum.Modules.Import.Sources;

/// <summary>One Snapshot of a Source: the manifest ID, and the commit SHA when the Source is git-based.</summary>
/// <param name="CommitSha">The commit of a git Source. For a directory with a <c>.git</c> folder, the commit its <c>HEAD</c> names, read as text: metadata that nothing checks against the files.</param>
/// <param name="Revision">What a citation of this Snapshot names after <c>@</c>: the commit SHA of a git Source, else the Snapshot ID.</param>
/// <param name="Files">The files in the manifest: those the scan read whole.</param>
/// <param name="ContentHeld">False for a push of <c>vellum scan</c>: the instance holds the manifest, the facts and the citations, and no file content.</param>
/// <param name="ReportedCommitSha">For a push or a directory: the commit that <c>.git/HEAD</c> names, as reported. Not verified.</param>
/// <param name="PushedBy">For a push: the agent token that pushed it, as <c>agent-token:&lt;id&gt;</c>. Null for a scan the instance ran.</param>
public sealed record SourceSnapshotDto(
    Guid ScanId, string SnapshotId, string? CommitSha, string Revision, string? Ref, int Files, int Facts, bool IsComplete, DateTimeOffset CreatedAt,
    bool ContentHeld, string? ReportedCommitSha, string? PushedBy);

/// <summary>
/// The proposals of a Source that wait for a person: those of its newest completed tracking
/// scan. Earlier scans of the same ref have no pending proposals left; they were superseded.
/// </summary>
/// <param name="ScanId">The scan whose proposals these are. Null when the Source has no completed tracking scan.</param>
/// <param name="CrossSourceConflicts">Conflicts where another Source of the project holds the entity with other values.</param>
/// <param name="BindQuestions">"Same as X?" questions: an element of another Source has the name of an element fact of this one.</param>
/// <param name="Acceptable">Pending proposals that "accept all from this Source" would accept: additions, changes and removals that no other Source holds and that change no truth label, with no conflict, question or relink, and every dependency accepted or in the set.</param>
public sealed record SourceReviewDto(
    Guid? ScanId, int Pending, int Additions, int Changes, int Removals, int Conflicts, int CrossSourceConflicts,
    int BindQuestions, int Relinks, int Acceptable);

/// <param name="Kind">One of <c>github</c>, <c>gitlab</c>, <c>bitbucket</c>, <c>azure-devops</c>, <c>directory</c>, <c>archive</c>, <c>agent-push</c>, <c>model-file</c>, <c>traces</c>, <c>newrelic</c>. This instance reads the first six.</param>
/// <param name="Citation">The <c>&lt;source&gt;</c> part of a citation <c>&lt;source&gt;@&lt;revision&gt;:path:start-end</c>.</param>
/// <param name="LatestScan">The newest scan of the Source, in any state. Null before the first.</param>
/// <param name="LatestSnapshot">The Snapshot of the newest completed scan. Null before the first.</param>
/// <param name="Connection">For a <c>traces</c> or <c>newrelic</c> Source: what it reads. Null for the other kinds.</param>
public sealed record SourceDto(
    Guid Id, string Kind, string Name, string Host, string Locator, string Citation,
    string CreatedBy, DateTimeOffset CreatedAt, ScanDto? LatestScan, SourceSnapshotDto? LatestSnapshot, SourceReviewDto Review,
    Runtime.RuntimeConnectionDto? Connection = null);

/// <param name="Kind"><c>github</c>, <c>gitlab</c>, <c>bitbucket</c>, <c>azure-devops</c>, <c>directory</c> (local profile only), <c>archive</c>, <c>traces</c> or <c>newrelic</c>. An <c>agent-push</c> Source is made by its first push.</param>
/// <param name="Name">What people call the Source. Without it the Source is named after its repository, folder or account. An archive or a traces Source needs one: its citations carry it.</param>
/// <param name="Repository">
/// For a git host: the repository as <c>owner/name</c> (GitHub), <c>group/name</c> or
/// <c>group/subgroup/name</c> (GitLab), <c>workspace/repository</c> (Bitbucket),
/// <c>organization/project/repository</c> (Azure DevOps), or the address of its page.
/// </param>
/// <param name="AccountId">For <c>newrelic</c>: the account ID.</param>
/// <param name="Region">For <c>newrelic</c>: <c>us</c> or <c>eu</c>. It chooses the fixed NerdGraph endpoint.</param>
/// <param name="ApiKey">For <c>newrelic</c>: a read-only User API key. It is stored envelope-encrypted and never returned. Only an Owner gives one.</param>
/// <param name="Path">For <c>directory</c>: a folder under the sources root, as a relative path such as <c>acme/orders</c>.</param>
public sealed record CreateSourceRequest(
    string? Kind, string? Name, string? Repository, long? AccountId = null, string? Region = null, string? ApiKey = null, string? Path = null);

public sealed record UpdateSourceRequest(string? Name);

/// <param name="Kind">A kind this instance reads: <c>github</c>, <c>gitlab</c>, <c>bitbucket</c>, <c>azure-devops</c>, <c>directory</c> or <c>archive</c>.</param>
/// <param name="Reason">Why the kind cannot be used now. Null when it can.</param>
/// <param name="Where">The web host of a git host, or the sources root of a directory. Null otherwise.</param>
public sealed record SourceKindDto(string Kind, bool Available, string? Reason, string? Where);

/// <param name="MaxArchiveBytes">The workspace limit <c>import.maxArchiveBytes</c>.</param>
public sealed record SourceKindsDto(IReadOnlyList<SourceKindDto> Kinds, long MaxArchiveBytes);

/// <param name="Ref">A branch, a tag or a commit SHA. Without it the workspace's default ref applies, else the repository's default branch.</param>
/// <param name="PullRequest">The number of a pull request, in place of a ref. Such a scan is a comparison.</param>
public sealed record StartSourceScanRequest(string? Ref, string[]? IncludePaths, string[]? ExcludePaths, int? PullRequest = null);

/// <param name="Truncated">True when a list holds <see cref="SourceRequests.MaxDiffItems"/> items and more differ.</param>
public sealed record ManifestDiffDto(IReadOnlyList<string> Added, IReadOnlyList<string> Removed, IReadOnlyList<string> Changed, bool Truncated);

public sealed record FactDiffItemDto(string Kind, string Name, string FactKey);

public sealed record FactDiffDto(IReadOnlyList<FactDiffItemDto> Added, IReadOnlyList<FactDiffItemDto> Removed, IReadOnlyList<FactDiffItemDto> Changed, bool Truncated);

/// <summary>Two Snapshots of one Source, compared: the files of the manifests by blob SHA, and the facts by key.</summary>
/// <param name="SameSnapshot">True when the two manifests are equal, which their IDs say.</param>
public sealed record SnapshotComparisonDto(
    Guid SourceId, SourceSnapshotDto Base, SourceSnapshotDto Head, bool SameSnapshot, ManifestDiffDto Files, FactDiffDto Facts);

/// <summary>An element of the project's model bound to a key of this Source.</summary>
public sealed record SourceBindingDto(Guid ElementId, string ElementName, string ElementKind, string ExternalKey);

/// <param name="AcknowledgeImpact">Must be true when an accepted removal or change breaks a scenario or leaves an entity pointing at what is removed.</param>
public sealed record AcceptSourceRequest(bool AcknowledgeImpact = false);

/// <summary>
/// Sources of a project: the list with what each waits for, a new Source, a scan of a Source,
/// its Snapshots and their comparison, its bindings, and "accept all from this Source".
/// </summary>
public static class SourceRequests
{
    /// <summary>The most items one list of a Snapshot comparison holds.</summary>
    public const int MaxDiffItems = 500;
    /// <summary>The most Snapshots one list shows, newest first.</summary>
    public const int MaxSnapshots = 100;

    public static async Task<IResult> List(
        Guid projectId, ClaimsPrincipal user, WorkspaceAuthorizationService auth, AppDbContext db, CancellationToken ct)
    {
        await auth.RequireProjectRoleAsync(projectId, user.UserId(), WorkspaceRole.Viewer, ct);
        var sources = await db.Sources.AsNoTracking().Where(s => s.ProjectId == projectId)
            .OrderBy(s => s.CreatedAt).ThenBy(s => s.Id).ToListAsync(ct);
        var result = new List<SourceDto>(sources.Count);
        foreach (var source in sources)
            result.Add(await ToDtoAsync(db, source, ct));
        return Results.Ok(result);
    }

    /// <summary>
    /// The kinds a new Source of this project can have on this instance, with the reason a kind
    /// cannot be used now: a git host whose API base URL is empty or refused, a directory on a
    /// hosted instance or without a sources root.
    /// </summary>
    public static async Task<IResult> Kinds(
        Guid projectId, ClaimsPrincipal user, WorkspaceAuthorizationService auth, SettingsResolver settings, SourcesRoot roots, CancellationToken ct)
    {
        await auth.RequireProjectRoleAsync(projectId, user.UserId(), WorkspaceRole.Viewer, ct);
        var workspace = await settings.GetWorkspaceAsync(await auth.GetProjectWorkspaceIdAsync(projectId, ct), ct);
        SourceKindDto Host(string kind, SettingsUrl url, string field, Func<Uri, string> webHost) => url is SettingsUrl.Usable usable
            ? new SourceKindDto(kind, true, null, webHost(usable.Url))
            : new SourceKindDto(kind, false, $"Set {field} in the workspace settings.", null);
        var kinds = new List<SourceKindDto> { Host("github", workspace.GithubApiBaseUrl, WorkspaceSettings.GithubField, ScanRequests.WebHost) };
        kinds.AddRange(GitHostInfo.All.Select(h => Host(h.Name, h.ApiBaseUrl(workspace), h.SettingsField, h.WebHost)));
        var directory = roots.Unavailable();
        kinds.Add(new SourceKindDto("directory", directory is null, directory?.Title, directory is null ? roots.RealRoot() : null));
        kinds.Add(new SourceKindDto("archive", true, null, null));
        return Results.Ok(new SourceKindsDto(kinds, workspace.Effective.Import.MaxArchiveBytes));
    }

    public static async Task<IResult> Get(
        Guid projectId, Guid sourceId, ClaimsPrincipal user, WorkspaceAuthorizationService auth, AppDbContext db, CancellationToken ct)
    {
        await auth.RequireProjectRoleAsync(projectId, user.UserId(), WorkspaceRole.Viewer, ct);
        var source = await db.Sources.AsNoTracking().FirstOrDefaultAsync(s => s.Id == sourceId && s.ProjectId == projectId, ct);
        return source is null ? NotFound("Source") : Results.Ok(await ToDtoAsync(db, source, ct));
    }

    /// <summary>
    /// Creates a Source. Its kind is one of the closed set; this instance reads <c>github</c>,
    /// <c>gitlab</c>, <c>bitbucket</c>, <c>azure-devops</c>, <c>directory</c> (local profile only)
    /// and <c>archive</c>, and refuses the others with <c>source_kind_unavailable</c>. One place
    /// is one Source per project.
    /// </summary>
    public static async Task<IResult> Create(
        Guid projectId, CreateSourceRequest request, ClaimsPrincipal user,
        WorkspaceAuthorizationService auth, SettingsResolver settings, SourcesRoot roots, Bootstrap bootstrap, AppDbContext db, TimeProvider time, CancellationToken ct)
    {
        var userId = user.UserId();
        await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Editor, ct);
        if (!EnumText.TryParse(request.Kind, out SourceKind kind))
            return Invalid("kind", $"A Source kind is one of: {string.Join(", ", Enum.GetValues<SourceKind>().Select(k => EnumText.Of(k)))}.");
        var name = string.IsNullOrWhiteSpace(request.Name) ? null : request.Name.Trim();
        if (name is not null && TextRule.Line("name", name, SourceEntity.MaxName) is { } nameError)
            return Invalid(nameError);

        var workspaceId = await auth.GetProjectWorkspaceIdAsync(projectId, ct);
        if (Runtime.RuntimeFetch.IsRuntime(kind))
            return await Runtime.RuntimeSources.CreateAsync(projectId, workspaceId, kind, name, request, userId, auth, bootstrap, db, time, ct);
        var workspace = await settings.GetWorkspaceAsync(workspaceId, ct);
        var place = Place(kind, request, name, workspace, roots);
        if (place.Error is { } refused)
            return refused;
        var (host, locator) = (place.Host!, place.Locator!);

        if (await db.Sources.AsNoTracking().FirstOrDefaultAsync(s => s.ProjectId == projectId && s.Host == host && s.Locator == locator, ct) is { } existing)
        {
            return Results.Conflict(new ErrorResponse("source_exists",
                $"The project already has the Source '{existing.Name}' for {existing.CitationName} ({existing.Id})."));
        }

        var source = new SourceEntity
        {
            Id = Guid.NewGuid(), ProjectId = projectId, WorkspaceId = workspaceId, Kind = kind, Name = name ?? locator,
            Host = host, Locator = locator, CreatedBy = userId, CreatedAt = time.GetUtcNow(),
        };
        db.Sources.Add(source);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return Results.Conflict(new ErrorResponse("source_exists", $"The project already has a Source for {source.CitationName}."));
        }
        return Results.Created($"/api/projects/{projectId}/sources/{source.Id}", await ToDtoAsync(db, source, ct));
    }

    /// <summary>Where a new Source of <paramref name="kind"/> is: its host and its locator, or why the request names no place this instance reads.</summary>
    internal sealed record SourcePlace(string? Host, string? Locator, IResult? Error);

    internal static SourcePlace Place(SourceKind kind, CreateSourceRequest request, string? name, WorkspaceSettings workspace, SourcesRoot roots)
    {
        static SourcePlace Refuse(IResult error) => new(null, null, error);
        switch (kind)
        {
            case SourceKind.Github:
            {
                if (workspace.GithubApiBaseUrl is not SettingsUrl.Usable github)
                    return Refuse(GitHubNotConfigured());
                var host = ScanRequests.WebHost(github.Url);
                return ScanRequests.TryReadRepository(request.Repository, host, out var repository, out var error)
                    ? new SourcePlace(host, repository, null)
                    : Refuse(Invalid("repository", error));
            }
            case SourceKind.Gitlab or SourceKind.Bitbucket or SourceKind.AzureDevops:
            {
                var gitHost = GitHostFetch.HostOf(kind);
                if (gitHost.ApiBaseUrl(workspace) is not SettingsUrl.Usable api)
                    return Refuse(HostNotConfigured(gitHost));
                var host = gitHost.WebHost(api.Url);
                return gitHost.TryReadLocator(request.Repository, host, out var locator, out var error)
                    ? new SourcePlace(host, locator, null)
                    : Refuse(Invalid("repository", error));
            }
            case SourceKind.Directory:
            {
                if (roots.Unavailable() is { } unavailable)
                    return Refuse(Results.BadRequest(unavailable));
                if (!SourcesRoot.TryReadLocator(request.Path, out var locator, out var error))
                    return Refuse(Invalid("path", error));
                return roots.Resolve(locator, out error) is null
                    ? Refuse(Results.BadRequest(new ErrorResponse("directory_refused", error)))
                    : new SourcePlace(DirectoryHost, locator, null);
            }
            case SourceKind.Archive:
            {
                if (name is null)
                    return Refuse(Invalid("name", "Give the archive Source a name, such as orders-service. Its citations carry it."));
                var slug = Slug(name);
                return slug.Length == 0
                    ? Refuse(Invalid("name", "Give a name with at least one letter or digit."))
                    : new SourcePlace(ArchiveHost, slug, null);
            }
            case SourceKind.AgentPush:
                return Refuse(Results.BadRequest(new ErrorResponse("source_kind_unavailable",
                    "An agent-push Source is made by its first push: run vellum scan with an agent token that has the push scope.")));
            case SourceKind.ModelFile:
                return Refuse(Results.BadRequest(new ErrorResponse("source_kind_unavailable",
                    "A model-file Source is made by uploading its file: POST /api/projects/{projectId}/sources/upload.")));
            default:
                return Refuse(Results.BadRequest(new ErrorResponse("source_kind_unavailable",
                    $"This instance cannot read a Source of kind '{EnumText.Of(kind)}' yet.")));
        }
    }

    /// <summary>The host part of a <c>directory</c> Source's citation: <c>directory/acme/orders@&lt;snapshot&gt;:path:lines</c>.</summary>
    public const string DirectoryHost = "directory";
    /// <summary>The host part of an <c>archive</c> Source's citation: <c>archive/orders@&lt;snapshot&gt;:path:lines</c>.</summary>
    public const string ArchiveHost = "archive";

    /// <summary>The locator of an archive Source: its name in lower case, with a dash for each run of other characters.</summary>
    internal static string Slug(string name)
    {
        var builder = new System.Text.StringBuilder();
        foreach (var c in name.ToLowerInvariant())
        {
            if (c is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '_')
                builder.Append(c);
            else if (builder.Length > 0 && builder[^1] != '-')
                builder.Append('-');
        }
        var slug = builder.ToString().Trim('-', '.');
        return slug.Length > 100 ? slug[..100].TrimEnd('-', '.') : slug;
    }
    public static async Task<IResult> Update(
        Guid projectId, Guid sourceId, UpdateSourceRequest request, ClaimsPrincipal user,
        WorkspaceAuthorizationService auth, AppDbContext db, CancellationToken ct)
    {
        await auth.RequireProjectRoleAsync(projectId, user.UserId(), WorkspaceRole.Editor, ct);
        var name = request.Name?.Trim();
        if (string.IsNullOrEmpty(name))
            return Invalid("name", "Give the Source a name.");
        if (TextRule.Line("name", name, SourceEntity.MaxName) is { } nameError)
            return Invalid(nameError);
        var source = await db.Sources.FirstOrDefaultAsync(s => s.Id == sourceId && s.ProjectId == projectId, ct);
        if (source is null)
            return NotFound("Source");
        source.Name = name;
        await db.SaveChangesAsync(ct);
        return Results.Ok(await ToDtoAsync(db, source, ct));
    }

    /// <summary>
    /// Queues a scan of the Source. For a git Source it is the same scan as <c>POST …/imports</c>
    /// starts for the repository; a pull request is a GitHub scan only. A directory is read
    /// again as it is now. An archive Source scans what is uploaded to it (<see cref="ArchiveUpload"/>).
    /// </summary>
    public static async Task<IResult> StartScan(
        Guid projectId, Guid sourceId, StartSourceScanRequest request, ClaimsPrincipal user,
        WorkspaceAuthorizationService auth, SettingsResolver settings, SourcesRoot roots, AppDbContext db,
        ScanSignal signal, TimeProvider time, CancellationToken ct)
    {
        await auth.RequireProjectRoleAsync(projectId, user.UserId(), WorkspaceRole.Editor, ct);
        var source = await db.Sources.AsNoTracking().FirstOrDefaultAsync(s => s.Id == sourceId && s.ProjectId == projectId, ct);
        if (source is null)
            return NotFound("Source");
        if (source.Kind == SourceKind.AgentPush)
        {
            return Results.Conflict(new ErrorResponse("push_only",
                "Vellum does not hold the files of an agent-push Source, so it cannot scan it. Run vellum scan on the machine that holds them."));
        }

        var workspace = await settings.GetWorkspaceAsync(source.WorkspaceId, ct);
        if (source.Kind == SourceKind.Traces)
            return Results.Conflict(new ErrorResponse("upload_required", "A traces Source reads a trace export file. Upload one to scan it."));
        if (source.Kind == SourceKind.Newrelic)
        {
            if (request.Ref is not null || request.PullRequest is not null || request.IncludePaths is { Length: > 0 } || request.ExcludePaths is { Length: > 0 })
                return Invalid("ref", "A New Relic Source has no ref, no pull request and no paths. Send an empty body.");
            return await ScanRequests.QueueAsync(projectId, source, new ScanRequests.ScanOptions(null, null, null, null),
                user, workspace, db, signal, time.GetUtcNow(), ct);
        }
        if (CanScan(source, request, workspace, roots) is { } refused)
            return refused;
        return await ScanRequests.QueueAsync(projectId, source,
            new ScanRequests.ScanOptions(request.Ref, request.IncludePaths, request.ExcludePaths, request.PullRequest),
            user, workspace, db, signal, time.GetUtcNow(), ct);
    }

    /// <summary>Null when a scan of the Source can be queued now. Else why not.</summary>
    internal static IResult? CanScan(SourceEntity source, StartSourceScanRequest request, WorkspaceSettings workspace, SourcesRoot roots)
    {
        if (request.PullRequest is not null && source.Kind != SourceKind.Github)
        {
            return Results.BadRequest(new ErrorResponse("pull_request_github_only",
                "A scan of a pull request reads GitHub only. Scan a branch, a tag or a commit of this Source."));
        }
        switch (source.Kind)
        {
            case SourceKind.Github:
            {
                if (workspace.GithubApiBaseUrl is not SettingsUrl.Usable github)
                    return GitHubNotConfigured();
                var host = ScanRequests.WebHost(github.Url);
                return host == source.Host ? null : Results.Conflict(new ErrorResponse("source_host_changed",
                    $"This Source is on {source.Host}, and the workspace now reads GitHub at {host}. Set github.apiBaseUrl back, or create a Source on the new host."));
            }
            case SourceKind.Gitlab or SourceKind.Bitbucket or SourceKind.AzureDevops:
            {
                var gitHost = GitHostFetch.HostOf(source.Kind);
                if (gitHost.ApiBaseUrl(workspace) is not SettingsUrl.Usable api)
                    return HostNotConfigured(gitHost);
                var host = gitHost.WebHost(api.Url);
                return host == source.Host ? null : Results.Conflict(new ErrorResponse("source_host_changed",
                    $"This Source is on {source.Host}, and the workspace now reads {gitHost.Display} at {host}. Set {gitHost.SettingsField} back, or create a Source on the new host."));
            }
            case SourceKind.Directory:
            {
                if (request.Ref is { Length: > 0 })
                    return Invalid("ref", "A directory has no refs. Leave the ref empty: the scan reads the folder as it is now.");
                if (roots.Unavailable() is { } unavailable)
                    return Results.BadRequest(unavailable);
                return roots.Resolve(source.Locator, out var error) is null
                    ? Results.Conflict(new ErrorResponse("directory_refused", error))
                    : null;
            }
            case SourceKind.Archive:
                return Results.BadRequest(new ErrorResponse("archive_needs_upload",
                    "An archive Source scans what is uploaded to it. Upload a .zip or .tar.gz to its archive address; each upload is a new Snapshot."));
            case SourceKind.ModelFile:
                return Results.Conflict(new ErrorResponse("source_is_upload",
                    "This Source is an uploaded file. Upload the file again to make a new Snapshot of it."));
            default:
                return Results.BadRequest(new ErrorResponse("source_kind_unavailable",
                    $"This instance cannot read a Source of kind '{EnumText.Of(source.Kind)}'."));
        }
    }

    /// <summary>
    /// Deletes a Source with its scans, Snapshots, proposals and the links of its accepted
    /// facts, then every stored blob that no remaining scan needs. The model keeps what people
    /// accepted: a deleted Source takes no element and no fact with it. A scan that runs stops
    /// when it finds its row gone.
    /// </summary>
    public static async Task<IResult> Delete(
        Guid projectId, Guid sourceId, ClaimsPrincipal user, WorkspaceAuthorizationService auth, AppDbContext db, TimeProvider time, CancellationToken ct)
    {
        await auth.RequireProjectRoleAsync(projectId, user.UserId(), WorkspaceRole.Editor, ct);
        var source = await db.Sources.AsNoTracking().FirstOrDefaultAsync(s => s.Id == sourceId && s.ProjectId == projectId, ct);
        if (source is null)
            return NotFound("Source");

        await using (var transaction = await db.Database.BeginTransactionAsync(ct))
        {
            var (host, locator) = (source.Host, source.Locator);
            await db.FactLinks.Where(l => l.ProjectId == projectId && l.Host == host && l.Repository == locator).ExecuteDeleteAsync(ct);
            await db.Rejections.Where(r => r.ProjectId == projectId && r.Host == host && r.Repository == locator).ExecuteDeleteAsync(ct);
            await db.TrackedRefs.Where(t => t.ProjectId == projectId && t.Host == host && t.Repository == locator).ExecuteDeleteAsync(ct);
            // The files, the Snapshot, its facts and the proposals of a scan go with its row.
            await db.Scans.Where(s => s.SourceId == sourceId).ExecuteDeleteAsync(ct);
            await db.Sources.Where(s => s.Id == sourceId).ExecuteDeleteAsync(ct);
            await transaction.CommitAsync(ct);
        }
        await ImportRetention.DropUnusedBlobsAsync(db, source.WorkspaceId, time.GetUtcNow(), ct);
        return Results.NoContent();
    }

    public static async Task<IResult> Snapshots(
        Guid projectId, Guid sourceId, ClaimsPrincipal user, WorkspaceAuthorizationService auth, AppDbContext db, CancellationToken ct)
    {
        await auth.RequireProjectRoleAsync(projectId, user.UserId(), WorkspaceRole.Viewer, ct);
        var source = await db.Sources.AsNoTracking().FirstOrDefaultAsync(s => s.Id == sourceId && s.ProjectId == projectId, ct);
        if (source is null)
            return NotFound("Source");
        var rows = await SnapshotRowsAsync(db, source.Id, null, MaxSnapshots, ct);
        return Results.Ok(rows.Select(r => ToDto(source, r)).ToList());
    }

    /// <summary>
    /// Compares two Snapshots of one Source. Staleness is this comparison: a fact of the base
    /// that the head does not hold, where the head looked. Two Snapshots of two Sources are
    /// never compared.
    /// </summary>
    public static async Task<IResult> Compare(
        Guid projectId, Guid sourceId, Guid @base, Guid head, ClaimsPrincipal user,
        WorkspaceAuthorizationService auth, AppDbContext db, CancellationToken ct)
    {
        await auth.RequireProjectRoleAsync(projectId, user.UserId(), WorkspaceRole.Viewer, ct);
        var source = await db.Sources.AsNoTracking().FirstOrDefaultAsync(s => s.Id == sourceId && s.ProjectId == projectId, ct);
        if (source is null)
            return NotFound("Source");
        var rows = await SnapshotRowsAsync(db, source.Id, [@base, head], 2, ct);
        var baseRow = rows.FirstOrDefault(r => r.Snapshot.ScanId == @base);
        var headRow = rows.FirstOrDefault(r => r.Snapshot.ScanId == head);
        if (baseRow is null || headRow is null)
        {
            return Results.NotFound(new ErrorResponse("not_found",
                "Snapshot not found. Name two completed scans of this Source: Snapshots of two Sources are not compared."));
        }

        var files = await ManifestAsync(db, [@base, head], ct);
        var before = files.Where(f => f.ScanId == @base).ToDictionary(f => f.Path, f => f.BlobSha, StringComparer.Ordinal);
        var after = files.Where(f => f.ScanId == head).ToDictionary(f => f.Path, f => f.BlobSha, StringComparer.Ordinal);
        var fileDiff = new ManifestDiffDto(
            Capped(after.Keys.Where(p => !before.ContainsKey(p)).Order(StringComparer.Ordinal), out var t1),
            Capped(before.Keys.Where(p => !after.ContainsKey(p)).Order(StringComparer.Ordinal), out var t2),
            Capped(after.Where(p => before.TryGetValue(p.Key, out var sha) && sha != p.Value).Select(p => p.Key).Order(StringComparer.Ordinal), out var t3),
            t1 || t2 || t3);

        var facts = await db.SnapshotFacts.AsNoTracking().Where(f => f.ScanId == @base || f.ScanId == head)
            .Select(f => new { f.ScanId, f.Kind, f.FactKey, f.Name, f.Attributes }).ToListAsync(ct);
        var oldFacts = facts.Where(f => f.ScanId == @base).GroupBy(f => f.FactKey).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var newFacts = facts.Where(f => f.ScanId == head).GroupBy(f => f.FactKey).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        FactDiffItemDto Item(string kind, string name, string key) => new(kind, name, key);
        var factDiff = new FactDiffDto(
            Capped(newFacts.Values.Where(f => !oldFacts.ContainsKey(f.FactKey)).OrderBy(f => f.FactKey, StringComparer.Ordinal)
                .Select(f => Item(EnumText.Of(f.Kind), f.Name, f.FactKey)), out var f1),
            Capped(oldFacts.Values.Where(f => !newFacts.ContainsKey(f.FactKey)).OrderBy(f => f.FactKey, StringComparer.Ordinal)
                .Select(f => Item(EnumText.Of(f.Kind), f.Name, f.FactKey)), out var f2),
            Capped(newFacts.Values.Where(f => oldFacts.TryGetValue(f.FactKey, out var old) && (old.Name != f.Name || old.Attributes != f.Attributes))
                .OrderBy(f => f.FactKey, StringComparer.Ordinal).Select(f => Item(EnumText.Of(f.Kind), f.Name, f.FactKey)), out var f3),
            f1 || f2 || f3);

        var baseDto = ToDto(source, baseRow);
        var headDto = ToDto(source, headRow);
        return Results.Ok(new SnapshotComparisonDto(source.Id, baseDto, headDto, baseDto.SnapshotId == headDto.SnapshotId, fileDiff, factDiff));
    }

    /// <summary>The elements of the project's model that hold a binding to this Source.</summary>
    public static async Task<IResult> Bindings(
        Guid projectId, Guid sourceId, ClaimsPrincipal user, HttpContext http,
        WorkspaceAuthorizationService auth, AppDbContext db, CancellationToken ct)
    {
        await auth.RequireProjectRoleAsync(projectId, user.UserId(), WorkspaceRole.Viewer, ct);
        var source = await db.Sources.AsNoTracking().FirstOrDefaultAsync(s => s.Id == sourceId && s.ProjectId == projectId, ct);
        if (source is null)
            return NotFound("Source");
        var streamId = await db.Projects.AsNoTracking().Where(p => p.Id == projectId).Select(p => p.StreamId).FirstAsync(ct);
        var (model, _) = await http.RequestServices.GetRequiredService<IEventStore>().ForProject(projectId).Load<ModelState>(StreamId.From(streamId), ct);
        var bound = model.Elements.Values
            .SelectMany(e => e.Bindings.Where(b => b.SourceId == source.Id).Select(b => new SourceBindingDto(e.Id, e.Name, e.Kind.ToString().ToLowerInvariant(), b.ExternalKey)))
            .OrderBy(b => b.ElementName, StringComparer.Ordinal).ThenBy(b => b.ExternalKey, StringComparer.Ordinal)
            .ToList();
        return Results.Ok(bound);
    }

    /// <summary>
    /// Accepts every pending addition, change and removal of the Source's review scan in one
    /// decision. Conflicts, "same as X?" questions and relinks stay for a person to decide one
    /// by one, and so does every proposal that depends on one of them.
    /// </summary>
    public static async Task<IResult> AcceptAll(
        Guid projectId, Guid sourceId, AcceptSourceRequest request, ClaimsPrincipal user, HttpContext http,
        WorkspaceAuthorizationService auth, AppDbContext db, TimeProvider time, CancellationToken ct)
    {
        var userId = user.UserId();
        await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Editor, ct);
        var source = await db.Sources.AsNoTracking().FirstOrDefaultAsync(s => s.Id == sourceId && s.ProjectId == projectId, ct);
        if (source is null)
            return NotFound("Source");
        if (await ReviewScanAsync(db, source, ct) is not { } scan)
            return Results.Conflict(new ErrorResponse("nothing_to_accept", "This Source has no completed scan whose proposals can be decided."));

        var pending = await PendingAsync(db, scan.Id, ct);
        var accept = (await AcceptableAsync(db, pending, ct)).ToArray();
        if (accept.Length == 0)
        {
            return Results.Conflict(new ErrorResponse("nothing_to_accept",
                "No pending proposal of this Source can be accepted in bulk. Decide its conflicts, questions and relinks one by one."));
        }
        return await DecideProposals.DecideAsync(http, db, scan,
            new DecideProposals.DecisionInput(accept, [], [], [], request.AcknowledgeImpact), userId, time, ct);
    }

    /// <summary>
    /// The github Source of a repository in a project, created with the repository's name when
    /// the project has none yet. Two requests at once create one Source.
    /// </summary>
    internal static async Task<SourceEntity> EnsureGitHubSourceAsync(
        AppDbContext db, Guid projectId, Guid workspaceId, string host, string repository, string createdBy, DateTimeOffset now, CancellationToken ct)
    {
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO import.sources (id, project_id, workspace_id, kind, name, host, locator, connection, created_by, created_at)
            VALUES ({Guid.NewGuid()}, {projectId}, {workspaceId}, 'github', {repository}, {host}, {repository}, {"{}"}::jsonb, {createdBy}, {now})
            ON CONFLICT (project_id, host, locator) DO NOTHING
            """, ct);
        return await db.Sources.AsNoTracking().FirstAsync(s => s.ProjectId == projectId && s.Host == host && s.Locator == repository, ct);
    }

    private sealed record PendingRow(Guid Id, ProposalAction Action, Guid[] DependsOn, string OtherSources, string Changes);

    private static Task<List<PendingRow>> PendingAsync(AppDbContext db, Guid scanId, CancellationToken ct) =>
        db.ImportProposals.AsNoTracking()
            .Where(p => p.ScanId == scanId && p.Status == ProposalStatus.Pending)
            .OrderBy(p => p.Seq)
            .Select(p => new PendingRow(p.Id, p.Action, p.DependsOn, p.OtherSources, p.Changes))
            .ToListAsync(ct);

    /// <summary>
    /// What a bulk accept takes: pending additions, changes and removals that no other Source
    /// holds and that do not change a truth label, at most <see cref="DecideProposals.MaxProposals"/>
    /// in the order of the scan. Then every one whose dependency is neither accepted nor in the
    /// set drops out, until no more do. A rejected or superseded dependency is not met.
    /// </summary>
    private static async Task<List<Guid>> AcceptableAsync(AppDbContext db, List<PendingRow> pending, CancellationToken ct)
    {
        var set = pending
            .Where(p => p.Action is ProposalAction.Add or ProposalAction.Change or ProposalAction.Remove
                && p.OtherSources == "[]"
                && !ProposalJson.ReadChanges(p.Changes).Any(c => c.Field == "factStatus"))
            .Take(DecideProposals.MaxProposals)
            .ToDictionary(p => p.Id);
        var outside = set.Values.SelectMany(p => p.DependsOn).Where(d => !set.ContainsKey(d)).Distinct().ToList();
        var accepted = (await db.ImportProposals.AsNoTracking()
            .Where(p => outside.Contains(p.Id) && p.Status == ProposalStatus.Accepted)
            .Select(p => p.Id).ToListAsync(ct)).ToHashSet();
        bool changed;
        do
        {
            changed = false;
            foreach (var row in set.Values.ToList())
            {
                if (row.DependsOn.Any(d => !set.ContainsKey(d) && !accepted.Contains(d)))
                {
                    set.Remove(row.Id);
                    changed = true;
                }
            }
        }
        while (changed);
        return [.. pending.Where(p => set.ContainsKey(p.Id)).Select(p => p.Id)];
    }

    /// <summary>The newest completed scan of the Source whose proposals can be decided, or null.</summary>
    internal static async Task<ScanEntity?> ReviewScanAsync(AppDbContext db, SourceEntity source, CancellationToken ct)
    {
        var tracked = await db.TrackedRefs.AsNoTracking()
            .FirstOrDefaultAsync(t => t.ProjectId == source.ProjectId && t.Host == source.Host && t.Repository == source.Locator, ct);
        var completed = await db.Scans.AsNoTracking()
            .Where(s => s.SourceId == source.Id && s.Status == ScanStatus.Completed && s.CommitSha != null)
            .OrderByDescending(s => s.CompletedAt).ThenByDescending(s => s.CreatedAt).Take(50).ToListAsync(ct);
        return completed.FirstOrDefault(s => ScanKindInfo.Of(s, tracked).Tracking);
    }

    internal static async Task<SourceDto> ToDtoAsync(AppDbContext db, SourceEntity source, CancellationToken ct)
    {
        var latest = await db.Scans.AsNoTracking().Where(s => s.SourceId == source.Id)
            .OrderByDescending(s => s.CreatedAt).ThenByDescending(s => s.Id).FirstOrDefaultAsync(ct);
        ScanDto? latestDto = null;
        if (latest is not null)
        {
            var counts = await ScanRequests.CountsAsync(db, [latest.Id], ct);
            var tracked = await db.TrackedRefs.AsNoTracking()
                .FirstOrDefaultAsync(t => t.ProjectId == source.ProjectId && t.Host == source.Host && t.Repository == source.Locator, ct);
            latestDto = ScanRequests.ToDto(latest, counts.GetValueOrDefault(latest.Id), tracked);
        }

        var snapshot = (await SnapshotRowsAsync(db, source.Id, null, 1, ct)).FirstOrDefault();

        var review = new SourceReviewDto(null, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        if (await ReviewScanAsync(db, source, ct) is { } scan)
        {
            var pending = await PendingAsync(db, scan.Id, ct);
            int Count(ProposalAction action) => pending.Count(p => p.Action == action);
            review = new SourceReviewDto(
                scan.Id, pending.Count, Count(ProposalAction.Add), Count(ProposalAction.Change), Count(ProposalAction.Remove),
                Count(ProposalAction.Conflict), pending.Count(p => p.Action == ProposalAction.Conflict && p.OtherSources != "[]"),
                Count(ProposalAction.Bind), Count(ProposalAction.Relink), (await AcceptableAsync(db, pending, ct)).Count);
        }

        return new SourceDto(
            source.Id, EnumText.Of(source.Kind), source.Name, source.Host, source.Locator, source.CitationName,
            source.CreatedBy, source.CreatedAt, latestDto,
            snapshot is null ? null : ToDto(source, snapshot), review,
            await Runtime.RuntimeSources.ConnectionAsync(db, source, ct));
    }

    private sealed record SnapshotRow(SnapshotEntity Snapshot, string? Ref, bool ContentNotHeld, string? ReportedCommitSha, string RequestedBy);

    /// <summary>The Snapshots of a Source, newest first, each with the ref its scan read.</summary>
    private static async Task<List<SnapshotRow>> SnapshotRowsAsync(AppDbContext db, Guid sourceId, Guid[]? scanIds, int take, CancellationToken ct)
    {
        var query = db.ImportSnapshots.AsNoTracking().Where(s => s.SourceId == sourceId);
        if (scanIds is not null)
            query = query.Where(s => scanIds.Contains(s.ScanId));
        var snapshots = await query.OrderByDescending(s => s.CreatedAt).ThenByDescending(s => s.ScanId).Take(take).ToListAsync(ct);
        var ids = snapshots.Select(s => s.ScanId).ToList();
        var scans = await db.Scans.AsNoTracking().Where(s => ids.Contains(s.Id))
            .Select(s => new { s.Id, s.ResolvedRef, s.ContentNotHeld, s.ReportedCommitSha, s.RequestedBy }).ToDictionaryAsync(s => s.Id, ct);
        // A snapshot row goes with its scan row (cascade), so every snapshot has one.
        return [.. snapshots.Select(s => scans[s.ScanId]).Zip(snapshots, (scan, s) =>
            new SnapshotRow(s, scan.ResolvedRef, scan.ContentNotHeld, scan.ReportedCommitSha, scan.RequestedBy))];
    }

    private static SourceSnapshotDto ToDto(SourceEntity source, SnapshotRow row)
    {
        var snapshot = row.Snapshot;
        return new(
            snapshot.ScanId, snapshot.SnapshotId, SourceEntity.IsGit(source.Kind) ? snapshot.CommitSha : null,
            SourceEntity.IsGit(source.Kind) ? snapshot.CommitSha : snapshot.SnapshotId,
            row.Ref, snapshot.ManifestFiles, snapshot.FactCount, snapshot.IsComplete, snapshot.CreatedAt,
            !row.ContentNotHeld, row.ReportedCommitSha, row.ContentNotHeld ? row.RequestedBy : null);
    }

    private sealed record ManifestRow(Guid ScanId, string Path, string BlobSha);

    private static Task<List<ManifestRow>> ManifestAsync(AppDbContext db, Guid[] scanIds, CancellationToken ct) =>
        db.ScanFiles.AsNoTracking()
            .Where(f => scanIds.Contains(f.ScanId) && f.State == ScanFileState.Read)
            .Select(f => new ManifestRow(f.ScanId, f.Path, f.BlobSha))
            .ToListAsync(ct);

    private static List<T> Capped<T>(IEnumerable<T> items, out bool truncated)
    {
        var list = items.Take(MaxDiffItems + 1).ToList();
        truncated = list.Count > MaxDiffItems;
        if (truncated)
            list.RemoveAt(MaxDiffItems);
        return list;
    }

    private static IResult GitHubNotConfigured() => Results.Conflict(new ErrorResponse("github_not_configured",
        "The workspace has no usable GitHub API base URL. Set github.apiBaseUrl in the workspace settings to an address the instance policy allows."));

    internal static IResult HostNotConfigured(GitHostInfo host) => Results.Conflict(new ErrorResponse($"{host.Name.Replace('-', '_')}_not_configured",
        $"The workspace has no usable {host.Display} API base URL. Set {host.SettingsField} in the workspace settings to an address the instance policy allows."));
}
