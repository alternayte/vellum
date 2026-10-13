namespace Orders.Contracts;

/// <summary>
/// Published by orders-api when an order is accepted. Consumers must be idempotent:
/// the outbox delivers at least once.
/// </summary>
public sealed record OrderCreated(
    Guid OrderId,
    Guid CustomerId,
    decimal Total,
    DateTimeOffset CreatedAt);
