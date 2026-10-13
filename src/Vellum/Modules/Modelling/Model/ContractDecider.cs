using Vellum.Modules.Trace.Engine;
using Vellum.Shared;
using Decision = Vellum.Shared.CommandResult<System.Collections.Generic.IReadOnlyList<object>>;

namespace Vellum.Modules.Modelling.Model;

public sealed record AddOperationCommand(
    OperationId Id, ElementId OwnerId, string Name, Transport Transport,
    SchemaId? RequestSchemaId, SchemaId? ResponseSchemaId,
    int TimeoutMs, RetryPolicy Retry);

/// <summary>A null member leaves the value as it is. The schema IDs need a flag, because null clears them.</summary>
public sealed record UpdateOperationCommand(
    OperationId OperationId,
    string? Name = null, ElementId? OwnerId = null, Transport? Transport = null,
    SchemaId? RequestSchemaId = null, bool SetRequestSchemaId = false,
    SchemaId? ResponseSchemaId = null, bool SetResponseSchemaId = false,
    int? TimeoutMs = null, RetryPolicy? Retry = null);

public sealed record AddChannelCommand(ChannelId Id, string Name, ChannelKind Kind);

public sealed record UpdateChannelCommand(ChannelId ChannelId, string? Name = null, ChannelKind? Kind = null);

public sealed record AddSubscriptionCommand(
    SubscriptionId Id, ChannelId ChannelId, ElementId ConsumerId,
    RetryPolicy Retry, ChannelId? DeadLetterChannelId);

public sealed record UpdateSubscriptionCommand(
    SubscriptionId SubscriptionId,
    ChannelId? ChannelId = null, ElementId? ConsumerId = null, RetryPolicy? Retry = null,
    ChannelId? DeadLetterChannelId = null, bool SetDeadLetterChannelId = false);

/// <summary>
/// Decisions for the contracts a scenario plays against: operations, channels and
/// subscriptions. A contract never points at something that is not in the model
/// (SDD guarantee 3). Schema references are checked by the caller, because schemas live in
/// their own streams.
/// </summary>
public static class ContractDecider
{
    /// <summary>
    /// The most operations, channels or subscriptions one model holds. A contract list is read
    /// from the stored state of the branch, so the state stays bounded. An import document has
    /// the same limit per kind.
    /// </summary>
    public const int MaxPerKind = 5_000;

    public static Decision AddOperation(ModelState state, AddOperationCommand cmd)
    {
        if (state.RefuseNewId(cmd.Id) is { } idRefusal)
            return idRefusal;
        if (state.Operations.Count >= MaxPerKind)
            return new Decision.Conflict($"A model holds at most {MaxPerKind} operations. Remove one before you add another.");

        var operation = new OperationState(
            cmd.Id, cmd.OwnerId, cmd.Name, cmd.Transport,
            cmd.RequestSchemaId, cmd.ResponseSchemaId, cmd.TimeoutMs, cmd.Retry);
        if (Validate(state, operation) is { } error)
            return new Decision.Invalid([error]);

        return new Decision.Success([new ModelEvent.OperationAdded(
            cmd.Id, cmd.OwnerId, cmd.Name, cmd.Transport,
            cmd.RequestSchemaId, cmd.ResponseSchemaId, cmd.TimeoutMs, cmd.Retry)]);
    }

    public static Decision UpdateOperation(ModelState state, UpdateOperationCommand cmd)
    {
        if (!state.Operations.TryGetValue(cmd.OperationId, out var current))
            return new Decision.NotFound("Operation not found");

        var updated = current with
        {
            Name = cmd.Name ?? current.Name,
            OwnerId = cmd.OwnerId ?? current.OwnerId,
            Transport = cmd.Transport ?? current.Transport,
            RequestSchemaId = cmd.SetRequestSchemaId ? cmd.RequestSchemaId : current.RequestSchemaId,
            ResponseSchemaId = cmd.SetResponseSchemaId ? cmd.ResponseSchemaId : current.ResponseSchemaId,
            TimeoutMs = cmd.TimeoutMs ?? current.TimeoutMs,
            Retry = cmd.Retry ?? current.Retry,
        };
        if (Validate(state, updated) is { } error)
            return new Decision.Invalid([error]);
        if (updated == current)
            return new Decision.Success([]);

        return new Decision.Success([new ModelEvent.OperationChanged(
            updated.Id, updated.OwnerId, updated.Name, updated.Transport,
            updated.RequestSchemaId, updated.ResponseSchemaId, updated.TimeoutMs, updated.Retry)]);
    }

