using Microsoft.EntityFrameworkCore;
using Vellum.Kernel.Aggregates;
using Vellum.Kernel.EventStore;
using Vellum.Kernel.EventTypes;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")!;

builder.Services.AddDbContext<EventStoreDbContext>(options =>
    options.UseNpgsql(connectionString)
           .UseSnakeCaseNamingConvention());

builder.Services.AddScoped<IEventStore, EventStore>();
builder.Services.AddSingleton<EventTypeRegistry>();
builder.Services.AddSingleton<IEventTypeRegistry>(sp => sp.GetRequiredService<EventTypeRegistry>());
builder.Services.AddScoped<AggregateStore>();

var app = builder.Build();

app.MapGet("/health", () => Results.Ok());

app.Run();
