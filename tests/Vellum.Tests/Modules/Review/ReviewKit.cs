using System.Text.Json;
using Vellum.Modules.Build;
using Vellum.Modules.Modelling;
using Vellum.Modules.Promises;
using Vellum.Modules.Review.Rules;
using Vellum.Modules.Trace.Engine;

namespace Vellum.Tests.Modules.Review;

/// <summary>
/// Small models for the rule tests, built in memory from the events of the model stream. An
/// ID is <c>G(n)</c>, so a test names a thing by its number.
/// </summary>
internal sealed class Fixture
{
    public static Guid G(int n) => new($"00000000-0000-0000-0000-{n:D12}");

    private readonly List<object> _events = [];
    private readonly List<Func<ModelState, ModelState>> _labels = [];
    private readonly List<BuildSchema> _schemas = [];
    private readonly List<Scenario> _scenarios = [];
    private readonly List<PromiseDto> _promises = [];

    public Fixture Element(int id, ElementKind kind, string name, int? parent = null)
    {
        _events.Add(new ModelEvent.ElementAdded(G(id), kind, name, null, null, null, ElementStatus.Current, parent is { } p ? G(p) : null, []));
        return this;
    }

    public Fixture Relationship(int id, int from, int to)
    {
        _events.Add(new ModelEvent.RelationshipAdded(G(id), G(from), G(to), "uses", null, null));
        return this;
    }

    public Fixture Operation(int id, int owner, string name, int? request = null, int? response = null, int timeoutMs = 1000, int attempts = 1)
    {
        _events.Add(new ModelEvent.OperationAdded(G(id), G(owner), name, Transport.Http,
            request is { } q ? G(q) : null, response is { } r ? G(r) : null, timeoutMs, new RetryPolicy(attempts, 0)));
        return this;
    }

    public Fixture Channel(int id, string name, ChannelKind kind = ChannelKind.Topic)
    {
        _events.Add(new ModelEvent.ChannelAdded(G(id), name, kind));
        return this;
    }

    public Fixture Subscription(int id, int channel, int consumer, int attempts = 1, int? deadLetter = null)
    {
        _events.Add(new ModelEvent.SubscriptionAdded(G(id), G(channel), G(consumer), new RetryPolicy(attempts, 0), deadLetter is { } d ? G(d) : null));
        return this;
    }

    public Fixture Binding(int id, int source, int target, string? key = null)
    {
        _events.Add(new ModelEvent.BindingAdded(G(id), G(source), G(target), key));
        return this;
    }

    public Fixture Message(int id, string name, int producer, int? schema = null)
    {
        _events.Add(new ModelEvent.MessageAdded(G(id), name, null, G(producer), [], schema is { } s ? G(s) : null, []));
        return this;
    }

    public Fixture Schema(int id, string name, string content = """{"type":"object","required":["id"],"properties":{"id":{"type":"string"}}}""")
    {
        _schemas.Add(new BuildSchema(G(id), name, null, content, 1));
        return this;
    }

    /// <summary>Gives a fact another truth label, or marks it stale, as an import does.</summary>
    public Fixture Label(int id, FactStatus status, bool stale = false)
    {
        // Remove and Add, not SetItem: equality of a fact leaves the stale mark out, so SetItem would keep the old value.
        var mark = stale ? new StaleMark("abc123", "evidence-not-found") : null;
        _labels.Add(model =>
            model.Elements.TryGetValue(G(id), out var e) ? model with { Elements = model.Elements.Remove(G(id)).Add(G(id), e with { FactStatus = status, Stale = mark }) }
            : model.Operations.TryGetValue(G(id), out var o) ? model with { Operations = model.Operations.Remove(G(id)).Add(G(id), o with { FactStatus = status, Stale = mark }) }
            : model.Channels.TryGetValue(G(id), out var c) ? model with { Channels = model.Channels.Remove(G(id)).Add(G(id), c with { FactStatus = status, Stale = mark }) }
            : model.Subscriptions.TryGetValue(G(id), out var s) ? model with { Subscriptions = model.Subscriptions.Remove(G(id)).Add(G(id), s with { FactStatus = status, Stale = mark }) }
            : model.Bindings.TryGetValue(G(id), out var b) ? model with { Bindings = model.Bindings.Remove(G(id)).Add(G(id), b with { FactStatus = status, Stale = mark }) }
            : model.Messages.TryGetValue(G(id), out var m) ? model with { Messages = model.Messages.Remove(G(id)).Add(G(id), m with { FactStatus = status, Stale = mark }) }
            : model.Relationships.TryGetValue(G(id), out var r) ? model with { Relationships = model.Relationships.Remove(G(id)).Add(G(id), r with { FactStatus = status, Stale = mark }) }
            : throw new InvalidOperationException($"No fact {id}"));
        return this;
    }

