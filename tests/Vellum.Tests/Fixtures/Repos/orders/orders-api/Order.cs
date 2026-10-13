namespace Orders.Api;

public sealed record OrderLine(string Sku, int Quantity, decimal UnitPrice);

public sealed record CreateOrder(Guid CustomerId, IReadOnlyList<OrderLine> Lines);

public sealed class Order
{
    private Order() { }

    public Guid Id { get; private set; }
    public Guid CustomerId { get; private set; }
    public List<OrderLine> Lines { get; private set; } = [];
    public decimal Total { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public static Order Create(Guid customerId, IReadOnlyList<OrderLine> lines) => new()
    {
        Id = Guid.NewGuid(),
        CustomerId = customerId,
        Lines = [.. lines],
        Total = lines.Sum(line => line.Quantity * line.UnitPrice),
        CreatedAt = DateTimeOffset.UtcNow,
    };
}
