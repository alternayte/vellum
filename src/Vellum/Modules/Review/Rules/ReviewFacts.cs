using Vellum.Modules.Build;
using Vellum.Modules.Modelling.Model;
using Vellum.Modules.Promises;
using Vellum.Modules.Trace.Engine;
using Vellum.Modules.Trace.Scenarios;

namespace Vellum.Modules.Review.Rules;

/// <summary>
/// What the rules read: the pinned model, the scenarios, the schemas and the promises of one
/// <see cref="BuildInput"/>, with the lookups the rules share. Nothing here reads a clock, a
/// database or the order of an input list.
/// </summary>
internal sealed class ReviewFacts
{
    private readonly Dictionary<Guid, SortedSet<Guid>> _path = [];
    private readonly Dictionary<Guid, PromiseDto> _promises;

    public ReviewFacts(BuildView view)
    {
        View = view;
        Model = view.Model;
        Trace = TraceModelBuilder.Build(Model, view.Schemas.ToDictionary(s => new SchemaId(s.Id), s => s.Content));
        _promises = view.Promises.ToDictionary(p => p.Id);

        Calls = [.. Steps<CallStep>()];
        Publishes = [.. Steps<PublishStep>()];
        Writes = [.. Steps<WriteStep>()];

        foreach (var promise in view.Requirements)
        {
            var links = promise.Current.Links;
            foreach (var id in links.OperationIds.Concat(links.ChannelIds).Concat(links.SubscriptionIds).Concat(links.MessageIds).Concat(links.ElementIds))
                OnPath(id, promise.Id);
            foreach (var scenarioId in links.ScenarioIds.Where(view.Steps.ContainsKey))
            {
                OnPath(scenarioId, promise.Id);
                foreach (var step in view.Steps[scenarioId].Select(s => s.Step))
                {
                    foreach (var id in References(step))
                        OnPath(id, promise.Id);
                }
            }
        }

        // What an on-path contract hangs from is on the path too: the service that serves an
        // operation, the channel and the consumer of a subscription, the producer of a message
        // and the bindings that leave an on-path channel.
        foreach (var (id, promises) in _path.ToList())
        {
            IEnumerable<Guid> more =
                Model.Operations.TryGetValue(id, out var operation) ? [operation.OwnerId]
                : Model.Subscriptions.TryGetValue(id, out var subscription)
                    ? new Guid?[] { subscription.ChannelId, subscription.ConsumerId, subscription.DeadLetterChannelId?.Value }.OfType<Guid>()
                : Model.Messages.TryGetValue(id, out var message) ? [message.ProducerId]
                : Model.Channels.ContainsKey(id) ? Model.Bindings.Values.Where(b => b.SourceChannelId.Value == id).SelectMany(b => new Guid[] { b.Id, b.TargetChannelId })
                : [];
            foreach (var extra in more.ToList())
            {
                foreach (var promise in promises)
                    OnPath(extra, promise);
            }
        }
    }

    public BuildView View { get; }
    public ModelState Model { get; }
    public TraceModel Trace { get; }
    public IReadOnlyList<(Scenario Scenario, CallStep Step)> Calls { get; }
    public IReadOnlyList<(Scenario Scenario, PublishStep Step)> Publishes { get; }
    public IReadOnlyList<(Scenario Scenario, WriteStep Step)> Writes { get; }

    /// <summary>The IDs that a scenario step names: who acts and what it acts on.</summary>
    public static IEnumerable<Guid> References(Step step) => step switch
    {
        CallStep call => [call.From, call.Operation],
        PublishStep publish => [publish.From, publish.Channel, publish.Message, .. (publish.Handlers?.Keys ?? []).Select(h => h.Value)],
        WriteStep write => [write.By, write.Store],
        _ => [],
    };

    /// <summary>The approved promises whose path holds the thing: a link, or a step of a linked scenario.</summary>
    public IReadOnlyList<PromiseDto> PromisesOn(Guid id) =>
        _path.TryGetValue(id, out var promises) ? [.. promises.Select(p => _promises[p]).OrderBy(p => p.RequirementId, StringComparer.Ordinal)] : [];

    public bool IsOnPath(Guid id) => _path.ContainsKey(id);

    /// <summary>Every model fact on the path of an approved promise, by ID.</summary>
    public IEnumerable<Guid> PathFacts => _path.Keys.Where(id => Model.KindOf(id) is not null).Order();