    public static Decision RemoveOperation(ModelState state, OperationId operationId) =>
        state.Operations.ContainsKey(operationId)
            ? new Decision.Success([new ModelEvent.OperationRemoved(operationId)])
            : new Decision.NotFound("Operation not found");

    public static Decision RecordOperationProvenance(
        ModelState state, OperationId operationId, FactStatus status, Provenance? provenance)
    {
        if (!state.Operations.TryGetValue(operationId, out var operation))
            return new Decision.NotFound("Operation not found");
        if (FactRules.Validate(status, provenance) is { } error)
            return new Decision.Invalid([error]);
        if (operation.FactStatus == status && SameClaim(operation.Provenance, provenance))
            return new Decision.Success([]);

        return new Decision.Success([new ModelEvent.OperationProvenanceRecorded(operationId, status, provenance)]);
    }

    public static Decision AddChannel(ModelState state, AddChannelCommand cmd)
    {
        if (state.RefuseNewId(cmd.Id) is { } idRefusal)
            return idRefusal;
        if (state.Channels.Count >= MaxPerKind)
            return new Decision.Conflict($"A model holds at most {MaxPerKind} channels. Remove one before you add another.");

        if (Validate(new ChannelState(cmd.Id, cmd.Name, cmd.Kind)) is { } error)
            return new Decision.Invalid([error]);

        return new Decision.Success([new ModelEvent.ChannelAdded(cmd.Id, cmd.Name, cmd.Kind)]);
    }

    public static Decision UpdateChannel(ModelState state, UpdateChannelCommand cmd)
    {
        if (!state.Channels.TryGetValue(cmd.ChannelId, out var current))
            return new Decision.NotFound("Channel not found");

        var updated = current with { Name = cmd.Name ?? current.Name, Kind = cmd.Kind ?? current.Kind };
        if (Validate(updated) is { } error)
            return new Decision.Invalid([error]);
        if (updated == current)
            return new Decision.Success([]);

        return new Decision.Success([new ModelEvent.ChannelChanged(updated.Id, updated.Name, updated.Kind)]);
    }

    /// <summary>A channel that a subscription listens to or dead-letters to stays.</summary>
    public static Decision RemoveChannel(ModelState state, ChannelId channelId)
    {
        if (!state.Channels.TryGetValue(channelId, out var channel))
            return new Decision.NotFound("Channel not found");

        var references = state.Subscriptions.Values
            .Where(s => s.ChannelId == channelId || s.DeadLetterChannelId == channelId)
            .OrderBy(s => s.Id.Value)
            .Select(s => Describe(state, s))
            .ToList();
        if (references.Count > 0)
        {
            return new Decision.Conflict(
                $"Channel '{channel.Name}' cannot be removed. It is referenced by {string.Join(", ", references)}. " +
                "Remove or repoint them first.");
        }

        return new Decision.Success([new ModelEvent.ChannelRemoved(channelId)]);
    }

    public static Decision RecordChannelProvenance(
        ModelState state, ChannelId channelId, FactStatus status, Provenance? provenance)
    {
        if (!state.Channels.TryGetValue(channelId, out var channel))
            return new Decision.NotFound("Channel not found");
        if (FactRules.Validate(status, provenance) is { } error)
            return new Decision.Invalid([error]);
        if (channel.FactStatus == status && SameClaim(channel.Provenance, provenance))
            return new Decision.Success([]);

        return new Decision.Success([new ModelEvent.ChannelProvenanceRecorded(channelId, status, provenance)]);
    }

    public static Decision AddSubscription(ModelState state, AddSubscriptionCommand cmd)
    {
        if (state.RefuseNewId(cmd.Id) is { } idRefusal)
            return idRefusal;
        if (state.Subscriptions.Count >= MaxPerKind)
            return new Decision.Conflict($"A model holds at most {MaxPerKind} subscriptions. Remove one before you add another.");

        var subscription = new SubscriptionState(
            cmd.Id, cmd.ChannelId, cmd.ConsumerId, cmd.Retry, cmd.DeadLetterChannelId);
        if (Validate(state, subscription) is { } error)
            return new Decision.Invalid([error]);

        return new Decision.Success([new ModelEvent.SubscriptionAdded(
            cmd.Id, cmd.ChannelId, cmd.ConsumerId, cmd.Retry, cmd.DeadLetterChannelId)]);
    }

