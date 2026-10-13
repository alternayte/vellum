using Vellum.Modules.Modelling.Model;
using Vellum.Modules.Trace.Engine;
using Vellum.Shared;

namespace Vellum.Modules.Modelling.Contracts;

/// <summary>Attempts include the first try; <c>maxAttempts = 1</c> means no retry.</summary>
public sealed record RetryDto(int MaxAttempts, int BackoffMs)
{
    public static RetryDto From(RetryPolicy retry) => new(retry.MaxAttempts, retry.BackoffMs);

    public RetryPolicy ToDomain() => new(MaxAttempts, BackoffMs);
}

public sealed record OperationDto(
    Guid Id, Guid OwnerId, string Name, string Transport,
    Guid? RequestSchemaId, Guid? ResponseSchemaId,
    int TimeoutMs, RetryDto Retry,
    string FactStatus, ProvenanceDto? Provenance, StaleDto? Stale = null)
{
    public static OperationDto From(OperationState operation) => new(
        operation.Id, operation.OwnerId, operation.Name, operation.Transport.ToString().ToLowerInvariant(),
        operation.RequestSchemaId, operation.ResponseSchemaId,
        operation.TimeoutMs, RetryDto.From(operation.Retry),
        operation.FactStatus.ToApiString(), ProvenanceMapper.ToDto(operation.Provenance), StaleDto.From(operation.Stale));
}

public sealed record ChannelDto(
    Guid Id, string Name, string Kind,
    string FactStatus, ProvenanceDto? Provenance, StaleDto? Stale = null)
{
    public static ChannelDto From(ChannelState channel) => new(
        channel.Id, channel.Name, channel.Kind.ToString().ToLowerInvariant(),
        channel.FactStatus.ToApiString(), ProvenanceMapper.ToDto(channel.Provenance), StaleDto.From(channel.Stale));
}

public sealed record SubscriptionDto(
    Guid Id, Guid ChannelId, Guid ConsumerId,
    RetryDto Retry, Guid? DeadLetterChannelId,
    string FactStatus, ProvenanceDto? Provenance, StaleDto? Stale = null)
{
    public static SubscriptionDto From(SubscriptionState subscription) => new(
        subscription.Id, subscription.ChannelId, subscription.ConsumerId,
        RetryDto.From(subscription.Retry), subscription.DeadLetterChannelId,
        subscription.FactStatus.ToApiString(), ProvenanceMapper.ToDto(subscription.Provenance), StaleDto.From(subscription.Stale));
}

/// <summary>What the contract requests share: reading a fact status label.</summary>
internal static class ContractRequests
{
    public static bool TryReadFactStatus(string? text, out FactStatus? status, out ValidationError? error)
    {
        status = null;
        error = null;
        if (text is null)
            return true;

        if (!FactStatusFormat.TryParseApiString(text, out var parsed))
        {
            error = new ValidationError("factStatus", "Invalid fact status");
            return false;
        }

        status = parsed;
        return true;
    }
}
