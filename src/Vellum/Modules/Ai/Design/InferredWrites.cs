using Deedbox;
using Vellum.Modules.Modelling.Model;
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
    public sealed record Message(Guid Id, string Name, string? Description, Guid ProducerId, Guid[] ConsumerIds) : Addition(Id);
}

/// <summary>Why a set of additions was not applied. Nothing of the set was written.</summary>
internal sealed record AdditionRefused(Guid Id, string Reason, bool Conflict);

/// <summary>
/// Writes what a person accepted from an AI proposal. Each addition goes through the decision
/// that the modelling endpoints use (<see cref="ModelDecider"/>, <see cref="ContractDecider"/>),
/// followed by the decision that records its status and provenance. The status is always
/// <see cref="FactStatus.Inferred"/>: a model's suggestion is not a declared fact because a
/// person accepted it (SDD guarantee 2). A set of additions is one append to the model stream,
/// so it is applied whole or not at all.
/// </summary>
internal static class InferredWrites
{
    /// <summary>The source pointer of a fact that came from a proposal, for example <c>ai:anthropic/model-x#flow-proposal:&lt;id&gt;</c>.</summary>
    public static string Source(ProposalProvider provider, string proposalKind, Guid proposalId) =>
        $"ai:{provider.Kind}/{provider.Model}#{proposalKind}-proposal:{proposalId}";

    /// <param name="additions">In an order in which each addition finds what it references.</param>
    /// <returns>Null when every addition was written.</returns>
    public static async Task<AdditionRefused?> ApplyAsync(
        IEventStore store, Guid projectId, Guid streamId,
        IReadOnlyList<(Addition Addition, double Confidence)> additions, string source, string userId, CancellationToken ct)
    {
        // The acceptance stamp is taken once, so the decision stays a pure function of the state.
        var acceptedBy = Guid.Parse(userId);
        var acceptedAt = DateTimeOffset.UtcNow;
        AdditionRefused? refused = null;

        var result = await store.ForProject(projectId).Decide<ModelState, bool>(streamId, state =>
        {
            refused = null;
            var events = new List<object>();
            foreach (var (addition, confidence) in additions)
            {
                // A request that was applied and whose answer was lost is sent again. The
                // addition is then in the model with this source, and is not written twice.
                if (SourceOf(state, addition.Id) == source)
                    continue;

                var provenance = new Provenance(source, null, confidence, acceptedBy, acceptedAt);
                var decision = Add(state, addition).Then(state, after => Record(after, addition, provenance));
                if (decision is not Decision.Success success)
                {
                    refused = new AdditionRefused(addition.Id, Reason(decision), decision is Decision.Conflict);
                    return decision;
                }

                events.AddRange(success.Value);
                state = success.Value.Aggregate(state, ModelState.Evolve);
            }

            return new Decision.Success(events);
        }, _ => true, ct);

        return result is CommandResult<bool>.Success ? null : refused;
    }

    private static Decision Add(ModelState state, Addition addition) => addition switch
    {
        Addition.Element e => ModelDecider.AddElement(state, new AddElementCommand(
            e.Id, e.Kind, e.Name, e.Description, e.Technology, null, ElementStatus.Current,
            e.ParentId is { } parent ? new ElementId(parent) : null, [])),
        Addition.Relationship r => ModelDecider.AddRelationship(state, new AddRelationshipCommand(r.Id, r.FromId, r.ToId, r.Label, r.Technology, r.Kind)),
        Addition.Operation o => ContractDecider.AddOperation(state, new AddOperationCommand(o.Id, o.OwnerId, o.Name, o.Transport, null, null, 0, RetryPolicy.None)),
        Addition.Channel c => ContractDecider.AddChannel(state, new AddChannelCommand(c.Id, c.Name, c.Kind)),
        Addition.Subscription s => ContractDecider.AddSubscription(state, new AddSubscriptionCommand(s.Id, s.ChannelId, s.ConsumerId, RetryPolicy.None, null)),
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
        Addition.Message => ModelDecider.RecordMessageProvenance(state, addition.Id, FactStatus.Inferred, provenance),
        _ => throw new InvalidOperationException($"Unknown addition {addition.GetType().Name}."),
    };

    private static string? SourceOf(ModelState state, Guid id) =>
        state.Elements.TryGetValue(id, out var element) ? element.Provenance?.Source
        : state.Relationships.TryGetValue(id, out var relationship) ? relationship.Provenance?.Source
        : state.Operations.TryGetValue(id, out var operation) ? operation.Provenance?.Source
        : state.Channels.TryGetValue(id, out var channel) ? channel.Provenance?.Source
        : state.Subscriptions.TryGetValue(id, out var subscription) ? subscription.Provenance?.Source
        : state.Messages.TryGetValue(id, out var message) ? message.Provenance?.Source
        : null;

    private static string Reason(Decision decision) => decision switch
    {
        Decision.Invalid invalid => string.Join("; ", invalid.Errors.Select(e => $"{e.Field}: {e.Message}")),
        Decision.Conflict conflict => conflict.Message,
        Decision.NotFound notFound => notFound.Message,
        _ => "The addition was refused.",
    };
}
