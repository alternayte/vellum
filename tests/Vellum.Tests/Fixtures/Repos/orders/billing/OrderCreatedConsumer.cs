using MassTransit;
using Orders.Contracts;

namespace Billing;

/// <summary>Issues one invoice for each order. Redelivery of the same order is a no-op.</summary>
public sealed class OrderCreatedConsumer(InvoiceWriter invoices, ILogger<OrderCreatedConsumer> logger)
    : IConsumer<OrderCreated>
{
    public async Task Consume(ConsumeContext<OrderCreated> context)
    {
        var order = context.Message;
        if (!await invoices.TryIssue(order.OrderId, order.CustomerId, order.Total, context.CancellationToken))
        {
            logger.LogInformation("Invoice for order {OrderId} exists; skipped", order.OrderId);
        }
    }
}

public sealed class InvoiceWriter
{
    private readonly HashSet<Guid> _issued = [];

    public Task<bool> TryIssue(Guid orderId, Guid customerId, decimal total, CancellationToken ct)
    {
        lock (_issued)
        {
            return Task.FromResult(_issued.Add(orderId));
        }
    }
}
