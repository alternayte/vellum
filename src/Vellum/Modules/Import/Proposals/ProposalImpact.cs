using Deedbox;
using Microsoft.EntityFrameworkCore;
using Vellum.Modules.Modelling;
using Vellum.Modules.Trace.Engine;
using Vellum.Modules.Trace;
using Vellum.Shared;

namespace Vellum.Modules.Import.Proposals;

/// <param name="StepIds">The steps of the scenario that name the entity, and the IDs of the assertions and faults that do (<c>assertion:&lt;id&gt;</c>, <c>fault</c>).</param>
public sealed record ImpactScenarioDto(Guid Id, string Name, IReadOnlyList<string> StepIds);

public sealed record ImpactEntityDto(string EntityKind, Guid Id, string Name);

/// <summary>
/// What accepting a proposal would break. For a <c>remove</c>: the stored scenarios that
/// name the entity, and the entities of the model that point at it. For the <c>remove</c> of
/// a binding, also the publish steps with a handler for a subscription that the message no
/// longer reaches without the binding. For a <c>change</c> that
/// alters the schema of a message or the route of an operation: the stored scenarios that use it.
/// </summary>
public sealed record ProposalImpactDto(IReadOnlyList<ImpactScenarioDto> Scenarios, IReadOnlyList<ImpactEntityDto> Entities)
{
    public static readonly ProposalImpactDto None = new([], []);

    public bool IsEmpty => Scenarios.Count == 0 && Entities.Count == 0;
}

/// <summary>
/// Computes <see cref="ProposalImpactDto"/> from the model and the stored scenarios as they
/// are now, so the list a person reads and the check a decision makes agree.
/// </summary>
internal sealed class ProposalImpact
{
    /// <summary>The most scenarios of one project that are read. A project with more reports the first ones by name.</summary>
    public const int MaxScenarios = 500;

    private readonly ModelState _model;
    private readonly List<(Guid Id, string Name, Scenario Document)> _scenarios;

    private ProposalImpact(ModelState model, List<(Guid, string, Scenario)> scenarios)
    {
        _model = model;
        _scenarios = scenarios;
    }

    public static async Task<ProposalImpact> LoadAsync(
        IServiceProvider services, IEventStore store, Guid projectId, ModelState model, CancellationToken ct)
    {
        var rows = await services.GetRequiredService<AppDbContext>().Scenarios.AsNoTracking()
            .Where(s => s.ProjectId == projectId)
            .OrderBy(s => s.Name).ThenBy(s => s.Id)
            .Take(MaxScenarios)
            .Select(s => new { s.Id, s.Name })
            .ToListAsync(ct);

        var scenarios = new List<(Guid, string, Scenario)>();
        foreach (var row in rows)
        {
            var state = await ScenarioStreams.LoadAsync(store, row.Id, ct);
            if (state.DocumentIn(projectId) is { } document)
                scenarios.Add((row.Id, row.Name, document));
        }
        return new ProposalImpact(model, scenarios);
    }

    /// <summary>The same, over scenarios the caller holds: the ones a pinned state names, at their pinned versions.</summary>
    public static ProposalImpact Of(ModelState model, IEnumerable<Scenario> scenarios) => new(model, [.. scenarios
        .OrderBy(s => s.Name, StringComparer.Ordinal).ThenBy(s => s.Id.Value)
        .Take(MaxScenarios)
        .Select(s => (s.Id.Value, s.Name, s))]);

    /// <param name="changedFields">For a change: the model fields the proposal alters.</param>
    public ProposalImpactDto Of(string action, string entityKind, Guid entityId, IReadOnlyCollection<string> changedFields)
    {
        if (action == ProposalAction.Remove)
        {
            return new ProposalImpactDto(
                Scenarios([entityId], entityKind == EntityKinds.Binding ? new BindingId(entityId) : null),
                Dependents(entityKind, entityId));
        }

        if (action is not (ProposalAction.Change or ProposalAction.Conflict))
            return ProposalImpactDto.None;

        // A scenario plays a message against its schema and calls an operation by its route.
        var targets = entityKind switch
        {
            EntityKinds.Message when changedFields.Contains(Field.SchemaId) => [entityId],
            EntityKinds.Operation when changedFields.Contains(Field.Name) => [entityId],
            EntityKinds.Schema when changedFields.Contains(Field.Content) =>
                _model.Messages.Values.Where(m => m.SchemaId?.Value == entityId).Select(m => m.Id.Value).ToList(),
            _ => new List<Guid>(),
        };
        return targets.Count == 0 ? ProposalImpactDto.None : new ProposalImpactDto(Scenarios(targets), []);
    }

    /// <summary>The stored scenarios with a step, an assertion or a fault that names one of <paramref name="targets"/>.</summary>
    public IReadOnlyList<ImpactScenarioDto> Naming(IReadOnlyCollection<Guid> targets) => Scenarios(targets);

