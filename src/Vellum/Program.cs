using System.Reflection;
using System.Security.Claims;
using Deedbox;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi;
using Scalar.AspNetCore;
using Vellum.Shared;
using Vellum.Shared.Outbound;
using Vellum.Shared.Telemetry;
using Vellum.Modules.Ai;
using Vellum.Modules.GitHub;
using Vellum.Modules.Identity;
using Vellum.Modules.Identity.AgentTokens;
using Vellum.Modules.Import;
using Vellum.Modules.Mcp;
using Vellum.Modules.Modelling;
using Vellum.Modules.Build;
using Vellum.Modules.Costs;
using Vellum.Modules.LoadTests;
using Vellum.Modules.Promises;
using Vellum.Modules.Diagrams;
using Vellum.Modules.Docs;
using Vellum.Modules.Drafts;
using Vellum.Modules.Schemas;
using Vellum.Modules.Review;
using Vellum.Modules.Settings;
using Vellum.Modules.Settings.Crypto;
using Vellum.Modules.Sharing;
using Vellum.Modules.Stress;
using Vellum.Modules.Trace;
using Vellum.Modules.Views;
using Vellum.Modules.Workspaces;

// A start that is refused must end the process. As process 1 of a container, a process that
// dies of an unhandled exception does not end: the runtime aborts, and the kernel ignores
// that signal for process 1. So the process writes the reason and exits by itself. A test
// host runs this file inside the test process, which keeps its own handling.
if (Assembly.GetEntryAssembly() == typeof(Program).Assembly)
{
    AppDomain.CurrentDomain.UnhandledException += (_, unhandled) =>
    {
        Console.Error.WriteLine(unhandled.ExceptionObject is BootstrapException refusal
            ? $"Vellum did not start. {refusal.Message}"
            : $"Vellum stopped on an error. {unhandled.ExceptionObject}");
        Environment.Exit(1);
    };
}

var builder = WebApplication.CreateBuilder(args);

// Maintenance commands. They read the configuration, do one thing and end; no host starts.
if (KeysCommand.Matches(args))
    return await KeysCommand.RunAsync(args, builder.Configuration, Console.Out);
if (args is ["health"])
    return await HealthCommand.RunAsync(builder.Configuration, Console.Error);

// OpenAPI
builder.Services.AddOpenApi(options => options.AddSchemaTransformer((schema, context, _) =>
{
    // A typed architecture ID is a GUID string on the wire; its converter hides that from
    // the schema generator.
    if (typeof(IArchitectureId).IsAssignableFrom(context.JsonTypeInfo.Type))
    {
        schema.Type = JsonSchemaType.String;
        schema.Format = "uuid";
    }

    return Task.CompletedTask;
}));

// Exception handling. Every error answer is the shared envelope (Shared/ErrorEnvelope.cs);
// an exception that no handler takes is a 500 with nothing of the exception in it.
builder.Services.AddExceptionHandler<UnauthorizedExceptionHandler>();
builder.Services.AddExceptionHandler<ConcurrencyExceptionHandler>();
builder.Services.AddExceptionHandler<InvalidCursorExceptionHandler>();
// A bad route or body value is 400 in every environment. Development would otherwise throw,
// and the exception handler would answer 500.
builder.Services.Configure<Microsoft.AspNetCore.Routing.RouteHandlerOptions>(options => options.ThrowOnBadRequest = false);
builder.Services.AddRequestLimits();

// Operator telemetry. Nothing is exported unless OTEL_EXPORTER_OTLP_ENDPOINT names a collector.
builder.Services.AddTelemetry(builder.Configuration, GetInstance.Version);

// Event store. Each module registers its own streams and projections. The connection
// string is read when the host starts, so configuration that a test host overrides applies.
// The event store's data source reads and writes the module tables in its inline projections,
// so it needs the Postgres enum types of the modules as much as EF Core does.
AppDatabase.MapEnumTypes();
var servicesBeforeDeedbox = builder.Services.Count;
builder.Services.AddDeedbox(deedbox => deedbox
    .UsePostgres(services => services.GetRequiredService<IConfiguration>().GetConnectionString("DefaultConnection")!)
    .AddModelStreams()
    .AddSchemaStreams()
    .AddScenarioStreams()
    .AddPromiseStreams()
    .AddDecisionStreams());

