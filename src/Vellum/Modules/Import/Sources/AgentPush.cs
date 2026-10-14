using System.Globalization;
using System.Security.Claims;
using System.Text.RegularExpressions;
using Deedbox;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Vellum.Modules.Identity.AgentTokens;
using Vellum.Modules.Import.Extraction;
using Vellum.Modules.Import.Proposals;
using Vellum.Modules.Import.Scans;
using Vellum.Modules.Settings;
using Vellum.Modules.Workspaces;
using Vellum.Shared;
using static Vellum.Shared.ResultExtensions;

namespace Vellum.Modules.Import.Sources;

/// <summary>
/// The <c>agent-push</c> kind: <c>vellum scan</c> runs the extractors of this build on a
/// machine that holds the code and pushes the manifest, the facts and their citations under an
/// agent token with the <c>push</c> scope. No file content reaches the instance, and no blob
/// is stored. The push becomes a Snapshot of its Source and is planned like every other scan:
/// its proposals wait for a person, and it can set or clear stale marks, never a value.
/// <para>
/// A push is untrusted input, like repository text. Every list and every text is bounded, every
/// citation names a file of the manifest, every file of the manifest is one that the scope
/// rules read, and the rules must be the rules of this build: the facts of other rules would
/// not match the keys of the accepted facts.
/// </para>
/// </summary>
public static partial class AgentPush
{
    /// <summary>The host part of the citation of every agent-push Source: <c>agent-push/&lt;slug&gt;@&lt;snapshot&gt;:path:lines</c>.</summary>
    public const string Host = "agent-push";
    /// <summary>The largest push body: below the server's own limit of 30 MB for any request.</summary>
    public const long MaxBodyBytes = 24L * 1024 * 1024;
    public const int MaxSkipped = 100_000;
    public const int MaxCitationsPerFact = 200;
    public const int MaxCitations = 200_000;
    public const int MaxAttributesPerFact = 500;
    public const int MaxFactKey = 1024;
    public const int MaxFactName = 1024;
    public const int MaxRuleId = 200;
    public const int MaxSlug = 100;
    /// <summary>How long a push may hold its Source before a worker fails it as interrupted.</summary>
    public static readonly TimeSpan Lease = TimeSpan.FromMinutes(10);

    [GeneratedRegex("^[0-9a-f]{40}$")]
    private static partial Regex BlobSha();

    [GeneratedRegex("^(?:[0-9a-f]{40}|[0-9a-f]{64})$")]
    private static partial Regex CommitSha();

    [GeneratedRegex("^[a-z0-9:-]{1,64}$")]
    private static partial Regex ReasonCode();

    public static AgentPushSettings SettingsOf(ImportSettings import) => new(
        [.. RuleSet.Current.Select(r => new AgentPushRule(r.Rule, r.Version))],
        import.MaxFiles, import.MaxFileBytes, import.MaxTotalBytes, [.. import.ExcludeGlobs], ExtractionLimits.Default.MaxFacts);

    /// <summary>The rules and the limits a push of this project is checked against, so that <c>vellum scan</c> reads no file before it knows.</summary>
    public static async Task<IResult> Settings(
        Guid projectId, ClaimsPrincipal user, WorkspaceAuthorizationService auth, SettingsResolver settings, CancellationToken ct)
    {
        await auth.RequireProjectRoleAsync(projectId, user.UserId(), WorkspaceRole.Editor, ct);
        var workspace = await settings.GetWorkspaceAsync(await auth.GetProjectWorkspaceIdAsync(projectId, ct), ct);
        return Results.Ok(SettingsOf(workspace.Effective.Import));
    }

    public static async Task<IResult> Push(
        Guid projectId, AgentPushRequest request, ClaimsPrincipal user, HttpContext http,
        WorkspaceAuthorizationService auth, SettingsResolver settings, AppDbContext db, TimeProvider time, CancellationToken ct)
    {
        var userId = user.UserId();
        await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Editor, ct);
        var workspaceId = await auth.GetProjectWorkspaceIdAsync(projectId, ct);
        var workspace = await settings.GetWorkspaceAsync(workspaceId, ct);

        if (request is null)
            return Invalid("body", "Send a push as JSON.");
        if (Unknown(request.Rules) is { } unknownRules)
        {
            return Results.Conflict(new ErrorResponse("extractor_version_mismatch",
                "The push was extracted with other rules than this server's",
                $"{unknownRules} Use the vellum CLI of the same version as this server: GET /api/projects/{{projectId}}/sources/push lists its rules."));
        }
        if (Check(request, workspace.Effective.Import) is { } refusal)
            return refusal;