    /// <summary>
    /// The stored scenarios with steps that run in a handler of a step that publishes one of
    /// <paramref name="messages"/>: the steps that consume the message, and every step nested
    /// in them. A step that only publishes the message is not one of them.
    /// </summary>
    public IReadOnlyList<ImpactScenarioDto> Consuming(IReadOnlyCollection<Guid> messages)
    {
        var found = new List<ImpactScenarioDto>();
        foreach (var (id, name, document) in _scenarios)
        {
            var steps = new List<(string, string, string?)>();
            CollectConsumers(document.Steps, messages, steps);
            if (steps.Count > 0)
                found.Add(new ImpactScenarioDto(id, name, [.. steps.Select(s => s.Item1).Distinct()]));
        }
        return found;
    }

    private void CollectConsumers(IEnumerable<Step> steps, IReadOnlyCollection<Guid> messages, List<(string, string, string?)> found)
    {
        foreach (var step in steps)
        {
            switch (step)
            {
                case CallStep call:
                    CollectConsumers(call.Then, messages, found);
                    CollectConsumers(call.OnError, messages, found);
                    break;
                case PublishStep publish:
                    foreach (var handler in publish.Handlers.Values)
                    {
                        if (messages.Contains(publish.Message.Value))
                            List(handler, found);
                        else
                            CollectConsumers(handler, messages, found);
                    }
                    break;
                case ParallelStep parallel:
                    foreach (var branch in parallel.Branches)
                        CollectConsumers(branch, messages, found);
                    break;
                case BranchStep branch:
                    CollectConsumers(branch.Then, messages, found);
                    CollectConsumers(branch.Else, messages, found);
                    break;
            }
        }
    }

    /// <summary>
    /// Every step of a stored scenario in the order it is written, nested steps too, with its
    /// kind (<c>call</c>, <c>publish</c>, <c>write</c>, <c>parallel</c>, <c>branch</c>) and the
    /// name of what it calls, publishes or writes to. Empty for a scenario that was not read.
    /// </summary>
    public IReadOnlyList<(string Id, string Kind, string? Label)> StepsOf(Guid scenarioId)
    {
        var found = new List<(string, string, string?)>();
        foreach (var (id, _, document) in _scenarios)
        {
            if (id == scenarioId)
                List(document.Steps, found);
        }
        return found;
    }

    private void List(IEnumerable<Step> steps, List<(string, string, string?)> found)
    {
        foreach (var step in steps)
        {
            switch (step)
            {
                case CallStep call:
                    found.Add((call.Id, "call", _model.Operations.TryGetValue(call.Operation, out var operation) ? operation.Name : null));
                    List(call.Then, found);
                    List(call.OnError, found);
                    break;
                case PublishStep publish:
                    found.Add((publish.Id, "publish", _model.Messages.TryGetValue(publish.Message, out var message) ? message.Name : null));
                    foreach (var handler in publish.Handlers.Values)
                        List(handler, found);
                    break;
                case WriteStep write:
                    found.Add((write.Id, "write", _model.Elements.TryGetValue(write.Store, out var store) ? $"{store.Name} · {write.Dataset}" : write.Dataset));
                    break;
                case ParallelStep parallel:
                    found.Add((parallel.Id, "parallel", null));
                    foreach (var branch in parallel.Branches)
                        List(branch, found);
                    break;
                case BranchStep branch:
                    found.Add((branch.Id, "branch", null));
                    List(branch.Then, found);
                    List(branch.Else, found);
                    break;
            }
        }
    }

    private List<ImpactScenarioDto> Scenarios(IReadOnlyCollection<Guid> targets, BindingId? removedBinding = null)
    {
        var found = new List<ImpactScenarioDto>();
        foreach (var (id, name, document) in _scenarios)
        {
            var steps = new List<string>();
            Collect(document.Steps, targets, steps);
            if (removedBinding is { } binding)
                CollectCutOff(document.Steps, binding, steps);
            steps.AddRange(document.Assertions.OfType<EventCountAssertion>()
                .Where(a => a.Target is { } target && targets.Contains(target)).Select(a => "assertion:" + a.Id));
            if (document.Faults.Any(f => targets.Contains(f.Target)))
                steps.Add("fault");
            if (steps.Count > 0)
                found.Add(new ImpactScenarioDto(id, name, [.. steps.Distinct()]));
        }
        return found;
    }

