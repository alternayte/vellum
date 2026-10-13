using Deedbox;
using Microsoft.EntityFrameworkCore;
using Vellum.Modules.Import;
using Vellum.Modules.Modelling.Model;
using Vellum.Modules.Trace.Engine;
using Vellum.Modules.Trace.Scenarios;
using Vellum.Modules.Workspaces;

namespace Vellum.Modules.Promises;

/// <summary>
/// What a promise is read against, read once for a request: a model, the scenarios and the
/// newest import snapshot of each repository. The model is the project's main model as it is
/// now, or a pinned model when an export is made for one. Links are checked against it when a
/// promise is written and again on every read, so a link to a thing that was removed later
/// shows as missing and is never dropped.
/// </summary>
public sealed class PromiseWorld
{
    private readonly IReadOnlyDictionary<Guid, Scenario> _documents;

    private PromiseWorld(
        Guid mainStreamId, long modelVersion, ModelState model, IReadOnlyDictionary<Guid, string> scenarioNames,
        IReadOnlyDictionary<Guid, Scenario> documents, IReadOnlyDictionary<string, string> latestCommits)
    {
        MainStreamId = mainStreamId;
        ModelVersion = modelVersion;
        Model = model;
        ScenarioNames = scenarioNames;
        _documents = documents;
        LatestCommits = latestCommits;
        Known = new KnownThings(
            scenarioNames.Keys.ToHashSet(),
            model.Operations.Keys.Select(id => id.Value).ToHashSet(),
            model.Channels.Keys.Select(id => id.Value).ToHashSet(),
            model.Subscriptions.Keys.Select(id => id.Value).ToHashSet(),
            model.Messages.Keys.Select(id => id.Value).ToHashSet(),
            model.Elements.Keys.Select(id => id.Value).ToHashSet());
    }

    /// <summary>The model stream this view is for, and the version of it that <see cref="Model"/> is.</summary>
    public Guid MainStreamId { get; }
    public long ModelVersion { get; }
    public ModelState Model { get; }
    public IReadOnlyDictionary<Guid, string> ScenarioNames { get; }
    public KnownThings Known { get; }

    /// <summary>The commit of the newest snapshot of each repository (<c>owner/name</c>) of the project.</summary>
    public IReadOnlyDictionary<string, string> LatestCommits { get; }

    /// <summary>The documents of the scenarios that were asked for and exist.</summary>
    public IReadOnlyDictionary<Guid, Scenario> Scenarios => _documents;

    /// <param name="scenarioDocuments">The scenarios whose documents the caller needs. The names of all are always read.</param>
    /// <param name="pinned">The model to read against. Null means the main model at its current version.</param>
    public static async Task<PromiseWorld> LoadAsync(
        IServiceProvider services, Guid projectId, IEnumerable<Guid> scenarioDocuments, CancellationToken ct, PinnedModel? pinned = null)
    {
        var store = services.GetRequiredService<IEventStore>();
        Guid mainStreamId;
        ModelState model;
        long version;
        if (pinned is not null)
            (mainStreamId, model, version) = (pinned.Pin.StreamId, pinned.State, pinned.Pin.Version);
        else
        {
            mainStreamId = await services.GetRequiredService<WorkspacesDbContext>().Projects.AsNoTracking()
                .Where(p => p.Id == projectId).Select(p => p.StreamId).FirstAsync(ct);
            (model, version) = await store.Load<ModelState>(StreamId.From(mainStreamId), ct);
        }

        var names = await services.GetRequiredService<ScenariosDbContext>().Scenarios.AsNoTracking()
            .Where(s => s.ProjectId == projectId)
            .ToDictionaryAsync(s => s.Id, s => s.Name, ct);

        var documents = new Dictionary<Guid, Scenario>();
        foreach (var id in scenarioDocuments.Distinct().Where(names.ContainsKey).Order())
        {
            if ((await ScenarioStreams.LoadAsync(store, id, ct)).DocumentIn(projectId) is { } document)
                documents[id] = document;
        }

        var snapshots = await services.GetRequiredService<ImportDbContext>().Snapshots.AsNoTracking()
            .Where(s => s.ProjectId == projectId)
            .OrderBy(s => s.CreatedAt).ThenBy(s => s.ScanId)
            .Select(s => new { s.Repository, s.CommitSha })
            .ToListAsync(ct);
        var latest = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var snapshot in snapshots)
            latest[snapshot.Repository] = snapshot.CommitSha;

        return new PromiseWorld(mainStreamId, version, model, names, documents, latest);
    }

    /// <summary>
    /// A hash of a linked thing as it is now, or null when it does not exist (or, for a
    /// scenario, when its document was not loaded). A stale mark is a note of a scan and not
    /// part of the fact, so it is left out.
    /// </summary>
    public string? LinkHash(string kind, Guid id) => kind switch
    {
        "scenario" => _documents.TryGetValue(id, out var scenario) ? PromiseJson.Hash(scenario) : null,
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
