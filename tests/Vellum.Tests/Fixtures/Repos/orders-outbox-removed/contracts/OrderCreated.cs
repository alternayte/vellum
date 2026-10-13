namespace Orders.Contracts;

/// <summary>Published by orders-api when an order is accepted.</summary>
public sealed record OrderCreated(
    Guid OrderId,
    Guid CustomerId,
    decimal TotalAmount,
    DateTimeOffset CreatedAt);
