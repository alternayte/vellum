using Vellum.Modules.Trace.Engine;
using Vellum.Shared;

namespace Vellum.Modules.Modelling;

/// <summary>
/// The rules a model holds to, each written once. A decider applies the rule of the fact it
/// is about to write (<see cref="Containment"/>, <see cref="Operation"/> and the rest) and
/// answers with the field. A write that no decider made, a merge or an import, applies its
/// events with <see cref="Apply"/> and refuses when <see cref="NewViolations"/> finds a rule
/// that the result breaks and the model before did not. A new rule goes here, in the rule of
/// its fact, and both kinds of write enforce it.
/// </summary>
public static class ModelInvariants
{
    private static readonly IReadOnlyDictionary<ElementKind, ElementKind?> ValidParentKinds = new Dictionary<ElementKind, ElementKind?>
    {
        [ElementKind.Actor] = null,
        [ElementKind.System] = null,
        [ElementKind.App] = ElementKind.System,
        [ElementKind.Store] = ElementKind.System,
        [ElementKind.Component] = ElementKind.App,
    };

    /// <summary>Containment by kind: an actor and a system are top-level, an app and a store sit in a system, a component in an app.</summary>
    public static ValidationError? Containment(ModelState state, ElementKind kind, ElementId? parentId)
    {
        var requiredParentKind = ValidParentKinds[kind];

        if (requiredParentKind is null)
        {
            if (parentId.HasValue)
                return new ValidationError("parentId", $"{kind} must be top-level (no parent)");
            return null;
        }

        if (!parentId.HasValue)
            return new ValidationError("parentId", $"{kind} requires a parent of kind {requiredParentKind}");

        if (!state.Elements.TryGetValue(parentId.Value, out var parent))
            return new ValidationError("parentId", $"Parent element {parentId.Value} not found");

        if (parent.Kind != requiredParentKind)
            return new ValidationError("parentId", $"{kind} requires a parent of kind {requiredParentKind}, got {parent.Kind}");

        return null;
    }

    /// <summary>A relationship joins two elements of the model, and carries a message of the model or none.</summary>
    public static ValidationError? Relationship(ModelState state, ElementId fromId, ElementId toId, MessageId? messageId = null)
    {
        if (!state.Elements.ContainsKey(fromId))
            return new ValidationError("fromId", $"Source element {fromId} not found");
        if (!state.Elements.ContainsKey(toId))
            return new ValidationError("toId", $"Target element {toId} not found");
        if (messageId is { } message && !state.Messages.ContainsKey(message))
            return new ValidationError("messageId", $"Message {message} not found");
        return null;
    }

    /// <summary>A message has a producer and consumers that are elements of the model.</summary>
    public static ValidationError? Message(ModelState state, ElementId? producerId, IEnumerable<ElementId>? consumerIds)
    {
        if (producerId is { } producer && !state.Elements.ContainsKey(producer))
            return new ValidationError("producerId", $"Producer element {producer} not found");
        foreach (var consumer in consumerIds ?? [])
        {
            if (!state.Elements.ContainsKey(consumer))
                return new ValidationError("consumerIds", $"Consumer element {consumer} not found");
        }
        return null;
    }

    public static ValidationError? Operation(ModelState state, OperationState operation)
    {
        if (string.IsNullOrWhiteSpace(operation.Name))
            return new ValidationError("name", "Name is required");
        if (TextRule.Line("name", operation.Name) is { } nameError)
            return nameError;
        if (!Enum.IsDefined(operation.Transport))
            return new ValidationError("transport", "Transport must be http, grpc or command");
        if (!state.Elements.ContainsKey(operation.OwnerId))
            return new ValidationError("ownerId", $"Owner element {operation.OwnerId} not found");
        if (operation.TimeoutMs is < 0 or > TraceLimits.MaxDurationMs)
            return new ValidationError("timeoutMs", $"Timeout must be between 0 and {TraceLimits.MaxDurationMs} ms");
        return Retry(operation.Retry);
    }

    public static ValidationError? Channel(ChannelState channel)
    {
        if (string.IsNullOrWhiteSpace(channel.Name))
            return new ValidationError("name", "Name is required");
        if (TextRule.Line("name", channel.Name) is { } nameError)
            return nameError;
        if (!Enum.IsDefined(channel.Kind))
            return new ValidationError("kind", "Kind must be topic, queue or exchange");
        return null;
    }

