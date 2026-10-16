using Deedbox;
using Vellum.Modules.Modelling;
using Vellum.Modules.Trace.Engine;
using Vellum.Shared;
using Decision = Vellum.Shared.CommandResult<System.Collections.Generic.IReadOnlyList<object>>;

namespace Vellum.Modules.Ai.Design;

/// <summary>One thing an accepted proposal adds to the model. The ID is chosen by the server.</summary>
internal abstract record Addition(Guid Id)
{
    public sealed record Element(Guid Id, ElementKind Kind, string Name, string? Description, string? Technology, Guid? ParentId) : Addition(Id);
    public sealed record Relationship(Guid Id, Guid FromId, Guid ToId, string? Label, string? Technology, RelationshipKind Kind) : Addition(Id);
    public sealed record Operation(Guid Id, Guid OwnerId, string Name, Transport Transport) : Addition(Id);
    public sealed record Channel(Guid Id, string Name, ChannelKind Kind) : Addition(Id);
    public sealed record Subscription(Guid Id, Guid ChannelId, Guid ConsumerId) : Addition(Id);
    public sealed record Binding(Guid Id, Guid SourceChannelId, Guid TargetChannelId, string? RoutingKey) : Addition(Id);
    public sealed record Message(Guid Id, string Name, string? Description, Guid ProducerId, Guid[] ConsumerIds) : Addition(Id);
}

/// <summary>Why a set of additions was not applied. Nothing of the set was written.</summary>
internal sealed record AdditionRefused(Guid Id, string Reason, bool Conflict);

/// <summary>
/// Writes what a person accepted from an AI proposal. Each addition goes through the decision
/// that the modelling endpoints use (<see cref="ModelDecider"/>, <see cref="ContractDecider"/>),
/// followed by the decision that records its status and provenance. The status is always
/// <see cref="FactStatus.Inferred"/>: a model's suggestion is not a declared fact because a
/// person accepted it. A set of additions is one append to the model stream,
/// so it is applied whole or not at all, and the append commits with the proposal row that
/// records the decision.
/// </summary>
internal static class InferredWrites
{
    /// <summary>
    /// The source pointer of a fact that came from a proposal: <c>ai:anthropic/model-x#flow-proposal:&lt;id&gt;</c>
    /// for one the assistant generated, <c>agent-token:&lt;token id&gt;#flow-proposal:&lt;id&gt;</c> for one that was submitted.
    /// </summary>
    /// <param name="origin">Who submitted the proposal. Null for a proposal the assistant generated.</param>
    public static string Source(ProposalProvider provider, string? origin, string proposalKind, Guid proposalId) =>
        $"{origin ?? $"ai:{provider.Kind}/{provider.Model}"}#{proposalKind}-proposal:{proposalId}";

    /// <summary>
    /// Appends the additions inside the transaction of <paramref name="pinned"/>, a store from
    /// <c>UseDbContext</c>. The caller commits, so the append and the caller's own rows are
    /// one unit. An addition that the branch holds already under another ID
    /// (<see cref="ExistingFacts"/>) is refused as a conflict that names the existing thing:
    /// the model can have changed since the proposal was made.
    /// </summary>
    /// <param name="additions">In an order in which each addition finds what it references.</param>
    /// <returns>Null when every addition was written.</returns>
    public static async Task<AdditionRefused?> ApplyAsync(
        IEventStore pinned, Guid streamId,
        IReadOnlyList<(Addition Addition, double Confidence)> additions, string source, string userId, DateTimeOffset acceptedAt,
        CancellationToken ct, IReadOnlyDictionary<Guid, string>? sources = null)
    {
        // The acceptance stamp is taken once, by the caller, so the decision stays a pure
        // function of the state.
        var acceptedBy = Guid.Parse(userId);
        AdditionRefused? refused = null;

        var result = await pinned.DecideInTransaction<ModelState, bool>(streamId, state =>
        {
            refused = null;
            var events = new List<object>();
            foreach (var (addition, confidence) in additions)
            {
                // An item read from a document cites its lines: that citation is its source pointer.
                var provenance = new Provenance(sources?.GetValueOrDefault(addition.Id) ?? source, null, confidence, acceptedBy, acceptedAt);
                var (decision, refusal) = Decide(state, addition, provenance);
                if (decision is not Decision.Success success)
                {
                    refused = refusal;
                    return decision;
                }

                events.AddRange(success.Value);
                state = success.Value.Aggregate(state, ModelState.Evolve);
            }

            return new Decision.Success(events);
        }, _ => true, ct);

        return result is CommandResult<bool>.Success ? null : refused;
    }

