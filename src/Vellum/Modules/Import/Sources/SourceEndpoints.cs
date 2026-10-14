using System.Security.Claims;
using Deedbox;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Vellum.Modules.Import.Proposals;
using Vellum.Modules.Import.Scans;
using Vellum.Modules.Modelling;
using Vellum.Modules.Settings;
using Vellum.Modules.Workspaces;
using Vellum.Shared;
using static Vellum.Shared.ResultExtensions;

namespace Vellum.Modules.Import.Sources;

/// <summary>One Snapshot of a Source: the manifest ID, and the commit SHA when the Source is git-based.</summary>
/// <param name="Revision">What a citation of this Snapshot names after <c>@</c>: the commit SHA of a git Source, else the Snapshot ID.</param>
/// <param name="Files">The files in the manifest: those the scan read whole.</param>
public sealed record SourceSnapshotDto(
    Guid ScanId, string SnapshotId, string? CommitSha, string Revision, string? Ref, int Files, int Facts, bool IsComplete, DateTimeOffset CreatedAt);

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

/// <param name="Kind">One of <c>github</c>, <c>gitlab</c>, <c>bitbucket</c>, <c>azure-devops</c>, <c>directory</c>, <c>archive</c>, <c>agent-push</c>, <c>model-file</c>, <c>traces</c>, <c>newrelic</c>. This instance reads <c>github</c>.</param>
/// <param name="Citation">The <c>&lt;source&gt;</c> part of a citation <c>&lt;source&gt;@&lt;revision&gt;:path:start-end</c>.</param>
/// <param name="LatestScan">The newest scan of the Source, in any state. Null before the first.</param>
/// <param name="LatestSnapshot">The Snapshot of the newest completed scan. Null before the first.</param>
public sealed record SourceDto(
    Guid Id, string Kind, string Name, string Host, string Locator, string Citation,
    string CreatedBy, DateTimeOffset CreatedAt, ScanDto? LatestScan, SourceSnapshotDto? LatestSnapshot, SourceReviewDto Review);

/// <param name="Kind">The kind of the Source. This instance creates <c>github</c> Sources only.</param>
/// <param name="Name">What people call the Source. Without it the Source is named after its repository.</param>
/// <param name="Repository">For <c>github</c>: <c>owner/name</c>, or the address of the repository on the workspace's GitHub host.</param>
public sealed record CreateSourceRequest(string? Kind, string? Name, string? Repository);