// Build-time OpenAPI generation starts the host with no database. Deedbox checks its
// schema when the host starts, so its hosted services do not run in that process.
var generatingOpenApi = Assembly.GetEntryAssembly()?.GetName().Name == "GetDocument.Insider";
if (generatingOpenApi)
{
    for (var i = builder.Services.Count - 1; i >= servicesBeforeDeedbox; i--)
    {
        if (builder.Services[i].ServiceType == typeof(IHostedService))
            builder.Services.RemoveAt(i);
    }
}

// The one DbContext. Each module configures its own tables in its own Postgres schema. The
// connection string is read when the context is made, so configuration that a test host
// overrides applies.
builder.Services.AddDbContext<AppDbContext>((services, options) => options
    .UseAppDatabase(services.GetRequiredService<IConfiguration>().GetConnectionString("DefaultConnection")));

// Modules
builder.Services.AddIdentityModule(builder.Configuration);
builder.Services.AddWorkspacesModule();
builder.Services.AddModellingModule();
builder.Services.AddViewsModule();
builder.Services.AddDocsModule();
builder.Services.AddDraftsModule();
builder.Services.AddSchemasModule();
builder.Services.AddTraceModule();
builder.Services.AddMcpModule();
builder.Services.AddSettingsModule(generatingOpenApi);
builder.Services.AddOutbound();
builder.Services.AddGitHubModule();
builder.Services.AddAiModule();
builder.Services.AddImportModule(runWorker: !generatingOpenApi);
builder.Services.AddPromisesModule();
builder.Services.AddBuildModule();
builder.Services.AddStressModule();
builder.Services.AddLoadTestsModule();
builder.Services.AddCostsModule();
builder.Services.AddReviewModule();
builder.Services.AddSharingModule();

var app = builder.Build();

// The environment is read once, here. An unsafe hosted bootstrap stops the start with a
// message that states the fix.
if (!generatingOpenApi)
    app.Services.GetRequiredService<Bootstrap>();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

// The address and the scheme of the client, from the proxies the operator names. First, so
// everything below sees them.
app.UseTrustedProxies();
app.UseExceptionHandler(new ExceptionHandlerOptions { ExceptionHandler = UnhandledException.WriteAsync });
app.UseSecurityHeaders();
// Only /embed/{token} of a view link may be framed, by the origins its workspace allows.
app.UseEmbedFraming();
app.UseNoPageOutsideTheApp();
app.UseSameOriginWrites();
app.UseAuthentication();
// After authentication, so a caller with a valid credential is known; before authorization
// and the agent boundary, so a refused credential counts against its address.
app.UseRateLimiter();
app.UseAuthorization();
app.UseAgentBoundary();
app.UseBodyLimits();
app.UseRequestBodyLimits();

// Every event a request appends records who caused it and which request it belongs to.
app.Use(async (http, next) =>
{
    http.RequestServices.GetRequiredService<DeedboxContext>().Metadata = new EventMetadata
    {
        CorrelationId = http.TraceIdentifier,
        Actor = AgentPrincipal.Actor(http.User),
    };
    await next(http);
});


// Endpoints
app.MapIdentityEndpoints();
app.MapWorkspaceEndpoints();
app.MapModellingEndpoints();
app.MapContractEndpoints();
app.MapRevisionEndpoints();
app.MapVariantEndpoints();
app.MapViewEndpoints();
app.MapDiagramEndpoints();
app.MapSpaceEndpoints();
app.MapDocEndpoints();
app.MapDraftEndpoints();
app.MapMergeEndpoints();
app.MapCommentEndpoints();
app.MapSchemaEndpoints();
app.MapScenarioEndpoints();
app.MapMcpEndpoints();
app.MapSettingsEndpoints();
app.MapGitHubEndpoints();
app.MapAiEndpoints();
app.MapImportEndpoints();
app.MapPromiseEndpoints();
app.MapBuildEndpoints();
app.MapStressEndpoints();
app.MapLoadTestEndpoints();
app.MapCostEndpoints();
app.MapReviewEndpoints();
app.MapSharingEndpoints();

