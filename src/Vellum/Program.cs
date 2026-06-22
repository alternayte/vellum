using Microsoft.AspNetCore.Identity;
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
using Vellum.Modules.Modelling.Model;
using Vellum.Modules.Views;
using Vellum.Modules.Workspaces;
using Vellum.Modules.Workspaces.Entities;
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
builder.Services.AddViewsModule(builder.Configuration);

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
app.MapViewEndpoints();

if (app.Environment.IsDevelopment())
{
    using var scope = app.Services.CreateScope();
    var services = scope.ServiceProvider;

    // Apply all migrations
    await services.GetRequiredService<EventStoreDbContext>().Database.MigrateAsync();
    await services.GetRequiredService<AppIdentityDbContext>().Database.MigrateAsync();
    await services.GetRequiredService<WorkspacesDbContext>().Database.MigrateAsync();
    await services.GetRequiredService<ModellingDbContext>().Database.MigrateAsync();
    await services.GetRequiredService<ViewsDbContext>().Database.MigrateAsync();

    // Seed dev data
    if (args.Contains("seed"))
    {
        var userManager = services.GetRequiredService<UserManager<ApplicationUser>>();
        var workspacesDb = services.GetRequiredService<WorkspacesDbContext>();
        var aggregateStore = services.GetRequiredService<AggregateStore>();
        var eventRegistry = services.GetRequiredService<EventTypeRegistry>();
        var projection = services.GetRequiredService<ModelProjection>();

        // Create dev user
        var devUser = await userManager.FindByEmailAsync("dev@vellum.local");
        if (devUser is null)
        {
            devUser = new ApplicationUser
            {
                UserName = "dev@vellum.local",
                Email = "dev@vellum.local",
                DisplayName = "Dev User"
            };
            var seedResult = await userManager.CreateAsync(devUser, "Dev123!");
            if (!seedResult.Succeeded)
                throw new InvalidOperationException(
                    $"Failed to create dev user: {string.Join(", ", seedResult.Errors.Select(e => e.Description))}");
        }

        // Create workspace + project
        var workspaceId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        if (await workspacesDb.Workspaces.FindAsync(workspaceId) is null)
        {
            var streamId = Guid.Parse("00000000-0000-0000-0000-000000000010");
            var projectId = Guid.Parse("00000000-0000-0000-0000-000000000002");

            workspacesDb.Workspaces.Add(new WorkspaceEntity { Id = workspaceId, Name = "Dev Workspace", CreatedBy = devUser.Id });
            workspacesDb.Memberships.Add(new MembershipEntity { Id = Guid.NewGuid(), WorkspaceId = workspaceId, UserId = devUser.Id, Role = "Owner" });
            workspacesDb.Projects.Add(new ProjectEntity { Id = projectId, WorkspaceId = workspaceId, Name = "Sample Architecture", StreamId = streamId });
            await workspacesDb.SaveChangesAsync();

            // Seed model elements via the aggregate
            var metadata = new EventMetadata { ActorId = Guid.Parse(devUser.Id), CorrelationId = Guid.NewGuid() };
            projection.SetContext(projectId, streamId);

            var userId = Guid.NewGuid();
            var webAppId = Guid.NewGuid();
            var ordersSystemId = Guid.NewGuid();
            var paymentsSystemId = Guid.NewGuid();
            var apiAppId = Guid.NewGuid();
            var workerAppId = Guid.NewGuid();
            var dbStoreId = Guid.NewGuid();
            var handlerCompId = Guid.NewGuid();

            var seedEvents = new ModelEvent[]
            {
                new ModelEvent.ElementAdded(userId, ElementKind.Actor, "Customer", "End user of the platform", null, null, ElementStatus.Current, null, ["external"]),
                new ModelEvent.ElementAdded(webAppId, ElementKind.System, "Web Application", "Customer-facing web app", "React", null, ElementStatus.Current, null, ["frontend"]),
                new ModelEvent.ElementAdded(ordersSystemId, ElementKind.System, "Orders System", "Handles order lifecycle", "dotnet", null, ElementStatus.Current, null, ["core"]),
                new ModelEvent.ElementAdded(paymentsSystemId, ElementKind.System, "Payments System", "Processes payments", "go", null, ElementStatus.Planned, null, ["core"]),
                new ModelEvent.ElementAdded(apiAppId, ElementKind.App, "Orders API", "REST API for orders", "ASP.NET Core", null, ElementStatus.Current, ordersSystemId, []),
                new ModelEvent.ElementAdded(workerAppId, ElementKind.App, "Order Worker", "Background job processor", "dotnet", null, ElementStatus.Current, ordersSystemId, []),
                new ModelEvent.ElementAdded(dbStoreId, ElementKind.Store, "Orders DB", "PostgreSQL database", "PostgreSQL", null, ElementStatus.Current, ordersSystemId, []),
                new ModelEvent.ElementAdded(handlerCompId, ElementKind.Component, "OrderHandler", "Processes incoming orders", "C#", null, ElementStatus.Current, apiAppId, []),
                // Relationships
                new ModelEvent.RelationshipAdded(Guid.NewGuid(), userId, webAppId, "Uses", "HTTPS", null),
                new ModelEvent.RelationshipAdded(Guid.NewGuid(), webAppId, ordersSystemId, "Places orders", "HTTP/JSON", null),
                new ModelEvent.RelationshipAdded(Guid.NewGuid(), ordersSystemId, paymentsSystemId, "Requests payment", "gRPC", null),
                new ModelEvent.RelationshipAdded(Guid.NewGuid(), apiAppId, dbStoreId, "Reads/writes", "SQL", null),
                new ModelEvent.RelationshipAdded(Guid.NewGuid(), apiAppId, workerAppId, "Enqueues jobs", "Redis", null),
            };

            var state = seedEvents.Aggregate(ModelState.Initial, (s, e) => s.Evolve(e));
            await aggregateStore.SaveAsync(streamId, "model", 0, state, seedEvents, metadata);
        }

        Console.WriteLine("Dev seed complete.");
        return;
    }
}

app.Run();

public partial class Program;
