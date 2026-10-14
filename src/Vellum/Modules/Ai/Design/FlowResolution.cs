using Vellum.Modules.Modelling;
using Vellum.Modules.Trace.Engine;
using Vellum.Modules.Trace;

namespace Vellum.Modules.Ai.Design;

/// <summary>
/// A thing that the model proposed as new and that the branch holds already. The proposal
/// uses the existing thing; nothing is created for the alias.
/// </summary>
public sealed record ResolvedToExisting(string Alias, Guid Id, string Name);

/// <summary>
/// The parts of a revised scenario that the model was not shown, because a thing they
/// reference had no alias in the table of the request: it is private-sourced and may not be
/// sent, or it is no longer in the branch. They are kept as they are and put back into the
/// proposal. Their references use aliases that only the server knows
/// (<see cref="FlowAssembler.KeptAlias"/>).
/// </summary>
internal sealed record FlowCarried(
    IReadOnlyList<FlowStepOutput> Steps, IReadOnlyList<FlowFaultOutput> Faults, IReadOnlyList<FlowAssertionOutput> Assertions,
    IReadOnlyDictionary<string, AliasRef> Aliases)
{
    public static readonly FlowCarried None = new([], [], [], new Dictionary<string, AliasRef>());
}

/// <summary>A scenario to revise, split into what the model reads and what stays out of the prompt.</summary>
internal sealed record RevisedFlow(
    IReadOnlyList<FlowStepOutput> Steps, IReadOnlyList<FlowFaultOutput> Faults, IReadOnlyList<FlowAssertionOutput> Assertions,
    FlowCarried Carried);

internal static partial class FlowAssembler
{
    private const string KeptPrefix = "~";

    /// <summary>
    /// The server-side alias of a thing that has no alias in the table. The model never sees
    /// it and cannot guess it: it holds the ID.
    /// </summary>
    public static string KeptAlias(Guid id) => $"{KeptPrefix}{id:N}";

    /// <summary>
    /// Splits the scenario to revise. A step, a fault or an assertion with a reference that has
    /// no alias is carried: it is not sent, never as a placeholder either, and
    /// <see cref="WithCarried"/> puts it back unchanged.
    /// </summary>
    /// <param name="aliasOf">The alias of a thing in the table of the request, or null.</param>
    public static RevisedFlow SplitRevised(Scenario scenario, Func<Guid, string?> aliasOf, ModelState model)
    {
        var labels = TraceModelBuilder.Labels(model);
        string Name(Guid id) => aliasOf(id) ?? KeptAlias(id);
        static bool Kept(string? alias) => alias is not null && alias.StartsWith(KeptPrefix, StringComparison.Ordinal);

        var kept = new Dictionary<string, AliasRef>(StringComparer.Ordinal);
        void Keep(string? alias, string kind)
        {
            if (!Kept(alias))
                return;
            var id = Guid.ParseExact(alias![KeptPrefix.Length..], "N");
            kept[alias] = new AliasRef(kind, id, labels.GetValueOrDefault(id, "(not in the alias table)"));
        }

        // A fault or an assertion names a thing of any of four kinds. A thing that is gone counts as an element.
        string KindOf(Guid id) =>
            model.Operations.ContainsKey(id) ? AliasKinds.Operation
            : model.Channels.ContainsKey(id) ? AliasKinds.Channel
            : model.Subscriptions.ContainsKey(id) ? AliasKinds.Subscription
            : AliasKinds.Element;

        var sent = new List<FlowStepOutput>();
        var carried = new List<FlowStepOutput>();
        foreach (var row in Flatten(scenario, Name))
        {
            var handlerKey = row.Slot == "handler" ? row.SlotKey : null;
            if (!new[] { row.From, row.Operation, row.Channel, row.Message, row.By, row.Store, handlerKey }.Any(Kept))
            {
                sent.Add(row);
                continue;
            }

            Keep(row.From, AliasKinds.Element);
            Keep(row.By, AliasKinds.Element);
            Keep(row.Store, AliasKinds.Element);
            Keep(row.Operation, AliasKinds.Operation);
            Keep(row.Channel, AliasKinds.Channel);
            Keep(row.Message, AliasKinds.Message);
            Keep(handlerKey, AliasKinds.Subscription);
            carried.Add(row);
        }

        var (faults, assertions) = FlattenChecks(scenario, Name);
        foreach (var target in faults.Select(f => f.Target).Concat(assertions.Select(a => a.Target)).Where(Kept))
            Keep(target, KindOf(Guid.ParseExact(target![KeptPrefix.Length..], "N")));

        return new RevisedFlow(
            sent, [.. faults.Where(f => !Kept(f.Target))], [.. assertions.Where(a => !Kept(a.Target))],
            new FlowCarried(carried, [.. faults.Where(f => Kept(f.Target))], [.. assertions.Where(a => Kept(a.Target))], kept));
    }