    public static ValidationError? Subscription(ModelState state, SubscriptionState subscription)
    {
        if (!state.Channels.ContainsKey(subscription.ChannelId))
            return new ValidationError("channelId", $"Channel {subscription.ChannelId} not found");
        if (!state.Elements.ContainsKey(subscription.ConsumerId))
            return new ValidationError("consumerId", $"Consumer element {subscription.ConsumerId} not found");
        if (subscription.DeadLetterChannelId is { } deadLetter)
        {
            if (!state.Channels.ContainsKey(deadLetter))
                return new ValidationError("deadLetterChannelId", $"Dead-letter channel {deadLetter} not found");
            if (deadLetter == subscription.ChannelId)
                return new ValidationError("deadLetterChannelId", "The dead-letter channel must differ from the channel");
        }
        return Retry(subscription.Retry);
    }

    /// <summary>A binding routes from an exchange or a topic to a queue or an exchange of the model, and is not a loop.</summary>
    public static ValidationError? Binding(ModelState state, BindingState binding)
    {
        if (!state.Channels.TryGetValue(binding.SourceChannelId, out var source))
            return new ValidationError("sourceChannelId", $"Source channel {binding.SourceChannelId} not found");
        if (!state.Channels.TryGetValue(binding.TargetChannelId, out var target))
            return new ValidationError("targetChannelId", $"Target channel {binding.TargetChannelId} not found");
        if (binding.SourceChannelId == binding.TargetChannelId)
            return new ValidationError("targetChannelId", "The target channel must differ from the source channel");
        if (!CanRouteFrom(source.Kind))
            return new ValidationError("sourceChannelId",
                $"'{source.Name}', which is {KindWord(source.Kind)}, cannot be the source of a binding: the source must be an exchange or a topic");
        if (!CanRouteTo(target.Kind))
            return new ValidationError("targetChannelId",
                $"'{target.Name}', which is {KindWord(target.Kind)}, cannot be the target of a binding: the target must be a queue or an exchange");
        if (binding.RoutingKey is { } key && string.IsNullOrWhiteSpace(key))
            return new ValidationError("routingKey", "A routing key must not be blank");
        return TextRule.Line("routingKey", binding.RoutingKey);
    }

    /// <summary>The other binding with the same source, target and routing key, which name one binding.</summary>
    public static BindingState? SameRoute(ModelState state, BindingState binding) =>
        state.Bindings.Values.FirstOrDefault(b =>
            b.Id != binding.Id
            && b.SourceChannelId == binding.SourceChannelId
            && b.TargetChannelId == binding.TargetChannelId
            && b.RoutingKey == binding.RoutingKey);

    /// <summary>
    /// A model holds at most <see cref="ContractDecider.MaxPerKind"/> of each kind of contract,
    /// so a contract list read from the stored state stays bounded.
    /// </summary>
    public static string? Limit(string kind, int count) =>
        count > ContractDecider.MaxPerKind ? $"A model holds at most {ContractDecider.MaxPerKind} {kind}" : null;

    /// <summary>An exchange or a topic routes; a queue holds messages for its consumers.</summary>
    public static bool CanRouteFrom(ChannelKind kind) => kind is ChannelKind.Exchange or ChannelKind.Topic;

    public static bool CanRouteTo(ChannelKind kind) => kind is ChannelKind.Queue or ChannelKind.Exchange;

    public static string KindWord(ChannelKind kind) => kind switch
    {
        ChannelKind.Exchange => "an exchange",
        ChannelKind.Queue => "a queue",
        _ => "a topic",
    };

