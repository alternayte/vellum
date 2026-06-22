using Microsoft.EntityFrameworkCore;
using Scalar.AspNetCore;
using Scrutor;
using Vellum.Kernel.Aggregates;
using Vellum.Kernel.CommandHandling;
using Vellum.Kernel.EventStore;
using Vellum.Kernel.EventTypes;
using Vellum.Kernel.Outbox;
using Vellum.Kernel.Projections;
using Vellum.Modules.Identity;
using Vellum.Modules.Modelling;
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
builder.Services.AddModellingModule(builder.Configuration);

// Command handlers (Scrutor scan + TransactionBehavior decoration)
builder.Services.Scan(s => s.FromAssemblyOf<Program>()
    .AddClasses(c => c.AssignableTo(typeof(ICommandHandler<,>)))
    .AsImplementedInterfaces()
    .WithScopedLifetime());
builder.Services.Decorate(typeof(ICommandHandler<,>), typeof(TransactionBehavior<,>));

var app = builder.Build();

// Register modelling events
var registry = app.Services.GetRequiredService<EventTypeRegistry>();
ModellingModule.RegisterEvents(registry);

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
app.MapModellingEndpoints();

app.Run();

public partial class Program;
