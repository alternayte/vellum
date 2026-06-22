using Microsoft.EntityFrameworkCore;
using Vellum.Kernel.EventStore;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")!;

builder.Services.AddDbContext<EventStoreDbContext>(options =>
    options.UseNpgsql(connectionString)
           .UseSnakeCaseNamingConvention());

builder.Services.AddScoped<IEventStore, EventStore>();

var app = builder.Build();

app.MapGet("/health", () => Results.Ok());

app.Run();