    private static void Collect(IEnumerable<Step> steps, IReadOnlyCollection<Guid> targets, List<string> found)
    {
        foreach (var step in steps)
        {
            switch (step)
            {
                case CallStep call:
                    if (targets.Contains(call.Operation.Value) || targets.Contains(call.From.Value))
                        found.Add(call.Id);
                    Collect(call.Then, targets, found);
                    Collect(call.OnError, targets, found);
                    break;
                case PublishStep publish:
                    if (targets.Contains(publish.Channel.Value) || targets.Contains(publish.Message.Value)
                        || targets.Contains(publish.From.Value) || publish.Handlers.Keys.Any(k => targets.Contains(k.Value)))
                        found.Add(publish.Id);
                    foreach (var handler in publish.Handlers.Values)
                        Collect(handler, targets, found);
                    break;
                case WriteStep write:
                    if (targets.Contains(write.By.Value) || targets.Contains(write.Store.Value))
                        found.Add(write.Id);
                    break;
                case ParallelStep parallel:
                    foreach (var branch in parallel.Branches)
                        Collect(branch, targets, found);
                    break;
                case BranchStep branch:
                    Collect(branch.Then, targets, found);
                    Collect(branch.Else, targets, found);
                    break;
            }
        }
    }

    /// <summary>The publish steps with a handler for a subscription that is reachable only through <paramref name="removed"/>.</summary>
    private void CollectCutOff(IEnumerable<Step> steps, BindingId removed, List<string> found)
    {
        foreach (var step in steps)
        {
            switch (step)
            {
                case CallStep call:
                    CollectCutOff(call.Then, removed, found);
                    CollectCutOff(call.OnError, removed, found);
                    break;
                case PublishStep publish:
                    if (publish.Handlers.Count > 0)
                    {
                        var reached = Reachable(publish.Channel, removed);
                        if (publish.Handlers.Keys.Any(k =>
                                _model.Subscriptions.TryGetValue(k, out var subscription) && !reached.Contains(subscription.ChannelId)))
                            found.Add(publish.Id);
                    }
                    foreach (var handler in publish.Handlers.Values)
                        CollectCutOff(handler, removed, found);
                    break;
                case ParallelStep parallel:
                    foreach (var branch in parallel.Branches)
                        CollectCutOff(branch, removed, found);
                    break;
                case BranchStep branch:
                    CollectCutOff(branch.Then, removed, found);
                    CollectCutOff(branch.Else, removed, found);
                    break;
            }
        }
    }

    /// <summary>The channels a message published to <paramref name="channel"/> reaches when <paramref name="without"/> is gone.</summary>
    private HashSet<ChannelId> Reachable(ChannelId channel, BindingId without)
    {
        var reached = new HashSet<ChannelId> { channel };
        var open = new Queue<ChannelId>([channel]);
        while (open.TryDequeue(out var current))
        {
            foreach (var binding in _model.Bindings.Values)
            {
                if (binding.Id != without && binding.SourceChannelId == current && reached.Add(binding.TargetChannelId))
                    open.Enqueue(binding.TargetChannelId);
            }
        }
        return reached;
    }

    /// <summary>The entities of the model that point at the entity.</summary>
    private List<ImpactEntityDto> Dependents(string entityKind, Guid id)
    {
        var found = new List<ImpactEntityDto>();
        switch (entityKind)
        {
            case EntityKinds.Element:
                found.AddRange(_model.Elements.Values.Where(e => e.ParentId?.Value == id).Select(e => new ImpactEntityDto(EntityKinds.Element, e.Id, e.Name)));
                found.AddRange(_model.Relationships.Values.Where(r => r.FromId.Value == id || r.ToId.Value == id)
                    .Select(r => new ImpactEntityDto("relationship", r.Id, r.Label ?? "")));
                found.AddRange(_model.Operations.Values.Where(o => o.OwnerId.Value == id).Select(o => new ImpactEntityDto(EntityKinds.Operation, o.Id, o.Name)));
                found.AddRange(_model.Subscriptions.Values.Where(s => s.ConsumerId.Value == id)
                    .Select(s => new ImpactEntityDto(EntityKinds.Subscription, s.Id, ContractDecider.SubscriptionLabel(_model, s))));
                found.AddRange(_model.Messages.Values.Where(m => m.ProducerId.Value == id || m.ConsumerIds.Any(c => c.Value == id))
                    .Select(m => new ImpactEntityDto(EntityKinds.Message, m.Id, m.Name)));
                break;
            case EntityKinds.Channel:
                found.AddRange(_model.Subscriptions.Values.Where(s => s.ChannelId.Value == id || s.DeadLetterChannelId?.Value == id)
                    .Select(s => new ImpactEntityDto(EntityKinds.Subscription, s.Id, ContractDecider.SubscriptionLabel(_model, s))));
                found.AddRange(_model.Bindings.Values.Where(b => b.SourceChannelId.Value == id || b.TargetChannelId.Value == id)
                    .Select(b => new ImpactEntityDto(EntityKinds.Binding, b.Id, ContractDecider.BindingLabel(_model, b))));
                break;
            case EntityKinds.Schema:
                found.AddRange(_model.Messages.Values.Where(m => m.SchemaId?.Value == id).Select(m => new ImpactEntityDto(EntityKinds.Message, m.Id, m.Name)));
                break;
        }
        return [.. found.OrderBy(e => e.EntityKind, StringComparer.Ordinal).ThenBy(e => e.Name, StringComparer.Ordinal).ThenBy(e => e.Id)];
    }
}