    /// <summary>
    /// Every rule that <paramref name="state"/> breaks, one line each, naming the fact and the
    /// rule. Sorted by fact ID within each kind, so the text is stable.
    /// </summary>
    public static IReadOnlyList<string> Check(ModelState state)
    {
        var problems = new List<string>();
        void Add(string subject, ValidationError? error)
        {
            if (error is not null)
                problems.Add($"{subject}: {error.Message}");
        }

        foreach (var element in state.Elements.Values.OrderBy(e => e.Id.Value))
            Add($"element '{element.Name}' ({element.Id})", Containment(state, element.Kind, element.ParentId));

        foreach (var relationship in state.Relationships.Values.OrderBy(r => r.Id.Value))
            Add($"relationship ({relationship.Id})", Relationship(state, relationship.FromId, relationship.ToId, relationship.MessageId));

        foreach (var message in state.Messages.Values.OrderBy(m => m.Id.Value))
            Add($"message '{message.Name}' ({message.Id})", Message(state, message.ProducerId, message.ConsumerIds));

        foreach (var operation in state.Operations.Values.OrderBy(o => o.Id.Value))
            Add(ContractDecider.Describe(operation), Operation(state, operation));

        foreach (var channel in state.Channels.Values.OrderBy(c => c.Id.Value))
            Add($"channel '{channel.Name}' ({channel.Id})", Channel(channel));

        foreach (var subscription in state.Subscriptions.Values.OrderBy(s => s.Id.Value))
            Add(ContractDecider.Describe(state, subscription), Subscription(state, subscription));

        foreach (var binding in state.Bindings.Values.OrderBy(b => b.Id.Value))
        {
            var name = ContractDecider.Describe(state, binding);
            Add(name, Binding(state, binding));
            // Each of two bindings on one route is reported; the one with the higher ID repeats the other.
            if (SameRoute(state, binding) is { } same && same.Id.Value.CompareTo(binding.Id.Value) < 0)
                problems.Add($"{name}: repeats another binding with the same source, target and routing key");
        }

        (string Kind, int Count)[] contracts =
        [
            ("operations", state.Operations.Count),
            ("channels", state.Channels.Count),
            ("subscriptions", state.Subscriptions.Count),
            ("bindings", state.Bindings.Count),
        ];
        foreach (var (kind, count) in contracts)
        {
            if (Limit(kind, count) is { } limit)
                problems.Add($"the model holds {count} {kind}: {limit}");
        }

        return problems;
    }

    /// <summary>
    /// The rules that <paramref name="after"/> breaks and <paramref name="before"/> did not. A
    /// write is held to the rules it would break, not to ones its model broke already.
    /// </summary>
    public static IReadOnlyList<string> NewViolations(ModelState before, ModelState after) =>
        Check(after).Except(Check(before)).ToList();

    /// <summary>
    /// Applies events that no decider made, one by one. <see cref="ModelState.Evolve"/> skips
    /// an event that does not fit, so each is checked first: an add of something absent under
    /// an ID that names nothing, a change or a removal of something present. Returns the state
    /// after the events, or the first event that does not fit.
    /// </summary>
    public static (ModelState State, string? Misfit) Apply(ModelState state, IEnumerable<object> events)
    {
        foreach (var @event in events)
        {
            if (Misfit(state, @event) is { } misfit)
                return (state, misfit);
            state = ModelState.Evolve(state, @event);
        }
        return (state, null);
    }

    private static ValidationError? Retry(RetryPolicy? retry)
    {
        if (retry is null)
            return new ValidationError("retry", "Retry policy is required");
        if (retry.MaxAttempts is < 1 or > TraceLimits.MaxAttempts)
            return new ValidationError("retry.maxAttempts", $"Attempts must be between 1 and {TraceLimits.MaxAttempts}");
        if (retry.BackoffMs is < 0 or > TraceLimits.MaxDurationMs)
            return new ValidationError("retry.backoffMs", $"Backoff must be between 0 and {TraceLimits.MaxDurationMs} ms");
        return null;
    }

