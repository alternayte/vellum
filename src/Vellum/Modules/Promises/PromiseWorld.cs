using Deedbox;
using Microsoft.EntityFrameworkCore;
using Vellum.Modules.Import;
using Vellum.Modules.Import.Scans;
using Vellum.Modules.Modelling;
using Vellum.Modules.Schemas;
using Vellum.Modules.Settings;
using Vellum.Modules.Trace.Engine;
using Vellum.Modules.Trace;
using Vellum.Modules.Workspaces;
using Vellum.Shared;

namespace Vellum.Modules.Promises;

/// <summary>A scenario at one version of its stream, with the hash of its document.</summary>
public sealed record ScenarioSnapshot(Scenario Document, long Version, string Hash)
{
    public static ScenarioSnapshot Of(Scenario document, long version) => new(document, version, PromiseJson.Hash(document));
}

/// <summary>The line of history the accepted facts of a repository follow, and the commit of the last accepted decision.</summary>
public sealed record TrackedCommit(string RefName, string? CommitSha);

/// <summary>
/// What a promise is read against, read once for a request: a model, the scenarios, the
/// schema versions and, for each repository, the commit the model follows. All of it is as
/// it is now, or as an export pinned it. Links are checked against it when a promise is
/// written and again on every read, so a link to a thing that was removed later shows as
/// missing and is never dropped; and every input a run recorded is compared with it, so a
/// result whose input moved shows as not current.
/// </summary>
public sealed class PromiseWorld
{
    /// <param name="scenarioNames">The name of every scenario of the project.</param>
    /// <param name="scenarios">The scenarios that were read, by ID. A scenario that is absent here was not read or does not exist.</param>
    /// <param name="schemaVersions">The content version of every schema of the project.</param>
    /// <param name="tracked">By <c>host/owner/name</c>.</param>
    /// <param name="latestCommits">The commit of the newest snapshot, by <c>host/owner/name</c>.</param>
    /// <param name="repositories"><c>host/owner/name</c> of each repository with an accepted link or a tracked ref.</param>
    public PromiseWorld(
        Guid mainStreamId, long modelVersion, ModelState model,
        IReadOnlyDictionary<Guid, string> scenarioNames,
        IReadOnlyDictionary<Guid, ScenarioSnapshot> scenarios,
        IReadOnlyDictionary<Guid, int> schemaVersions,
        IReadOnlyDictionary<string, TrackedCommit> tracked,
        IReadOnlyDictionary<string, string> latestCommits,
        IReadOnlySet<string> repositories,
        string? gitHubHost = null)
    {
        MainStreamId = mainStreamId;
        ModelVersion = modelVersion;
        Model = model;
        ScenarioNames = scenarioNames;
        Scenarios = scenarios;
        SchemaVersions = schemaVersions;
        Tracked = tracked;
        LatestCommits = latestCommits;
        Known = new KnownThings(
            scenarioNames.Keys.ToHashSet(),
            model.Operations.Keys.Select(id => id.Value).ToHashSet(),
            model.Channels.Keys.Select(id => id.Value).ToHashSet(),
            model.Subscriptions.Keys.Select(id => id.Value).ToHashSet(),
            model.Messages.Keys.Select(id => id.Value).ToHashSet(),
            model.Elements.Keys.Select(id => id.Value).ToHashSet(),
            scenarios.ToDictionary(s => s.Key, s => s.Value.Document.Faults ?? []),
            gitHubHost,
            repositories);
    }

    /// <summary>The model stream this view is for, and the version of it that <see cref="Model"/> is.</summary>
    public Guid MainStreamId { get; }
    public long ModelVersion { get; }
    public ModelState Model { get; }
    public IReadOnlyDictionary<Guid, string> ScenarioNames { get; }
    public KnownThings Known { get; }

    /// <summary>The scenarios that were asked for and exist, each with its stream version and document hash.</summary>
    public IReadOnlyDictionary<Guid, ScenarioSnapshot> Scenarios { get; }

    public IReadOnlyDictionary<Guid, int> SchemaVersions { get; }

    /// <summary>The tracked ref of each repository of the project, by <c>host/owner/name</c>.</summary>
    public IReadOnlyDictionary<string, TrackedCommit> Tracked { get; }

    /// <summary>The commit of the newest snapshot of each repository of the project, by <c>host/owner/name</c>.</summary>
    public IReadOnlyDictionary<string, string> LatestCommits { get; }

    public static string RepositoryKey(string host, string repository) => $"{host}/{repository}";

