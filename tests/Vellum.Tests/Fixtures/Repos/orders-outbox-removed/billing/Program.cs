using Billing;
using MassTransit;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddSingleton<InvoiceWriter>();

builder.Services.AddMassTransit(bus =>
{
    bus.AddConsumer<OrderCreatedConsumer>();

    bus.UsingRabbitMq((context, cfg) =>
    {
        cfg.Host(builder.Configuration["RabbitMq:Host"]);

        cfg.ReceiveEndpoint("billing.order-created", endpoint =>
        {
            endpoint.UseMessageRetry(retry => retry.Interval(3, TimeSpan.FromSeconds(5)));
            endpoint.ConfigureConsumer<OrderCreatedConsumer>(context);
        });
    });
});

builder.Build().Run();
