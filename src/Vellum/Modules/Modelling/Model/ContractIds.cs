using System.Text.Json.Serialization;

namespace Vellum.Modules.Modelling.Model;

/// <summary>An invocable contract on a service: an HTTP route, a gRPC method or a command.</summary>
[JsonConverter(typeof(OperationIdJsonConverter))]
public readonly record struct OperationId(Guid Value) : IArchitectureId
{
    public static implicit operator OperationId(Guid value) => new(value);
    public static implicit operator Guid(OperationId id) => id.Value;
    public override string ToString() => Value.ToString();
}

/// <summary>A topic, queue or exchange that carries messages between services.</summary>
[JsonConverter(typeof(ChannelIdJsonConverter))]
public readonly record struct ChannelId(Guid Value) : IArchitectureId
{
    public static implicit operator ChannelId(Guid value) => new(value);
    public static implicit operator Guid(ChannelId id) => id.Value;
    public override string ToString() => Value.ToString();
}

/// <summary>One consumer's binding to a channel, with its own delivery rules.</summary>
[JsonConverter(typeof(SubscriptionIdJsonConverter))]
public readonly record struct SubscriptionId(Guid Value) : IArchitectureId
{
    public static implicit operator SubscriptionId(Guid value) => new(value);
    public static implicit operator Guid(SubscriptionId id) => id.Value;
    public override string ToString() => Value.ToString();
}

/// <summary>A route from an exchange or a topic to a queue or an exchange.</summary>
[JsonConverter(typeof(BindingIdJsonConverter))]
public readonly record struct BindingId(Guid Value) : IArchitectureId
{
    public static implicit operator BindingId(Guid value) => new(value);
    public static implicit operator Guid(BindingId id) => id.Value;
    public override string ToString() => Value.ToString();
}

[JsonConverter(typeof(ScenarioIdJsonConverter))]
public readonly record struct ScenarioId(Guid Value) : IArchitectureId
{
    public static implicit operator ScenarioId(Guid value) => new(value);
    public static implicit operator Guid(ScenarioId id) => id.Value;
    public override string ToString() => Value.ToString();
}

public sealed class OperationIdJsonConverter : GuidIdJsonConverter<OperationId>
{
    protected override OperationId FromGuid(Guid value) => new(value);
    protected override Guid ToGuid(OperationId value) => value.Value;
}

public sealed class ChannelIdJsonConverter : GuidIdJsonConverter<ChannelId>
{
    protected override ChannelId FromGuid(Guid value) => new(value);
    protected override Guid ToGuid(ChannelId value) => value.Value;
}

public sealed class SubscriptionIdJsonConverter : GuidIdJsonConverter<SubscriptionId>
{
    protected override SubscriptionId FromGuid(Guid value) => new(value);
    protected override Guid ToGuid(SubscriptionId value) => value.Value;
}

public sealed class BindingIdJsonConverter : GuidIdJsonConverter<BindingId>
{
    protected override BindingId FromGuid(Guid value) => new(value);
    protected override Guid ToGuid(BindingId value) => value.Value;
}

public sealed class ScenarioIdJsonConverter : GuidIdJsonConverter<ScenarioId>
{
    protected override ScenarioId FromGuid(Guid value) => new(value);
    protected override Guid ToGuid(ScenarioId value) => value.Value;
}