        var name = request.Name.Trim();
        var locator = TextUtil.Bounded(TextUtil.Slug(name), MaxSlug).Trim('-');
        var actor = AgentPrincipal.Actor(user) ?? userId;
        var now = time.GetUtcNow();
        var manifest = request.Files.Select(f => new ManifestEntry(f.Path, f.Size, f.BlobSha)).ToList();
        var snapshotId = SnapshotManifest.Id(manifest);

        var created = await db.Database.ExecuteSqlAsync($"""
            INSERT INTO import.sources (id, project_id, workspace_id, kind, name, host, locator, connection, created_by, created_at)
            VALUES ({Guid.NewGuid()}, {projectId}, {workspaceId}, 'agent_push', {name}, {Host}, {locator}, {"{}"}::jsonb, {actor}, {now})
            ON CONFLICT (project_id, host, locator) DO NOTHING
            """, ct) == 1;
        var source = await db.Sources.AsNoTracking().FirstAsync(s => s.ProjectId == projectId && s.Host == Host && s.Locator == locator, ct);

        var scope = request.Scope;
        var limits = new ScanLimits(scope.MaxFiles, scope.MaxFileBytes, scope.MaxTotalBytes, [.. scope.ExcludeGlobs]);
        var scan = new ScanEntity
        {
            Id = Guid.NewGuid(), ProjectId = projectId, WorkspaceId = workspaceId, SourceId = source.Id,
            Host = source.Host, Repository = source.Locator,
            // The revision of a Snapshot that is no commit is its ID: citations read agent-push/<slug>@<snapshot>:path:lines.
            CommitSha = snapshotId, ReportedCommitSha = request.CommitSha, ContentNotHeld = true,
            IncludePaths = [.. scope.IncludePaths], ExcludePaths = [.. scope.ExcludePaths], Limits = limits.ToJson(),
            Status = ScanStatus.Running, TreeListed = true,
            FilesListed = Math.Max(request.FilesListed, request.Files.Count + request.Skipped.Count),
            FilesRead = request.Files.Count, FilesSkipped = request.Skipped.Count, BytesRead = request.Files.Sum(f => f.Size),
            Partial = !request.IsComplete,
            Notes = ScanNotes.Write(request.Skipped.Select(s => ScanNotes.Of(s.Reason, s.Path))),
            RequestedBy = actor, CreatedAt = now, UpdatedAt = now, StartedAt = now, ClaimedAt = now, Attempts = 1,
            // A lease like a worker's: a push whose request dies is failed by a worker when it runs out.
            LeaseOwner = $"push:{AgentPrincipal.TokenId(user)}", LeaseExpiresAt = now + Lease, LeaseToken = 1,
        };

        await using (var transaction = await db.Database.BeginTransactionAsync(ct))
        {
            if (await ScanRequests.ChargeAsync(db, workspaceId, scan.Id, workspace.Effective.Usage.MaxScansPerDay, now, ct) is { } limited)
                return limited;
            db.Scans.Add(scan);
            db.ScanFiles.AddRange(request.Files.Select(f => new ScanFileEntity
            {
                ScanId = scan.Id, Path = f.Path, PathHash = KeyHash.Of(f.Path), BlobSha = f.BlobSha, Size = f.Size, State = ScanFileState.Read,
            }));
            db.ScanFiles.AddRange(request.Skipped.Select(s => new ScanFileEntity
            {
                // A skipped file of a push has no blob SHA: the machine did not read it, or read it and did not use it.
                ScanId = scan.Id, Path = s.Path, PathHash = KeyHash.Of(s.Path), BlobSha = "", Size = 0, State = ScanFileState.Skipped, Reason = s.Reason,
            }));
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            {
                return Results.Conflict(new ErrorResponse("scan_in_progress",
                    $"Another push of the Source '{source.Name}' is running. Wait for it, then push again."));
            }
            await transaction.CommitAsync(ct);
        }
        db.ChangeTracker.Clear();

