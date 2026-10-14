using Deedbox;
using Vellum.Modules.Identity.AgentTokens;
using Vellum.Modules.Modelling.Model;
using Vellum.Modules.Workspaces.Authorization;
using Vellum.Shared;
using Decision = Vellum.Shared.CommandResult<System.Collections.Generic.IReadOnlyList<object>>;

namespace Vellum.Modules.Modelling.Facts;

/// <summary>The truth label of one fact after a person changed it.</summary>
/// <param name="EntityType"><c>element</c>, <c>relationship</c>, <c>message</c>, <c>operation</c>, <c>channel</c>, <c>subscription</c> or <c>binding</c>.</param>
public sealed record FactLabelDto(string EntityType, Guid EntityId, string FactStatus, ProvenanceDto? Provenance);

/// <summary>What a person does to the truth label of a fact. There is no other change: a person cannot type a label.</summary>
public enum FactLabelAction
{
    /// <summary>An inferred, observed-in-source or unknown fact becomes declared.</summary>
    AcceptAsDeclared,

    /// <summary>A declared fact becomes unknown.</summary>
    MarkUnknown,
}

/// <summary>
/// The two label changes a person makes by hand (SDD guarantee 2). "Accept as declared" turns
/// an inferred, observed-in-source or unknown fact into a declared one; "Mark as unknown"
/// turns a declared fact into an unknown one. Each keeps the source, the source revision and
/// the confidence of the fact, and the server stamps the person and the time. Neither can
/// give <c>measured</c> or <c>observed-in-source</c>: those labels claim evidence, and only
/// an import or a measured result gives evidence (guarantee 8).
/// </summary>
public static class FactLabels
{
    public static readonly IReadOnlyList<string> EntityTypes =
        ["element", "relationship", "message", "operation", "channel", "subscription", "binding"];

    /// <summary>Maps <c>POST /facts/{entityType}/{entityId}/accept</c> and <c>/mark-unknown</c> on the project group.</summary>
    public static void Map(RouteGroupBuilder project)
    {
        var facts = project.MapGroup("/facts").WithTags("Facts");
        facts.MapPost("/{entityType}/{entityId:guid}/accept", (
            Guid projectId, string entityType, Guid entityId, Guid? branchId,
            HttpContext http, IEventStore store, TimeProvider time, CancellationToken ct) =>
            HandleAsync(http, store, time, projectId, entityType, entityId, branchId, FactLabelAction.AcceptAsDeclared, ct))
            .RequireHuman()
            .Produces<FactLabelDto>();
        facts.MapPost("/{entityType}/{entityId:guid}/mark-unknown", (
            Guid projectId, string entityType, Guid entityId, Guid? branchId,
            HttpContext http, IEventStore store, TimeProvider time, CancellationToken ct) =>
            HandleAsync(http, store, time, projectId, entityType, entityId, branchId, FactLabelAction.MarkUnknown, ct))
            .RequireHuman()
            .Produces<FactLabelDto>();
    }

    private static Task<IResult> HandleAsync(
        HttpContext http, IEventStore store, TimeProvider time, Guid projectId, string entityType, Guid entityId, Guid? branchId,
        FactLabelAction action, CancellationToken ct) =>
        ModelBranches.RunAsync(http, projectId, branchId, WorkspaceRole.Editor, async (streamId, userId) =>
        {
            if (!EntityTypes.Contains(entityType))
                return ResultExtensions.Invalid("entityType", $"Must be one of: {string.Join(", ", EntityTypes)}");

            // The stamp is taken once, so the decision stays a pure function of the state.
            var (actor, at) = (Guid.Parse(userId), time.GetUtcNow());
            var result = await store.ForProject(projectId).Decide<ModelState, FactLabelDto>(
                streamId,
                state => Decide(state, entityType, entityId, action, actor, at),
                state => Read(state, entityType, entityId)!,
                ct);
            return result.ToHttpResult();
        }, ct);

    /// <summary>The events that change the label, or the refusal. The fact keeps its source, source revision and confidence.</summary>
    public static Decision Decide(
        ModelState state, string entityType, Guid entityId, FactLabelAction action, Guid actor, DateTimeOffset at)
    {
        if (Current(state, entityType, entityId) is not var (status, provenance))
            return new Decision.NotFound($"{char.ToUpperInvariant(entityType[0])}{entityType[1..]} not found");

        FactStatus next;
        switch (action)
        {
            case FactLabelAction.AcceptAsDeclared:
                if (status is not (FactStatus.Inferred or FactStatus.ObservedInSource or FactStatus.Unknown))
                {
                    return new Decision.Conflict(
                        $"Only an inferred, observed-in-source or unknown fact is accepted as declared. This {entityType} is {status.ToApiString()}.");
                }
                next = FactStatus.Declared;
                break;
            case FactLabelAction.MarkUnknown:
                if (status != FactStatus.Declared)
                    return new Decision.Conflict($"Only a declared fact is marked as unknown. This {entityType} is {status.ToApiString()}.");
                next = FactStatus.Unknown;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(action));
        }

        var stamped = new Provenance(provenance?.Source, provenance?.Revision, provenance?.Confidence, actor, at);
        return entityType switch
        {
            "element" => ModelDecider.RecordElementProvenance(state, new ElementId(entityId), next, stamped),
            "relationship" => ModelDecider.RecordRelationshipProvenance(state, new RelationshipId(entityId), next, stamped),
            "message" => ModelDecider.RecordMessageProvenance(state, new MessageId(entityId), next, stamped),
            "operation" => ContractDecider.RecordOperationProvenance(state, new OperationId(entityId), next, stamped),
            "channel" => ContractDecider.RecordChannelProvenance(state, new ChannelId(entityId), next, stamped),
            "subscription" => ContractDecider.RecordSubscriptionProvenance(state, new SubscriptionId(entityId), next, stamped),
            "binding" => ContractDecider.RecordBindingProvenance(state, new BindingId(entityId), next, stamped),
            _ => throw new ArgumentOutOfRangeException(nameof(entityType)),
        };
    }

    private static (FactStatus Status, Provenance? Provenance)? Current(ModelState state, string entityType, Guid id) => entityType switch
    {
        "element" => state.Elements.TryGetValue(new ElementId(id), out var e) ? (e.FactStatus, e.Provenance) : null,
        "relationship" => state.Relationships.TryGetValue(new RelationshipId(id), out var r) ? (r.FactStatus, r.Provenance) : null,
        "message" => state.Messages.TryGetValue(new MessageId(id), out var m) ? (m.FactStatus, m.Provenance) : null,
        "operation" => state.Operations.TryGetValue(new OperationId(id), out var o) ? (o.FactStatus, o.Provenance) : null,
        "channel" => state.Channels.TryGetValue(new ChannelId(id), out var c) ? (c.FactStatus, c.Provenance) : null,
        "subscription" => state.Subscriptions.TryGetValue(new SubscriptionId(id), out var s) ? (s.FactStatus, s.Provenance) : null,
        "binding" => state.Bindings.TryGetValue(new BindingId(id), out var b) ? (b.FactStatus, b.Provenance) : null,
        _ => null,
    };

    private static FactLabelDto? Read(ModelState state, string entityType, Guid id) =>
        Current(state, entityType, id) is var (status, provenance)
            ? new FactLabelDto(entityType, id, status.ToApiString(), ProvenanceMapper.ToDto(provenance))
            : null;
}