public sealed record UpdateSourceRequest(string? Name);

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

    public static async Task<IResult> Get(
        Guid projectId, Guid sourceId, ClaimsPrincipal user, WorkspaceAuthorizationService auth, AppDbContext db, CancellationToken ct)
    {
        await auth.RequireProjectRoleAsync(projectId, user.UserId(), WorkspaceRole.Viewer, ct);
        var source = await db.Sources.AsNoTracking().FirstOrDefaultAsync(s => s.Id == sourceId && s.ProjectId == projectId, ct);
        return source is null ? NotFound("Source") : Results.Ok(await ToDtoAsync(db, source, ct));
    }

    /// <summary>
    /// Creates a Source. Its kind is one of the closed set; this instance reads <c>github</c> and
    /// refuses the others with <c>source_kind_unavailable</c>. One place is one Source per project.
    /// </summary>
    public static async Task<IResult> Create(
        Guid projectId, CreateSourceRequest request, ClaimsPrincipal user,
        WorkspaceAuthorizationService auth, SettingsResolver settings, AppDbContext db, TimeProvider time, CancellationToken ct)
    {
        var userId = user.UserId();
        await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Editor, ct);
        if (!EnumText.TryParse(request.Kind, out SourceKind kind))
            return Invalid("kind", $"A Source kind is one of: {string.Join(", ", Enum.GetValues<SourceKind>().Select(k => EnumText.Of(k)))}.");
        if (kind != SourceKind.Github)
        {
            return Results.BadRequest(new ErrorResponse("source_kind_unavailable",
                $"This instance cannot read a Source of kind '{EnumText.Of(kind)}' yet. Create a github Source."));
        }
        var name = string.IsNullOrWhiteSpace(request.Name) ? null : request.Name.Trim();
        if (name is not null && TextRule.Line("name", name, SourceEntity.MaxName) is { } nameError)
            return Invalid(nameError);

        var workspaceId = await auth.GetProjectWorkspaceIdAsync(projectId, ct);
        var workspace = await settings.GetWorkspaceAsync(workspaceId, ct);
        if (workspace.GithubApiBaseUrl is not SettingsUrl.Usable github)
            return GitHubNotConfigured();
        var host = ScanRequests.WebHost(github.Url);
        if (!ScanRequests.TryReadRepository(request.Repository, host, out var repository, out var repositoryError))
            return Invalid("repository", repositoryError);

        if (await db.Sources.AsNoTracking().FirstOrDefaultAsync(s => s.ProjectId == projectId && s.Host == host && s.Locator == repository, ct) is { } existing)
        {
            return Results.Conflict(new ErrorResponse("source_exists",
                $"The project already has the Source '{existing.Name}' for {existing.CitationName} ({existing.Id})."));
        }

        var source = new SourceEntity
        {
            Id = Guid.NewGuid(), ProjectId = projectId, WorkspaceId = workspaceId, Kind = kind, Name = name ?? repository,
            Host = host, Locator = repository, CreatedBy = userId, CreatedAt = time.GetUtcNow(),
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

    /// <summary>Queues a scan of the Source. It is the same scan as <c>POST …/imports</c> starts for the Source's repository.</summary>
    public static async Task<IResult> StartScan(
        Guid projectId, Guid sourceId, StartSourceScanRequest request, ClaimsPrincipal user,
        WorkspaceAuthorizationService auth, SettingsResolver settings, AppDbContext db,
        ScanSignal signal, TimeProvider time, CancellationToken ct)
    {
        await auth.RequireProjectRoleAsync(projectId, user.UserId(), WorkspaceRole.Editor, ct);
        var source = await db.Sources.AsNoTracking().FirstOrDefaultAsync(s => s.Id == sourceId && s.ProjectId == projectId, ct);
        if (source is null)
            return NotFound("Source");

        var workspace = await settings.GetWorkspaceAsync(source.WorkspaceId, ct);
        if (workspace.GithubApiBaseUrl is not SettingsUrl.Usable github)
            return GitHubNotConfigured();
        if (ScanRequests.WebHost(github.Url) != source.Host)
        {
            return Results.Conflict(new ErrorResponse("source_host_changed",
                $"This Source is on {source.Host}, and the workspace now reads GitHub at {ScanRequests.WebHost(github.Url)}. Set github.apiBaseUrl back, or create a Source on the new host."));
        }
        return await ScanRequests.QueueAsync(projectId, source,
            new ScanRequests.ScanOptions(request.Ref, request.IncludePaths, request.ExcludePaths, request.PullRequest),
            user, workspace, db, signal, time.GetUtcNow(), ct);
    }

    public static async Task<IResult> Snapshots(
        Guid projectId, Guid sourceId, ClaimsPrincipal user, WorkspaceAuthorizationService auth, AppDbContext db, CancellationToken ct)
    {
        await auth.RequireProjectRoleAsync(projectId, user.UserId(), WorkspaceRole.Viewer, ct);
        var source = await db.Sources.AsNoTracking().FirstOrDefaultAsync(s => s.Id == sourceId && s.ProjectId == projectId, ct);
        if (source is null)
            return NotFound("Source");
        var rows = await SnapshotRowsAsync(db, source.Id, null, MaxSnapshots, ct);
        return Results.Ok(rows.Select(r => ToDto(source, r.Snapshot, r.Ref)).ToList());
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

        var baseDto = ToDto(source, baseRow.Snapshot, baseRow.Ref);
        var headDto = ToDto(source, headRow.Snapshot, headRow.Ref);
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

    private static async Task<SourceDto> ToDtoAsync(AppDbContext db, SourceEntity source, CancellationToken ct)
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
            snapshot is null ? null : ToDto(source, snapshot.Snapshot, snapshot.Ref), review);
    }

    private sealed record SnapshotRow(SnapshotEntity Snapshot, string? Ref);

    /// <summary>The Snapshots of a Source, newest first, each with the ref its scan read.</summary>
    private static async Task<List<SnapshotRow>> SnapshotRowsAsync(AppDbContext db, Guid sourceId, Guid[]? scanIds, int take, CancellationToken ct)
    {
        var query = db.ImportSnapshots.AsNoTracking().Where(s => s.SourceId == sourceId);
        if (scanIds is not null)
            query = query.Where(s => scanIds.Contains(s.ScanId));
        var snapshots = await query.OrderByDescending(s => s.CreatedAt).ThenByDescending(s => s.ScanId).Take(take).ToListAsync(ct);
        var ids = snapshots.Select(s => s.ScanId).ToList();
        var refs = await db.Scans.AsNoTracking().Where(s => ids.Contains(s.Id))
            .Select(s => new { s.Id, s.ResolvedRef }).ToDictionaryAsync(s => s.Id, s => s.ResolvedRef, ct);
        return [.. snapshots.Select(s => new SnapshotRow(s, refs.GetValueOrDefault(s.ScanId)))];
    }

    private static SourceSnapshotDto ToDto(SourceEntity source, SnapshotEntity snapshot, string? reference) => new(
        snapshot.ScanId, snapshot.SnapshotId, SourceEntity.IsGit(source.Kind) ? snapshot.CommitSha : null,
        SourceEntity.IsGit(source.Kind) ? snapshot.CommitSha : snapshot.SnapshotId,
        reference, snapshot.ManifestFiles, snapshot.FactCount, snapshot.IsComplete, snapshot.CreatedAt);

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
}