    /// <summary>
    /// The decision that an accept makes for one addition against <paramref name="state"/>:
    /// the check for a thing the model holds already (<see cref="ExistingFacts"/>), the
    /// modelling decision with its rules (<see cref="ModelInvariants"/>), and the record of the
    /// status and provenance. A submitted proposal is checked with this same decision, so an
    /// item that an accept would refuse is dropped when it is submitted.
    /// </summary>
    /// <returns>The decision, and why the addition is refused when it is not a success.</returns>
    public static (Decision Decision, AdditionRefused? Refused) Decide(ModelState state, Addition addition, Provenance provenance)
    {
        if (ExistingFacts.Find(state, Describe(addition), except: addition.Id) is var match and not ExistingMatch.None)
        {
            var message = match is ExistingMatch.One one
                ? $"The model holds this already: '{one.Name}' ({one.Id})."
                : $"The model holds more than one thing that fits ({string.Join(", ", ((ExistingMatch.Ambiguous)match).Names.Select(n => $"'{n}'"))}).";
            return (new Decision.Conflict(message), new AdditionRefused(addition.Id, message, true));
        }

        var decision = Add(state, addition).Then(state, after => Record(after, addition, provenance));
        return decision is Decision.Success
            ? (decision, null)
            : (decision, new AdditionRefused(addition.Id, Reason(decision), decision is Decision.Conflict));
    }

    /// <summary>The addition as <see cref="ExistingFacts"/> compares it. Every reference of an addition is an ID.</summary>
    private static ProposedThing Describe(Addition addition) => addition switch
    {
        Addition.Element e => new ProposedThing(AliasKinds.Element, e.Name, e.Kind.ToString(), e.ParentId),
        Addition.Operation o => new ProposedThing(AliasKinds.Operation, o.Name, OwnerId: o.OwnerId),
        Addition.Channel c => new ProposedThing(AliasKinds.Channel, c.Name),
        Addition.Subscription s => new ProposedThing(AliasKinds.Subscription, "", ChannelId: s.ChannelId, ConsumerId: s.ConsumerId),
        Addition.Binding b => new ProposedThing(AliasKinds.Binding, "",
            SourceChannelId: b.SourceChannelId, TargetChannelId: b.TargetChannelId, RoutingKey: b.RoutingKey),
        Addition.Message m => new ProposedThing(AliasKinds.Message, m.Name),
        Addition.Relationship r => new ProposedThing(AliasKinds.Relationship, "",
            FromId: r.FromId, ToId: r.ToId, Label: r.Label, RelationshipKind: r.Kind.ToString()),
        _ => throw new InvalidOperationException($"Unknown addition {addition.GetType().Name}."),
    };

    private static Decision Add(ModelState state, Addition addition) => addition switch
    {
        Addition.Element e => ModelDecider.AddElement(state, new AddElementCommand(
            e.Id, e.Kind, e.Name, e.Description, e.Technology, null, ElementStatus.Current,
            e.ParentId is { } parent ? new ElementId(parent) : null, [])),
        Addition.Relationship r => ModelDecider.AddRelationship(state, new AddRelationshipCommand(r.Id, r.FromId, r.ToId, r.Label, r.Technology, r.Kind)),
        Addition.Operation o => ContractDecider.AddOperation(state, new AddOperationCommand(o.Id, o.OwnerId, o.Name, o.Transport, null, null, 0, RetryPolicy.None)),
        Addition.Channel c => ContractDecider.AddChannel(state, new AddChannelCommand(c.Id, c.Name, c.Kind)),
        Addition.Subscription s => ContractDecider.AddSubscription(state, new AddSubscriptionCommand(s.Id, s.ChannelId, s.ConsumerId, RetryPolicy.None, null)),
        Addition.Binding b => ContractDecider.AddBinding(state, new AddBindingCommand(b.Id, b.SourceChannelId, b.TargetChannelId, b.RoutingKey)),
        Addition.Message m => ModelDecider.AddMessage(state, new AddMessageCommand(m.Id, m.Name, m.Description, m.ProducerId, m.ConsumerIds.ToElementIds(), null, [])),
        _ => throw new InvalidOperationException($"Unknown addition {addition.GetType().Name}."),
    };

    private static Decision Record(ModelState state, Addition addition, Provenance provenance) => addition switch
    {
        Addition.Element => ModelDecider.RecordElementProvenance(state, addition.Id, FactStatus.Inferred, provenance),
        Addition.Relationship => ModelDecider.RecordRelationshipProvenance(state, addition.Id, FactStatus.Inferred, provenance),
        Addition.Operation => ContractDecider.RecordOperationProvenance(state, addition.Id, FactStatus.Inferred, provenance),
        Addition.Channel => ContractDecider.RecordChannelProvenance(state, addition.Id, FactStatus.Inferred, provenance),
        Addition.Subscription => ContractDecider.RecordSubscriptionProvenance(state, addition.Id, FactStatus.Inferred, provenance),
        Addition.Binding => ContractDecider.RecordBindingProvenance(state, addition.Id, FactStatus.Inferred, provenance),
        Addition.Message => ModelDecider.RecordMessageProvenance(state, addition.Id, FactStatus.Inferred, provenance),
        _ => throw new InvalidOperationException($"Unknown addition {addition.GetType().Name}."),
    };

    private static string Reason(Decision decision) => decision switch
    {
        Decision.Invalid invalid => string.Join("; ", invalid.Errors.Select(e => $"{e.Field}: {e.Message}")),
        Decision.Conflict conflict => conflict.Message,
        Decision.NotFound notFound => notFound.Message,
        _ => "The addition was refused.",
    };
}