        var facts = request.Facts.Select(f => new ExtractedFact(
            Enum.Parse<FactKind>(f.Kind), f.Key, f.Name, f.Attributes,
            [.. f.Evidence.Select(e => new SourceEvidence(e.Path, e.StartLine, e.EndLine, e.Rule, e.RuleVersion))])).ToList();
        var coverageFiles = request.Files.Select(f => (f.Path, Reason: (string?)null))
            .Concat(request.Skipped.Select(s => (s.Path, Reason: (string?)s.Reason)))
            .ToDictionary(f => f.Path, f => f.Reason, StringComparer.Ordinal);
        var absent = ScanScope.AbsentGap(limits, scan.IncludePaths, scan.ExcludePaths);
        var gap = request.ExtractionGap;

        // The stale marks that follow are conclusions of the push; the scan row names the token.
        http.RequestServices.GetRequiredService<DeedboxContext>().Metadata = new EventMetadata
        {
            Actor = "system:import-scan",
            CorrelationId = scan.Id.ToString(),
        };
        SnapshotOutcome outcome;
        try
        {
            outcome = await SnapshotWriter.WriteAsync(http.RequestServices, scan, WriteMode.Complete(scan.LeaseToken),
                new SnapshotInput(facts, request.IsComplete, gap, scan.Notes,
                    () => new ScanCoverage(coverageFiles, absent, gap), request.Files.Count,
                    new ManifestSummary(snapshotId, manifest.Count)),
                now, ct);
        }
        catch
        {
            await FailAsync(db, scan.Id, ScanFailures.Internal, time);
            throw;
        }

        switch (outcome)
        {
            case SnapshotOutcome.ProjectGone:
                return NotFound("Project");
            case SnapshotOutcome.ModelBusy:
                await FailAsync(db, scan.Id, ScanFailures.ModelBusy, time);
                return Results.Conflict(new ErrorResponse("model_busy",
                    "The project's model changed each time the push was compared with it. Nothing was written. Push again."));
            case SnapshotOutcome.LeaseLost:
                return Results.Conflict(new ErrorResponse("scan_changed", "The push was failed while it was planned. Push again."));
        }

