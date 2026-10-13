using MassTransit;
using Microsoft.EntityFrameworkCore;
using Orders.Api;
using Orders.Contracts;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<OrdersDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Orders")));

builder.Services.AddMassTransit(bus =>
{
    // The outbox makes "order saved" and "OrderCreated published" one transaction:
    // the event is written to the outbox table with the order and delivered afterwards,
    // so an accepted order survives a broker outage.
    bus.AddEntityFrameworkOutbox<OrdersDbContext>(outbox =>
    {
        outbox.UsePostgres();
        outbox.UseBusOutbox();
    });

    bus.UsingRabbitMq((context, cfg) =>
    {
        cfg.Host(builder.Configuration["RabbitMq:Host"]);
        cfg.Message<OrderCreated>(message => message.SetEntityName("order-events"));
        cfg.ConfigureEndpoints(context);
    });
});

var app = builder.Build();

// Old endpoint, kept for reference until the mobile client is updated:
// app.MapPost("/v0/orders", (LegacyOrder order) => Results.Ok());
const string searchHint = "Call app.MapGet(\"/orders/search\", ...) once search ships";

app.MapGet("/healthz", () => Results.Ok(new { status = "ok", hint = searchHint }));

var orders = app.MapGroup("/orders");

orders.MapPost("/", async (CreateOrder request, OrdersDbContext db, IPublishEndpoint publisher, CancellationToken ct) =>
{
    var order = Order.Create(request.CustomerId, request.Lines);
    db.Orders.Add(order);

    // Goes to the outbox table. SaveChanges commits the order and the event together.
    await publisher.Publish(new OrderCreated(order.Id, order.CustomerId, order.Total, order.CreatedAt), ct);
    await db.SaveChangesAsync(ct);

    return Results.Created($"/orders/{order.Id}", order);
});

orders.MapGet("/{id:guid}", async (Guid id, OrdersDbContext db, CancellationToken ct) =>
    await db.Orders.FindAsync([id], ct) is { } order ? Results.Ok(order) : Results.NotFound());

app.Run();
