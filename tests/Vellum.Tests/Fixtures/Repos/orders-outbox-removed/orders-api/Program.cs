using MassTransit;
using Microsoft.EntityFrameworkCore;
using Orders.Api;
using Orders.Contracts;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<OrdersDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Orders")));

builder.Services.AddMassTransit(bus =>
{
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
    await db.SaveChangesAsync(ct);

    // Publish straight to the broker: one table less, and the event leaves sooner.
    await publisher.Publish(new OrderCreated(order.Id, order.CustomerId, order.Total, order.CreatedAt), ct);

    return Results.Created($"/orders/{order.Id}", order);
});

orders.MapGet("/{id:guid}", async (Guid id, OrdersDbContext db, CancellationToken ct) =>
    await db.Orders.FindAsync([id], ct) is { } order ? Results.Ok(order) : Results.NotFound());

app.Run();