    public static Decision UpdateSubscription(ModelState state, UpdateSubscriptionCommand cmd)
    {
        if (!state.Subscriptions.TryGetValue(cmd.SubscriptionId, out var current))
            return new Decision.NotFound("Subscription not found");

        var updated = current with
        {
            ChannelId = cmd.ChannelId ?? current.ChannelId,
            ConsumerId = cmd.ConsumerId ?? current.ConsumerId,
            Retry = cmd.Retry ?? current.Retry,
            DeadLetterChannelId = cmd.SetDeadLetterChannelId ? cmd.DeadLetterChannelId : current.DeadLetterChannelId,
        };
        if (Validate(state, updated) is { } error)
            return new Decision.Invalid([error]);
        if (updated == current)
            return new Decision.Success([]);

        return new Decision.Success([new ModelEvent.SubscriptionChanged(
            updated.Id, updated.ChannelId, updated.ConsumerId, updated.Retry, updated.DeadLetterChannelId)]);
    }

    public static Decision RemoveSubscription(ModelState state, SubscriptionId subscriptionId) =>
        state.Subscriptions.ContainsKey(subscriptionId)
            ? new Decision.Success([new ModelEvent.SubscriptionRemoved(subscriptionId)])
            : new Decision.NotFound("Subscription not found");

    public static Decision RecordSubscriptionProvenance(
        ModelState state, SubscriptionId subscriptionId, FactStatus status, Provenance? provenance)
    {
        if (!state.Subscriptions.TryGetValue(subscriptionId, out var subscription))
            return new Decision.NotFound("Subscription not found");
        if (FactRules.Validate(status, provenance) is { } error)
            return new Decision.Invalid([error]);
        if (subscription.FactStatus == status && SameClaim(subscription.Provenance, provenance))
            return new Decision.Success([]);

        return new Decision.Success([new ModelEvent.SubscriptionProvenanceRecorded(subscriptionId, status, provenance)]);
    }

    /// <summary>
    /// The contracts that name one of <paramref name="elementIds"/>: operations they serve and
    /// subscriptions they consume. Sorted, so the text of a refusal is stable.
    /// </summary>
    public static IReadOnlyList<string> ReferencesToElements(ModelState state, IReadOnlySet<ElementId> elementIds) =>
    [
        .. state.Operations.Values
            .Where(o => elementIds.Contains(o.OwnerId))
            .OrderBy(o => o.Id.Value)
            .Select(Describe),
        .. state.Subscriptions.Values
            .Where(s => elementIds.Contains(s.ConsumerId))
            .OrderBy(s => s.Id.Value)
            .Select(s => Describe(state, s)),
    ];

    /// <summary>The operations and messages that use a schema, for the refusal to delete it.</summary>
    public static IReadOnlyList<string> ReferencesToSchema(ModelState state, SchemaId schemaId) =>
    [
        .. state.Operations.Values
            .Where(o => o.RequestSchemaId == schemaId || o.ResponseSchemaId == schemaId)
            .OrderBy(o => o.Id.Value)
            .Select(Describe),
        .. state.Messages.Values
            .Where(m => m.SchemaId == schemaId)
            .OrderBy(m => m.Id.Value)
            .Select(m => $"message '{m.Name}' ({m.Id})"),
    ];

    /// <summary>Every schema that an operation or a message of <paramref name="state"/> names.</summary>
    public static IReadOnlySet<Guid> ReferencedSchemaIds(ModelState state)
    {
        var ids = new HashSet<Guid>();
        foreach (var operation in state.Operations.Values)
        {
            if (operation.RequestSchemaId is { } request) ids.Add(request);
            if (operation.ResponseSchemaId is { } response) ids.Add(response);
        }
        foreach (var message in state.Messages.Values)
        {
            if (message.SchemaId is { } schema) ids.Add(schema);
        }
        return ids;
    }

    /// <summary>
    /// Every contract reference in <paramref name="state"/> that points at nothing. A merge
    /// builds its result from two branches, so it checks the whole result with this.
    /// </summary>
    public static IReadOnlyList<string> DanglingReferences(ModelState state)
    {
        var problems = new List<string>();

        foreach (var operation in state.Operations.Values.OrderBy(o => o.Id.Value))
        {
            if (!state.Elements.ContainsKey(operation.OwnerId))
                problems.Add($"{Describe(operation)} belongs to element {operation.OwnerId}, which does not exist");
        }

        foreach (var subscription in state.Subscriptions.Values.OrderBy(s => s.Id.Value))
        {
            var name = Describe(state, subscription);
            if (!state.Channels.ContainsKey(subscription.ChannelId))
                problems.Add($"{name} listens to channel {subscription.ChannelId}, which does not exist");
            if (!state.Elements.ContainsKey(subscription.ConsumerId))
                problems.Add($"{name} has consumer {subscription.ConsumerId}, which does not exist");
            if (subscription.DeadLetterChannelId is { } deadLetter && !state.Channels.ContainsKey(deadLetter))
                problems.Add($"{name} dead-letters to channel {deadLetter}, which does not exist");
        }

        return problems;
    }