    /// <summary>
    /// The commit a result about <paramref name="repositoryKey"/> must be for to be current:
    /// the commit of the last accepted decision of the tracked ref; when nothing was accepted
    /// yet, the commit of the newest snapshot. Null when the project holds neither.
    /// </summary>
    public (string Commit, bool Accepted)? Followed(string repositoryKey) =>
        Tracked.TryGetValue(repositoryKey, out var tracked) && tracked.CommitSha is { } accepted ? (accepted, true)
        : LatestCommits.TryGetValue(repositoryKey, out var latest) ? (latest, false)
        : null;

    /// <summary>The scenarios whose documents every read of these promises needs: the linked ones and the ones their checks play.</summary>
    public static IEnumerable<Guid> ScenarioIds(IEnumerable<PromiseState> states) =>
        states.SelectMany(s => s.Revisions).SelectMany(r =>
            r.Content.Links.ScenarioIds.Concat(r.Obligations.Select(o => o.Recipe.ScenarioCheck?.ScenarioId).OfType<Guid>()));

    /// <param name="scenarioDocuments">The scenarios whose documents the caller needs. The names of all are always read.</param>
    /// <param name="pinned">The model to read against. Null means the main model at its current version.</param>
    /// <param name="withGitHubHost">True when a write must know the workspace's GitHub host, to check an evidence source.</param>
    public static async Task<PromiseWorld> LoadAsync(
        IServiceProvider services, Guid projectId, IEnumerable<Guid> scenarioDocuments, CancellationToken ct,
        PinnedModel? pinned = null, bool withGitHubHost = false)
    {
        var store = services.GetRequiredService<IEventStore>();
        var workspaces = services.GetRequiredService<AppDbContext>();
        Guid mainStreamId;
        ModelState model;
        long version;
        if (pinned is not null)
            (mainStreamId, model, version) = (pinned.Pin.StreamId, pinned.State, pinned.Pin.Version);
        else
        {
            mainStreamId = await workspaces.Projects.AsNoTracking()
                .Where(p => p.Id == projectId).Select(p => p.StreamId).FirstAsync(ct);
            (model, version) = await store.Load<ModelState>(StreamId.From(mainStreamId), ct);
        }

        var names = await services.GetRequiredService<AppDbContext>().Scenarios.AsNoTracking()
            .Where(s => s.ProjectId == projectId)
            .ToDictionaryAsync(s => s.Id, s => s.Name, ct);

        var scenarios = new Dictionary<Guid, ScenarioSnapshot>();
        foreach (var id in scenarioDocuments.Distinct().Where(names.ContainsKey).Order())
        {
            var (state, scenarioVersion) = await LoadScenarioAsync(store, id, ct);
            if (state.DocumentIn(projectId) is { } document)
                scenarios[id] = ScenarioSnapshot.Of(document, scenarioVersion);
        }

        var schemaVersions = await services.GetRequiredService<AppDbContext>().Schemas.AsNoTracking()
            .Where(s => s.ProjectId == projectId)
            .ToDictionaryAsync(s => s.Id, s => s.Version, ct);

        var (tracked, latest, repositories) = await SourcesAsync(services.GetRequiredService<AppDbContext>(), projectId, ct);

        string? gitHubHost = null;
        if (withGitHubHost)
        {
            var workspaceId = await workspaces.Projects.AsNoTracking().Where(p => p.Id == projectId).Select(p => p.WorkspaceId).FirstAsync(ct);
            gitHubHost = await GitHubHostAsync(services.GetRequiredService<SettingsResolver>(), workspaceId, ct);
        }

        return new PromiseWorld(mainStreamId, version, model, names, scenarios, schemaVersions, tracked, latest, repositories, gitHubHost);
    }

    /// <summary>What the import tables say about the repositories of a project: the tracked refs, the newest snapshots and the followed repositories.</summary>
    public static async Task<(Dictionary<string, TrackedCommit> Tracked, Dictionary<string, string> Latest, HashSet<string> Repositories)> SourcesAsync(
        AppDbContext db, Guid projectId, CancellationToken ct)
    {
        var trackedRows = await db.TrackedRefs.AsNoTracking()
            .Where(t => t.ProjectId == projectId)
            .Select(t => new { t.Host, t.Repository, t.RefName, t.CommitSha })
            .ToListAsync(ct);
        var tracked = trackedRows.ToDictionary(
            t => RepositoryKey(t.Host, t.Repository), t => new TrackedCommit(t.RefName, t.CommitSha), StringComparer.Ordinal);

        // A pull request or a bare commit is never the newest state of a repository.
        var snapshots = await ScanSnapshots.OfRefs(db, projectId).AsNoTracking()
            .OrderBy(s => s.CreatedAt).ThenBy(s => s.ScanId)
            .Select(s => new { s.Host, s.Repository, s.CommitSha })
            .ToListAsync(ct);
        var latest = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var snapshot in snapshots)
            latest[RepositoryKey(snapshot.Host, snapshot.Repository)] = snapshot.CommitSha;

