using Deedbox;
using Microsoft.EntityFrameworkCore;
using Vellum.Modules.Modelling;
using Vellum.Modules.Review;
using Vellum.Modules.Trace;
using Vellum.Modules.Trace.Engine;
using Vellum.Shared;

namespace Vellum.Modules.Comments;

/// <summary>A thread target as a stable ID. A flow step is the scenario ID and the step ID.</summary>
public sealed record TargetRef(TargetKind Kind, Guid Id, string? StepId);

/// <summary>
/// The names of the targets of one branch, read once for a request. A target that the book
/// cannot find does not exist on that branch now: its thread is detached.
/// </summary>
internal sealed class TargetBook
{
    private readonly Dictionary<TargetRef, string> _names;

    private TargetBook(ModelState model, long modelVersion, Guid modelStreamId, Dictionary<TargetRef, string> names)
    {
        Model = model;
        ModelVersion = modelVersion;
        ModelStreamId = modelStreamId;
        _names = names;
    }

    public ModelState Model { get; }

    /// <summary>The version of the branch's model stream that the book read.</summary>
    public long ModelVersion { get; }

    public Guid ModelStreamId { get; }

    /// <summary>The current name of the target, or null when it does not exist on the branch.</summary>
    public string? NameOf(TargetRef target) => Bounded(ModelName(Model, target) ?? _names.GetValueOrDefault(target));

    private static string? Bounded(string? name) =>
        name is { Length: > CommentsTables.MaxTargetName } ? name[..CommentsTables.MaxTargetName] : name;

    public static bool IsModelKind(TargetKind kind) =>
        kind is TargetKind.Element or TargetKind.Relationship or TargetKind.Message or TargetKind.Operation or TargetKind.Channel;

    /// <summary>Reads the branch model and every target outside the model that <paramref name="targets"/> names.</summary>
    public static async Task<TargetBook> LoadAsync(
        IServiceProvider services, Guid projectId, Guid modelStreamId, IEnumerable<TargetRef> targets, CancellationToken ct)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var store = services.GetRequiredService<IEventStore>();
        var (model, version) = await store.Load<ModelState>(StreamId.From(modelStreamId), ct);

        var wanted = targets.Where(t => !IsModelKind(t.Kind)).Distinct().ToList();
        var names = new Dictionary<TargetRef, string>();

        IReadOnlyList<Guid> Ids(TargetKind kind) => [.. wanted.Where(t => t.Kind == kind).Select(t => t.Id).Distinct()];

        if (wanted.Any(t => t.Kind == TargetKind.Project))
        {
            var project = await db.Projects.AsNoTracking().Where(p => p.Id == projectId).Select(p => p.Name).FirstOrDefaultAsync(ct);
            if (project is not null)
                names[new TargetRef(TargetKind.Project, projectId, null)] = project;
        }

        if (Ids(TargetKind.Promise) is { Count: > 0 } promiseIds)
        {
            foreach (var p in await db.Promises.AsNoTracking()
                         .Where(p => p.ProjectId == projectId && promiseIds.Contains(p.Id))
                         .Select(p => new { p.Id, p.RequirementId, p.Title }).ToListAsync(ct))
                names[new TargetRef(TargetKind.Promise, p.Id, null)] = $"{p.RequirementId} {p.Title}";
        }

        if (Ids(TargetKind.Decision) is { Count: > 0 } decisionIds)
        {
            foreach (var d in await db.Decisions.AsNoTracking()
                         .Where(d => d.ProjectId == projectId && decisionIds.Contains(d.Id))
                         .Select(d => new { d.Id, d.Number, d.Title }).ToListAsync(ct))
                names[new TargetRef(TargetKind.Decision, d.Id, null)] = $"{DecisionState.Label(d.Number)} {d.Title}";
        }

        if (Ids(TargetKind.Document) is { Count: > 0 } documentIds)
        {
            foreach (var d in await db.Documents.AsNoTracking()
                         .Where(d => d.ProjectId == projectId && documentIds.Contains(d.Id))
                         .Select(d => new { d.Id, d.Title }).ToListAsync(ct))
                names[new TargetRef(TargetKind.Document, d.Id, null)] = d.Title;
        }

        foreach (var scenarioId in Ids(TargetKind.Step))
        {
            var scenario = (await ScenarioStreams.LoadAsync(store, scenarioId, ct)).DocumentIn(projectId);
            if (scenario is null)
                continue;
            foreach (var step in AllSteps(scenario.Steps))
                names[new TargetRef(TargetKind.Step, scenarioId, step.Id)] = $"{scenario.Name} · {step.Id}";
        }

        return new TargetBook(model, version, modelStreamId, names);
    }

    /// <summary>
    /// The last name each of <paramref name="ids"/> had in the history of the stream, read
    /// from the first event. A rename just before a delete is the name a detached thread shows.
    /// </summary>
    public static async Task<IReadOnlyDictionary<TargetRef, string>> LastNamesAsync(
        IEventStore store, Guid modelStreamId, IReadOnlyCollection<TargetRef> targets, CancellationToken ct)
    {
        const int pageSize = 500;
        var last = new Dictionary<TargetRef, string>();
        if (targets.Count == 0)
            return last;

        var state = ModelState.Initial;
        long after = 0;
        while (true)
        {
            var page = await store.ReadStream(StreamId.From(modelStreamId), after, pageSize, ct);
            if (page.Count == 0)
                break;
            foreach (var envelope in page)
            {
                if (envelope.Event is StreamDeleted)
                    return last;
                state = ModelState.Evolve(state, envelope.Event);
                foreach (var target in targets)
                {
                    if (Bounded(ModelName(state, target)) is { } name)
                        last[target] = name;
                }
            }
            after = page[^1].Version;
        }

        return last;
    }

    private static string? ModelName(ModelState model, TargetRef target) => target.Kind switch
    {
        TargetKind.Element => model.Elements.GetValueOrDefault(new ElementId(target.Id))?.Name,
        TargetKind.Relationship => model.Relationships.GetValueOrDefault(new RelationshipId(target.Id)) is { } r
            ? r.Label is { Length: > 0 } label
                ? label
                : $"{model.Elements.GetValueOrDefault(r.FromId)?.Name ?? "?"} → {model.Elements.GetValueOrDefault(r.ToId)?.Name ?? "?"}"
            : null,
        TargetKind.Message => model.Messages.GetValueOrDefault(new MessageId(target.Id))?.Name,
        TargetKind.Operation => model.Operations.GetValueOrDefault(new OperationId(target.Id))?.Name,
        TargetKind.Channel => model.Channels.GetValueOrDefault(new ChannelId(target.Id))?.Name,
        _ => null,
    };

    private static IEnumerable<Step> AllSteps(IEnumerable<Step> steps)
    {
        foreach (var step in steps)
        {
            yield return step;
            var inner = step switch
            {
                CallStep call => call.Then.Concat(call.OnError),
                PublishStep publish => publish.Handlers.Values.SelectMany(h => h),
                ParallelStep parallel => parallel.Branches.SelectMany(b => b),
                BranchStep branch => branch.Then.Concat(branch.Else),
                _ => [],
            };
            foreach (var nested in AllSteps(inner))
                yield return nested;
        }
    }
}