    public static string Describe(OperationState operation) => $"operation '{operation.Name}' ({operation.Id})";

    public static string Describe(ModelState state, SubscriptionState subscription) =>
        $"subscription '{SubscriptionLabel(state, subscription)}' ({subscription.Id})";

    /// <summary>A subscription has no name of its own; it reads as "consumer on channel".</summary>
    public static string SubscriptionLabel(ModelState state, SubscriptionState subscription)
    {
        var consumer = state.Elements.TryGetValue(subscription.ConsumerId, out var element)
            ? element.Name
            : subscription.ConsumerId.ToString();
        var channel = state.Channels.TryGetValue(subscription.ChannelId, out var found)
            ? found.Name
            : subscription.ChannelId.ToString();
        return $"{consumer} on {channel}";
    }

    private static ValidationError? Validate(ModelState state, OperationState operation)
    {
        if (string.IsNullOrWhiteSpace(operation.Name))
            return new ValidationError("name", "Name is required");
        if (TextRule.Line("name", operation.Name) is { } nameError)
            return nameError;
        if (!Enum.IsDefined(operation.Transport))
            return new ValidationError("transport", "Transport must be http, grpc or command");
        if (!state.Elements.ContainsKey(operation.OwnerId))
            return new ValidationError("ownerId", "Owner element not found");
        if (operation.TimeoutMs is < 0 or > TraceLimits.MaxDurationMs)
            return new ValidationError("timeoutMs", $"Timeout must be between 0 and {TraceLimits.MaxDurationMs} ms");
        return Validate(operation.Retry);
    }

    private static ValidationError? Validate(ChannelState channel)
    {
        if (string.IsNullOrWhiteSpace(channel.Name))
            return new ValidationError("name", "Name is required");
        if (TextRule.Line("name", channel.Name) is { } nameError)
            return nameError;
        if (!Enum.IsDefined(channel.Kind))
            return new ValidationError("kind", "Kind must be topic, queue or exchange");
        return null;
    }

    private static ValidationError? Validate(ModelState state, SubscriptionState subscription)
    {
        if (!state.Channels.ContainsKey(subscription.ChannelId))
            return new ValidationError("channelId", "Channel not found");
        if (!state.Elements.ContainsKey(subscription.ConsumerId))
            return new ValidationError("consumerId", "Consumer element not found");
        if (subscription.DeadLetterChannelId is { } deadLetter)
        {
            if (!state.Channels.ContainsKey(deadLetter))
                return new ValidationError("deadLetterChannelId", "Dead-letter channel not found");
            if (deadLetter == subscription.ChannelId)
                return new ValidationError("deadLetterChannelId", "The dead-letter channel must differ from the channel");
        }
        return Validate(subscription.Retry);
    }

    private static ValidationError? Validate(RetryPolicy? retry)
    {
        if (retry is null)
            return new ValidationError("retry", "Retry policy is required");
        if (retry.MaxAttempts is < 1 or > TraceLimits.MaxAttempts)
            return new ValidationError("retry.maxAttempts", $"Attempts must be between 1 and {TraceLimits.MaxAttempts}");
        if (retry.BackoffMs is < 0 or > TraceLimits.MaxDurationMs)
            return new ValidationError("retry.backoffMs", $"Backoff must be between 0 and {TraceLimits.MaxDurationMs} ms");
        return null;
    }

    /// <summary>Acceptance stamps change on every call and must not by themselves produce an event.</summary>
    private static bool SameClaim(Provenance? a, Provenance? b) =>
        a?.Source == b?.Source && a?.Revision == b?.Revision && a?.Confidence == b?.Confidence;
}

public static class DecisionChain
{
    /// <summary>
    /// Runs <paramref name="next"/> on the state after the events of <paramref name="first"/>
    /// and returns both event lists, or the first failure.
    /// </summary>
    public static Decision Then(this Decision first, ModelState state, Func<ModelState, Decision> next)
    {
        if (first is not Decision.Success head)
            return first;

        var second = next(head.Value.Aggregate(state, ModelState.Evolve));
        if (second is not Decision.Success tail)
            return second;

        return new Decision.Success([.. head.Value, .. tail.Value]);
    }
}