    private static string? Misfit(ModelState state, object @event) => @event switch
    {
        ModelEvent.ElementAdded e => Unused(state, "element", e.Id.Value),
        ModelEvent.ElementRenamed e => Present(state.Elements.ContainsKey(e.ElementId), "element", e.ElementId.Value),
        ModelEvent.ElementDescriptionChanged e => Present(state.Elements.ContainsKey(e.ElementId), "element", e.ElementId.Value),
        ModelEvent.ElementTechnologyChanged e => Present(state.Elements.ContainsKey(e.ElementId), "element", e.ElementId.Value),
        ModelEvent.ElementOwnerChanged e => Present(state.Elements.ContainsKey(e.ElementId), "element", e.ElementId.Value),
        ModelEvent.ElementReparented e => Present(state.Elements.ContainsKey(e.ElementId), "element", e.ElementId.Value),
        ModelEvent.ElementStatusChanged e => Present(state.Elements.ContainsKey(e.ElementId), "element", e.ElementId.Value),
        ModelEvent.ElementRetagged e => Present(state.Elements.ContainsKey(e.ElementId), "element", e.ElementId.Value),
        ModelEvent.ElementIconChanged e => Present(state.Elements.ContainsKey(e.ElementId), "element", e.ElementId.Value),
        ModelEvent.ElementProvenanceRecorded e => Present(state.Elements.ContainsKey(e.ElementId), "element", e.ElementId.Value),
        ModelEvent.ElementRemoved e => Present(state.Elements.ContainsKey(e.ElementId), "element", e.ElementId.Value),

        ModelEvent.RelationshipAdded e => Unused(state, "relationship", e.Id.Value),
        ModelEvent.RelationshipLabelChanged e => Present(state.Relationships.ContainsKey(e.RelationshipId), "relationship", e.RelationshipId.Value),
        ModelEvent.RelationshipTechnologyChanged e => Present(state.Relationships.ContainsKey(e.RelationshipId), "relationship", e.RelationshipId.Value),
        ModelEvent.RelationshipLineShapeChanged e => Present(state.Relationships.ContainsKey(e.RelationshipId), "relationship", e.RelationshipId.Value),
        ModelEvent.RelationshipKindChanged e => Present(state.Relationships.ContainsKey(e.RelationshipId), "relationship", e.RelationshipId.Value),
        ModelEvent.RelationshipProvenanceRecorded e => Present(state.Relationships.ContainsKey(e.RelationshipId), "relationship", e.RelationshipId.Value),
        ModelEvent.RelationshipRemoved e => Present(state.Relationships.ContainsKey(e.RelationshipId), "relationship", e.RelationshipId.Value),

        ModelEvent.MessageAdded e => Unused(state, "message", e.Id.Value),
        ModelEvent.MessageUpdated e => Present(state.Messages.ContainsKey(e.MessageId), "message", e.MessageId.Value),
        ModelEvent.MessageProvenanceRecorded e => Present(state.Messages.ContainsKey(e.MessageId), "message", e.MessageId.Value),
        ModelEvent.MessageRemoved e => Present(state.Messages.ContainsKey(e.MessageId), "message", e.MessageId.Value),

        ModelEvent.OperationAdded e => Unused(state, "operation", e.Id.Value),
        ModelEvent.OperationChanged e => Present(state.Operations.ContainsKey(e.OperationId), "operation", e.OperationId.Value),
        ModelEvent.OperationProvenanceRecorded e => Present(state.Operations.ContainsKey(e.OperationId), "operation", e.OperationId.Value),
        ModelEvent.OperationRemoved e => Present(state.Operations.ContainsKey(e.OperationId), "operation", e.OperationId.Value),

        ModelEvent.ChannelAdded e => Unused(state, "channel", e.Id.Value),
        ModelEvent.ChannelChanged e => Present(state.Channels.ContainsKey(e.ChannelId), "channel", e.ChannelId.Value),
        ModelEvent.ChannelProvenanceRecorded e => Present(state.Channels.ContainsKey(e.ChannelId), "channel", e.ChannelId.Value),
        ModelEvent.ChannelRemoved e => Present(state.Channels.ContainsKey(e.ChannelId), "channel", e.ChannelId.Value),

        ModelEvent.SubscriptionAdded e => Unused(state, "subscription", e.Id.Value),
        ModelEvent.SubscriptionChanged e => Present(state.Subscriptions.ContainsKey(e.SubscriptionId), "subscription", e.SubscriptionId.Value),
        ModelEvent.SubscriptionProvenanceRecorded e => Present(state.Subscriptions.ContainsKey(e.SubscriptionId), "subscription", e.SubscriptionId.Value),
        ModelEvent.SubscriptionRemoved e => Present(state.Subscriptions.ContainsKey(e.SubscriptionId), "subscription", e.SubscriptionId.Value),

        ModelEvent.BindingAdded e => Unused(state, "binding", e.Id.Value),
        ModelEvent.BindingChanged e => Present(state.Bindings.ContainsKey(e.BindingId), "binding", e.BindingId.Value),
        ModelEvent.BindingProvenanceRecorded e => Present(state.Bindings.ContainsKey(e.BindingId), "binding", e.BindingId.Value),
        ModelEvent.BindingRemoved e => Present(state.Bindings.ContainsKey(e.BindingId), "binding", e.BindingId.Value),

        // A new kind of model event must be given its rule here before a merge or an import may write it.
        _ => throw new InvalidOperationException($"No fit rule for the event {@event.GetType().Name}."),
    };

    // One ID names one entity of any kind. A draft and main can each give the same ID to
    // different kinds; the result must not hold both.
    private static string? Unused(ModelState state, string kind, Guid id) =>
        state.KindOf(id) is { } owner ? $"it adds {kind} {id}, but the model already uses that ID for {owner}" : null;

    private static string? Present(bool exists, string kind, Guid id) =>
        exists ? null : $"it changes {kind} {id}, which the model does not have";
}