        var linked = await db.FactLinks.AsNoTracking()
            .Where(l => l.ProjectId == projectId)
            .Select(l => new { l.Host, l.Repository })
            .Distinct()
            .ToListAsync(ct);
        var repositories = linked.Select(l => RepositoryKey(l.Host, l.Repository)).Concat(tracked.Keys).ToHashSet(StringComparer.Ordinal);
        return (tracked, latest, repositories);
    }

    /// <summary>
    /// The host of the repository pages of the workspace's GitHub: <c>github.com</c> for
    /// <c>api.github.com</c>, else the API host. Null when the workspace has no usable address.
    /// </summary>
    public static async Task<string?> GitHubHostAsync(SettingsResolver settings, Guid workspaceId, CancellationToken ct) =>
        (await settings.GetWorkspaceAsync(workspaceId, ct)).GithubApiBaseUrl is SettingsUrl.Usable api ? WebHost(api.Url) : null;

    public static string WebHost(Uri apiBaseUrl)
    {
        var host = apiBaseUrl.IdnHost.ToLowerInvariant();
        if (host == "api.github.com")
            return "github.com";
        return apiBaseUrl.IsDefaultPort ? host : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{host}:{apiBaseUrl.Port}");
    }

    /// <summary>A scenario stream as it is now. An ID that names a stream of another kind, or a deleted stream, is no scenario.</summary>
    internal static async Task<(ScenarioState State, long Version)> LoadScenarioAsync(IEventStore store, Guid scenarioId, CancellationToken ct)
    {
        try
        {
            var loaded = await store.Load<ScenarioState>(StreamId.From(scenarioId), ct);
            return (loaded.State, loaded.Version);
        }
        catch (DeedboxException e) when (e.Code is DeedboxError.StreamTypeMismatch or DeedboxError.StreamDeleted)
        {
            return (ScenarioState.Initial, 0);
        }
    }

    /// <summary>
    /// A hash of a linked thing as it is now, or null when it does not exist (or, for a
    /// scenario, when its document was not loaded). A stale mark is a note of a scan and not
    /// part of the fact, so it is left out.
    /// </summary>
    public string? LinkHash(string kind, Guid id) => kind switch
    {
        "scenario" => Scenarios.TryGetValue(id, out var scenario) ? scenario.Hash : null,
        "operation" => Model.Operations.TryGetValue(id, out var operation) ? PromiseJson.Hash(operation with { Stale = null }) : null,
        "channel" => Model.Channels.TryGetValue(id, out var channel) ? PromiseJson.Hash(channel with { Stale = null }) : null,
        "subscription" => Model.Subscriptions.TryGetValue(id, out var subscription) ? PromiseJson.Hash(subscription with { Stale = null }) : null,
        "message" => Model.Messages.TryGetValue(id, out var message) ? PromiseJson.Hash(message with { Stale = null }) : null,
        "element" => Model.Elements.TryGetValue(id, out var element) ? PromiseJson.Hash(element with { Stale = null }) : null,
        _ => null,
    };

    public static string LinkKey(string kind, Guid id) => $"{kind}:{id}";

    /// <summary>The hash of every link that exists, by <see cref="LinkKey"/>. Scenario documents must be loaded.</summary>
    public IReadOnlyDictionary<string, string> LinkHashes(PromiseLinks links)
    {
        var hashes = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (kind, id) in links.All())
        {
            if (LinkHash(kind, id) is { } hash)
                hashes[LinkKey(kind, id)] = hash;
        }
        return hashes;
    }

    /// <summary>A name for a linked thing, for a sentence a person reads.</summary>
    public string Describe(string kind, Guid id)
    {
        var name = kind switch
        {
            "scenario" => ScenarioNames.GetValueOrDefault(id),
            "operation" => Model.Operations.TryGetValue(id, out var operation) ? operation.Name : null,
            "channel" => Model.Channels.TryGetValue(id, out var channel) ? channel.Name : null,
            "subscription" => Model.Subscriptions.TryGetValue(id, out var subscription) ? ContractDecider.SubscriptionLabel(Model, subscription) : null,
            "message" => Model.Messages.TryGetValue(id, out var message) ? message.Name : null,
            "element" => Model.Elements.TryGetValue(id, out var element) ? element.Name : null,
            _ => null,
        };
        return name is null ? $"{kind} {id}" : $"{kind} '{name}' ({id})";
    }
}