if (!generatingOpenApi && app.Configuration.GetValue("Database:MigrateOnStartup", true))
{
    await DatabaseMigrations.ApplyAsync(
        app.Services, app.Configuration.GetConnectionString("DefaultConnection")!);
}

if (!generatingOpenApi)
{
    // Fail closed on a wrong master key, in both profiles, before anything is read, signed
    // or written with it.
    var bootstrap = app.Services.GetRequiredService<Bootstrap>();
    MasterKeyGuard.EnsureLoaded(bootstrap.Keys,
        await KeyInventory.ReadAsync(app.Configuration.GetConnectionString("DefaultConnection")!));
    if (bootstrap.UnusedKeyFile is { } unusedKeyFile)
        app.Logger.LogWarning(
            "The key file {Path} is not used, because the environment gives the master key. Keep it only as a backup of an old key.",
            unusedKeyFile);
    if (app.Services.GetRequiredService<Vellum.Modules.GitHub.EnvironmentGitHubToken>().IgnoredByHostedProfile)
        app.Logger.LogWarning("GH_TOKEN is set and ignored: the hosted profile uses only workspace tokens.");

    await SetupState.PrepareAsync(app.Services);
}

if (app.Environment.IsDevelopment())
{
    using var scope = app.Services.CreateScope();
    var services = scope.ServiceProvider;

    // Seed dev data
    if (args.Contains("seed"))
    {
        var userManager = services.GetRequiredService<UserManager<ApplicationUser>>();
        var db = services.GetRequiredService<AppDbContext>();
        var store = services.GetRequiredService<IEventStore>();

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
            var seedResult = await userManager.CreateAsync(devUser, "DevPassword123!");
            if (!seedResult.Succeeded)
                throw new InvalidOperationException(
                    $"Failed to create dev user: {string.Join(", ", seedResult.Errors.Select(e => e.Description))}");
        }

        // The seeded user stands in for first-run setup, so it is the instance admin.
        if (await db.InstanceAdmins.FindAsync(devUser.Id) is null)
        {
            db.InstanceAdmins.Add(new InstanceAdminEntity { UserId = devUser.Id });
            await db.SaveChangesAsync();
        }

        // Create workspace + project
        var workspaceId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        if (await db.Workspaces.FindAsync(workspaceId) is null)
        {
            var streamId = Guid.Parse("00000000-0000-0000-0000-000000000010");
            var projectId = Guid.Parse("00000000-0000-0000-0000-000000000002");

            db.Workspaces.Add(new WorkspaceEntity { Id = workspaceId, Name = "Dev Workspace", CreatedBy = devUser.Id });
            db.Memberships.Add(new MembershipEntity { Id = Guid.NewGuid(), WorkspaceId = workspaceId, UserId = devUser.Id, Role = WorkspaceRole.Owner, CreatedAt = services.GetRequiredService<TimeProvider>().GetUtcNow() });
            db.Projects.Add(new ProjectEntity { Id = projectId, WorkspaceId = workspaceId, Name = "Sample Architecture", StreamId = streamId });

            var userId = Guid.NewGuid();
            var webAppId = Guid.NewGuid();
            var ordersSystemId = Guid.NewGuid();
            var paymentsSystemId = Guid.NewGuid();
            var apiAppId = Guid.NewGuid();
            var workerAppId = Guid.NewGuid();
            var dbStoreId = Guid.NewGuid();
            var handlerCompId = Guid.NewGuid();

            var seedEvents = new object[]
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

            // One transaction: the workspace rows, the model events and the read-model rows.
            await store
                .ForProject(projectId)
                .WithMetadata(metadata => metadata with { Actor = $"user:{devUser.Id}" })
                .UseDbContext(db)
                .Append(StreamId.From(streamId), ExpectedVersion.NoStream, seedEvents);
        }

        Console.WriteLine("Dev seed complete.");
        return 0;
    }
}

app.UseStaticFiles();
app.MapAppPages();

await app.RunAsync();
return 0;

public partial class Program;