    /// <summary>
    /// Puts the carried parts back into an answer. A carried step wins over a step of the
    /// answer with the same ID: the model did not see it, so it cannot have revised it.
    /// </summary>
    public static (FlowOutput Output, IReadOnlyDictionary<string, AliasRef> Aliases, List<ScenarioIssue> Issues) WithCarried(
        FlowOutput output, IReadOnlyDictionary<string, AliasRef> aliases, FlowCarried? carried)
    {
        if (carried is null || carried.Steps.Count + carried.Faults.Count + carried.Assertions.Count == 0)
            return (output, aliases, []);

        var issues = new List<ScenarioIssue>();
        var keptSteps = carried.Steps.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var step in output.Steps.Where(s => keptSteps.Contains(s.Id)))
        {
            issues.Add(new(step.Id, "kept-step-not-revised",
                $"Step '{step.Id}' references a thing that was not in the alias table. It is kept as it was; the answer's version of it was left out."));
        }

        var keptAssertions = carried.Assertions.Select(a => a.Id).ToHashSet(StringComparer.Ordinal);
        var merged = new Dictionary<string, AliasRef>(aliases, StringComparer.Ordinal);
        foreach (var (alias, named) in carried.Aliases)
            merged[alias] = named;

        return (output with
        {
            Steps = [.. output.Steps.Where(s => !keptSteps.Contains(s.Id)), .. carried.Steps],
            Faults = [.. output.Faults, .. carried.Faults],
            Assertions = [.. output.Assertions.Where(a => !keptAssertions.Contains(a.Id)), .. carried.Assertions],
        }, merged, issues);
    }

    /// <summary>
    /// Compares every well-formed missing entry with the branch (<see cref="ExistingFacts"/>).
    /// An entry that names a thing the branch holds is removed from <c>missing</c>, and its
    /// alias becomes an alias of the existing thing, so every reference to it resolves there
    /// and nothing is created. An entry that fits more than one thing, or whose name breaks
    /// <see cref="ProposedNames"/>, is dropped and listed. An entry that a person created
    /// already is left alone: it would match itself.
    /// </summary>
    public static (FlowOutput Output, IReadOnlyDictionary<string, AliasRef> Aliases, List<ResolvedToExisting> Resolved, List<RejectedReference> Rejected)
        MatchExisting(FlowOutput output, IReadOnlyDictionary<string, AliasRef> aliases, IReadOnlyDictionary<string, Guid> created, ModelState model)
    {
        var table = new Dictionary<string, AliasRef>(aliases, StringComparer.Ordinal);
        var resolved = new List<ResolvedToExisting>();
        var rejected = new List<RejectedReference>();

        var undecided = new Dictionary<string, FlowMissingOutput>(StringComparer.Ordinal);
        foreach (var entry in output.Missing)
        {
            if (MissingAlias().IsMatch(entry.Alias) && AliasKinds.Contracts.Contains(entry.Kind) && !created.ContainsKey(entry.Alias))
                undecided.TryAdd(entry.Alias, entry);
        }

        var gone = new HashSet<string>(StringComparer.Ordinal);
        for (var changed = true; changed;)
        {
            changed = false;
            foreach (var entry in undecided.Values.ToList())
            {
                // An entry is compared after the entries it references, so that an operation on
                // an owner that turns out to exist is compared with that owner's operations.
                if (MissingReferences(entry).Any(r => r.Alias is not null && r.Alias != entry.Alias && undecided.ContainsKey(r.Alias)))
                    continue;

                undecided.Remove(entry.Alias);
                changed = true;

                Guid? Existing(string? alias, string kind) =>
                    alias is null ? null
                    : table.TryGetValue(alias, out var named) ? named.Kind == kind ? named.Id : null
                    : created.TryGetValue(alias, out var id) ? id
                    : null;

                var thing = new ProposedThing(
                    entry.Kind, entry.Name, entry.ElementKind,
                    Existing(entry.ParentAlias, AliasKinds.Element),
                    entry.ParentAlias is not null && Existing(entry.ParentAlias, AliasKinds.Element) is null,
                    Existing(entry.OwnerAlias, AliasKinds.Element),
                    Existing(entry.ChannelAlias, AliasKinds.Channel),
                    Existing(entry.ConsumerAlias, AliasKinds.Element));

                if (entry.Kind != AliasKinds.Subscription && ProposedNames.Problem(model, entry.Kind, entry.Name, thing.OwnerId) is { } problem)
                {
                    rejected.Add(new($"missing:{entry.Alias}.name", entry.Alias, $"{problem} The entry was dropped."));
                    gone.Add(entry.Alias);
                    continue;
                }

                switch (ExistingFacts.Find(model, thing))
                {
                    case ExistingMatch.One one:
                        table[entry.Alias] = new AliasRef(entry.Kind, one.Id, one.Name);
                        resolved.Add(new ResolvedToExisting(entry.Alias, one.Id, one.Name));
                        gone.Add(entry.Alias);
                        break;
                    case ExistingMatch.Ambiguous several:
                        rejected.Add(new($"missing:{entry.Alias}", entry.Alias,
                            $"More than one existing {entry.Kind} fits ({string.Join(", ", several.Names.Select(n => $"'{n}'"))}). The server does not choose one. The entry was dropped."));
                        gone.Add(entry.Alias);
                        break;
                }
            }
        }

        // The first entry of an alias is the one that was compared. A later one with the same alias goes with it.
        return (output with { Missing = [.. output.Missing.Where(m => !gone.Contains(m.Alias))] }, table, resolved, rejected);
    }
}
