using Vellum.Modules.Modelling;

namespace Vellum.Modules.Trace.Engine;

public enum Transport { Http, Grpc, Command }

public enum ChannelKind { Topic, Queue, Exchange }

/// <summary>Attempts include the first try; <c>MaxAttempts = 1</c> means no retry.</summary>
public sealed record RetryPolicy(int MaxAttempts = 1, int BackoffMs = 0)
{
    public static RetryPolicy None { get; } = new();
}

public sealed record OperationContract(
    OperationId Id, ElementId OwnerId, string Name, Transport Transport,
    SchemaId? RequestSchemaId, SchemaId? ResponseSchemaId,
    int TimeoutMs, RetryPolicy Retry);

public sealed record ChannelContract(ChannelId Id, string Name, ChannelKind Kind);

public sealed record SubscriptionContract(
    SubscriptionId Id, ChannelId ChannelId, ElementId ConsumerId,
    RetryPolicy Retry, ChannelId? DeadLetterChannelId);

/// <summary>A route from an exchange or a topic to a queue or an exchange. A null key passes every message.</summary>
public sealed record BindingContract(
    BindingId Id, ChannelId SourceChannelId, ChannelId TargetChannelId, string? RoutingKey);

public sealed record MessageContract(MessageId Id, string Name, SchemaId? SchemaId);

/// <summary>
/// The contract snapshot a trace runs against. It is a plain value so that playback of a
/// pinned revision depends on nothing but this record and the scenario (SDD guarantee 4).
/// </summary>
public sealed record TraceModel(
    IReadOnlyDictionary<ElementId, string> Elements,
    IReadOnlyDictionary<OperationId, OperationContract> Operations,
    IReadOnlyDictionary<ChannelId, ChannelContract> Channels,
    IReadOnlyDictionary<SubscriptionId, SubscriptionContract> Subscriptions,
    IReadOnlyDictionary<MessageId, MessageContract> Messages,
    IReadOnlyDictionary<SchemaId, string> Schemas)
{
    /// <summary>Empty unless set: a model without bindings plays as it did before they existed.</summary>
    public IReadOnlyDictionary<BindingId, BindingContract> Bindings { get; init; } =
        new Dictionary<BindingId, BindingContract>();
}