    public Fixture Scenario(int id, string name, params Step[] steps)
    {
        _scenarios.Add(new Scenario(G(id), name, steps, [], []));
        return this;
    }

    /// <summary>An approved promise in force that links the given things. Its evidence is a passed, current scenario check unless said otherwise.</summary>
    public Fixture Promise(
        int id, string requirementId, int[]? scenarios = null, int[]? operations = null, int[]? channels = null, int[]? elements = null,
        string state = PromiseLifecycle.Approved, string result = RunResult.Passed, bool current = true)
    {
        Guid[] Ids(int[]? ids) => [.. (ids ?? []).Select(G)];
        var obligation = new ObligationDto($"{requirementId}/O1", "O1", 1, ObligationKind.ScenarioCheck, true, "deterministic", "Check",
            new ObligationRecipe("Check"), Obligation.Manual);
        var status = new ObligationStatusDto($"{requirementId}/O1", result, result is RunResult.Passed or RunResult.Failed ? current : null,
            null, null, null, null, null, null, null, [], [], null, null, [], null, null);
        var revision = new PromiseRevisionDto(
            1, state == PromiseLifecycle.Draft ? RevisionStatus.Draft : RevisionStatus.Approved, false, null, state == PromiseLifecycle.Approved,
            "Title", "Claim", null, null, null, null, null, [], null,
            new PromiseLinks(Ids(scenarios), Ids(operations), Ids(channels), [], [], Ids(elements)),
            [obligation], "u", DateTimeOffset.UnixEpoch, null, null, null, "hash", null,
            new EvidenceStatusDto(new EvidenceCountsDto(result == RunResult.Passed && current ? 1 : 0, result == RunResult.Failed && current ? 1 : 0, 0, 0, 0), [status], [], [], [], []));
        _promises.Add(new PromiseDto(G(id), G(999), requirementId, PromiseOrigin.Human, state, 1, state == PromiseLifecycle.Draft ? null : 1, false, [],
            revision, null, [revision], "u", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch));
        return this;
    }

    public BuildInput Input()
    {
        var model = _labels.Aggregate(_events.Aggregate(ModelState.Initial, ModelState.Evolve), (state, label) => label(state));
        return new BuildInput(G(999), "Fixture", new ModelPin(G(998), 1, null, null), model, _schemas, _scenarios, _promises);
    }

    public ReviewReport Review() => RuleSet.Run(Input());

    public static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    public static CallStep Call(string id, int from, int operation, string? request = null, params Step[] then) =>
        new(id, G(from), G(operation), request is null ? null : Json(request), null, then, []);

    public static PublishStep Publish(string id, int from, int channel, int message, string? payload = null, string? key = null, params (int Subscription, Step[] Steps)[] handlers) =>
        new(id, G(from), G(channel), G(message), payload is null ? null : Json(payload), new Dictionary<string, string>(),
            handlers.ToDictionary(h => new SubscriptionId(G(h.Subscription)), h => (IReadOnlyList<Step>)h.Steps), key);

    public static WriteStep Write(string id, int by, int store, string dataset) => new(id, G(by), G(store), dataset, null);
}

internal static class ReportExtensions
{
    public static Finding Of(this ReviewReport report, string ruleId, int subject) =>
        report.Findings.Single(f => f.RuleId == ruleId && f.Subject.Id == Fixture.G(subject));

    public static Finding? Find(this ReviewReport report, string ruleId, int subject) =>
        report.Findings.SingleOrDefault(f => f.RuleId == ruleId && f.Subject.Id == Fixture.G(subject));

    public static string Result(this ReviewReport report, string ruleId, int subject) => report.Of(ruleId, subject).Result;
}