        var pending = await db.ImportProposals.CountAsync(p => p.ScanId == scan.Id && p.Status == ProposalStatus.Pending, ct);
        var completed = await db.Scans.AsNoTracking().FirstAsync(s => s.Id == scan.Id, ct);
        return Results.Created($"/api/projects/{projectId}/imports/{scan.Id}", new AgentPushResponse(
            source.Id, source.Name, created, scan.Id, snapshotId, manifest.Count, facts.Count, pending, completed.IsComplete));
    }

    /// <summary>A sentence for rules that differ from this build's, or null when the push names exactly this build's rules.</summary>
    private static string? Unknown(IReadOnlyList<AgentPushRule>? rules)
    {
        if (rules is null || rules.Count > 10_000)
            return "The push names no usable list of rules.";
        var mine = RuleSet.Current.Select(r => (r.Rule, r.Version)).ToHashSet();
        var theirs = rules.Where(r => r is not null).Select(r => (r.Id ?? "", r.Version)).ToHashSet();
        var unknown = theirs.Except(mine).Count();
        var missing = mine.Except(theirs).Count();
        return unknown == 0 && missing == 0 && theirs.Count == rules.Count ? null : string.Create(CultureInfo.InvariantCulture,
            $"{unknown} rule versions of the push are not this server's, and {missing} of this server's are not in the push.");
    }

    /// <summary>The 400 for the first bound or rule the push breaks, or null.</summary>
    private static IResult? Check(AgentPushRequest request, ImportSettings import)
    {
        if (request.Name is not { } rawName || string.IsNullOrWhiteSpace(rawName))
            return Invalid("name", "Name the Source.");
        if (TextRule.Line("name", rawName.Trim(), SourceEntity.MaxName) is { } nameError)
            return Invalid(nameError);
        if (TextUtil.Slug(rawName).Length == 0)
            return Invalid("name", "A Source name holds at least one letter or digit.");
        if (request.CommitSha is not null && !CommitSha().IsMatch(request.CommitSha))
            return Invalid("commitSha", "A commit SHA is 40 or 64 lower-case hexadecimal characters.");
        if (request.ExtractionGap is not (null or CoverageGap.Limit or CoverageGap.Failed))
            return Invalid("extractionGap", "The extraction gap is null, limit or failed.");
        if (request.FilesListed < 0)
            return Invalid("filesListed", "The count of files is 0 or more.");

        // The scope: within the workspace's import settings, with every glob of the workspace.
        if (request.Scope is not { } scope || scope.ExcludeGlobs is null || scope.IncludePaths is null || scope.ExcludePaths is null)
            return Invalid("scope", "Send the scope the files were selected with.");
        if (scope.MaxFiles < 1 || scope.MaxFiles > import.MaxFiles)
            return Invalid("scope.maxFiles", $"The push may read at most import.maxFiles = {import.MaxFiles} files.");
        if (scope.MaxFileBytes < 1 || scope.MaxFileBytes > import.MaxFileBytes)
            return Invalid("scope.maxFileBytes", $"The push may read files of at most import.maxFileBytes = {import.MaxFileBytes} bytes.");
        if (scope.MaxTotalBytes < 1 || scope.MaxTotalBytes > import.MaxTotalBytes)
            return Invalid("scope.maxTotalBytes", $"The push may read at most import.maxTotalBytes = {import.MaxTotalBytes} bytes.");
        if (scope.ExcludeGlobs.Count > SettingsValidator.MaxExcludeGlobs + import.ExcludeGlobs.Count
            || scope.ExcludeGlobs.Any(g => g is not { Length: >= 1 and <= 200 } || g.Any(char.IsControl)))
            return Invalid("scope.excludeGlobs", "A glob is 1 to 200 characters with no control character.");
        if (import.ExcludeGlobs.Except(scope.ExcludeGlobs, StringComparer.Ordinal).Any())
            return Invalid("scope.excludeGlobs", "The scope leaves out every glob of the workspace's import.excludeGlobs.");
        if ((ScopePaths("scope.includePaths", scope.IncludePaths) ?? ScopePaths("scope.excludePaths", scope.ExcludePaths)) is { } pathError)
            return pathError;

        // The manifest: files that this scope reads, each once, within the limits.
        if (request.Files is not { } files || request.Skipped is not { } skipped || request.Facts is not { } facts)
            return Invalid("files", "Send the manifest, the skipped files and the facts, each as a list.");
        if (files.Count > scope.MaxFiles)
            return Invalid("files", $"The manifest holds more than scope.maxFiles = {scope.MaxFiles} files.");
        var limits = new ScanLimits(scope.MaxFiles, scope.MaxFileBytes, scope.MaxTotalBytes, [.. scope.ExcludeGlobs]);
        var gapOf = ScanScope.AbsentGap(limits, [.. scope.IncludePaths], [.. scope.ExcludePaths]);
        var paths = new HashSet<string>(StringComparer.Ordinal);
        long total = 0;
        foreach (var file in files)
        {
            if (file?.Path is not { } path || file.BlobSha is not { } sha)
                return Invalid("files", "Each file has a path, a size and a blob SHA.");
            if (gapOf(path) is { } gap)
                return Invalid("files", $"The scope rules do not read '{Shown(path)}' ({gap}), so it is not in a manifest.");
            if (!paths.Add(path))
                return Invalid("files", $"The manifest names '{Shown(path)}' twice.");
            if (!BlobSha().IsMatch(sha))
                return Invalid("files", $"The blob SHA of '{Shown(path)}' is not 40 lower-case hexadecimal characters.");
            if (file.Size < 0 || file.Size > scope.MaxFileBytes)
                return Invalid("files", $"The size of '{Shown(path)}' is outside scope.maxFileBytes.");
            total += file.Size;
        }
        if (total > scope.MaxTotalBytes)
            return Invalid("files", "The files of the manifest are larger than scope.maxTotalBytes together.");
        var manifest = paths.ToHashSet(StringComparer.Ordinal);

        if (skipped.Count > MaxSkipped)
            return Invalid("skipped", $"At most {MaxSkipped} skipped files.");
        foreach (var skip in skipped)
        {
            if (skip?.Path is not { } path || skip.Reason is not { } reason || !ReasonCode().IsMatch(reason))
                return Invalid("skipped", "Each skipped file has a path and a reason code such as secret-file or limit:file-bytes.");
            // A file that the path filter, the scope or a glob leaves out is not listed: the server knows those rules.
            if (gapOf(path) is { } gap)
                return Invalid("skipped", $"The scope rules do not read '{Shown(path)}' ({gap}), so it is not listed as skipped.");
            if (!paths.Add(path))
                return Invalid("skipped", $"'{Shown(path)}' is named twice.");
        }

        // The facts: bounded, of known kinds, citing files of the manifest with the rules of this build.
        if (facts.Count > ExtractionLimits.Default.MaxFacts)
            return Invalid("facts", $"At most {ExtractionLimits.Default.MaxFacts} facts.");
        var rules = RuleSet.Current.Select(r => (r.Rule, r.Version)).ToHashSet();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var citations = 0;
        foreach (var fact in facts)
        {
            if (fact?.Key is not { } key || fact.Name is not { } name || fact.Kind is not { } kind || fact.Attributes is not { } attributes || fact.Evidence is not { } evidence)
                return Invalid("facts", "Each fact has a kind, a key, a name, attributes and evidence.");
            if (!Enum.TryParse<FactKind>(kind, ignoreCase: false, out var parsed) || !Enum.IsDefined(parsed) || kind != parsed.ToString())
                return Invalid("facts", $"'{Shown(kind)}' is no fact kind.");
            if (key.Length is 0 or > MaxFactKey || name.Length > MaxFactName)
                return Invalid("facts", $"A fact key is 1 to {MaxFactKey} characters and a name at most {MaxFactName}.");
            if (!keys.Add(key))
                return Invalid("facts", "Two facts have one key.");
            if (attributes.Count > MaxAttributesPerFact || attributes.Any(a => a.Key is not { Length: >= 1 and <= TextRule.MaxName } || a.Value is null || a.Value.Length > FactMapping.MaxAttributeValue))
                return Invalid("facts", $"A fact has at most {MaxAttributesPerFact} attributes, each named in at most {TextRule.MaxName} characters, with a value of at most {FactMapping.MaxAttributeValue}.");
            if (evidence.Count > MaxCitationsPerFact || (citations += evidence.Count) > MaxCitations)
                return Invalid("facts", $"A fact has at most {MaxCitationsPerFact} citations, and a push at most {MaxCitations}.");
            foreach (var citation in evidence)
            {
                if (citation?.Path is not { } path || citation.Rule is not { } rule)
                    return Invalid("facts", "Each citation has a path, lines and a rule.");
                if (!manifest.Contains(path))
                    return Invalid("facts", $"A citation names '{Shown(path)}', which is not in the manifest.");
                if (citation.StartLine < 1 || citation.EndLine < citation.StartLine || citation.EndLine > 10_000_000)
                    return Invalid("facts", "A citation names lines from 1, and its last line is not before its first.");
                if (!rules.Contains((rule, citation.RuleVersion)))
                    return Invalid("facts", $"A citation names the rule '{Shown(rule)}' version {citation.RuleVersion}, which this server does not have.");
            }
        }
        return null;
    }

    private static IResult? ScopePaths(string field, IReadOnlyList<string> paths)
    {
        if (paths.Count > ScanRequests.MaxScopePaths)
            return Invalid(field, $"At most {ScanRequests.MaxScopePaths} paths.");
        foreach (var path in paths)
        {
            if (path is not { Length: >= 1 and <= ScanRequests.MaxScopePathLength } || TextRule.Line(field, path, ScanRequests.MaxScopePathLength) is not null
                || path.Contains('\\') || path.Split('/').Any(segment => segment is "" or "." or ".."))
                return Invalid(field, $"A path is a directory or a file, such as src/Api, of at most {ScanRequests.MaxScopePathLength} characters.");
        }
        return null;
    }

    /// <summary>A path of the push as an error message shows it: bounded, with no control character.</summary>
    private static string Shown(string value) => new([.. value.Where(c => !char.IsControl(c)).Take(200)]);

    private static async Task FailAsync(AppDbContext db, Guid scanId, ScanError error, TimeProvider time)
    {
        var now = time.GetUtcNow();
        await db.Scans
            .Where(s => s.Id == scanId && s.Status == ScanStatus.Running)
            .ExecuteUpdateAsync(set => set
                .SetProperty(s => s.Status, ScanStatus.Failed)
                .SetProperty(s => s.ErrorCode, error.Code)
                .SetProperty(s => s.ErrorMessage, error.Message)
                .SetProperty(s => s.CompletedAt, now)
                .SetProperty(s => s.UpdatedAt, now)
                .SetProperty(s => s.LeaseOwner, (string?)null)
                .SetProperty(s => s.LeaseExpiresAt, (DateTimeOffset?)null), CancellationToken.None);
    }
}