    /// <summary><c>blocking</c> for a subject on the path of an approved promise, else <paramref name="otherwise"/>.</summary>
    public string SeverityOn(Guid id, string otherwise = Severity.Warning) => IsOnPath(id) ? Severity.Blocking : otherwise;

    /// <summary>" It is on the path of ORD-014." for a subject on a promise path, else nothing.</summary>
    public string PathNote(Guid id) => PromisesOn(id) is { Count: > 0 } promises
        ? $" It is on the path of {string.Join(", ", promises.Select(p => p.RequirementId))}."
        : "";

    public Cited Cite(Guid id)
    {
        var kind =
            Model.Elements.ContainsKey(id) ? "element"
            : Model.Operations.ContainsKey(id) ? "operation"
            : Model.Channels.ContainsKey(id) ? "channel"
            : Model.Subscriptions.ContainsKey(id) ? "subscription"
            : Model.Bindings.ContainsKey(id) ? "binding"
            : Model.Messages.ContainsKey(id) ? "message"
            : Model.Relationships.ContainsKey(id) ? "relationship"
            : View.SchemasById.ContainsKey(id) ? "schema"
            : View.Steps.ContainsKey(id) ? "scenario"
            : _promises.ContainsKey(id) ? "promise"
            : "unknown";
        return new Cited(kind, id, kind == "promise" ? _promises[id].RequirementId : View.Name(id));
    }

    public string Name(Guid id) => View.Name(id);

    /// <summary>
    /// Why a rule cannot read a fact as stated: it is inferred, its truth is unknown, it is
    /// marked stale, or the model does not hold it. Null when every fact is declared, observed
    /// in source or measured and is not stale. A rule gives <c>unknown</c> for a fact in doubt.
    /// </summary>
    public string? Doubt(params Guid?[] ids)
    {
        foreach (var id in ids.OfType<Guid>())
        {
            if (Fact(id) is not { } fact)
                return $"the model holds nothing with the ID {id}";
            if (fact.Stale is not null)
                return $"the {fact.Kind} '{fact.Name}' is marked stale";
            if (fact.Status == FactStatus.Inferred)
                return $"the {fact.Kind} '{fact.Name}' is inferred and no person confirmed it";
            if (fact.Status == FactStatus.Unknown)
                return $"the truth of the {fact.Kind} '{fact.Name}' is unknown";
        }
        return null;
    }

    public (string Kind, string Name, FactStatus Status, StaleMark? Stale)? Fact(Guid id) =>
        Model.Elements.TryGetValue(id, out var element) ? ("element", element.Name, element.FactStatus, element.Stale)
        : Model.Operations.TryGetValue(id, out var operation) ? ("operation", operation.Name, operation.FactStatus, operation.Stale)
        : Model.Channels.TryGetValue(id, out var channel) ? ("channel", channel.Name, channel.FactStatus, channel.Stale)
        : Model.Subscriptions.TryGetValue(id, out var subscription) ? ("subscription", Name(id), subscription.FactStatus, subscription.Stale)
        : Model.Bindings.TryGetValue(id, out var binding) ? ("binding", Name(id), binding.FactStatus, binding.Stale)
        : Model.Messages.TryGetValue(id, out var message) ? ("message", message.Name, message.FactStatus, message.Stale)
        : Model.Relationships.TryGetValue(id, out var relationship) ? ("relationship", relationship.Label ?? Name(id), relationship.FactStatus, relationship.Stale)
        : null;

    /// <summary>The subscriptions on the channel and on every channel its bindings lead to. Routing keys are not read.</summary>
    public IReadOnlyList<SubscriptionState> ReachableSubscriptions(ChannelId channel)
    {
        var reached = ChannelRouting.Reachable(Trace, channel);
        return [.. View.Subscriptions.Where(s => reached.Contains(s.ChannelId))];
    }

    private IEnumerable<(Scenario Scenario, T Step)> Steps<T>() where T : Step =>
        View.Scenarios.SelectMany(scenario => View.Steps[scenario.Id.Value].Select(s => s.Step).OfType<T>().Select(step => (scenario, step)));

    private void OnPath(Guid id, Guid promiseId)
    {
        if (!_path.TryGetValue(id, out var promises))
            _path[id] = promises = [];
        promises.Add(promiseId);
    }
}
