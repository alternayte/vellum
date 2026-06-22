using Microsoft.EntityFrameworkCore;
using Scalar.AspNetCore;
using Vellum.Kernel.Aggregates;
using Vellum.Kernel.CommandHandling;
using Vellum.Kernel.EventStore;
using Vellum.Kernel.EventTypes;
using Vellum.Kernel.Outbox;
using Vellum.Kernel.Projections;
using Vellum.Modules.Identity;
using Vellum.Modules.Workspaces;
using Vellum.Shared;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")!;

// OpenAPI
builder.Services.AddOpenApi();

// Exception handling
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<UnauthorizedExceptionHandler>();

// Kernel
builder.Services.AddDbContext<EventStoreDbContext>(options =>
    options.UseNpgsql(connectionString)
           .UseSnakeCaseNamingConvention());

builder.Services.AddScoped<IEventStore, EventStore>();
builder.Services.AddScoped<EventCollector>();
builder.Services.AddScoped<AggregateStore>();
builder.Services.AddSingleton<EventTypeRegistry>();
builder.Services.AddSingleton<IEventTypeRegistry>(sp => sp.GetRequiredService<EventTypeRegistry>());
builder.Services.AddHostedService<OutboxDispatcher>();
builder.Services.AddHostedService<AsyncProjectionHost>();

// Modules
builder.Services.AddIdentityModule(builder.Configuration);
builder.Services.AddWorkspacesModule(builder.Configuration);

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

app.UseExceptionHandler();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health", () => Results.Ok());

// Endpoints
app.MapIdentityEndpoints();
app.MapWorkspaceEndpoints();

app.Run();

public partial class Program;
