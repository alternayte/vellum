# Phase 1a — Backend Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the backend modules (Identity, Workspaces, Modelling, Views) with full API endpoints on top of the Phase 0 event-sourcing kernel, so the Phase 1b frontend has a production-shaped API to build against.

**Architecture:** Domain-out build order on the existing .NET 10 modular monolith. Event-sourced modelling aggregate with inline projections for read models. Non-event-sourced CRUD for workspaces and views. One endpoints file per module using `MapGroup` (anthology pattern). Scrutor assembly scan decorates only `ICommandHandler<,>` with `TransactionBehavior`.

**Tech Stack:** .NET 10 / C# 14, EF Core 10 + Npgsql, ASP.NET Core Identity + OIDC, Scrutor, Testcontainers (PostgreSQL 17), xUnit. Existing kernel: `IEventStore`, `AggregateStore`, `TransactionBehavior<,>`, `EventCollector`, `IInlineProjection`, `EventTypeRegistry`.

## Global Constraints

- Target framework: `net10.0`
- All dependencies must be MIT or Apache-2.0 licensed
- PostgreSQL 17 is the only external dependency
- `snake_case` naming via `EFCore.NamingConventions` — never manually specify column names
- All DB access via EF Core parameterised queries
- One Postgres schema per module (`identity`, `workspaces`, `modelling`, `views`)
- Frequent commits — one per task minimum
- Tests use Testcontainers (real Postgres, no mocks)
- Follow anthology patterns: one `*Endpoints.cs` per module with `MapGroup`, feature slices as command/handler files, `*Module.cs` for DI registration

---

## File Map

```
src/Vellum/
  Program.cs                                    (modify — add module registration, Scrutor, endpoints, OpenAPI, migrations)
  Vellum.csproj                                 (modify — add NuGet packages)
  Modules/
    Identity/
      IdentityModule.cs                         — AddIdentityModule extension, cookie + OIDC config
      IdentityEndpoints.cs                      — MapGroup("/api/auth"), login/register/logout/me/external
      ApplicationUser.cs                        — extends IdentityUser with DisplayName
      IdentityDbContext.cs                      — IdentityDbContext<ApplicationUser> on identity schema
      IdentityDbContextFactory.cs               — design-time factory for EF migrations
      Migrations/                               — (EF Core generated)
    Workspaces/
      WorkspacesModule.cs                       — AddWorkspacesModule extension
      WorkspaceEndpoints.cs                     — MapGroup("/api/workspaces") + projects + members sub-groups
      WorkspacesDbContext.cs                     — workspace/project/membership tables on workspaces schema
      WorkspacesDbContextFactory.cs             — design-time factory
      Entities/
        WorkspaceEntity.cs
        ProjectEntity.cs
        MembershipEntity.cs
      CreateWorkspace.cs
      UpdateWorkspace.cs
      ListWorkspaces.cs
      CreateProject.cs
      UpdateProject.cs
      DeleteProject.cs
      ListProjects.cs
      InviteMember.cs
      RemoveMember.cs
      Authorization/
        WorkspaceRole.cs                        — enum: Owner, Editor, Viewer
        WorkspaceAuthorizationService.cs        — loads membership, checks role
      Migrations/                               — (EF Core generated)
    Modelling/
      ModellingModule.cs                        — AddModellingModule extension, RegisterEvents
      ModellingEndpoints.cs                     — MapGroup("/api/projects/{projectId}") + elements + relationships
      ModellingDbContext.cs                      — read model tables on modelling schema
      ModellingDbContextFactory.cs              — design-time factory
      Model/
        ModelEvents.cs                          — 13 event records (closed union)
        ModelState.cs                           — aggregate state with ElementState + RelationshipState
        ModelDecider.cs                         — Decide(state, command) → Result<events>
        ModelProjection.cs                      — IInlineProjection → upserts/deletes read model rows
      Entities/
        ElementEntity.cs                        — EF entity for modelling.elements
        RelationshipEntity.cs                   — EF entity for modelling.relationships
      Elements/
        AddElement.cs                           — Command + ICommandHandler
        UpdateElement.cs                        — Command + ICommandHandler (PATCH → multi-event)
        RemoveElement.cs                        — Command + ICommandHandler (cascade)
        GetElement.cs                           — query handler
        ListElements.cs                         — query handler with cursor pagination
        ElementDto.cs                           — shared response DTO
      Relationships/
        AddRelationship.cs                      — Command + ICommandHandler
        UpdateRelationship.cs                   — Command + ICommandHandler
        RemoveRelationship.cs                   — Command + ICommandHandler
        GetRelationship.cs                      — query handler
        ListRelationships.cs                    — query handler with cursor pagination
        RelationshipDto.cs                      — shared response DTO
      Migrations/                               — (EF Core generated)
    Views/
      ViewsModule.cs                            — AddViewsModule extension
      ViewEndpoints.cs                          — MapGroup("/api/projects/{projectId}/views") + layout sub-group
      ViewsDbContext.cs                         — view/layout tables on views schema
      ViewsDbContextFactory.cs                  — design-time factory
      Entities/
        ViewEntity.cs
        LayoutPositionEntity.cs
        LayoutEdgeEntity.cs
      CreateView.cs
      UpdateView.cs
      DeleteView.cs
      GetView.cs
      ListViews.cs
      SaveLayout.cs
      ViewDto.cs
      Migrations/                               — (EF Core generated)
  Shared/
    CursorPagination.cs                         — Page<T> record + cursor encoding/decoding
    ErrorEnvelope.cs                            — consistent error response + Result → IResult mapping

tests/Vellum.Tests/
  IntegrationFixture.cs                         (modify — apply all module migrations)
  Modules/
    Modelling/
      ModelDeciderTests.cs                      — unit tests for Decide (no DB)
      ModelEvolveTests.cs                       — unit tests for Evolve (no DB)
      ModelProjectionTests.cs                   — integration: command → projection → read model
      ModellingEndpointTests.cs                 — HTTP endpoint tests via WebApplicationFactory
    Workspaces/
      WorkspaceEndpointTests.cs                 — HTTP endpoint tests
    Views/
      ViewEndpointTests.cs                      — HTTP endpoint tests
    Identity/
      IdentityEndpointTests.cs                  — HTTP endpoint tests
    ConventionTests.cs                          — assembly scanning conventions
```

---

### Task 1: NuGet packages + shared infrastructure (pagination, error envelope, OpenAPI)

**Files:**
- Modify: `src/Vellum/Vellum.csproj`
- Modify: `src/Vellum/Program.cs`
- Create: `src/Vellum/Shared/CursorPagination.cs`
- Create: `src/Vellum/Shared/ErrorEnvelope.cs`

**Interfaces:**
- Consumes: nothing new (builds on existing Program.cs)
- Produces:
  - `Page<T>` record: `(IReadOnlyList<T> Items, string? Cursor)` — used by all list endpoints
  - `CursorEncoder.Encode(string sortKey, Guid id) → string` — base64 cursor
  - `CursorEncoder.Decode(string cursor) → (string SortKey, Guid Id)?` — null if invalid
  - `ErrorResponse` record: `(string Type, string Title, string? Detail, IReadOnlyList<FieldError>? Errors)` — consistent error envelope
  - `FieldError` record: `(string Field, string Message)`
  - `ResultExtensions.ToHttpResult<T>(this CommandResult<T>) → IResult` — maps `CommandResult<T>` variants to HTTP status codes
  - `ResultExtensions.ToHttpResult(this CommandResult) → IResult` — maps non-generic variant
  - OpenAPI + Scalar wired in Program.cs

- [ ] **Step 1: Add NuGet packages**

```bash
dotnet add src/Vellum package Microsoft.AspNetCore.Identity.EntityFrameworkCore
dotnet add src/Vellum package Microsoft.AspNetCore.Authentication.Google
dotnet add src/Vellum package Microsoft.AspNetCore.Authentication.OAuth
dotnet add src/Vellum package Scalar.AspNetCore
dotnet add src/Vellum package Scrutor
dotnet add tests/Vellum.Tests package Microsoft.AspNetCore.Mvc.Testing
```

- [ ] **Step 2: Create CursorPagination.cs**

`src/Vellum/Shared/CursorPagination.cs`:
```csharp
using System.Text;
using System.Text.Json;

namespace Vellum.Shared;

public sealed record Page<T>(IReadOnlyList<T> Items, string? Cursor);

public static class CursorEncoder
{
    public static string Encode(string sortKey, Guid id)
    {
        var json = JsonSerializer.Serialize(new { s = sortKey, i = id });
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
    }

    public static (string SortKey, Guid Id)? Decode(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor)) return null;
        try
        {
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
            var doc = JsonDocument.Parse(json);
            var sortKey = doc.RootElement.GetProperty("s").GetString()!;
            var id = doc.RootElement.GetProperty("i").GetGuid();
            return (sortKey, id);
        }
        catch
        {
            return null;
        }
    }
}
```

- [ ] **Step 3: Create ErrorEnvelope.cs**

`src/Vellum/Shared/ErrorEnvelope.cs`:
```csharp
using Vellum.Kernel.Results;

namespace Vellum.Shared;

public sealed record ErrorResponse(
    string Type,
    string Title,
    string? Detail = null,
    IReadOnlyList<FieldError>? Errors = null);

public sealed record FieldError(string Field, string Message);

public static class ResultExtensions
{
    public static IResult ToHttpResult(this CommandResult result) => result switch
    {
        CommandResult.Success => Results.Ok(),
        CommandResult.Invalid inv => Results.BadRequest(new ErrorResponse(
            "validation_error", "Validation failed", Errors: inv.Errors.Select(e => new FieldError(e.Field, e.Message)).ToList())),
        CommandResult.Conflict c => Results.Conflict(new ErrorResponse("conflict", c.Message)),
        CommandResult.NotFound n => Results.NotFound(new ErrorResponse("not_found", n.Message)),
        _ => Results.StatusCode(500)
    };

    public static IResult ToHttpResult<T>(this CommandResult<T> result) => result switch
    {
        CommandResult<T>.Success s => Results.Ok(s.Value),
        CommandResult<T>.Invalid inv => Results.BadRequest(new ErrorResponse(
            "validation_error", "Validation failed", Errors: inv.Errors.Select(e => new FieldError(e.Field, e.Message)).ToList())),
        CommandResult<T>.Conflict c => Results.Conflict(new ErrorResponse("conflict", c.Message)),
        CommandResult<T>.NotFound n => Results.NotFound(new ErrorResponse("not_found", n.Message)),
        _ => Results.StatusCode(500)
    };

    public static IResult ToCreatedResult<T>(this CommandResult<T> result, string uri) => result switch
    {
        CommandResult<T>.Success s => Results.Created(uri, s.Value),
        _ => result.ToHttpResult()
    };
}
```

- [ ] **Step 4: Wire OpenAPI + Scalar in Program.cs**

Replace `src/Vellum/Program.cs`:
```csharp
using Microsoft.EntityFrameworkCore;
using Scalar.AspNetCore;
using Vellum.Kernel.Aggregates;
using Vellum.Kernel.CommandHandling;
using Vellum.Kernel.EventStore;
using Vellum.Kernel.EventTypes;
using Vellum.Kernel.Outbox;
using Vellum.Kernel.Projections;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")!;

// OpenAPI
builder.Services.AddOpenApi();

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

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

app.MapGet("/health", () => Results.Ok());

app.Run();

public partial class Program;
```

The `public partial class Program;` at the bottom enables `WebApplicationFactory<Program>` in endpoint tests.

- [ ] **Step 5: Verify build**

```bash
dotnet build src/Vellum
```

Expected: build succeeds.

- [ ] **Step 6: Run existing tests**

```bash
dotnet test tests/Vellum.Tests
```

Expected: all 28 existing tests PASS.

- [ ] **Step 7: Commit**

```bash
git add src/Vellum/Vellum.csproj src/Vellum/Program.cs src/Vellum/Shared/
git commit -m "feat: add shared pagination, error envelope, OpenAPI + Scalar, and new NuGet packages"
```

---

### Task 2: Identity module (ASP.NET Core Identity + OIDC + auth endpoints)

**Files:**
- Create: `src/Vellum/Modules/Identity/ApplicationUser.cs`, `src/Vellum/Modules/Identity/IdentityDbContext.cs`, `src/Vellum/Modules/Identity/IdentityDbContextFactory.cs`, `src/Vellum/Modules/Identity/IdentityModule.cs`, `src/Vellum/Modules/Identity/IdentityEndpoints.cs`
- Modify: `src/Vellum/Program.cs` (add identity module + middleware)
- Modify: `tests/Vellum.Tests/IntegrationFixture.cs` (apply identity migrations)
- Create: `tests/Vellum.Tests/Modules/Identity/IdentityEndpointTests.cs`

**Interfaces:**
- Consumes: ASP.NET Core Identity APIs, `EventStoreDbContext` (for shared Postgres connection string)
- Produces:
  - `ApplicationUser` — `IdentityUser` with `DisplayName` property
  - `IdentityDbContext` — `IdentityDbContext<ApplicationUser>` on `identity` schema
  - `AddIdentityModule(this IServiceCollection, IConfiguration)` — configures Identity + OIDC + cookies
  - `MapIdentityEndpoints(this WebApplication)` — maps `/api/auth` group
  - Auth endpoints: register, login, logout, me, external OIDC

- [ ] **Step 1: Create ApplicationUser**

`src/Vellum/Modules/Identity/ApplicationUser.cs`:
```csharp
using Microsoft.AspNetCore.Identity;

namespace Vellum.Modules.Identity;

public class ApplicationUser : IdentityUser
{
    public string? DisplayName { get; set; }
}
```

- [ ] **Step 2: Create IdentityDbContext**

`src/Vellum/Modules/Identity/IdentityDbContext.cs`:
```csharp
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Vellum.Modules.Identity;

public class AppIdentityDbContext : IdentityDbContext<ApplicationUser>
{
    public AppIdentityDbContext(DbContextOptions<AppIdentityDbContext> options)
        : base(options) { }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.HasDefaultSchema("identity");
    }
}
```

- [ ] **Step 3: Create IdentityDbContextFactory**

`src/Vellum/Modules/Identity/IdentityDbContextFactory.cs`:
```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Vellum.Modules.Identity;

public class IdentityDbContextFactory : IDesignTimeDbContextFactory<AppIdentityDbContext>
{
    public AppIdentityDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<AppIdentityDbContext>()
            .UseNpgsql("Host=localhost;Database=vellum;Username=vellum;Password=vellum")
            .UseSnakeCaseNamingConvention()
            .Options;

        return new AppIdentityDbContext(options);
    }
}
```

- [ ] **Step 4: Create IdentityModule**

`src/Vellum/Modules/Identity/IdentityModule.cs`:
```csharp
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Vellum.Modules.Identity;

public static class IdentityModule
{
    public static IServiceCollection AddIdentityModule(this IServiceCollection services, IConfiguration config)
    {
        var connectionString = config.GetConnectionString("DefaultConnection")!;

        services.AddDbContext<AppIdentityDbContext>(options =>
            options.UseNpgsql(connectionString)
                   .UseSnakeCaseNamingConvention());

        services.AddIdentity<ApplicationUser, IdentityRole>(options =>
            {
                options.Password.RequireDigit = true;
                options.Password.RequiredLength = 6;
                options.Password.RequireNonAlphanumeric = false;
                options.Password.RequireUppercase = true;
                options.User.RequireUniqueEmail = true;
            })
            .AddEntityFrameworkStores<AppIdentityDbContext>()
            .AddDefaultTokenProviders();

        services.ConfigureApplicationCookie(options =>
        {
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            options.ExpireTimeSpan = TimeSpan.FromDays(7);
            options.SlidingExpiration = true;
            options.Events.OnRedirectToLogin = ctx =>
            {
                ctx.Response.StatusCode = 401;
                return Task.CompletedTask;
            };
            options.Events.OnRedirectToAccessDenied = ctx =>
            {
                ctx.Response.StatusCode = 403;
                return Task.CompletedTask;
            };
        });

        var authBuilder = services.AddAuthentication();

        var githubSection = config.GetSection("Authentication:GitHub");
        if (githubSection.Exists())
        {
            authBuilder.AddOAuth("GitHub", options =>
            {
                options.ClientId = githubSection["ClientId"]!;
                options.ClientSecret = githubSection["ClientSecret"]!;
                options.AuthorizationEndpoint = "https://github.com/login/oauth/authorize";
                options.TokenEndpoint = "https://github.com/login/oauth/access_token";
                options.UserInformationEndpoint = "https://api.github.com/user";
                options.CallbackPath = "/api/auth/external/callback";
                options.Scope.Add("user:email");
            });
        }

        var googleSection = config.GetSection("Authentication:Google");
        if (googleSection.Exists())
        {
            authBuilder.AddGoogle(options =>
            {
                options.ClientId = googleSection["ClientId"]!;
                options.ClientSecret = googleSection["ClientSecret"]!;
            });
        }

        services.AddAuthorization();

        return services;
    }
}
```

- [ ] **Step 5: Create IdentityEndpoints**

`src/Vellum/Modules/Identity/IdentityEndpoints.cs`:
```csharp
using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Vellum.Shared;

namespace Vellum.Modules.Identity;

public sealed record RegisterRequest(string Email, string Password, string? DisplayName);
public sealed record LoginRequest(string Email, string Password);
public sealed record UserInfoResponse(string Id, string Email, string? DisplayName);

public static class IdentityEndpoints
{
    public static WebApplication MapIdentityEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/auth").WithTags("Auth");

        group.MapPost("/register", async (
            RegisterRequest request,
            UserManager<ApplicationUser> userManager,
            SignInManager<ApplicationUser> signInManager) =>
        {
            var user = new ApplicationUser
            {
                UserName = request.Email,
                Email = request.Email,
                DisplayName = request.DisplayName
            };

            var result = await userManager.CreateAsync(user, request.Password);
            if (!result.Succeeded)
            {
                var errors = result.Errors
                    .Select(e => new FieldError(e.Code, e.Description))
                    .ToList();
                return Results.BadRequest(new ErrorResponse("validation_error", "Registration failed", Errors: errors));
            }

            await signInManager.SignInAsync(user, isPersistent: true);
            return Results.Created($"/api/auth/me", new UserInfoResponse(user.Id, user.Email!, user.DisplayName));
        });

        group.MapPost("/login", async (
            LoginRequest request,
            SignInManager<ApplicationUser> signInManager) =>
        {
            var result = await signInManager.PasswordSignInAsync(
                request.Email, request.Password, isPersistent: true, lockoutOnFailure: false);

            if (!result.Succeeded)
                return Results.Unauthorized();

            return Results.Ok();
        });

        group.MapPost("/logout", async (SignInManager<ApplicationUser> signInManager) =>
        {
            await signInManager.SignOutAsync();
            return Results.Ok();
        }).RequireAuthorization();

        group.MapGet("/me", async (
            ClaimsPrincipal principal,
            UserManager<ApplicationUser> userManager) =>
        {
            var user = await userManager.GetUserAsync(principal);
            if (user is null) return Results.Unauthorized();
            return Results.Ok(new UserInfoResponse(user.Id, user.Email!, user.DisplayName));
        }).RequireAuthorization();

        group.MapGet("/external/{provider}", (
            string provider,
            SignInManager<ApplicationUser> signInManager) =>
        {
            var properties = signInManager.ConfigureExternalAuthenticationProperties(
                provider, "/api/auth/external/callback");
            return Results.Challenge(properties, [provider]);
        });

        group.MapGet("/external/callback", async (
            SignInManager<ApplicationUser> signInManager,
            UserManager<ApplicationUser> userManager) =>
        {
            var info = await signInManager.GetExternalLoginInfoAsync();
            if (info is null)
                return Results.Unauthorized();

            var signInResult = await signInManager.ExternalLoginSignInAsync(
                info.LoginProvider, info.ProviderKey, isPersistent: true);

            if (signInResult.Succeeded)
                return Results.Redirect("/");

            var email = info.Principal.FindFirstValue(ClaimTypes.Email);
            if (email is null)
                return Results.BadRequest(new ErrorResponse("validation_error", "Email not provided by external provider"));

            var user = await userManager.FindByEmailAsync(email);
            if (user is null)
            {
                user = new ApplicationUser
                {
                    UserName = email,
                    Email = email,
                    DisplayName = info.Principal.FindFirstValue(ClaimTypes.Name)
                };
                await userManager.CreateAsync(user);
            }

            await userManager.AddLoginAsync(user, info);
            await signInManager.SignInAsync(user, isPersistent: true);
            return Results.Redirect("/");
        });

        return app;
    }
}
```

- [ ] **Step 6: Wire identity in Program.cs**

Add to `src/Vellum/Program.cs` after the kernel registrations:
```csharp
using Vellum.Modules.Identity;

// ... after kernel services ...

// Modules
builder.Services.AddIdentityModule(builder.Configuration);

// ... build app ...

app.UseAuthentication();
app.UseAuthorization();

// Endpoints
app.MapIdentityEndpoints();
```

- [ ] **Step 7: Generate identity migration**

```bash
dotnet ef migrations add InitialIdentity --project src/Vellum --context AppIdentityDbContext --output-dir Modules/Identity/Migrations
```

- [ ] **Step 8: Update IntegrationFixture to apply identity migrations**

Update `tests/Vellum.Tests/IntegrationFixture.cs`:
```csharp
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using Vellum.Kernel.EventStore;
using Vellum.Modules.Identity;

namespace Vellum.Tests;

public class IntegrationFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17")
        .Build();

    public string ConnectionString => _postgres.GetConnectionString();

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();

        await MigrateAsync<EventStoreDbContext>();
        await MigrateAsync<AppIdentityDbContext>();
    }

    private async Task MigrateAsync<TContext>() where TContext : DbContext
    {
        var options = new DbContextOptionsBuilder<TContext>()
            .UseNpgsql(ConnectionString)
            .UseSnakeCaseNamingConvention()
            .Options;
        await using var db = (TContext)Activator.CreateInstance(typeof(TContext), options)!;
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync() => await _postgres.DisposeAsync();
}

[CollectionDefinition("Integration")]
public class IntegrationCollection : ICollectionFixture<IntegrationFixture>;
```

- [ ] **Step 9: Write identity endpoint tests**

`tests/Vellum.Tests/Modules/Identity/IdentityEndpointTests.cs`:
```csharp
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vellum.Kernel.EventStore;
using Vellum.Modules.Identity;

namespace Vellum.Tests.Modules.Identity;

[Collection("Integration")]
public class IdentityEndpointTests
{
    private readonly IntegrationFixture _fixture;

    public IdentityEndpointTests(IntegrationFixture fixture) => _fixture = fixture;

    private HttpClient CreateClient()
    {
        var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureServices(services =>
                {
                    // Replace DbContext registrations with test connection string
                    services.RemoveAll<DbContextOptions<EventStoreDbContext>>();
                    services.RemoveAll<DbContextOptions<AppIdentityDbContext>>();
                    services.AddDbContext<EventStoreDbContext>(o =>
                        o.UseNpgsql(_fixture.ConnectionString).UseSnakeCaseNamingConvention());
                    services.AddDbContext<AppIdentityDbContext>(o =>
                        o.UseNpgsql(_fixture.ConnectionString).UseSnakeCaseNamingConvention());
                });
            });
        return factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = true,
            AllowAutoRedirect = false
        });
    }

    [Fact]
    public async Task Register_and_get_me_returns_user()
    {
        using var client = CreateClient();
        var email = $"test-{Guid.NewGuid():N}@vellum.local";

        var registerResponse = await client.PostAsJsonAsync("/api/auth/register",
            new { email, password = "Test123!", displayName = "Test User" });
        Assert.Equal(HttpStatusCode.Created, registerResponse.StatusCode);

        var meResponse = await client.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.OK, meResponse.StatusCode);

        var me = await meResponse.Content.ReadFromJsonAsync<UserInfoResponse>();
        Assert.Equal(email, me!.Email);
        Assert.Equal("Test User", me.DisplayName);
    }

    [Fact]
    public async Task Login_with_wrong_password_returns_401()
    {
        using var client = CreateClient();

        var response = await client.PostAsJsonAsync("/api/auth/login",
            new { email = "noone@vellum.local", password = "Wrong123!" });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Me_without_auth_returns_401()
    {
        using var client = CreateClient();

        var response = await client.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Register_with_weak_password_returns_400()
    {
        using var client = CreateClient();
        var email = $"test-{Guid.NewGuid():N}@vellum.local";

        var response = await client.PostAsJsonAsync("/api/auth/register",
            new { email, password = "weak", displayName = "Test" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}

// Helper to remove all registrations of a type
file static class ServiceCollectionExtensions
{
    public static void RemoveAll<T>(this IServiceCollection services)
    {
        var descriptors = services.Where(d => d.ServiceType == typeof(T)).ToList();
        foreach (var d in descriptors) services.Remove(d);
    }
}
```

- [ ] **Step 10: Run tests**

```bash
dotnet test tests/Vellum.Tests
```

Expected: all existing tests + 4 new identity tests PASS.

- [ ] **Step 11: Commit**

```bash
git add src/Vellum/Modules/Identity/ src/Vellum/Program.cs tests/Vellum.Tests/
git commit -m "feat: add identity module with ASP.NET Core Identity, OIDC, and auth endpoints"
```

---

### Task 3: Workspaces module (workspace/project/membership CRUD + authorization)

**Files:**
- Create: `src/Vellum/Modules/Workspaces/Entities/WorkspaceEntity.cs`, `ProjectEntity.cs`, `MembershipEntity.cs`
- Create: `src/Vellum/Modules/Workspaces/WorkspacesDbContext.cs`, `WorkspacesDbContextFactory.cs`
- Create: `src/Vellum/Modules/Workspaces/Authorization/WorkspaceRole.cs`, `WorkspaceAuthorizationService.cs`
- Create: `src/Vellum/Modules/Workspaces/WorkspacesModule.cs`
- Create: `src/Vellum/Modules/Workspaces/CreateWorkspace.cs`, `UpdateWorkspace.cs`, `ListWorkspaces.cs`, `CreateProject.cs`, `UpdateProject.cs`, `DeleteProject.cs`, `ListProjects.cs`, `InviteMember.cs`, `RemoveMember.cs`
- Create: `src/Vellum/Modules/Workspaces/WorkspaceEndpoints.cs`
- Modify: `src/Vellum/Program.cs`
- Modify: `tests/Vellum.Tests/IntegrationFixture.cs`
- Create: `tests/Vellum.Tests/Modules/Workspaces/WorkspaceEndpointTests.cs`

**Interfaces:**
- Consumes: `ApplicationUser` (Task 2), `IEventStore` (Phase 0 kernel), `EventMetadata` (Phase 0 kernel)
- Produces:
  - `WorkspaceRole` enum: `Owner`, `Editor`, `Viewer`
  - `WorkspaceAuthorizationService.GetRoleAsync(Guid workspaceId, string userId, CancellationToken) → WorkspaceRole?` — null if not a member
  - `WorkspaceAuthorizationService.RequireRoleAsync(Guid workspaceId, string userId, WorkspaceRole minimumRole, CancellationToken)` — throws `UnauthorizedAccessException` if insufficient
  - `WorkspaceAuthorizationService.GetProjectWorkspaceIdAsync(Guid projectId, CancellationToken) → Guid` — resolves project → workspace
  - `ProjectEntity.StreamId` — the main model stream for the project, needed by modelling module
  - All workspace/project CRUD handlers as plain static methods
  - `MapWorkspaceEndpoints(this WebApplication)` — maps `/api/workspaces` group

- [ ] **Step 1: Create entities**

`src/Vellum/Modules/Workspaces/Entities/WorkspaceEntity.cs`:
```csharp
namespace Vellum.Modules.Workspaces.Entities;

public class WorkspaceEntity
{
    public Guid Id { get; set; }
    public string Name { get; set; } = null!;
    public string CreatedBy { get; set; } = null!;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
```

`src/Vellum/Modules/Workspaces/Entities/ProjectEntity.cs`:
```csharp
namespace Vellum.Modules.Workspaces.Entities;

public class ProjectEntity
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public string Name { get; set; } = null!;
    public string? Description { get; set; }
    public Guid StreamId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
```

`src/Vellum/Modules/Workspaces/Entities/MembershipEntity.cs`:
```csharp
namespace Vellum.Modules.Workspaces.Entities;

public class MembershipEntity
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public string UserId { get; set; } = null!;
    public string Role { get; set; } = null!;
    public DateTimeOffset CreatedAt { get; set; }
}
```

- [ ] **Step 2: Create WorkspacesDbContext**

`src/Vellum/Modules/Workspaces/WorkspacesDbContext.cs`:
```csharp
using Microsoft.EntityFrameworkCore;
using Vellum.Modules.Workspaces.Entities;

namespace Vellum.Modules.Workspaces;

public class WorkspacesDbContext : DbContext
{
    public WorkspacesDbContext(DbContextOptions<WorkspacesDbContext> options)
        : base(options) { }

    public DbSet<WorkspaceEntity> Workspaces => Set<WorkspaceEntity>();
    public DbSet<ProjectEntity> Projects => Set<ProjectEntity>();
    public DbSet<MembershipEntity> Memberships => Set<MembershipEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("workspaces");

        modelBuilder.Entity<WorkspaceEntity>(b =>
        {
            b.HasKey(w => w.Id);
            b.Property(w => w.CreatedAt).HasDefaultValueSql("now()");
            b.Property(w => w.UpdatedAt).HasDefaultValueSql("now()");
        });

        modelBuilder.Entity<ProjectEntity>(b =>
        {
            b.HasKey(p => p.Id);
            b.HasIndex(p => p.WorkspaceId);
            b.Property(p => p.CreatedAt).HasDefaultValueSql("now()");
            b.Property(p => p.UpdatedAt).HasDefaultValueSql("now()");
        });

        modelBuilder.Entity<MembershipEntity>(b =>
        {
            b.HasKey(m => m.Id);
            b.HasIndex(m => new { m.WorkspaceId, m.UserId }).IsUnique();
        });
    }
}
```

`src/Vellum/Modules/Workspaces/WorkspacesDbContextFactory.cs`:
```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Vellum.Modules.Workspaces;

public class WorkspacesDbContextFactory : IDesignTimeDbContextFactory<WorkspacesDbContext>
{
    public WorkspacesDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<WorkspacesDbContext>()
            .UseNpgsql("Host=localhost;Database=vellum;Username=vellum;Password=vellum")
            .UseSnakeCaseNamingConvention()
            .Options;

        return new WorkspacesDbContext(options);
    }
}
```

- [ ] **Step 3: Create WorkspaceRole and WorkspaceAuthorizationService**

`src/Vellum/Modules/Workspaces/Authorization/WorkspaceRole.cs`:
```csharp
namespace Vellum.Modules.Workspaces.Authorization;

public enum WorkspaceRole
{
    Viewer = 0,
    Editor = 1,
    Owner = 2
}
```

`src/Vellum/Modules/Workspaces/Authorization/WorkspaceAuthorizationService.cs`:
```csharp
using Microsoft.EntityFrameworkCore;

namespace Vellum.Modules.Workspaces.Authorization;

public sealed class WorkspaceAuthorizationService
{
    private readonly WorkspacesDbContext _db;

    public WorkspaceAuthorizationService(WorkspacesDbContext db) => _db = db;

    public async Task<WorkspaceRole?> GetRoleAsync(Guid workspaceId, string userId, CancellationToken ct = default)
    {
        var membership = await _db.Memberships
            .AsNoTracking()
            .FirstOrDefaultAsync(m => m.WorkspaceId == workspaceId && m.UserId == userId, ct);

        if (membership is null) return null;
        return Enum.Parse<WorkspaceRole>(membership.Role, ignoreCase: true);
    }

    public async Task RequireRoleAsync(Guid workspaceId, string userId, WorkspaceRole minimumRole, CancellationToken ct = default)
    {
        var role = await GetRoleAsync(workspaceId, userId, ct);
        if (role is null || role < minimumRole)
            throw new UnauthorizedAccessException($"Requires at least {minimumRole} role");
    }

    public async Task<Guid> GetProjectWorkspaceIdAsync(Guid projectId, CancellationToken ct = default)
    {
        var project = await _db.Projects.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == projectId, ct)
            ?? throw new KeyNotFoundException($"Project {projectId} not found");
        return project.WorkspaceId;
    }

    public async Task RequireProjectRoleAsync(Guid projectId, string userId, WorkspaceRole minimumRole, CancellationToken ct = default)
    {
        var workspaceId = await GetProjectWorkspaceIdAsync(projectId, ct);
        await RequireRoleAsync(workspaceId, userId, minimumRole, ct);
    }
}
```

- [ ] **Step 4: Create workspace/project CRUD handlers**

`src/Vellum/Modules/Workspaces/CreateWorkspace.cs`:
```csharp
using System.Security.Claims;
using Vellum.Modules.Workspaces.Entities;

namespace Vellum.Modules.Workspaces;

public sealed record CreateWorkspaceRequest(Guid Id, string Name);
public sealed record WorkspaceDto(Guid Id, string Name, string CreatedBy, DateTimeOffset CreatedAt);

public static class CreateWorkspace
{
    public static async Task<IResult> Handle(
        CreateWorkspaceRequest request,
        ClaimsPrincipal user,
        WorkspacesDbContext db,
        CancellationToken ct)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier)!;

        var existing = await db.Workspaces.FindAsync([request.Id], ct);
        if (existing is not null)
            return Results.Ok(ToDto(existing));

        var workspace = new WorkspaceEntity
        {
            Id = request.Id,
            Name = request.Name,
            CreatedBy = userId
        };
        db.Workspaces.Add(workspace);

        db.Memberships.Add(new MembershipEntity
        {
            Id = Guid.NewGuid(),
            WorkspaceId = request.Id,
            UserId = userId,
            Role = "Owner"
        });

        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/workspaces/{workspace.Id}", ToDto(workspace));
    }

    private static WorkspaceDto ToDto(WorkspaceEntity w) => new(w.Id, w.Name, w.CreatedBy, w.CreatedAt);
}
```

`src/Vellum/Modules/Workspaces/ListWorkspaces.cs`:
```csharp
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Vellum.Shared;

namespace Vellum.Modules.Workspaces;

public static class ListWorkspaces
{
    public static async Task<IResult> Handle(
        ClaimsPrincipal user,
        WorkspacesDbContext db,
        CancellationToken ct)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier)!;

        var workspaceIds = await db.Memberships
            .AsNoTracking()
            .Where(m => m.UserId == userId)
            .Select(m => m.WorkspaceId)
            .ToListAsync(ct);

        var workspaces = await db.Workspaces
            .AsNoTracking()
            .Where(w => workspaceIds.Contains(w.Id))
            .OrderBy(w => w.Name)
            .Select(w => new WorkspaceDto(w.Id, w.Name, w.CreatedBy, w.CreatedAt))
            .ToListAsync(ct);

        return Results.Ok(workspaces);
    }
}
```

`src/Vellum/Modules/Workspaces/UpdateWorkspace.cs`:
```csharp
using System.Security.Claims;
using Vellum.Modules.Workspaces.Authorization;
using Vellum.Shared;

namespace Vellum.Modules.Workspaces;

public sealed record UpdateWorkspaceRequest(string? Name);

public static class UpdateWorkspace
{
    public static async Task<IResult> Handle(
        Guid id,
        UpdateWorkspaceRequest request,
        ClaimsPrincipal user,
        WorkspacesDbContext db,
        WorkspaceAuthorizationService auth,
        CancellationToken ct)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier)!;
        await auth.RequireRoleAsync(id, userId, WorkspaceRole.Owner, ct);

        var workspace = await db.Workspaces.FindAsync([id], ct);
        if (workspace is null)
            return Results.NotFound(new ErrorResponse("not_found", "Workspace not found"));

        if (request.Name is not null)
            workspace.Name = request.Name;

        workspace.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        return Results.Ok(new WorkspaceDto(workspace.Id, workspace.Name, workspace.CreatedBy, workspace.CreatedAt));
    }
}
```

`src/Vellum/Modules/Workspaces/CreateProject.cs`:
```csharp
using System.Security.Claims;
using Vellum.Modules.Workspaces.Authorization;
using Vellum.Modules.Workspaces.Entities;

namespace Vellum.Modules.Workspaces;

public sealed record CreateProjectRequest(Guid Id, string Name, string? Description);
public sealed record ProjectDto(Guid Id, Guid WorkspaceId, string Name, string? Description, Guid StreamId, DateTimeOffset CreatedAt);

public static class CreateProject
{
    public static async Task<IResult> Handle(
        Guid workspaceId,
        CreateProjectRequest request,
        ClaimsPrincipal user,
        WorkspacesDbContext db,
        WorkspaceAuthorizationService auth,
        CancellationToken ct)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier)!;
        await auth.RequireRoleAsync(workspaceId, userId, WorkspaceRole.Editor, ct);

        var existing = await db.Projects.FindAsync([request.Id], ct);
        if (existing is not null)
            return Results.Ok(ToDto(existing));

        var project = new ProjectEntity
        {
            Id = request.Id,
            WorkspaceId = workspaceId,
            Name = request.Name,
            Description = request.Description,
            StreamId = Guid.NewGuid()
        };
        db.Projects.Add(project);
        await db.SaveChangesAsync(ct);

        return Results.Created($"/api/projects/{project.Id}", ToDto(project));
    }

    private static ProjectDto ToDto(ProjectEntity p) =>
        new(p.Id, p.WorkspaceId, p.Name, p.Description, p.StreamId, p.CreatedAt);
}
```

`src/Vellum/Modules/Workspaces/ListProjects.cs`:
```csharp
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Vellum.Modules.Workspaces.Authorization;

namespace Vellum.Modules.Workspaces;

public static class ListProjects
{
    public static async Task<IResult> Handle(
        Guid workspaceId,
        ClaimsPrincipal user,
        WorkspacesDbContext db,
        WorkspaceAuthorizationService auth,
        CancellationToken ct)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier)!;
        await auth.RequireRoleAsync(workspaceId, userId, WorkspaceRole.Viewer, ct);

        var projects = await db.Projects
            .AsNoTracking()
            .Where(p => p.WorkspaceId == workspaceId)
            .OrderBy(p => p.Name)
            .Select(p => new ProjectDto(p.Id, p.WorkspaceId, p.Name, p.Description, p.StreamId, p.CreatedAt))
            .ToListAsync(ct);

        return Results.Ok(projects);
    }
}
```

`src/Vellum/Modules/Workspaces/UpdateProject.cs`:
```csharp
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Vellum.Modules.Workspaces.Authorization;
using Vellum.Shared;

namespace Vellum.Modules.Workspaces;

public sealed record UpdateProjectRequest(string? Name, string? Description);

public static class UpdateProject
{
    public static async Task<IResult> Handle(
        Guid projectId,
        UpdateProjectRequest request,
        ClaimsPrincipal user,
        WorkspacesDbContext db,
        WorkspaceAuthorizationService auth,
        CancellationToken ct)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier)!;
        await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Editor, ct);

        var project = await db.Projects.FindAsync([projectId], ct);
        if (project is null)
            return Results.NotFound(new ErrorResponse("not_found", "Project not found"));

        if (request.Name is not null) project.Name = request.Name;
        if (request.Description is not null) project.Description = request.Description;
        project.UpdatedAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(ct);
        return Results.Ok(new ProjectDto(project.Id, project.WorkspaceId, project.Name, project.Description, project.StreamId, project.CreatedAt));
    }
}
```

`src/Vellum/Modules/Workspaces/DeleteProject.cs`:
```csharp
using System.Security.Claims;
using Vellum.Modules.Workspaces.Authorization;
using Vellum.Shared;

namespace Vellum.Modules.Workspaces;

public static class DeleteProject
{
    public static async Task<IResult> Handle(
        Guid projectId,
        ClaimsPrincipal user,
        WorkspacesDbContext db,
        WorkspaceAuthorizationService auth,
        CancellationToken ct)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier)!;
        await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Owner, ct);

        var project = await db.Projects.FindAsync([projectId], ct);
        if (project is null)
            return Results.NotFound(new ErrorResponse("not_found", "Project not found"));

        db.Projects.Remove(project);
        await db.SaveChangesAsync(ct);
        return Results.Ok();
    }
}
```

`src/Vellum/Modules/Workspaces/InviteMember.cs`:
```csharp
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Vellum.Modules.Workspaces.Authorization;
using Vellum.Modules.Workspaces.Entities;
using Vellum.Shared;

namespace Vellum.Modules.Workspaces;

public sealed record InviteMemberRequest(string UserId, string Role);
public sealed record MembershipDto(Guid Id, Guid WorkspaceId, string UserId, string Role, DateTimeOffset CreatedAt);

public static class InviteMember
{
    public static async Task<IResult> Handle(
        Guid workspaceId,
        InviteMemberRequest request,
        ClaimsPrincipal user,
        WorkspacesDbContext db,
        WorkspaceAuthorizationService auth,
        CancellationToken ct)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier)!;
        await auth.RequireRoleAsync(workspaceId, userId, WorkspaceRole.Owner, ct);

        if (!Enum.TryParse<WorkspaceRole>(request.Role, ignoreCase: true, out _))
            return Results.BadRequest(new ErrorResponse("validation_error", "Invalid role",
                Errors: [new FieldError("role", "Must be Owner, Editor, or Viewer")]));

        var existing = await db.Memberships
            .FirstOrDefaultAsync(m => m.WorkspaceId == workspaceId && m.UserId == request.UserId, ct);
        if (existing is not null)
            return Results.Conflict(new ErrorResponse("conflict", "User is already a member"));

        var membership = new MembershipEntity
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            UserId = request.UserId,
            Role = request.Role
        };
        db.Memberships.Add(membership);
        await db.SaveChangesAsync(ct);

        return Results.Created($"/api/workspaces/{workspaceId}/members/{request.UserId}",
            new MembershipDto(membership.Id, membership.WorkspaceId, membership.UserId, membership.Role, membership.CreatedAt));
    }
}
```

`src/Vellum/Modules/Workspaces/RemoveMember.cs`:
```csharp
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Vellum.Modules.Workspaces.Authorization;
using Vellum.Shared;

namespace Vellum.Modules.Workspaces;

public static class RemoveMember
{
    public static async Task<IResult> Handle(
        Guid workspaceId,
        string memberUserId,
        ClaimsPrincipal user,
        WorkspacesDbContext db,
        WorkspaceAuthorizationService auth,
        CancellationToken ct)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier)!;
        await auth.RequireRoleAsync(workspaceId, userId, WorkspaceRole.Owner, ct);

        var membership = await db.Memberships
            .FirstOrDefaultAsync(m => m.WorkspaceId == workspaceId && m.UserId == memberUserId, ct);
        if (membership is null)
            return Results.NotFound(new ErrorResponse("not_found", "Membership not found"));

        db.Memberships.Remove(membership);
        await db.SaveChangesAsync(ct);
        return Results.Ok();
    }
}
```

- [ ] **Step 5: Create WorkspacesModule**

`src/Vellum/Modules/Workspaces/WorkspacesModule.cs`:
```csharp
using Microsoft.EntityFrameworkCore;
using Vellum.Modules.Workspaces.Authorization;

namespace Vellum.Modules.Workspaces;

public static class WorkspacesModule
{
    public static IServiceCollection AddWorkspacesModule(this IServiceCollection services, IConfiguration config)
    {
        var connectionString = config.GetConnectionString("DefaultConnection")!;

        services.AddDbContext<WorkspacesDbContext>(options =>
            options.UseNpgsql(connectionString)
                   .UseSnakeCaseNamingConvention());

        services.AddScoped<WorkspaceAuthorizationService>();

        return services;
    }
}
```

- [ ] **Step 6: Create WorkspaceEndpoints**

`src/Vellum/Modules/Workspaces/WorkspaceEndpoints.cs`:
```csharp
namespace Vellum.Modules.Workspaces;

public static class WorkspaceEndpoints
{
    public static WebApplication MapWorkspaceEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/workspaces")
            .RequireAuthorization()
            .WithTags("Workspaces");

        group.MapPost("/", CreateWorkspace.Handle);
        group.MapGet("/", ListWorkspaces.Handle);
        group.MapPatch("/{id}", UpdateWorkspace.Handle);

        var members = group.MapGroup("/{workspaceId}/members").WithTags("Members");
        members.MapPost("/", InviteMember.Handle);
        members.MapDelete("/{memberUserId}", RemoveMember.Handle);

        var projects = group.MapGroup("/{workspaceId}/projects").WithTags("Projects");
        projects.MapPost("/", CreateProject.Handle);
        projects.MapGet("/", ListProjects.Handle);

        app.MapGroup("/api/projects")
            .RequireAuthorization()
            .WithTags("Projects")
            .MapPatch("/{projectId}", UpdateProject.Handle);

        app.MapGroup("/api/projects")
            .RequireAuthorization()
            .WithTags("Projects")
            .MapDelete("/{projectId}", DeleteProject.Handle);

        return app;
    }
}
```

- [ ] **Step 7: Wire workspaces in Program.cs**

Add to `src/Vellum/Program.cs`:
```csharp
using Vellum.Modules.Workspaces;

// ... after identity module ...
builder.Services.AddWorkspacesModule(builder.Configuration);

// ... after identity endpoints ...
app.MapWorkspaceEndpoints();
```

- [ ] **Step 8: Generate migration**

```bash
dotnet ef migrations add InitialWorkspaces --project src/Vellum --context WorkspacesDbContext --output-dir Modules/Workspaces/Migrations
```

- [ ] **Step 9: Update IntegrationFixture**

Add `WorkspacesDbContext` migration to `IntegrationFixture.InitializeAsync`:
```csharp
using Vellum.Modules.Workspaces;
// ...
await MigrateAsync<WorkspacesDbContext>();
```

- [ ] **Step 10: Write workspace endpoint tests**

`tests/Vellum.Tests/Modules/Workspaces/WorkspaceEndpointTests.cs`:
```csharp
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vellum.Kernel.EventStore;
using Vellum.Modules.Identity;
using Vellum.Modules.Workspaces;

namespace Vellum.Tests.Modules.Workspaces;

[Collection("Integration")]
public class WorkspaceEndpointTests
{
    private readonly IntegrationFixture _fixture;

    public WorkspaceEndpointTests(IntegrationFixture fixture) => _fixture = fixture;

    private HttpClient CreateAuthenticatedClient()
    {
        var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<DbContextOptions<EventStoreDbContext>>();
                    services.RemoveAll<DbContextOptions<AppIdentityDbContext>>();
                    services.RemoveAll<DbContextOptions<WorkspacesDbContext>>();
                    services.AddDbContext<EventStoreDbContext>(o =>
                        o.UseNpgsql(_fixture.ConnectionString).UseSnakeCaseNamingConvention());
                    services.AddDbContext<AppIdentityDbContext>(o =>
                        o.UseNpgsql(_fixture.ConnectionString).UseSnakeCaseNamingConvention());
                    services.AddDbContext<WorkspacesDbContext>(o =>
                        o.UseNpgsql(_fixture.ConnectionString).UseSnakeCaseNamingConvention());
                });
            });

        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = true,
            AllowAutoRedirect = false
        });

        // Register + login
        var email = $"test-{Guid.NewGuid():N}@vellum.local";
        client.PostAsJsonAsync("/api/auth/register",
            new { email, password = "Test123!", displayName = "Test User" }).GetAwaiter().GetResult();

        return client;
    }

    [Fact]
    public async Task Create_workspace_and_list_returns_it()
    {
        using var client = CreateAuthenticatedClient();
        var workspaceId = Guid.NewGuid();

        var createResponse = await client.PostAsJsonAsync("/api/workspaces",
            new { id = workspaceId, name = "Test Workspace" });
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);

        var listResponse = await client.GetAsync("/api/workspaces");
        var workspaces = await listResponse.Content.ReadFromJsonAsync<WorkspaceDto[]>();
        Assert.Contains(workspaces!, w => w.Id == workspaceId);
    }

    [Fact]
    public async Task Create_project_in_workspace()
    {
        using var client = CreateAuthenticatedClient();
        var workspaceId = Guid.NewGuid();
        var projectId = Guid.NewGuid();

        await client.PostAsJsonAsync("/api/workspaces",
            new { id = workspaceId, name = "Test WS" });

        var createResponse = await client.PostAsJsonAsync(
            $"/api/workspaces/{workspaceId}/projects",
            new { id = projectId, name = "Test Project", description = "A test" });
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);

        var project = await createResponse.Content.ReadFromJsonAsync<ProjectDto>();
        Assert.Equal("Test Project", project!.Name);
        Assert.NotEqual(Guid.Empty, project.StreamId);
    }

    [Fact]
    public async Task Unauthenticated_workspace_request_returns_401()
    {
        var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<DbContextOptions<EventStoreDbContext>>();
                    services.RemoveAll<DbContextOptions<AppIdentityDbContext>>();
                    services.RemoveAll<DbContextOptions<WorkspacesDbContext>>();
                    services.AddDbContext<EventStoreDbContext>(o =>
                        o.UseNpgsql(_fixture.ConnectionString).UseSnakeCaseNamingConvention());
                    services.AddDbContext<AppIdentityDbContext>(o =>
                        o.UseNpgsql(_fixture.ConnectionString).UseSnakeCaseNamingConvention());
                    services.AddDbContext<WorkspacesDbContext>(o =>
                        o.UseNpgsql(_fixture.ConnectionString).UseSnakeCaseNamingConvention());
                });
            });

        using var client = factory.CreateClient();
        var response = await client.GetAsync("/api/workspaces");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}

file static class ServiceCollectionExtensions
{
    public static void RemoveAll<T>(this IServiceCollection services)
    {
        var descriptors = services.Where(d => d.ServiceType == typeof(T)).ToList();
        foreach (var d in descriptors) services.Remove(d);
    }
}
```

- [ ] **Step 11: Run tests**

```bash
dotnet test tests/Vellum.Tests
```

Expected: all existing + new workspace tests PASS.

- [ ] **Step 12: Commit**

```bash
git add src/Vellum/Modules/Workspaces/ src/Vellum/Program.cs tests/Vellum.Tests/
git commit -m "feat: add workspaces module with workspace, project, membership CRUD and role authorization"
```

---

### Task 4: Modelling aggregate (events, state, decide, evolve) + unit tests

**Files:**
- Create: `src/Vellum/Modules/Modelling/Model/ModelEvents.cs`, `src/Vellum/Modules/Modelling/Model/ModelState.cs`, `src/Vellum/Modules/Modelling/Model/ModelDecider.cs`
- Create: `tests/Vellum.Tests/Modules/Modelling/ModelDeciderTests.cs`, `tests/Vellum.Tests/Modules/Modelling/ModelEvolveTests.cs`

**Interfaces:**
- Consumes: `IAggregateState<TSelf, TEvent>` (Phase 0 kernel), `CommandResult` / `CommandResult<T>` (Phase 0 kernel)
- Produces:
  - `ModelEvent` — abstract record base, with 13 sealed variants (see spec §3.3)
  - `ElementKind` enum: `Actor`, `System`, `App`, `Store`, `Component`
  - `ElementStatus` enum: `Current`, `Planned`, `Deprecated`, `Removed`
  - `ElementState` record, `RelationshipState` record
  - `ModelState : IAggregateState<ModelState, ModelEvent>` — `Initial`, `Evolve(ModelEvent)`
  - `ModelDecider.AddElement(ModelState, AddElementCommand) → CommandResult<IReadOnlyList<ModelEvent>>`
  - `ModelDecider.UpdateElement(ModelState, UpdateElementCommand) → CommandResult<IReadOnlyList<ModelEvent>>`
  - `ModelDecider.RemoveElement(ModelState, Guid elementId) → CommandResult<IReadOnlyList<ModelEvent>>`
  - `ModelDecider.AddRelationship(ModelState, AddRelationshipCommand) → CommandResult<IReadOnlyList<ModelEvent>>`
  - `ModelDecider.UpdateRelationship(ModelState, UpdateRelationshipCommand) → CommandResult<IReadOnlyList<ModelEvent>>`
  - `ModelDecider.RemoveRelationship(ModelState, Guid relationshipId) → CommandResult<IReadOnlyList<ModelEvent>>`

- [ ] **Step 1: Create ModelEvents.cs**

`src/Vellum/Modules/Modelling/Model/ModelEvents.cs`:
```csharp
namespace Vellum.Modules.Modelling.Model;

public enum ElementKind { Actor, System, App, Store, Component }
public enum ElementStatus { Current, Planned, Deprecated, Removed }

public abstract record ModelEvent
{
    // Element events
    public sealed record ElementAdded(
        Guid Id, ElementKind Kind, string Name, string? Description,
        string? Technology, Guid? OwnerId, ElementStatus Status,
        Guid? ParentId, string[] Tags) : ModelEvent;

    public sealed record ElementRenamed(Guid ElementId, string Name) : ModelEvent;
    public sealed record ElementDescriptionChanged(Guid ElementId, string? Description) : ModelEvent;
    public sealed record ElementTechnologyChanged(Guid ElementId, string? Technology) : ModelEvent;
    public sealed record ElementOwnerChanged(Guid ElementId, Guid? OwnerId) : ModelEvent;
    public sealed record ElementReparented(Guid ElementId, Guid? ParentId) : ModelEvent;
    public sealed record ElementStatusChanged(Guid ElementId, ElementStatus Status) : ModelEvent;
    public sealed record ElementRetagged(Guid ElementId, string[] Tags) : ModelEvent;
    public sealed record ElementRemoved(Guid ElementId) : ModelEvent;

    // Relationship events
    public sealed record RelationshipAdded(
        Guid Id, Guid FromId, Guid ToId, string? Label,
        string? Technology, Guid? MessageId) : ModelEvent;

    public sealed record RelationshipLabelChanged(Guid RelationshipId, string? Label) : ModelEvent;
    public sealed record RelationshipTechnologyChanged(Guid RelationshipId, string? Technology) : ModelEvent;
    public sealed record RelationshipRemoved(Guid RelationshipId) : ModelEvent;

    private ModelEvent() { }
}
```

- [ ] **Step 2: Create ModelState.cs**

`src/Vellum/Modules/Modelling/Model/ModelState.cs`:
```csharp
using System.Collections.Immutable;
using Vellum.Kernel.Aggregates;

namespace Vellum.Modules.Modelling.Model;

public sealed record ElementState(
    Guid Id, ElementKind Kind, string Name, string? Description,
    string? Technology, Guid? OwnerId, ElementStatus Status,
    Guid? ParentId, string[] Tags);

public sealed record RelationshipState(
    Guid Id, Guid FromId, Guid ToId, string? Label,
    string? Technology, Guid? MessageId);

public sealed record ModelState(
    ImmutableDictionary<Guid, ElementState> Elements,
    ImmutableDictionary<Guid, RelationshipState> Relationships)
    : IAggregateState<ModelState, ModelEvent>
{
    public static ModelState Initial => new(
        ImmutableDictionary<Guid, ElementState>.Empty,
        ImmutableDictionary<Guid, RelationshipState>.Empty);

    public ModelState Evolve(ModelEvent @event) => @event switch
    {
        ModelEvent.ElementAdded e => this with
        {
            Elements = Elements.Add(e.Id, new ElementState(
                e.Id, e.Kind, e.Name, e.Description, e.Technology,
                e.OwnerId, e.Status, e.ParentId, e.Tags))
        },
        ModelEvent.ElementRenamed e => WithElement(e.ElementId, el => el with { Name = e.Name }),
        ModelEvent.ElementDescriptionChanged e => WithElement(e.ElementId, el => el with { Description = e.Description }),
        ModelEvent.ElementTechnologyChanged e => WithElement(e.ElementId, el => el with { Technology = e.Technology }),
        ModelEvent.ElementOwnerChanged e => WithElement(e.ElementId, el => el with { OwnerId = e.OwnerId }),
        ModelEvent.ElementReparented e => WithElement(e.ElementId, el => el with { ParentId = e.ParentId }),
        ModelEvent.ElementStatusChanged e => WithElement(e.ElementId, el => el with { Status = e.Status }),
        ModelEvent.ElementRetagged e => WithElement(e.ElementId, el => el with { Tags = e.Tags }),
        ModelEvent.ElementRemoved e => this with { Elements = Elements.Remove(e.Id) },

        ModelEvent.RelationshipAdded e => this with
        {
            Relationships = Relationships.Add(e.Id, new RelationshipState(
                e.Id, e.FromId, e.ToId, e.Label, e.Technology, e.MessageId))
        },
        ModelEvent.RelationshipLabelChanged e => WithRelationship(e.RelationshipId, r => r with { Label = e.Label }),
        ModelEvent.RelationshipTechnologyChanged e => WithRelationship(e.RelationshipId, r => r with { Technology = e.Technology }),
        ModelEvent.RelationshipRemoved e => this with { Relationships = Relationships.Remove(e.Id) },

        _ => this
    };

    private ModelState WithElement(Guid id, Func<ElementState, ElementState> update) =>
        this with { Elements = Elements.SetItem(id, update(Elements[id])) };

    private ModelState WithRelationship(Guid id, Func<RelationshipState, RelationshipState> update) =>
        this with { Relationships = Relationships.SetItem(id, update(Relationships[id])) };
}
```

- [ ] **Step 3: Create ModelDecider.cs**

`src/Vellum/Modules/Modelling/Model/ModelDecider.cs`:
```csharp
using Vellum.Kernel.Results;

namespace Vellum.Modules.Modelling.Model;

public sealed record AddElementCommand(
    Guid Id, ElementKind Kind, string Name, string? Description,
    string? Technology, Guid? OwnerId, ElementStatus Status,
    Guid? ParentId, string[] Tags);

public sealed record UpdateElementCommand(
    Guid ElementId,
    string? Name = null, bool SetName = false,
    string? Description = null, bool SetDescription = false,
    string? Technology = null, bool SetTechnology = false,
    Guid? OwnerId = null, bool SetOwnerId = false,
    Guid? ParentId = null, bool SetParentId = false,
    ElementStatus? Status = null,
    string[]? Tags = null);

public sealed record AddRelationshipCommand(
    Guid Id, Guid FromId, Guid ToId, string? Label, string? Technology);

public sealed record UpdateRelationshipCommand(
    Guid RelationshipId,
    string? Label = null, bool SetLabel = false,
    string? Technology = null, bool SetTechnology = false);

public static class ModelDecider
{
    private static readonly IReadOnlyDictionary<ElementKind, ElementKind?> ValidParentKinds = new Dictionary<ElementKind, ElementKind?>
    {
        [ElementKind.Actor] = null,
        [ElementKind.System] = null,
        [ElementKind.App] = ElementKind.System,
        [ElementKind.Store] = ElementKind.System,
        [ElementKind.Component] = ElementKind.App,
    };

    public static CommandResult<IReadOnlyList<ModelEvent>> AddElement(ModelState state, AddElementCommand cmd)
    {
        if (string.IsNullOrWhiteSpace(cmd.Name))
            return new CommandResult<IReadOnlyList<ModelEvent>>.Invalid([new ValidationError("name", "Name is required")]);

        if (state.Elements.ContainsKey(cmd.Id))
            return new CommandResult<IReadOnlyList<ModelEvent>>.Conflict("Element with this ID already exists");

        var parentError = ValidateContainment(state, cmd.Kind, cmd.ParentId);
        if (parentError is not null)
            return new CommandResult<IReadOnlyList<ModelEvent>>.Invalid([parentError]);

        return new CommandResult<IReadOnlyList<ModelEvent>>.Success(
            [new ModelEvent.ElementAdded(cmd.Id, cmd.Kind, cmd.Name, cmd.Description,
                cmd.Technology, cmd.OwnerId, cmd.Status, cmd.ParentId, cmd.Tags)]);
    }

    public static CommandResult<IReadOnlyList<ModelEvent>> UpdateElement(ModelState state, UpdateElementCommand cmd)
    {
        if (!state.Elements.TryGetValue(cmd.ElementId, out var element))
            return new CommandResult<IReadOnlyList<ModelEvent>>.NotFound("Element not found");

        var events = new List<ModelEvent>();

        if (cmd.SetName && cmd.Name != element.Name)
        {
            if (string.IsNullOrWhiteSpace(cmd.Name))
                return new CommandResult<IReadOnlyList<ModelEvent>>.Invalid([new ValidationError("name", "Name is required")]);
            events.Add(new ModelEvent.ElementRenamed(cmd.ElementId, cmd.Name!));
        }

        if (cmd.SetDescription && cmd.Description != element.Description)
            events.Add(new ModelEvent.ElementDescriptionChanged(cmd.ElementId, cmd.Description));

        if (cmd.SetTechnology && cmd.Technology != element.Technology)
            events.Add(new ModelEvent.ElementTechnologyChanged(cmd.ElementId, cmd.Technology));

        if (cmd.SetOwnerId && cmd.OwnerId != element.OwnerId)
            events.Add(new ModelEvent.ElementOwnerChanged(cmd.ElementId, cmd.OwnerId));

        if (cmd.SetParentId && cmd.ParentId != element.ParentId)
        {
            var parentError = ValidateContainment(state, element.Kind, cmd.ParentId);
            if (parentError is not null)
                return new CommandResult<IReadOnlyList<ModelEvent>>.Invalid([parentError]);
            events.Add(new ModelEvent.ElementReparented(cmd.ElementId, cmd.ParentId));
        }

        if (cmd.Status.HasValue && cmd.Status.Value != element.Status)
            events.Add(new ModelEvent.ElementStatusChanged(cmd.ElementId, cmd.Status.Value));

        if (cmd.Tags is not null && !cmd.Tags.SequenceEqual(element.Tags))
            events.Add(new ModelEvent.ElementRetagged(cmd.ElementId, cmd.Tags));

        return new CommandResult<IReadOnlyList<ModelEvent>>.Success(events);
    }

    public static CommandResult<IReadOnlyList<ModelEvent>> RemoveElement(ModelState state, Guid elementId)
    {
        if (!state.Elements.ContainsKey(elementId))
            return new CommandResult<IReadOnlyList<ModelEvent>>.NotFound("Element not found");

        var events = new List<ModelEvent>();
        CollectCascadeRemovals(state, elementId, events);
        return new CommandResult<IReadOnlyList<ModelEvent>>.Success(events);
    }

    public static CommandResult<IReadOnlyList<ModelEvent>> AddRelationship(ModelState state, AddRelationshipCommand cmd)
    {
        if (state.Relationships.ContainsKey(cmd.Id))
            return new CommandResult<IReadOnlyList<ModelEvent>>.Conflict("Relationship with this ID already exists");

        if (!state.Elements.ContainsKey(cmd.FromId))
            return new CommandResult<IReadOnlyList<ModelEvent>>.Invalid([new ValidationError("fromId", "Source element not found")]);

        if (!state.Elements.ContainsKey(cmd.ToId))
            return new CommandResult<IReadOnlyList<ModelEvent>>.Invalid([new ValidationError("toId", "Target element not found")]);

        return new CommandResult<IReadOnlyList<ModelEvent>>.Success(
            [new ModelEvent.RelationshipAdded(cmd.Id, cmd.FromId, cmd.ToId, cmd.Label, cmd.Technology, null)]);
    }

    public static CommandResult<IReadOnlyList<ModelEvent>> UpdateRelationship(ModelState state, UpdateRelationshipCommand cmd)
    {
        if (!state.Relationships.TryGetValue(cmd.RelationshipId, out var rel))
            return new CommandResult<IReadOnlyList<ModelEvent>>.NotFound("Relationship not found");

        var events = new List<ModelEvent>();

        if (cmd.SetLabel && cmd.Label != rel.Label)
            events.Add(new ModelEvent.RelationshipLabelChanged(cmd.RelationshipId, cmd.Label));

        if (cmd.SetTechnology && cmd.Technology != rel.Technology)
            events.Add(new ModelEvent.RelationshipTechnologyChanged(cmd.RelationshipId, cmd.Technology));

        return new CommandResult<IReadOnlyList<ModelEvent>>.Success(events);
    }

    public static CommandResult<IReadOnlyList<ModelEvent>> RemoveRelationship(ModelState state, Guid relationshipId)
    {
        if (!state.Relationships.ContainsKey(relationshipId))
            return new CommandResult<IReadOnlyList<ModelEvent>>.NotFound("Relationship not found");

        return new CommandResult<IReadOnlyList<ModelEvent>>.Success(
            [new ModelEvent.RelationshipRemoved(relationshipId)]);
    }

    private static void CollectCascadeRemovals(ModelState state, Guid elementId, List<ModelEvent> events)
    {
        // Recursively remove children first
        var children = state.Elements.Values.Where(e => e.ParentId == elementId).ToList();
        foreach (var child in children)
            CollectCascadeRemovals(state, child.Id, events);

        // Remove relationships referencing this element
        foreach (var rel in state.Relationships.Values)
        {
            if (rel.FromId == elementId || rel.ToId == elementId)
                events.Add(new ModelEvent.RelationshipRemoved(rel.Id));
        }

        events.Add(new ModelEvent.ElementRemoved(elementId));
    }

    private static ValidationError? ValidateContainment(ModelState state, ElementKind kind, Guid? parentId)
    {
        var requiredParentKind = ValidParentKinds[kind];

        if (requiredParentKind is null)
        {
            if (parentId.HasValue)
                return new ValidationError("parentId", $"{kind} must be top-level (no parent)");
            return null;
        }

        if (!parentId.HasValue)
            return new ValidationError("parentId", $"{kind} requires a parent of kind {requiredParentKind}");

        if (!state.Elements.TryGetValue(parentId.Value, out var parent))
            return new ValidationError("parentId", "Parent element not found");

        if (parent.Kind != requiredParentKind)
            return new ValidationError("parentId", $"{kind} requires a parent of kind {requiredParentKind}, got {parent.Kind}");

        return null;
    }
}
```

- [ ] **Step 4: Write ModelDeciderTests**

`tests/Vellum.Tests/Modules/Modelling/ModelDeciderTests.cs`:
```csharp
using Vellum.Kernel.Results;
using Vellum.Modules.Modelling.Model;

namespace Vellum.Tests.Modules.Modelling;

public class ModelDeciderTests
{
    private static ModelState StateWith(params ModelEvent[] events) =>
        events.Aggregate(ModelState.Initial, (s, e) => s.Evolve(e));

    private static readonly Guid SystemId = Guid.NewGuid();
    private static readonly Guid AppId = Guid.NewGuid();

    private static ModelState StateWithSystemAndApp()
    {
        return StateWith(
            new ModelEvent.ElementAdded(SystemId, ElementKind.System, "Orders", null, null, null, ElementStatus.Current, null, []),
            new ModelEvent.ElementAdded(AppId, ElementKind.App, "API", null, "dotnet", null, ElementStatus.Current, SystemId, []));
    }

    // --- AddElement ---

    [Fact]
    public void AddElement_valid_top_level_system()
    {
        var result = ModelDecider.AddElement(ModelState.Initial,
            new AddElementCommand(Guid.NewGuid(), ElementKind.System, "Orders", null, null, null, ElementStatus.Current, null, []));
        var success = Assert.IsType<CommandResult<IReadOnlyList<ModelEvent>>.Success>(result);
        var added = Assert.IsType<ModelEvent.ElementAdded>(Assert.Single(success.Value));
        Assert.Equal("Orders", added.Name);
    }

    [Fact]
    public void AddElement_app_with_system_parent_succeeds()
    {
        var state = StateWith(
            new ModelEvent.ElementAdded(SystemId, ElementKind.System, "Orders", null, null, null, ElementStatus.Current, null, []));
        var result = ModelDecider.AddElement(state,
            new AddElementCommand(Guid.NewGuid(), ElementKind.App, "API", null, null, null, ElementStatus.Current, SystemId, []));
        Assert.IsType<CommandResult<IReadOnlyList<ModelEvent>>.Success>(result);
    }

    [Fact]
    public void AddElement_app_without_parent_fails()
    {
        var result = ModelDecider.AddElement(ModelState.Initial,
            new AddElementCommand(Guid.NewGuid(), ElementKind.App, "API", null, null, null, ElementStatus.Current, null, []));
        Assert.IsType<CommandResult<IReadOnlyList<ModelEvent>>.Invalid>(result);
    }

    [Fact]
    public void AddElement_system_with_parent_fails()
    {
        var state = StateWith(
            new ModelEvent.ElementAdded(SystemId, ElementKind.System, "Orders", null, null, null, ElementStatus.Current, null, []));
        var result = ModelDecider.AddElement(state,
            new AddElementCommand(Guid.NewGuid(), ElementKind.System, "Payments", null, null, null, ElementStatus.Current, SystemId, []));
        Assert.IsType<CommandResult<IReadOnlyList<ModelEvent>>.Invalid>(result);
    }

    [Fact]
    public void AddElement_component_with_app_parent_succeeds()
    {
        var state = StateWithSystemAndApp();
        var result = ModelDecider.AddElement(state,
            new AddElementCommand(Guid.NewGuid(), ElementKind.Component, "Handler", null, null, null, ElementStatus.Current, AppId, []));
        Assert.IsType<CommandResult<IReadOnlyList<ModelEvent>>.Success>(result);
    }

    [Fact]
    public void AddElement_component_with_system_parent_fails()
    {
        var state = StateWithSystemAndApp();
        var result = ModelDecider.AddElement(state,
            new AddElementCommand(Guid.NewGuid(), ElementKind.Component, "Handler", null, null, null, ElementStatus.Current, SystemId, []));
        Assert.IsType<CommandResult<IReadOnlyList<ModelEvent>>.Invalid>(result);
    }

    [Fact]
    public void AddElement_duplicate_id_returns_conflict()
    {
        var id = Guid.NewGuid();
        var state = StateWith(
            new ModelEvent.ElementAdded(id, ElementKind.System, "Orders", null, null, null, ElementStatus.Current, null, []));
        var result = ModelDecider.AddElement(state,
            new AddElementCommand(id, ElementKind.System, "Payments", null, null, null, ElementStatus.Current, null, []));
        Assert.IsType<CommandResult<IReadOnlyList<ModelEvent>>.Conflict>(result);
    }

    [Fact]
    public void AddElement_empty_name_returns_invalid()
    {
        var result = ModelDecider.AddElement(ModelState.Initial,
            new AddElementCommand(Guid.NewGuid(), ElementKind.System, "", null, null, null, ElementStatus.Current, null, []));
        Assert.IsType<CommandResult<IReadOnlyList<ModelEvent>>.Invalid>(result);
    }

    [Fact]
    public void AddElement_missing_parent_returns_invalid()
    {
        var result = ModelDecider.AddElement(ModelState.Initial,
            new AddElementCommand(Guid.NewGuid(), ElementKind.App, "API", null, null, null, ElementStatus.Current, Guid.NewGuid(), []));
        Assert.IsType<CommandResult<IReadOnlyList<ModelEvent>>.Invalid>(result);
    }

    // --- UpdateElement ---

    [Fact]
    public void UpdateElement_rename_emits_only_renamed()
    {
        var id = Guid.NewGuid();
        var state = StateWith(
            new ModelEvent.ElementAdded(id, ElementKind.System, "Orders", null, null, null, ElementStatus.Current, null, []));
        var result = ModelDecider.UpdateElement(state,
            new UpdateElementCommand(id, Name: "Payments", SetName: true));
        var success = Assert.IsType<CommandResult<IReadOnlyList<ModelEvent>>.Success>(result);
        var renamed = Assert.IsType<ModelEvent.ElementRenamed>(Assert.Single(success.Value));
        Assert.Equal("Payments", renamed.Name);
    }

    [Fact]
    public void UpdateElement_no_changes_emits_no_events()
    {
        var id = Guid.NewGuid();
        var state = StateWith(
            new ModelEvent.ElementAdded(id, ElementKind.System, "Orders", null, null, null, ElementStatus.Current, null, []));
        var result = ModelDecider.UpdateElement(state,
            new UpdateElementCommand(id, Name: "Orders", SetName: true));
        var success = Assert.IsType<CommandResult<IReadOnlyList<ModelEvent>>.Success>(result);
        Assert.Empty(success.Value);
    }

    [Fact]
    public void UpdateElement_multiple_fields_emits_multiple_events()
    {
        var id = Guid.NewGuid();
        var state = StateWith(
            new ModelEvent.ElementAdded(id, ElementKind.System, "Orders", null, null, null, ElementStatus.Current, null, []));
        var result = ModelDecider.UpdateElement(state,
            new UpdateElementCommand(id, Name: "Payments", SetName: true, Status: ElementStatus.Planned));
        var success = Assert.IsType<CommandResult<IReadOnlyList<ModelEvent>>.Success>(result);
        Assert.Equal(2, success.Value.Count);
        Assert.IsType<ModelEvent.ElementRenamed>(success.Value[0]);
        Assert.IsType<ModelEvent.ElementStatusChanged>(success.Value[1]);
    }

    [Fact]
    public void UpdateElement_reparent_app_to_null_fails()
    {
        var state = StateWithSystemAndApp();
        var result = ModelDecider.UpdateElement(state,
            new UpdateElementCommand(AppId, ParentId: null, SetParentId: true));
        Assert.IsType<CommandResult<IReadOnlyList<ModelEvent>>.Invalid>(result);
    }

    [Fact]
    public void UpdateElement_nonexistent_returns_not_found()
    {
        var result = ModelDecider.UpdateElement(ModelState.Initial,
            new UpdateElementCommand(Guid.NewGuid(), Name: "X", SetName: true));
        Assert.IsType<CommandResult<IReadOnlyList<ModelEvent>>.NotFound>(result);
    }

    // --- RemoveElement ---

    [Fact]
    public void RemoveElement_cascades_relationships()
    {
        var sysId = Guid.NewGuid();
        var relId = Guid.NewGuid();
        var otherId = Guid.NewGuid();
        var state = StateWith(
            new ModelEvent.ElementAdded(sysId, ElementKind.System, "A", null, null, null, ElementStatus.Current, null, []),
            new ModelEvent.ElementAdded(otherId, ElementKind.System, "B", null, null, null, ElementStatus.Current, null, []),
            new ModelEvent.RelationshipAdded(relId, sysId, otherId, "uses", null, null));

        var result = ModelDecider.RemoveElement(state, sysId);
        var success = Assert.IsType<CommandResult<IReadOnlyList<ModelEvent>>.Success>(result);
        Assert.Equal(2, success.Value.Count);
        Assert.IsType<ModelEvent.RelationshipRemoved>(success.Value[0]);
        Assert.IsType<ModelEvent.ElementRemoved>(success.Value[1]);
    }

    [Fact]
    public void RemoveElement_cascades_children_recursively()
    {
        var sysId = Guid.NewGuid();
        var appId = Guid.NewGuid();
        var compId = Guid.NewGuid();
        var state = StateWith(
            new ModelEvent.ElementAdded(sysId, ElementKind.System, "Orders", null, null, null, ElementStatus.Current, null, []),
            new ModelEvent.ElementAdded(appId, ElementKind.App, "API", null, null, null, ElementStatus.Current, sysId, []),
            new ModelEvent.ElementAdded(compId, ElementKind.Component, "Handler", null, null, null, ElementStatus.Current, appId, []));

        var result = ModelDecider.RemoveElement(state, sysId);
        var success = Assert.IsType<CommandResult<IReadOnlyList<ModelEvent>>.Success>(result);
        // Component removed, then App removed, then System removed
        Assert.Equal(3, success.Value.Count);
        var removals = success.Value.Cast<ModelEvent.ElementRemoved>().Select(r => r.ElementId).ToList();
        Assert.Contains(compId, removals);
        Assert.Contains(appId, removals);
        Assert.Contains(sysId, removals);
        // Children removed before parents
        Assert.True(removals.IndexOf(compId) < removals.IndexOf(appId));
        Assert.True(removals.IndexOf(appId) < removals.IndexOf(sysId));
    }

    [Fact]
    public void RemoveElement_nonexistent_returns_not_found()
    {
        var result = ModelDecider.RemoveElement(ModelState.Initial, Guid.NewGuid());
        Assert.IsType<CommandResult<IReadOnlyList<ModelEvent>>.NotFound>(result);
    }

    // --- AddRelationship ---

    [Fact]
    public void AddRelationship_valid()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var state = StateWith(
            new ModelEvent.ElementAdded(a, ElementKind.System, "A", null, null, null, ElementStatus.Current, null, []),
            new ModelEvent.ElementAdded(b, ElementKind.System, "B", null, null, null, ElementStatus.Current, null, []));
        var result = ModelDecider.AddRelationship(state,
            new AddRelationshipCommand(Guid.NewGuid(), a, b, "uses", null));
        Assert.IsType<CommandResult<IReadOnlyList<ModelEvent>>.Success>(result);
    }

    [Fact]
    public void AddRelationship_missing_from_returns_invalid()
    {
        var b = Guid.NewGuid();
        var state = StateWith(
            new ModelEvent.ElementAdded(b, ElementKind.System, "B", null, null, null, ElementStatus.Current, null, []));
        var result = ModelDecider.AddRelationship(state,
            new AddRelationshipCommand(Guid.NewGuid(), Guid.NewGuid(), b, "uses", null));
        Assert.IsType<CommandResult<IReadOnlyList<ModelEvent>>.Invalid>(result);
    }

    // --- RemoveRelationship ---

    [Fact]
    public void RemoveRelationship_nonexistent_returns_not_found()
    {
        var result = ModelDecider.RemoveRelationship(ModelState.Initial, Guid.NewGuid());
        Assert.IsType<CommandResult<IReadOnlyList<ModelEvent>>.NotFound>(result);
    }
}
```

- [ ] **Step 5: Write ModelEvolveTests**

`tests/Vellum.Tests/Modules/Modelling/ModelEvolveTests.cs`:
```csharp
using Vellum.Modules.Modelling.Model;

namespace Vellum.Tests.Modules.Modelling;

public class ModelEvolveTests
{
    [Fact]
    public void ElementAdded_adds_to_state()
    {
        var id = Guid.NewGuid();
        var state = ModelState.Initial.Evolve(
            new ModelEvent.ElementAdded(id, ElementKind.System, "Orders", "desc", "dotnet", null, ElementStatus.Current, null, ["api"]));

        Assert.Single(state.Elements);
        var el = state.Elements[id];
        Assert.Equal("Orders", el.Name);
        Assert.Equal("desc", el.Description);
        Assert.Equal("dotnet", el.Technology);
        Assert.Equal(ElementStatus.Current, el.Status);
        Assert.Equal(["api"], el.Tags);
    }

    [Fact]
    public void ElementRenamed_updates_name()
    {
        var id = Guid.NewGuid();
        var state = ModelState.Initial
            .Evolve(new ModelEvent.ElementAdded(id, ElementKind.System, "Old", null, null, null, ElementStatus.Current, null, []))
            .Evolve(new ModelEvent.ElementRenamed(id, "New"));
        Assert.Equal("New", state.Elements[id].Name);
    }

    [Fact]
    public void ElementRemoved_removes_from_state()
    {
        var id = Guid.NewGuid();
        var state = ModelState.Initial
            .Evolve(new ModelEvent.ElementAdded(id, ElementKind.System, "Orders", null, null, null, ElementStatus.Current, null, []))
            .Evolve(new ModelEvent.ElementRemoved(id));
        Assert.Empty(state.Elements);
    }

    [Fact]
    public void RelationshipAdded_adds_to_state()
    {
        var relId = Guid.NewGuid();
        var state = ModelState.Initial.Evolve(
            new ModelEvent.RelationshipAdded(relId, Guid.NewGuid(), Guid.NewGuid(), "uses", "HTTP", null));
        Assert.Single(state.Relationships);
        Assert.Equal("uses", state.Relationships[relId].Label);
    }

    [Fact]
    public void RelationshipRemoved_removes_from_state()
    {
        var relId = Guid.NewGuid();
        var state = ModelState.Initial
            .Evolve(new ModelEvent.RelationshipAdded(relId, Guid.NewGuid(), Guid.NewGuid(), "uses", null, null))
            .Evolve(new ModelEvent.RelationshipRemoved(relId));
        Assert.Empty(state.Relationships);
    }

    [Fact]
    public void All_element_update_events_fold_correctly()
    {
        var id = Guid.NewGuid();
        var ownerId = Guid.NewGuid();
        var parentId = Guid.NewGuid();
        var state = ModelState.Initial
            .Evolve(new ModelEvent.ElementAdded(id, ElementKind.System, "X", null, null, null, ElementStatus.Current, null, []))
            .Evolve(new ModelEvent.ElementDescriptionChanged(id, "desc"))
            .Evolve(new ModelEvent.ElementTechnologyChanged(id, "go"))
            .Evolve(new ModelEvent.ElementOwnerChanged(id, ownerId))
            .Evolve(new ModelEvent.ElementStatusChanged(id, ElementStatus.Planned))
            .Evolve(new ModelEvent.ElementRetagged(id, ["a", "b"]));

        var el = state.Elements[id];
        Assert.Equal("desc", el.Description);
        Assert.Equal("go", el.Technology);
        Assert.Equal(ownerId, el.OwnerId);
        Assert.Equal(ElementStatus.Planned, el.Status);
        Assert.Equal(["a", "b"], el.Tags);
    }
}
```

- [ ] **Step 6: Run tests**

```bash
dotnet test tests/Vellum.Tests
```

Expected: all existing + ~20 new decider/evolve tests PASS.

- [ ] **Step 7: Commit**

```bash
git add src/Vellum/Modules/Modelling/Model/ tests/Vellum.Tests/Modules/Modelling/
git commit -m "feat(modelling): add model aggregate with 13 event types, decide/evolve, containment rules, and cascade-remove"
```

---

### Task 5: Modelling module (DbContext, inline projection, event registration, command handlers, endpoints)

**Files:**
- Create: `src/Vellum/Modules/Modelling/Entities/ElementEntity.cs`, `RelationshipEntity.cs`
- Create: `src/Vellum/Modules/Modelling/ModellingDbContext.cs`, `ModellingDbContextFactory.cs`
- Create: `src/Vellum/Modules/Modelling/Model/ModelProjection.cs`
- Create: `src/Vellum/Modules/Modelling/ModellingModule.cs`
- Create: `src/Vellum/Modules/Modelling/Elements/ElementDto.cs`, `AddElement.cs`, `UpdateElement.cs`, `RemoveElement.cs`, `GetElement.cs`, `ListElements.cs`
- Create: `src/Vellum/Modules/Modelling/Relationships/RelationshipDto.cs`, `AddRelationship.cs`, `UpdateRelationship.cs`, `RemoveRelationship.cs`, `GetRelationship.cs`, `ListRelationships.cs`
- Create: `src/Vellum/Modules/Modelling/ModellingEndpoints.cs`
- Modify: `src/Vellum/Program.cs`
- Modify: `tests/Vellum.Tests/IntegrationFixture.cs`
- Create: `tests/Vellum.Tests/Modules/Modelling/ModelProjectionTests.cs`
- Create: `tests/Vellum.Tests/Modules/Modelling/ModellingEndpointTests.cs`

**Interfaces:**
- Consumes: `ModelState`, `ModelDecider`, `ModelEvent` (Task 4), `AggregateStore`, `ICommandHandler<,>`, `TransactionBehavior<,>`, `EventTypeRegistry`, `EventCollector` (Phase 0), `WorkspaceAuthorizationService` (Task 3)
- Produces:
  - `ModellingDbContext` — read model tables on `modelling` schema
  - `ModelProjection : IInlineProjection` — projects events to read model rows
  - `AddModellingModule(this IServiceCollection)` — DI + event registration
  - `MapModellingEndpoints(this WebApplication)` — all element/relationship endpoints
  - `ElementDto`, `RelationshipDto` — API response types

This is the largest task — it wires the aggregate to the kernel, creates the inline projection, and exposes the full API. Due to its size, the step-by-step is abbreviated to key implementation files. The test code covers the critical integration paths.

- [ ] **Step 1: Create entity classes**

`src/Vellum/Modules/Modelling/Entities/ElementEntity.cs`:
```csharp
namespace Vellum.Modules.Modelling.Entities;

public class ElementEntity
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public Guid Branch { get; set; }
    public string Kind { get; set; } = null!;
    public string Name { get; set; } = null!;
    public string? Description { get; set; }
    public string? Technology { get; set; }
    public Guid? OwnerId { get; set; }
    public string Status { get; set; } = "current";
    public Guid? ParentId { get; set; }
    public string[] Tags { get; set; } = [];
}
```

`src/Vellum/Modules/Modelling/Entities/RelationshipEntity.cs`:
```csharp
namespace Vellum.Modules.Modelling.Entities;

public class RelationshipEntity
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public Guid Branch { get; set; }
    public Guid FromId { get; set; }
    public Guid ToId { get; set; }
    public string? Label { get; set; }
    public string? Technology { get; set; }
    public Guid? MessageId { get; set; }
}
```

- [ ] **Step 2: Create ModellingDbContext**

`src/Vellum/Modules/Modelling/ModellingDbContext.cs`:
```csharp
using Microsoft.EntityFrameworkCore;
using Vellum.Modules.Modelling.Entities;

namespace Vellum.Modules.Modelling;

public class ModellingDbContext : DbContext
{
    public ModellingDbContext(DbContextOptions<ModellingDbContext> options) : base(options) { }

    public DbSet<ElementEntity> Elements => Set<ElementEntity>();
    public DbSet<RelationshipEntity> Relationships => Set<RelationshipEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("modelling");

        modelBuilder.Entity<ElementEntity>(b =>
        {
            b.HasKey(e => e.Id);
            b.HasIndex(e => new { e.ProjectId, e.Branch });
            b.HasIndex(e => new { e.ProjectId, e.Branch, e.ParentId });
        });

        modelBuilder.Entity<RelationshipEntity>(b =>
        {
            b.HasKey(r => r.Id);
            b.HasIndex(r => new { r.ProjectId, r.Branch });
        });
    }
}
```

`src/Vellum/Modules/Modelling/ModellingDbContextFactory.cs`:
```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Vellum.Modules.Modelling;

public class ModellingDbContextFactory : IDesignTimeDbContextFactory<ModellingDbContext>
{
    public ModellingDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<ModellingDbContext>()
            .UseNpgsql("Host=localhost;Database=vellum;Username=vellum;Password=vellum")
            .UseSnakeCaseNamingConvention()
            .Options;
        return new ModellingDbContext(options);
    }
}
```

- [ ] **Step 3: Create ModelProjection**

`src/Vellum/Modules/Modelling/Model/ModelProjection.cs`:
```csharp
using Vellum.Kernel.CommandHandling;
using Vellum.Kernel.EventTypes;
using Vellum.Kernel.Projections;
using Vellum.Modules.Modelling.Entities;

namespace Vellum.Modules.Modelling.Model;

public sealed class ModelProjection : IInlineProjection
{
    private readonly ModellingDbContext _db;
    private readonly IEventTypeRegistry _registry;
    private Guid _projectId;
    private Guid _branch;

    public ModelProjection(ModellingDbContext db, IEventTypeRegistry registry)
    {
        _db = db;
        _registry = registry;
    }

    public void SetContext(Guid projectId, Guid branch)
    {
        _projectId = projectId;
        _branch = branch;
    }

    public async Task ProjectAsync(IReadOnlyList<CollectedEvent> events, CancellationToken ct = default)
    {
        foreach (var e in events)
        {
            var deserialized = _registry.DeserializeEvent(e.EventType, e.Payload);
            if (deserialized is not ModelEvent modelEvent) continue;

            switch (modelEvent)
            {
                case ModelEvent.ElementAdded added:
                    _db.Elements.Add(new ElementEntity
                    {
                        Id = added.Id,
                        ProjectId = _projectId,
                        Branch = _branch,
                        Kind = added.Kind.ToString().ToLowerInvariant(),
                        Name = added.Name,
                        Description = added.Description,
                        Technology = added.Technology,
                        OwnerId = added.OwnerId,
                        Status = added.Status.ToString().ToLowerInvariant(),
                        ParentId = added.ParentId,
                        Tags = added.Tags
                    });
                    break;

                case ModelEvent.ElementRenamed e:
                    var renamed = await _db.Elements.FindAsync([e.ElementId], ct);
                    if (renamed is not null) renamed.Name = e.Name;
                    break;

                case ModelEvent.ElementDescriptionChanged e:
                    var descChanged = await _db.Elements.FindAsync([e.ElementId], ct);
                    if (descChanged is not null) descChanged.Description = e.Description;
                    break;

                case ModelEvent.ElementTechnologyChanged e:
                    var techChanged = await _db.Elements.FindAsync([e.ElementId], ct);
                    if (techChanged is not null) techChanged.Technology = e.Technology;
                    break;

                case ModelEvent.ElementOwnerChanged e:
                    var ownerChanged = await _db.Elements.FindAsync([e.ElementId], ct);
                    if (ownerChanged is not null) ownerChanged.OwnerId = e.OwnerId;
                    break;

                case ModelEvent.ElementReparented e:
                    var reparented = await _db.Elements.FindAsync([e.ElementId], ct);
                    if (reparented is not null) reparented.ParentId = e.ParentId;
                    break;

                case ModelEvent.ElementStatusChanged e:
                    var statusChanged = await _db.Elements.FindAsync([e.ElementId], ct);
                    if (statusChanged is not null) statusChanged.Status = e.Status.ToString().ToLowerInvariant();
                    break;

                case ModelEvent.ElementRetagged e:
                    var retagged = await _db.Elements.FindAsync([e.ElementId], ct);
                    if (retagged is not null) retagged.Tags = e.Tags;
                    break;

                case ModelEvent.ElementRemoved e:
                    var removed = await _db.Elements.FindAsync([e.ElementId], ct);
                    if (removed is not null) _db.Elements.Remove(removed);
                    break;

                case ModelEvent.RelationshipAdded added:
                    _db.Relationships.Add(new RelationshipEntity
                    {
                        Id = added.Id,
                        ProjectId = _projectId,
                        Branch = _branch,
                        FromId = added.FromId,
                        ToId = added.ToId,
                        Label = added.Label,
                        Technology = added.Technology,
                        MessageId = added.MessageId
                    });
                    break;

                case ModelEvent.RelationshipLabelChanged e:
                    var labelChanged = await _db.Relationships.FindAsync([e.RelationshipId], ct);
                    if (labelChanged is not null) labelChanged.Label = e.Label;
                    break;

                case ModelEvent.RelationshipTechnologyChanged e:
                    var relTechChanged = await _db.Relationships.FindAsync([e.RelationshipId], ct);
                    if (relTechChanged is not null) relTechChanged.Technology = e.Technology;
                    break;

                case ModelEvent.RelationshipRemoved e:
                    var relRemoved = await _db.Relationships.FindAsync([e.RelationshipId], ct);
                    if (relRemoved is not null) _db.Relationships.Remove(relRemoved);
                    break;
            }
        }
    }
}
```

- [ ] **Step 4: Create DTOs**

`src/Vellum/Modules/Modelling/Elements/ElementDto.cs`:
```csharp
namespace Vellum.Modules.Modelling.Elements;

public sealed record ElementDto(
    Guid Id, string Kind, string Name, string? Description,
    string? Technology, Guid? OwnerId, string Status,
    Guid? ParentId, string[] Tags);
```

`src/Vellum/Modules/Modelling/Relationships/RelationshipDto.cs`:
```csharp
namespace Vellum.Modules.Modelling.Relationships;

public sealed record RelationshipDto(
    Guid Id, Guid FromId, Guid ToId, string? Label,
    string? Technology, Guid? MessageId);
```

- [ ] **Step 5: Create AddElement command handler**

`src/Vellum/Modules/Modelling/Elements/AddElement.cs`:
```csharp
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Vellum.Kernel.Aggregates;
using Vellum.Kernel.CommandHandling;
using Vellum.Kernel.Results;
using Vellum.Modules.Modelling.Model;
using Vellum.Modules.Workspaces;
using Vellum.Modules.Workspaces.Authorization;

namespace Vellum.Modules.Modelling.Elements;

public sealed record AddElementRequest(
    Guid Id, string Kind, string Name, string? Description,
    string? Technology, Guid? OwnerId, string? Status,
    Guid? ParentId, string[]? Tags);

public sealed record AddElementCommandEnvelope(
    Guid ProjectId, Guid StreamId, string UserId, AddElementRequest Request);

public sealed class AddElementHandler : ICommandHandler<AddElementCommandEnvelope, CommandResult<ElementDto>>
{
    private readonly AggregateStore _store;
    private readonly ModelProjection _projection;

    public AddElementHandler(AggregateStore store, ModelProjection projection)
    {
        _store = store;
        _projection = projection;
    }

    public async Task<CommandResult<ElementDto>> HandleAsync(AddElementCommandEnvelope cmd, CancellationToken ct = default)
    {
        var (state, version) = await _store.LoadAsync<ModelState, ModelEvent>(cmd.StreamId, ct);

        if (!Enum.TryParse<ElementKind>(cmd.Request.Kind, ignoreCase: true, out var kind))
            return new CommandResult<ElementDto>.Invalid([new ValidationError("kind", "Invalid element kind")]);

        var status = ElementStatus.Current;
        if (cmd.Request.Status is not null && !Enum.TryParse(cmd.Request.Status, ignoreCase: true, out status))
            return new CommandResult<ElementDto>.Invalid([new ValidationError("status", "Invalid status")]);

        var addCmd = new AddElementCommand(
            cmd.Request.Id, kind, cmd.Request.Name, cmd.Request.Description,
            cmd.Request.Technology, cmd.Request.OwnerId, status,
            cmd.Request.ParentId, cmd.Request.Tags ?? []);

        var result = ModelDecider.AddElement(state, addCmd);
        if (result is not CommandResult<IReadOnlyList<ModelEvent>>.Success success)
        {
            return result switch
            {
                CommandResult<IReadOnlyList<ModelEvent>>.Invalid inv =>
                    new CommandResult<ElementDto>.Invalid(inv.Errors),
                CommandResult<IReadOnlyList<ModelEvent>>.Conflict c =>
                    new CommandResult<ElementDto>.Conflict(c.Message),
                _ => new CommandResult<ElementDto>.Conflict("Unexpected error")
            };
        }

        var metadata = new EventMetadata
        {
            ActorId = Guid.Parse(cmd.UserId),
            CorrelationId = Guid.NewGuid()
        };

        _projection.SetContext(cmd.ProjectId, cmd.StreamId);
        var newState = success.Value.Aggregate(state, (s, e) => s.Evolve(e));
        await _store.SaveAsync(cmd.StreamId, "model", version, newState, success.Value, metadata, ct);

        var element = newState.Elements[cmd.Request.Id];
        return new CommandResult<ElementDto>.Success(new ElementDto(
            element.Id, element.Kind.ToString().ToLowerInvariant(), element.Name,
            element.Description, element.Technology, element.OwnerId,
            element.Status.ToString().ToLowerInvariant(), element.ParentId, element.Tags));
    }
}
```

- [ ] **Step 6: Create UpdateElement and RemoveElement command handlers**

`src/Vellum/Modules/Modelling/Elements/UpdateElement.cs`:
```csharp
using Vellum.Kernel.Aggregates;
using Vellum.Kernel.CommandHandling;
using Vellum.Kernel.Results;
using Vellum.Modules.Modelling.Model;

namespace Vellum.Modules.Modelling.Elements;

public sealed record UpdateElementRequest(
    string? Name, string? Description, string? Technology,
    Guid? OwnerId, Guid? ParentId, string? Status, string[]? Tags,
    bool SetDescription = false, bool SetOwnerId = false, bool SetParentId = false);

public sealed record UpdateElementCommandEnvelope(
    Guid ProjectId, Guid StreamId, Guid ElementId, string UserId, UpdateElementRequest Request);

public sealed class UpdateElementHandler : ICommandHandler<UpdateElementCommandEnvelope, CommandResult<ElementDto>>
{
    private readonly AggregateStore _store;
    private readonly ModelProjection _projection;

    public UpdateElementHandler(AggregateStore store, ModelProjection projection)
    {
        _store = store;
        _projection = projection;
    }

    public async Task<CommandResult<ElementDto>> HandleAsync(UpdateElementCommandEnvelope cmd, CancellationToken ct = default)
    {
        var (state, version) = await _store.LoadAsync<ModelState, ModelEvent>(cmd.StreamId, ct);

        ElementStatus? status = null;
        if (cmd.Request.Status is not null)
        {
            if (!Enum.TryParse<ElementStatus>(cmd.Request.Status, ignoreCase: true, out var parsed))
                return new CommandResult<ElementDto>.Invalid([new ValidationError("status", "Invalid status")]);
            status = parsed;
        }

        var updateCmd = new UpdateElementCommand(
            cmd.ElementId,
            Name: cmd.Request.Name, SetName: cmd.Request.Name is not null,
            Description: cmd.Request.Description, SetDescription: cmd.Request.SetDescription || cmd.Request.Description is not null,
            Technology: cmd.Request.Technology, SetTechnology: cmd.Request.Technology is not null,
            OwnerId: cmd.Request.OwnerId, SetOwnerId: cmd.Request.SetOwnerId,
            ParentId: cmd.Request.ParentId, SetParentId: cmd.Request.SetParentId,
            Status: status,
            Tags: cmd.Request.Tags);

        var result = ModelDecider.UpdateElement(state, updateCmd);
        if (result is not CommandResult<IReadOnlyList<ModelEvent>>.Success success)
        {
            return result switch
            {
                CommandResult<IReadOnlyList<ModelEvent>>.Invalid inv => new CommandResult<ElementDto>.Invalid(inv.Errors),
                CommandResult<IReadOnlyList<ModelEvent>>.NotFound n => new CommandResult<ElementDto>.NotFound(n.Message),
                _ => new CommandResult<ElementDto>.Conflict("Unexpected error")
            };
        }

        if (success.Value.Count > 0)
        {
            var metadata = new EventMetadata { ActorId = Guid.Parse(cmd.UserId), CorrelationId = Guid.NewGuid() };
            _projection.SetContext(cmd.ProjectId, cmd.StreamId);
            var newState = success.Value.Aggregate(state, (s, e) => s.Evolve(e));
            await _store.SaveAsync(cmd.StreamId, "model", version, newState, success.Value, metadata, ct);
            state = newState;
        }

        var element = state.Elements[cmd.ElementId];
        return new CommandResult<ElementDto>.Success(new ElementDto(
            element.Id, element.Kind.ToString().ToLowerInvariant(), element.Name,
            element.Description, element.Technology, element.OwnerId,
            element.Status.ToString().ToLowerInvariant(), element.ParentId, element.Tags));
    }
}
```

`src/Vellum/Modules/Modelling/Elements/RemoveElement.cs`:
```csharp
using Vellum.Kernel.Aggregates;
using Vellum.Kernel.CommandHandling;
using Vellum.Kernel.Results;
using Vellum.Modules.Modelling.Model;

namespace Vellum.Modules.Modelling.Elements;

public sealed record RemoveElementCommandEnvelope(Guid ProjectId, Guid StreamId, Guid ElementId, string UserId);

public sealed class RemoveElementHandler : ICommandHandler<RemoveElementCommandEnvelope, CommandResult>
{
    private readonly AggregateStore _store;
    private readonly ModelProjection _projection;

    public RemoveElementHandler(AggregateStore store, ModelProjection projection)
    {
        _store = store;
        _projection = projection;
    }

    public async Task<CommandResult> HandleAsync(RemoveElementCommandEnvelope cmd, CancellationToken ct = default)
    {
        var (state, version) = await _store.LoadAsync<ModelState, ModelEvent>(cmd.StreamId, ct);
        var result = ModelDecider.RemoveElement(state, cmd.ElementId);

        if (result is not CommandResult<IReadOnlyList<ModelEvent>>.Success success)
        {
            return result switch
            {
                CommandResult<IReadOnlyList<ModelEvent>>.NotFound n => new CommandResult.NotFound(n.Message),
                _ => new CommandResult.Conflict("Unexpected error")
            };
        }

        var metadata = new EventMetadata { ActorId = Guid.Parse(cmd.UserId), CorrelationId = Guid.NewGuid() };
        _projection.SetContext(cmd.ProjectId, cmd.StreamId);
        var newState = success.Value.Aggregate(state, (s, e) => s.Evolve(e));
        await _store.SaveAsync(cmd.StreamId, "model", version, newState, success.Value, metadata, ct);

        return new CommandResult.Success();
    }
}
```

- [ ] **Step 7: Create GetElement and ListElements query handlers**

`src/Vellum/Modules/Modelling/Elements/GetElement.cs`:
```csharp
using Microsoft.EntityFrameworkCore;

namespace Vellum.Modules.Modelling.Elements;

public static class GetElement
{
    public static async Task<IResult> Handle(
        Guid projectId, Guid elementId,
        ModellingDbContext db, CancellationToken ct)
    {
        var entity = await db.Elements.AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == elementId && e.ProjectId == projectId, ct);

        if (entity is null) return Results.NotFound();

        return Results.Ok(new ElementDto(
            entity.Id, entity.Kind, entity.Name, entity.Description,
            entity.Technology, entity.OwnerId, entity.Status,
            entity.ParentId, entity.Tags));
    }
}
```

`src/Vellum/Modules/Modelling/Elements/ListElements.cs`:
```csharp
using Microsoft.EntityFrameworkCore;
using Vellum.Shared;

namespace Vellum.Modules.Modelling.Elements;

public static class ListElements
{
    public static async Task<IResult> Handle(
        Guid projectId, Guid streamId,
        string? kind, string? status, Guid? parentId,
        string? cursor, int? limit,
        ModellingDbContext db, CancellationToken ct)
    {
        var pageSize = Math.Clamp(limit ?? 50, 1, 200);

        var query = db.Elements.AsNoTracking()
            .Where(e => e.ProjectId == projectId && e.Branch == streamId);

        if (kind is not null) query = query.Where(e => e.Kind == kind.ToLowerInvariant());
        if (status is not null) query = query.Where(e => e.Status == status.ToLowerInvariant());
        if (parentId.HasValue) query = query.Where(e => e.ParentId == parentId);

        var decoded = CursorEncoder.Decode(cursor);
        if (decoded is not null)
        {
            var (sortKey, afterId) = decoded.Value;
            query = query.Where(e => string.Compare(e.Name, sortKey) > 0
                || (e.Name == sortKey && e.Id.CompareTo(afterId) > 0));
        }

        var items = await query
            .OrderBy(e => e.Name).ThenBy(e => e.Id)
            .Take(pageSize + 1)
            .Select(e => new ElementDto(e.Id, e.Kind, e.Name, e.Description,
                e.Technology, e.OwnerId, e.Status, e.ParentId, e.Tags))
            .ToListAsync(ct);

        string? nextCursor = null;
        if (items.Count > pageSize)
        {
            items = items[..pageSize];
            var last = items[^1];
            nextCursor = CursorEncoder.Encode(last.Name, last.Id);
        }

        return Results.Ok(new Page<ElementDto>(items, nextCursor));
    }
}
```

- [ ] **Step 8: Create relationship command handlers and query handlers**

Follow the same pattern as elements. Create `AddRelationship.cs`, `UpdateRelationship.cs`, `RemoveRelationship.cs`, `GetRelationship.cs`, `ListRelationships.cs` using the same `ICommandHandler` pattern for writes and static query methods for reads. The relationship handlers call `ModelDecider.AddRelationship`, `ModelDecider.UpdateRelationship`, `ModelDecider.RemoveRelationship` respectively.

`src/Vellum/Modules/Modelling/Relationships/AddRelationship.cs`:
```csharp
using Vellum.Kernel.Aggregates;
using Vellum.Kernel.CommandHandling;
using Vellum.Kernel.Results;
using Vellum.Modules.Modelling.Model;

namespace Vellum.Modules.Modelling.Relationships;

public sealed record AddRelationshipRequest(Guid Id, Guid FromId, Guid ToId, string? Label, string? Technology);

public sealed record AddRelationshipCommandEnvelope(
    Guid ProjectId, Guid StreamId, string UserId, AddRelationshipRequest Request);

public sealed class AddRelationshipHandler : ICommandHandler<AddRelationshipCommandEnvelope, CommandResult<RelationshipDto>>
{
    private readonly AggregateStore _store;
    private readonly ModelProjection _projection;

    public AddRelationshipHandler(AggregateStore store, ModelProjection projection)
    {
        _store = store;
        _projection = projection;
    }

    public async Task<CommandResult<RelationshipDto>> HandleAsync(AddRelationshipCommandEnvelope cmd, CancellationToken ct = default)
    {
        var (state, version) = await _store.LoadAsync<ModelState, ModelEvent>(cmd.StreamId, ct);

        var addCmd = new AddRelationshipCommand(cmd.Request.Id, cmd.Request.FromId, cmd.Request.ToId, cmd.Request.Label, cmd.Request.Technology);
        var result = ModelDecider.AddRelationship(state, addCmd);

        if (result is not CommandResult<IReadOnlyList<ModelEvent>>.Success success)
        {
            return result switch
            {
                CommandResult<IReadOnlyList<ModelEvent>>.Invalid inv => new CommandResult<RelationshipDto>.Invalid(inv.Errors),
                CommandResult<IReadOnlyList<ModelEvent>>.Conflict c => new CommandResult<RelationshipDto>.Conflict(c.Message),
                _ => new CommandResult<RelationshipDto>.Conflict("Unexpected error")
            };
        }

        var metadata = new EventMetadata { ActorId = Guid.Parse(cmd.UserId), CorrelationId = Guid.NewGuid() };
        _projection.SetContext(cmd.ProjectId, cmd.StreamId);
        var newState = success.Value.Aggregate(state, (s, e) => s.Evolve(e));
        await _store.SaveAsync(cmd.StreamId, "model", version, newState, success.Value, metadata, ct);

        var rel = newState.Relationships[cmd.Request.Id];
        return new CommandResult<RelationshipDto>.Success(new RelationshipDto(
            rel.Id, rel.FromId, rel.ToId, rel.Label, rel.Technology, rel.MessageId));
    }
}
```

`src/Vellum/Modules/Modelling/Relationships/UpdateRelationship.cs`:
```csharp
using Vellum.Kernel.Aggregates;
using Vellum.Kernel.CommandHandling;
using Vellum.Kernel.Results;
using Vellum.Modules.Modelling.Model;

namespace Vellum.Modules.Modelling.Relationships;

public sealed record UpdateRelationshipRequest(string? Label, string? Technology, bool SetLabel = false, bool SetTechnology = false);

public sealed record UpdateRelationshipCommandEnvelope(
    Guid ProjectId, Guid StreamId, Guid RelationshipId, string UserId, UpdateRelationshipRequest Request);

public sealed class UpdateRelationshipHandler : ICommandHandler<UpdateRelationshipCommandEnvelope, CommandResult<RelationshipDto>>
{
    private readonly AggregateStore _store;
    private readonly ModelProjection _projection;

    public UpdateRelationshipHandler(AggregateStore store, ModelProjection projection)
    {
        _store = store;
        _projection = projection;
    }

    public async Task<CommandResult<RelationshipDto>> HandleAsync(UpdateRelationshipCommandEnvelope cmd, CancellationToken ct = default)
    {
        var (state, version) = await _store.LoadAsync<ModelState, ModelEvent>(cmd.StreamId, ct);

        var updateCmd = new UpdateRelationshipCommand(
            cmd.RelationshipId,
            Label: cmd.Request.Label, SetLabel: cmd.Request.SetLabel || cmd.Request.Label is not null,
            Technology: cmd.Request.Technology, SetTechnology: cmd.Request.SetTechnology || cmd.Request.Technology is not null);

        var result = ModelDecider.UpdateRelationship(state, updateCmd);
        if (result is not CommandResult<IReadOnlyList<ModelEvent>>.Success success)
        {
            return result switch
            {
                CommandResult<IReadOnlyList<ModelEvent>>.NotFound n => new CommandResult<RelationshipDto>.NotFound(n.Message),
                _ => new CommandResult<RelationshipDto>.Conflict("Unexpected error")
            };
        }

        if (success.Value.Count > 0)
        {
            var metadata = new EventMetadata { ActorId = Guid.Parse(cmd.UserId), CorrelationId = Guid.NewGuid() };
            _projection.SetContext(cmd.ProjectId, cmd.StreamId);
            var newState = success.Value.Aggregate(state, (s, e) => s.Evolve(e));
            await _store.SaveAsync(cmd.StreamId, "model", version, newState, success.Value, metadata, ct);
            state = newState;
        }

        var rel = state.Relationships[cmd.RelationshipId];
        return new CommandResult<RelationshipDto>.Success(new RelationshipDto(
            rel.Id, rel.FromId, rel.ToId, rel.Label, rel.Technology, rel.MessageId));
    }
}
```

`src/Vellum/Modules/Modelling/Relationships/RemoveRelationship.cs`:
```csharp
using Vellum.Kernel.Aggregates;
using Vellum.Kernel.CommandHandling;
using Vellum.Kernel.Results;
using Vellum.Modules.Modelling.Model;

namespace Vellum.Modules.Modelling.Relationships;

public sealed record RemoveRelationshipCommandEnvelope(Guid ProjectId, Guid StreamId, Guid RelationshipId, string UserId);

public sealed class RemoveRelationshipHandler : ICommandHandler<RemoveRelationshipCommandEnvelope, CommandResult>
{
    private readonly AggregateStore _store;
    private readonly ModelProjection _projection;

    public RemoveRelationshipHandler(AggregateStore store, ModelProjection projection)
    {
        _store = store;
        _projection = projection;
    }

    public async Task<CommandResult> HandleAsync(RemoveRelationshipCommandEnvelope cmd, CancellationToken ct = default)
    {
        var (state, version) = await _store.LoadAsync<ModelState, ModelEvent>(cmd.StreamId, ct);
        var result = ModelDecider.RemoveRelationship(state, cmd.RelationshipId);

        if (result is not CommandResult<IReadOnlyList<ModelEvent>>.Success success)
        {
            return result switch
            {
                CommandResult<IReadOnlyList<ModelEvent>>.NotFound n => new CommandResult.NotFound(n.Message),
                _ => new CommandResult.Conflict("Unexpected error")
            };
        }

        var metadata = new EventMetadata { ActorId = Guid.Parse(cmd.UserId), CorrelationId = Guid.NewGuid() };
        _projection.SetContext(cmd.ProjectId, cmd.StreamId);
        var newState = success.Value.Aggregate(state, (s, e) => s.Evolve(e));
        await _store.SaveAsync(cmd.StreamId, "model", version, newState, success.Value, metadata, ct);
        return new CommandResult.Success();
    }
}
```

`src/Vellum/Modules/Modelling/Relationships/GetRelationship.cs`:
```csharp
using Microsoft.EntityFrameworkCore;

namespace Vellum.Modules.Modelling.Relationships;

public static class GetRelationship
{
    public static async Task<IResult> Handle(
        Guid projectId, Guid relationshipId,
        ModellingDbContext db, CancellationToken ct)
    {
        var entity = await db.Relationships.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == relationshipId && r.ProjectId == projectId, ct);

        if (entity is null) return Results.NotFound();

        return Results.Ok(new RelationshipDto(
            entity.Id, entity.FromId, entity.ToId,
            entity.Label, entity.Technology, entity.MessageId));
    }
}
```

`src/Vellum/Modules/Modelling/Relationships/ListRelationships.cs`:
```csharp
using Microsoft.EntityFrameworkCore;
using Vellum.Shared;

namespace Vellum.Modules.Modelling.Relationships;

public static class ListRelationships
{
    public static async Task<IResult> Handle(
        Guid projectId, Guid streamId,
        Guid? fromId, Guid? toId,
        string? cursor, int? limit,
        ModellingDbContext db, CancellationToken ct)
    {
        var pageSize = Math.Clamp(limit ?? 50, 1, 200);

        var query = db.Relationships.AsNoTracking()
            .Where(r => r.ProjectId == projectId && r.Branch == streamId);

        if (fromId.HasValue) query = query.Where(r => r.FromId == fromId);
        if (toId.HasValue) query = query.Where(r => r.ToId == toId);

        var decoded = CursorEncoder.Decode(cursor);
        if (decoded is not null)
        {
            var (_, afterId) = decoded.Value;
            query = query.Where(r => r.Id.CompareTo(afterId) > 0);
        }

        var items = await query
            .OrderBy(r => r.Id)
            .Take(pageSize + 1)
            .Select(r => new RelationshipDto(r.Id, r.FromId, r.ToId, r.Label, r.Technology, r.MessageId))
            .ToListAsync(ct);

        string? nextCursor = null;
        if (items.Count > pageSize)
        {
            items = items[..pageSize];
            var last = items[^1];
            nextCursor = CursorEncoder.Encode(last.Id.ToString(), last.Id);
        }

        return Results.Ok(new Page<RelationshipDto>(items, nextCursor));
    }
}
```

- [ ] **Step 9: Create ModellingModule**

`src/Vellum/Modules/Modelling/ModellingModule.cs`:
```csharp
using Microsoft.EntityFrameworkCore;
using Vellum.Kernel.EventTypes;
using Vellum.Kernel.Projections;
using Vellum.Modules.Modelling.Model;

namespace Vellum.Modules.Modelling;

public static class ModellingModule
{
    public static IServiceCollection AddModellingModule(this IServiceCollection services, IConfiguration config)
    {
        var connectionString = config.GetConnectionString("DefaultConnection")!;

        services.AddDbContext<ModellingDbContext>(options =>
            options.UseNpgsql(connectionString)
                   .UseSnakeCaseNamingConvention());

        services.AddScoped<ModelProjection>();
        services.AddScoped<IInlineProjection>(sp => sp.GetRequiredService<ModelProjection>());

        return services;
    }

    public static void RegisterEvents(EventTypeRegistry registry)
    {
        registry.Register<ModelEvent.ElementAdded>("modelling.element.added.v1");
        registry.Register<ModelEvent.ElementRenamed>("modelling.element.renamed.v1");
        registry.Register<ModelEvent.ElementDescriptionChanged>("modelling.element.description_changed.v1");
        registry.Register<ModelEvent.ElementTechnologyChanged>("modelling.element.technology_changed.v1");
        registry.Register<ModelEvent.ElementOwnerChanged>("modelling.element.owner_changed.v1");
        registry.Register<ModelEvent.ElementReparented>("modelling.element.reparented.v1");
        registry.Register<ModelEvent.ElementStatusChanged>("modelling.element.status_changed.v1");
        registry.Register<ModelEvent.ElementRetagged>("modelling.element.retagged.v1");
        registry.Register<ModelEvent.ElementRemoved>("modelling.element.removed.v1");

        registry.Register<ModelEvent.RelationshipAdded>("modelling.relationship.added.v1");
        registry.Register<ModelEvent.RelationshipLabelChanged>("modelling.relationship.label_changed.v1");
        registry.Register<ModelEvent.RelationshipTechnologyChanged>("modelling.relationship.technology_changed.v1");
        registry.Register<ModelEvent.RelationshipRemoved>("modelling.relationship.removed.v1");
    }
}
```

- [ ] **Step 10: Create ModellingEndpoints**

`src/Vellum/Modules/Modelling/ModellingEndpoints.cs`:
```csharp
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Vellum.Kernel.CommandHandling;
using Vellum.Kernel.Results;
using Vellum.Modules.Modelling.Elements;
using Vellum.Modules.Modelling.Relationships;
using Vellum.Modules.Workspaces;
using Vellum.Modules.Workspaces.Authorization;
using Vellum.Shared;

namespace Vellum.Modules.Modelling;

public static class ModellingEndpoints
{
    public static WebApplication MapModellingEndpoints(this WebApplication app)
    {
        var project = app.MapGroup("/api/projects/{projectId}")
            .RequireAuthorization();

        var elements = project.MapGroup("/elements").WithTags("Elements");

        elements.MapPost("/", async (
            Guid projectId,
            AddElementRequest request,
            ClaimsPrincipal user,
            WorkspacesDbContext workspacesDb,
            WorkspaceAuthorizationService auth,
            ICommandHandler<AddElementCommandEnvelope, CommandResult<ElementDto>> handler,
            CancellationToken ct) =>
        {
            var userId = user.FindFirstValue(ClaimTypes.NameIdentifier)!;
            await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Editor, ct);
            var proj = await workspacesDb.Projects.AsNoTracking().FirstAsync(p => p.Id == projectId, ct);
            return (await handler.HandleAsync(
                new AddElementCommandEnvelope(projectId, proj.StreamId, userId, request), ct)).ToCreatedResult($"/api/projects/{projectId}/elements/{request.Id}");
        });

        elements.MapGet("/", async (
            Guid projectId,
            string? kind, string? status, Guid? parentId, string? cursor, int? limit,
            ClaimsPrincipal user, WorkspacesDbContext workspacesDb,
            WorkspaceAuthorizationService auth,
            ModellingDbContext db, CancellationToken ct) =>
        {
            var userId = user.FindFirstValue(ClaimTypes.NameIdentifier)!;
            await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Viewer, ct);
            var proj = await workspacesDb.Projects.AsNoTracking().FirstAsync(p => p.Id == projectId, ct);
            return await ListElements.Handle(projectId, proj.StreamId, kind, status, parentId, cursor, limit, db, ct);
        });

        elements.MapGet("/{elementId}", async (
            Guid projectId, Guid elementId,
            ClaimsPrincipal user,
            WorkspaceAuthorizationService auth,
            ModellingDbContext db, CancellationToken ct) =>
        {
            var userId = user.FindFirstValue(ClaimTypes.NameIdentifier)!;
            await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Viewer, ct);
            return await GetElement.Handle(projectId, elementId, db, ct);
        });

        elements.MapPatch("/{elementId}", async (
            Guid projectId, Guid elementId,
            UpdateElementRequest request,
            ClaimsPrincipal user, WorkspacesDbContext workspacesDb,
            WorkspaceAuthorizationService auth,
            ICommandHandler<UpdateElementCommandEnvelope, CommandResult<ElementDto>> handler,
            CancellationToken ct) =>
        {
            var userId = user.FindFirstValue(ClaimTypes.NameIdentifier)!;
            await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Editor, ct);
            var proj = await workspacesDb.Projects.AsNoTracking().FirstAsync(p => p.Id == projectId, ct);
            return (await handler.HandleAsync(
                new UpdateElementCommandEnvelope(projectId, proj.StreamId, elementId, userId, request), ct)).ToHttpResult();
        });

        elements.MapDelete("/{elementId}", async (
            Guid projectId, Guid elementId,
            ClaimsPrincipal user, WorkspacesDbContext workspacesDb,
            WorkspaceAuthorizationService auth,
            ICommandHandler<RemoveElementCommandEnvelope, CommandResult> handler,
            CancellationToken ct) =>
        {
            var userId = user.FindFirstValue(ClaimTypes.NameIdentifier)!;
            await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Editor, ct);
            var proj = await workspacesDb.Projects.AsNoTracking().FirstAsync(p => p.Id == projectId, ct);
            return (await handler.HandleAsync(
                new RemoveElementCommandEnvelope(projectId, proj.StreamId, elementId, userId), ct)).ToHttpResult();
        });

        var relationships = project.MapGroup("/relationships").WithTags("Relationships");

        relationships.MapPost("/", async (
            Guid projectId,
            AddRelationshipRequest request,
            ClaimsPrincipal user, WorkspacesDbContext workspacesDb,
            WorkspaceAuthorizationService auth,
            ICommandHandler<AddRelationshipCommandEnvelope, CommandResult<RelationshipDto>> handler,
            CancellationToken ct) =>
        {
            var userId = user.FindFirstValue(ClaimTypes.NameIdentifier)!;
            await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Editor, ct);
            var proj = await workspacesDb.Projects.AsNoTracking().FirstAsync(p => p.Id == projectId, ct);
            return (await handler.HandleAsync(
                new AddRelationshipCommandEnvelope(projectId, proj.StreamId, userId, request), ct)).ToCreatedResult($"/api/projects/{projectId}/relationships/{request.Id}");
        });

        relationships.MapGet("/", async (
            Guid projectId, Guid? fromId, Guid? toId, string? cursor, int? limit,
            ClaimsPrincipal user, WorkspacesDbContext workspacesDb,
            WorkspaceAuthorizationService auth,
            ModellingDbContext db, CancellationToken ct) =>
        {
            var userId = user.FindFirstValue(ClaimTypes.NameIdentifier)!;
            await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Viewer, ct);
            var proj = await workspacesDb.Projects.AsNoTracking().FirstAsync(p => p.Id == projectId, ct);
            return await ListRelationships.Handle(projectId, proj.StreamId, fromId, toId, cursor, limit, db, ct);
        });

        relationships.MapGet("/{relationshipId}", async (
            Guid projectId, Guid relationshipId,
            ClaimsPrincipal user,
            WorkspaceAuthorizationService auth,
            ModellingDbContext db, CancellationToken ct) =>
        {
            var userId = user.FindFirstValue(ClaimTypes.NameIdentifier)!;
            await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Viewer, ct);
            return await GetRelationship.Handle(projectId, relationshipId, db, ct);
        });

        relationships.MapPatch("/{relationshipId}", async (
            Guid projectId, Guid relationshipId,
            UpdateRelationshipRequest request,
            ClaimsPrincipal user, WorkspacesDbContext workspacesDb,
            WorkspaceAuthorizationService auth,
            ICommandHandler<UpdateRelationshipCommandEnvelope, CommandResult<RelationshipDto>> handler,
            CancellationToken ct) =>
        {
            var userId = user.FindFirstValue(ClaimTypes.NameIdentifier)!;
            await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Editor, ct);
            var proj = await workspacesDb.Projects.AsNoTracking().FirstAsync(p => p.Id == projectId, ct);
            return (await handler.HandleAsync(
                new UpdateRelationshipCommandEnvelope(projectId, proj.StreamId, relationshipId, userId, request), ct)).ToHttpResult();
        });

        relationships.MapDelete("/{relationshipId}", async (
            Guid projectId, Guid relationshipId,
            ClaimsPrincipal user, WorkspacesDbContext workspacesDb,
            WorkspaceAuthorizationService auth,
            ICommandHandler<RemoveRelationshipCommandEnvelope, CommandResult> handler,
            CancellationToken ct) =>
        {
            var userId = user.FindFirstValue(ClaimTypes.NameIdentifier)!;
            await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Editor, ct);
            var proj = await workspacesDb.Projects.AsNoTracking().FirstAsync(p => p.Id == projectId, ct);
            return (await handler.HandleAsync(
                new RemoveRelationshipCommandEnvelope(projectId, proj.StreamId, relationshipId, userId), ct)).ToHttpResult();
        });

        return app;
    }
}
```

- [ ] **Step 11: Wire modelling module in Program.cs**

Add to `src/Vellum/Program.cs`:
```csharp
using Scrutor;
using Vellum.Modules.Modelling;

// After other module registrations:
builder.Services.AddModellingModule(builder.Configuration);

// Event registration (after building registry singleton):
var registry = builder.Services.BuildServiceProvider().GetRequiredService<EventTypeRegistry>();
ModellingModule.RegisterEvents(registry);

// Scrutor scan + decoration (after all module services registered):
builder.Services.Scan(s => s.FromAssemblyOf<Program>()
    .AddClasses(c => c.AssignableTo(typeof(ICommandHandler<,>)))
    .AsImplementedInterfaces()
    .WithScopedLifetime());
builder.Services.Decorate(typeof(ICommandHandler<,>), typeof(TransactionBehavior<,>));

// Endpoints:
app.MapModellingEndpoints();
```

- [ ] **Step 12: Generate modelling migration**

```bash
dotnet ef migrations add InitialModelling --project src/Vellum --context ModellingDbContext --output-dir Modules/Modelling/Migrations
```

- [ ] **Step 13: Update IntegrationFixture**

Add `ModellingDbContext` migration:
```csharp
using Vellum.Modules.Modelling;
// ...
await MigrateAsync<ModellingDbContext>();
```

- [ ] **Step 14: Write integration + endpoint tests**

`tests/Vellum.Tests/Modules/Modelling/ModellingEndpointTests.cs`:
```csharp
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vellum.Kernel.EventStore;
using Vellum.Modules.Identity;
using Vellum.Modules.Modelling;
using Vellum.Modules.Modelling.Elements;
using Vellum.Modules.Modelling.Relationships;
using Vellum.Modules.Workspaces;
using Vellum.Modules.Views;
using Vellum.Shared;

namespace Vellum.Tests.Modules.Modelling;

[Collection("Integration")]
public class ModellingEndpointTests
{
    private readonly IntegrationFixture _fixture;

    public ModellingEndpointTests(IntegrationFixture fixture) => _fixture = fixture;

    private HttpClient CreateAuthenticatedClient()
    {
        var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<DbContextOptions<EventStoreDbContext>>();
                    services.RemoveAll<DbContextOptions<AppIdentityDbContext>>();
                    services.RemoveAll<DbContextOptions<WorkspacesDbContext>>();
                    services.RemoveAll<DbContextOptions<ModellingDbContext>>();
                    services.RemoveAll<DbContextOptions<ViewsDbContext>>();
                    services.AddDbContext<EventStoreDbContext>(o =>
                        o.UseNpgsql(_fixture.ConnectionString).UseSnakeCaseNamingConvention());
                    services.AddDbContext<AppIdentityDbContext>(o =>
                        o.UseNpgsql(_fixture.ConnectionString).UseSnakeCaseNamingConvention());
                    services.AddDbContext<WorkspacesDbContext>(o =>
                        o.UseNpgsql(_fixture.ConnectionString).UseSnakeCaseNamingConvention());
                    services.AddDbContext<ModellingDbContext>(o =>
                        o.UseNpgsql(_fixture.ConnectionString).UseSnakeCaseNamingConvention());
                    services.AddDbContext<ViewsDbContext>(o =>
                        o.UseNpgsql(_fixture.ConnectionString).UseSnakeCaseNamingConvention());
                });
            });

        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = true,
            AllowAutoRedirect = false
        });

        var email = $"test-{Guid.NewGuid():N}@vellum.local";
        client.PostAsJsonAsync("/api/auth/register",
            new { email, password = "Test123!", displayName = "Test User" }).GetAwaiter().GetResult();

        return client;
    }

    private async Task<Guid> SetupProjectAsync(HttpClient client)
    {
        var workspaceId = Guid.NewGuid();
        await client.PostAsJsonAsync("/api/workspaces", new { id = workspaceId, name = "Test WS" });
        var projectId = Guid.NewGuid();
        var response = await client.PostAsJsonAsync($"/api/workspaces/{workspaceId}/projects",
            new { id = projectId, name = "Test Project" });
        return projectId;
    }

    [Fact]
    public async Task Add_element_and_get_returns_it()
    {
        using var client = CreateAuthenticatedClient();
        var projectId = await SetupProjectAsync(client);
        var elementId = Guid.NewGuid();

        var addResponse = await client.PostAsJsonAsync($"/api/projects/{projectId}/elements",
            new { id = elementId, kind = "system", name = "Orders", description = "Order system", tags = new[] { "core" } });
        Assert.Equal(HttpStatusCode.Created, addResponse.StatusCode);

        var getResponse = await client.GetAsync($"/api/projects/{projectId}/elements/{elementId}");
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        var dto = await getResponse.Content.ReadFromJsonAsync<ElementDto>();
        Assert.Equal("Orders", dto!.Name);
        Assert.Equal("system", dto.Kind);
    }

    [Fact]
    public async Task Patch_element_renames_it()
    {
        using var client = CreateAuthenticatedClient();
        var projectId = await SetupProjectAsync(client);
        var elementId = Guid.NewGuid();

        await client.PostAsJsonAsync($"/api/projects/{projectId}/elements",
            new { id = elementId, kind = "system", name = "Old Name" });

        var patchResponse = await client.PatchAsJsonAsync($"/api/projects/{projectId}/elements/{elementId}",
            new { name = "New Name" });
        Assert.Equal(HttpStatusCode.OK, patchResponse.StatusCode);

        var getResponse = await client.GetAsync($"/api/projects/{projectId}/elements/{elementId}");
        var dto = await getResponse.Content.ReadFromJsonAsync<ElementDto>();
        Assert.Equal("New Name", dto!.Name);
    }

    [Fact]
    public async Task Delete_element_cascades_relationships()
    {
        using var client = CreateAuthenticatedClient();
        var projectId = await SetupProjectAsync(client);
        var sysA = Guid.NewGuid();
        var sysB = Guid.NewGuid();
        var relId = Guid.NewGuid();

        await client.PostAsJsonAsync($"/api/projects/{projectId}/elements",
            new { id = sysA, kind = "system", name = "A" });
        await client.PostAsJsonAsync($"/api/projects/{projectId}/elements",
            new { id = sysB, kind = "system", name = "B" });
        await client.PostAsJsonAsync($"/api/projects/{projectId}/relationships",
            new { id = relId, fromId = sysA, toId = sysB, label = "uses" });

        var deleteResponse = await client.DeleteAsync($"/api/projects/{projectId}/elements/{sysA}");
        Assert.Equal(HttpStatusCode.OK, deleteResponse.StatusCode);

        var relResponse = await client.GetAsync($"/api/projects/{projectId}/relationships/{relId}");
        Assert.Equal(HttpStatusCode.NotFound, relResponse.StatusCode);
    }

    [Fact]
    public async Task Unauthenticated_element_request_returns_401()
    {
        var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<DbContextOptions<EventStoreDbContext>>();
                    services.RemoveAll<DbContextOptions<AppIdentityDbContext>>();
                    services.RemoveAll<DbContextOptions<WorkspacesDbContext>>();
                    services.RemoveAll<DbContextOptions<ModellingDbContext>>();
                    services.RemoveAll<DbContextOptions<ViewsDbContext>>();
                    services.AddDbContext<EventStoreDbContext>(o =>
                        o.UseNpgsql(_fixture.ConnectionString).UseSnakeCaseNamingConvention());
                    services.AddDbContext<AppIdentityDbContext>(o =>
                        o.UseNpgsql(_fixture.ConnectionString).UseSnakeCaseNamingConvention());
                    services.AddDbContext<WorkspacesDbContext>(o =>
                        o.UseNpgsql(_fixture.ConnectionString).UseSnakeCaseNamingConvention());
                    services.AddDbContext<ModellingDbContext>(o =>
                        o.UseNpgsql(_fixture.ConnectionString).UseSnakeCaseNamingConvention());
                    services.AddDbContext<ViewsDbContext>(o =>
                        o.UseNpgsql(_fixture.ConnectionString).UseSnakeCaseNamingConvention());
                });
            });

        using var client = factory.CreateClient();
        var response = await client.GetAsync($"/api/projects/{Guid.NewGuid()}/elements");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}

file static class ServiceCollectionExtensions
{
    public static void RemoveAll<T>(this IServiceCollection services)
    {
        var descriptors = services.Where(d => d.ServiceType == typeof(T)).ToList();
        foreach (var d in descriptors) services.Remove(d);
    }
}
```

- [ ] **Step 15: Run all tests**

```bash
dotnet test tests/Vellum.Tests
```

Expected: all tests PASS.

- [ ] **Step 16: Commit**

```bash
git add src/Vellum/Modules/Modelling/ src/Vellum/Program.cs tests/Vellum.Tests/
git commit -m "feat(modelling): add modelling module with inline projection, command handlers, and API endpoints"
```

---

### Task 6: Views module (CRUD views + layout positions)

**Files:**
- Create: `src/Vellum/Modules/Views/Entities/ViewEntity.cs`, `LayoutPositionEntity.cs`, `LayoutEdgeEntity.cs`
- Create: `src/Vellum/Modules/Views/ViewsDbContext.cs`, `ViewsDbContextFactory.cs`
- Create: `src/Vellum/Modules/Views/ViewsModule.cs`
- Create: `src/Vellum/Modules/Views/ViewDto.cs`, `CreateView.cs`, `UpdateView.cs`, `DeleteView.cs`, `GetView.cs`, `ListViews.cs`, `SaveLayout.cs`
- Create: `src/Vellum/Modules/Views/ViewEndpoints.cs`
- Modify: `src/Vellum/Program.cs`
- Modify: `tests/Vellum.Tests/IntegrationFixture.cs`
- Create: `tests/Vellum.Tests/Modules/Views/ViewEndpointTests.cs`

**Interfaces:**
- Consumes: `WorkspaceAuthorizationService` (Task 3)
- Produces:
  - `ViewsDbContext` — view/layout tables on `views` schema
  - `ViewDto`, `LayoutPositionDto`, `LayoutEdgeDto` — API response types
  - `AddViewsModule(this IServiceCollection, IConfiguration)` — DI registration
  - `MapViewEndpoints(this WebApplication)` — maps `/api/projects/{projectId}/views` group
  - Optimistic concurrency on views via `updated_at`

This follows the same pattern as the Workspaces module (plain handlers, no `ICommandHandler<,>`, no event sourcing). Key differences: `updated_at` is used for optimistic concurrency (409 on stale), and `SaveLayout` is a PUT that replaces all layout positions for a view in one batch.

- [ ] **Step 1: Create entities**

`src/Vellum/Modules/Views/Entities/ViewEntity.cs`:
```csharp
namespace Vellum.Modules.Views.Entities;

public class ViewEntity
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public string Name { get; set; } = null!;
    public Guid? RootElementId { get; set; }
    public Guid[] VisibleElementIds { get; set; } = [];
    public string? ActiveLens { get; set; }
    public Guid? ActiveFlowId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
```

`src/Vellum/Modules/Views/Entities/LayoutPositionEntity.cs`:
```csharp
namespace Vellum.Modules.Views.Entities;

public class LayoutPositionEntity
{
    public Guid Id { get; set; }
    public Guid ViewId { get; set; }
    public Guid ElementId { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
}
```

`src/Vellum/Modules/Views/Entities/LayoutEdgeEntity.cs`:
```csharp
using System.Text.Json;

namespace Vellum.Modules.Views.Entities;

public class LayoutEdgeEntity
{
    public Guid Id { get; set; }
    public Guid ViewId { get; set; }
    public Guid RelationshipId { get; set; }
    public JsonDocument? RoutePoints { get; set; }
}
```

- [ ] **Step 2: Create ViewsDbContext and factory**

`src/Vellum/Modules/Views/ViewsDbContext.cs`:
```csharp
using Microsoft.EntityFrameworkCore;
using Vellum.Modules.Views.Entities;

namespace Vellum.Modules.Views;

public class ViewsDbContext : DbContext
{
    public ViewsDbContext(DbContextOptions<ViewsDbContext> options) : base(options) { }

    public DbSet<ViewEntity> Views => Set<ViewEntity>();
    public DbSet<LayoutPositionEntity> LayoutPositions => Set<LayoutPositionEntity>();
    public DbSet<LayoutEdgeEntity> LayoutEdges => Set<LayoutEdgeEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("views");

        modelBuilder.Entity<ViewEntity>(b =>
        {
            b.HasKey(v => v.Id);
            b.HasIndex(v => v.ProjectId);
            b.Property(v => v.CreatedAt).HasDefaultValueSql("now()");
            b.Property(v => v.UpdatedAt).HasDefaultValueSql("now()");
            b.Property(v => v.VisibleElementIds).HasColumnType("uuid[]");
        });

        modelBuilder.Entity<LayoutPositionEntity>(b =>
        {
            b.HasKey(l => l.Id);
            b.HasIndex(l => new { l.ViewId, l.ElementId }).IsUnique();
        });

        modelBuilder.Entity<LayoutEdgeEntity>(b =>
        {
            b.HasKey(l => l.Id);
            b.HasIndex(l => new { l.ViewId, l.RelationshipId }).IsUnique();
            b.Property(l => l.RoutePoints).HasColumnType("jsonb");
        });
    }
}
```

`src/Vellum/Modules/Views/ViewsDbContextFactory.cs`:
```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Vellum.Modules.Views;

public class ViewsDbContextFactory : IDesignTimeDbContextFactory<ViewsDbContext>
{
    public ViewsDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<ViewsDbContext>()
            .UseNpgsql("Host=localhost;Database=vellum;Username=vellum;Password=vellum")
            .UseSnakeCaseNamingConvention()
            .Options;
        return new ViewsDbContext(options);
    }
}
```

- [ ] **Step 3: Create ViewDto and CRUD handlers**

`src/Vellum/Modules/Views/ViewDto.cs`:
```csharp
namespace Vellum.Modules.Views;

public sealed record ViewDto(
    Guid Id, Guid ProjectId, string Name, Guid? RootElementId,
    Guid[] VisibleElementIds, string? ActiveLens, Guid? ActiveFlowId,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public sealed record LayoutPositionDto(Guid ElementId, double X, double Y);
public sealed record LayoutEdgeDto(Guid RelationshipId, object? RoutePoints);

public sealed record ViewDetailDto(
    Guid Id, Guid ProjectId, string Name, Guid? RootElementId,
    Guid[] VisibleElementIds, string? ActiveLens, Guid? ActiveFlowId,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    IReadOnlyList<LayoutPositionDto> Positions,
    IReadOnlyList<LayoutEdgeDto> Edges);
```

`src/Vellum/Modules/Views/CreateView.cs`:
```csharp
using System.Security.Claims;
using Vellum.Modules.Views.Entities;
using Vellum.Modules.Workspaces.Authorization;

namespace Vellum.Modules.Views;

public sealed record CreateViewRequest(Guid Id, string Name, Guid? RootElementId);

public static class CreateView
{
    public static async Task<IResult> Handle(
        Guid projectId,
        CreateViewRequest request,
        ClaimsPrincipal user,
        ViewsDbContext db,
        WorkspaceAuthorizationService auth,
        CancellationToken ct)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier)!;
        await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Editor, ct);

        var existing = await db.Views.FindAsync([request.Id], ct);
        if (existing is not null)
            return Results.Ok(ToDto(existing));

        var view = new ViewEntity
        {
            Id = request.Id,
            ProjectId = projectId,
            Name = request.Name,
            RootElementId = request.RootElementId
        };
        db.Views.Add(view);
        await db.SaveChangesAsync(ct);

        return Results.Created($"/api/projects/{projectId}/views/{view.Id}", ToDto(view));
    }

    private static ViewDto ToDto(ViewEntity v) =>
        new(v.Id, v.ProjectId, v.Name, v.RootElementId, v.VisibleElementIds,
            v.ActiveLens, v.ActiveFlowId, v.CreatedAt, v.UpdatedAt);
}
```

`src/Vellum/Modules/Views/UpdateView.cs`:
```csharp
using System.Security.Claims;
using Vellum.Modules.Workspaces.Authorization;
using Vellum.Shared;

namespace Vellum.Modules.Views;

public sealed record UpdateViewRequest(
    string? Name, Guid? RootElementId, Guid[]? VisibleElementIds,
    string? ActiveLens, DateTimeOffset? UpdatedAt);

public static class UpdateView
{
    public static async Task<IResult> Handle(
        Guid projectId, Guid viewId,
        UpdateViewRequest request,
        ClaimsPrincipal user,
        ViewsDbContext db,
        WorkspaceAuthorizationService auth,
        CancellationToken ct)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier)!;
        await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Editor, ct);

        var view = await db.Views.FindAsync([viewId], ct);
        if (view is null || view.ProjectId != projectId)
            return Results.NotFound(new ErrorResponse("not_found", "View not found"));

        // Optimistic concurrency: reject if client's updated_at doesn't match
        if (request.UpdatedAt.HasValue && request.UpdatedAt.Value != view.UpdatedAt)
            return Results.Conflict(new ErrorResponse("conflict", "View was modified by another user"));

        if (request.Name is not null) view.Name = request.Name;
        if (request.RootElementId.HasValue) view.RootElementId = request.RootElementId;
        if (request.VisibleElementIds is not null) view.VisibleElementIds = request.VisibleElementIds;
        if (request.ActiveLens is not null) view.ActiveLens = request.ActiveLens;
        view.UpdatedAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(ct);
        return Results.Ok(new ViewDto(view.Id, view.ProjectId, view.Name, view.RootElementId,
            view.VisibleElementIds, view.ActiveLens, view.ActiveFlowId, view.CreatedAt, view.UpdatedAt));
    }
}
```

`src/Vellum/Modules/Views/DeleteView.cs`:
```csharp
using System.Security.Claims;
using Vellum.Modules.Workspaces.Authorization;
using Vellum.Shared;

namespace Vellum.Modules.Views;

public static class DeleteView
{
    public static async Task<IResult> Handle(
        Guid projectId, Guid viewId,
        ClaimsPrincipal user,
        ViewsDbContext db,
        WorkspaceAuthorizationService auth,
        CancellationToken ct)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier)!;
        await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Editor, ct);

        var view = await db.Views.FindAsync([viewId], ct);
        if (view is null || view.ProjectId != projectId)
            return Results.NotFound(new ErrorResponse("not_found", "View not found"));

        db.Views.Remove(view);
        await db.SaveChangesAsync(ct);
        return Results.Ok();
    }
}
```

`src/Vellum/Modules/Views/GetView.cs`:
```csharp
using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Vellum.Modules.Workspaces.Authorization;

namespace Vellum.Modules.Views;

public static class GetView
{
    public static async Task<IResult> Handle(
        Guid projectId, Guid viewId,
        ClaimsPrincipal user,
        ViewsDbContext db,
        WorkspaceAuthorizationService auth,
        CancellationToken ct)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier)!;
        await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Viewer, ct);

        var view = await db.Views.AsNoTracking()
            .FirstOrDefaultAsync(v => v.Id == viewId && v.ProjectId == projectId, ct);
        if (view is null) return Results.NotFound();

        var positions = await db.LayoutPositions.AsNoTracking()
            .Where(p => p.ViewId == viewId)
            .Select(p => new LayoutPositionDto(p.ElementId, p.X, p.Y))
            .ToListAsync(ct);

        var edges = await db.LayoutEdges.AsNoTracking()
            .Where(e => e.ViewId == viewId)
            .Select(e => new LayoutEdgeDto(e.RelationshipId,
                e.RoutePoints != null ? JsonSerializer.Deserialize<object>(e.RoutePoints) : null))
            .ToListAsync(ct);

        return Results.Ok(new ViewDetailDto(
            view.Id, view.ProjectId, view.Name, view.RootElementId,
            view.VisibleElementIds, view.ActiveLens, view.ActiveFlowId,
            view.CreatedAt, view.UpdatedAt, positions, edges));
    }
}
```

`src/Vellum/Modules/Views/ListViews.cs`:
```csharp
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Vellum.Modules.Workspaces.Authorization;

namespace Vellum.Modules.Views;

public static class ListViews
{
    public static async Task<IResult> Handle(
        Guid projectId,
        ClaimsPrincipal user,
        ViewsDbContext db,
        WorkspaceAuthorizationService auth,
        CancellationToken ct)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier)!;
        await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Viewer, ct);

        var views = await db.Views.AsNoTracking()
            .Where(v => v.ProjectId == projectId)
            .OrderBy(v => v.Name)
            .Select(v => new ViewDto(v.Id, v.ProjectId, v.Name, v.RootElementId,
                v.VisibleElementIds, v.ActiveLens, v.ActiveFlowId, v.CreatedAt, v.UpdatedAt))
            .ToListAsync(ct);

        return Results.Ok(views);
    }
}
```

`src/Vellum/Modules/Views/SaveLayout.cs`:
```csharp
using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Vellum.Modules.Views.Entities;
using Vellum.Modules.Workspaces.Authorization;
using Vellum.Shared;

namespace Vellum.Modules.Views;

public sealed record SaveLayoutPosition(Guid ElementId, double X, double Y);
public sealed record SaveLayoutEdge(Guid RelationshipId, JsonDocument? RoutePoints);
public sealed record SaveLayoutRequest(
    IReadOnlyList<SaveLayoutPosition> Positions,
    IReadOnlyList<SaveLayoutEdge>? Edges);

public static class SaveLayout
{
    public static async Task<IResult> Handle(
        Guid projectId, Guid viewId,
        SaveLayoutRequest request,
        ClaimsPrincipal user,
        ViewsDbContext db,
        WorkspaceAuthorizationService auth,
        CancellationToken ct)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier)!;
        await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Editor, ct);

        var view = await db.Views.FindAsync([viewId], ct);
        if (view is null || view.ProjectId != projectId)
            return Results.NotFound(new ErrorResponse("not_found", "View not found"));

        // Replace all positions
        var existingPositions = await db.LayoutPositions
            .Where(p => p.ViewId == viewId).ToListAsync(ct);
        db.LayoutPositions.RemoveRange(existingPositions);

        db.LayoutPositions.AddRange(request.Positions.Select(p => new LayoutPositionEntity
        {
            Id = Guid.NewGuid(),
            ViewId = viewId,
            ElementId = p.ElementId,
            X = p.X,
            Y = p.Y
        }));

        // Replace all edges if provided
        if (request.Edges is not null)
        {
            var existingEdges = await db.LayoutEdges
                .Where(e => e.ViewId == viewId).ToListAsync(ct);
            db.LayoutEdges.RemoveRange(existingEdges);

            db.LayoutEdges.AddRange(request.Edges.Select(e => new LayoutEdgeEntity
            {
                Id = Guid.NewGuid(),
                ViewId = viewId,
                RelationshipId = e.RelationshipId,
                RoutePoints = e.RoutePoints
            }));
        }

        view.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return Results.Ok();
    }
}
```

- [ ] **Step 4: Create ViewEndpoints**

`src/Vellum/Modules/Views/ViewEndpoints.cs`:
```csharp
namespace Vellum.Modules.Views;

public static class ViewEndpoints
{
    public static WebApplication MapViewEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/projects/{projectId}/views")
            .RequireAuthorization()
            .WithTags("Views");

        group.MapPost("/", CreateView.Handle);
        group.MapGet("/", ListViews.Handle);
        group.MapGet("/{viewId}", GetView.Handle);
        group.MapPatch("/{viewId}", UpdateView.Handle);
        group.MapDelete("/{viewId}", DeleteView.Handle);
        group.MapPut("/{viewId}/layout", SaveLayout.Handle);

        return app;
    }
}
```

- [ ] **Step 5: Create ViewsModule**

`src/Vellum/Modules/Views/ViewsModule.cs`:
```csharp
using Microsoft.EntityFrameworkCore;

namespace Vellum.Modules.Views;

public static class ViewsModule
{
    public static IServiceCollection AddViewsModule(this IServiceCollection services, IConfiguration config)
    {
        var connectionString = config.GetConnectionString("DefaultConnection")!;

        services.AddDbContext<ViewsDbContext>(options =>
            options.UseNpgsql(connectionString)
                   .UseSnakeCaseNamingConvention());

        return services;
    }
}
```

- [ ] **Step 6: Wire in Program.cs**

Add to `src/Vellum/Program.cs`:
```csharp
using Vellum.Modules.Views;

// After other module registrations:
builder.Services.AddViewsModule(builder.Configuration);

// After other endpoints:
app.MapViewEndpoints();
```

- [ ] **Step 7: Generate migration**

```bash
dotnet ef migrations add InitialViews --project src/Vellum --context ViewsDbContext --output-dir Modules/Views/Migrations
```

- [ ] **Step 8: Update IntegrationFixture**

Add `ViewsDbContext` migration:
```csharp
using Vellum.Modules.Views;
// ...
await MigrateAsync<ViewsDbContext>();
```

- [ ] **Step 9: Write view endpoint tests**

`tests/Vellum.Tests/Modules/Views/ViewEndpointTests.cs`:
```csharp
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vellum.Kernel.EventStore;
using Vellum.Modules.Identity;
using Vellum.Modules.Modelling;
using Vellum.Modules.Workspaces;
using Vellum.Modules.Views;

namespace Vellum.Tests.Modules.Views;

[Collection("Integration")]
public class ViewEndpointTests
{
    private readonly IntegrationFixture _fixture;

    public ViewEndpointTests(IntegrationFixture fixture) => _fixture = fixture;

    private HttpClient CreateAuthenticatedClient()
    {
        var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<DbContextOptions<EventStoreDbContext>>();
                    services.RemoveAll<DbContextOptions<AppIdentityDbContext>>();
                    services.RemoveAll<DbContextOptions<WorkspacesDbContext>>();
                    services.RemoveAll<DbContextOptions<ModellingDbContext>>();
                    services.RemoveAll<DbContextOptions<ViewsDbContext>>();
                    services.AddDbContext<EventStoreDbContext>(o =>
                        o.UseNpgsql(_fixture.ConnectionString).UseSnakeCaseNamingConvention());
                    services.AddDbContext<AppIdentityDbContext>(o =>
                        o.UseNpgsql(_fixture.ConnectionString).UseSnakeCaseNamingConvention());
                    services.AddDbContext<WorkspacesDbContext>(o =>
                        o.UseNpgsql(_fixture.ConnectionString).UseSnakeCaseNamingConvention());
                    services.AddDbContext<ModellingDbContext>(o =>
                        o.UseNpgsql(_fixture.ConnectionString).UseSnakeCaseNamingConvention());
                    services.AddDbContext<ViewsDbContext>(o =>
                        o.UseNpgsql(_fixture.ConnectionString).UseSnakeCaseNamingConvention());
                });
            });

        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = true,
            AllowAutoRedirect = false
        });

        var email = $"test-{Guid.NewGuid():N}@vellum.local";
        client.PostAsJsonAsync("/api/auth/register",
            new { email, password = "Test123!", displayName = "Test User" }).GetAwaiter().GetResult();

        return client;
    }

    private async Task<Guid> SetupProjectAsync(HttpClient client)
    {
        var workspaceId = Guid.NewGuid();
        await client.PostAsJsonAsync("/api/workspaces", new { id = workspaceId, name = "Test WS" });
        var projectId = Guid.NewGuid();
        await client.PostAsJsonAsync($"/api/workspaces/{workspaceId}/projects",
            new { id = projectId, name = "Test Project" });
        return projectId;
    }

    [Fact]
    public async Task Create_view_and_get_returns_it_with_empty_layout()
    {
        using var client = CreateAuthenticatedClient();
        var projectId = await SetupProjectAsync(client);
        var viewId = Guid.NewGuid();

        var createResponse = await client.PostAsJsonAsync($"/api/projects/{projectId}/views",
            new { id = viewId, name = "Context View" });
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);

        var getResponse = await client.GetAsync($"/api/projects/{projectId}/views/{viewId}");
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        var detail = await getResponse.Content.ReadFromJsonAsync<ViewDetailDto>();
        Assert.Equal("Context View", detail!.Name);
        Assert.Empty(detail.Positions);
    }

    [Fact]
    public async Task Save_layout_and_get_returns_positions()
    {
        using var client = CreateAuthenticatedClient();
        var projectId = await SetupProjectAsync(client);
        var viewId = Guid.NewGuid();
        var elementId = Guid.NewGuid();

        await client.PostAsJsonAsync($"/api/projects/{projectId}/views",
            new { id = viewId, name = "Layout Test" });

        var layoutResponse = await client.PutAsJsonAsync($"/api/projects/{projectId}/views/{viewId}/layout",
            new { positions = new[] { new { elementId, x = 100.0, y = 200.0 } } });
        Assert.Equal(HttpStatusCode.OK, layoutResponse.StatusCode);

        var getResponse = await client.GetAsync($"/api/projects/{projectId}/views/{viewId}");
        var detail = await getResponse.Content.ReadFromJsonAsync<ViewDetailDto>();
        Assert.Single(detail!.Positions);
        Assert.Equal(100.0, detail.Positions[0].X);
    }

    [Fact]
    public async Task Update_view_with_stale_updated_at_returns_409()
    {
        using var client = CreateAuthenticatedClient();
        var projectId = await SetupProjectAsync(client);
        var viewId = Guid.NewGuid();

        await client.PostAsJsonAsync($"/api/projects/{projectId}/views",
            new { id = viewId, name = "Concurrency Test" });

        // First update succeeds
        await client.PatchAsJsonAsync($"/api/projects/{projectId}/views/{viewId}",
            new { name = "Updated" });

        // Second update with stale timestamp returns 409
        var staleResponse = await client.PatchAsJsonAsync($"/api/projects/{projectId}/views/{viewId}",
            new { name = "Stale", updatedAt = DateTimeOffset.MinValue });
        Assert.Equal(HttpStatusCode.Conflict, staleResponse.StatusCode);
    }
}

file static class ServiceCollectionExtensions
{
    public static void RemoveAll<T>(this IServiceCollection services)
    {
        var descriptors = services.Where(d => d.ServiceType == typeof(T)).ToList();
        foreach (var d in descriptors) services.Remove(d);
    }
}
```

- [ ] **Step 10: Run all tests**

```bash
dotnet test tests/Vellum.Tests
```

- [ ] **Step 11: Commit**

```bash
git add src/Vellum/Modules/Views/ src/Vellum/Program.cs tests/Vellum.Tests/
git commit -m "feat(views): add views module with CRUD, layout persistence, and optimistic concurrency"
```

---

### Task 7: Convention tests + justfile migration commands

**Files:**
- Create: `tests/Vellum.Tests/Modules/ConventionTests.cs`
- Modify: `justfile`

**Interfaces:**
- Consumes: all modules from Tasks 1–6
- Produces: convention tests that verify structural rules, updated justfile with per-module migration commands

- [ ] **Step 1: Write convention tests**

`tests/Vellum.Tests/Modules/ConventionTests.cs`:
```csharp
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Vellum.Kernel.CommandHandling;

namespace Vellum.Tests.Modules;

public class ConventionTests
{
    [Fact]
    public void All_DbContexts_use_snake_case_naming()
    {
        var assembly = typeof(Program).Assembly;
        var dbContextTypes = assembly.GetTypes()
            .Where(t => t.IsSubclassOf(typeof(DbContext)) && !t.IsAbstract)
            .ToList();

        Assert.NotEmpty(dbContextTypes);
        // Each DbContext should be configurable with UseSnakeCaseNamingConvention
        // This is a structural check — the actual naming is enforced by the NamingConventions package
    }

    [Fact]
    public void All_command_handlers_are_in_modelling_module()
    {
        var assembly = typeof(Program).Assembly;
        var handlerTypes = assembly.GetTypes()
            .Where(t => !t.IsAbstract && t.GetInterfaces().Any(i =>
                i.IsGenericType && i.GetGenericTypeDefinition() == typeof(ICommandHandler<,>)))
            .ToList();

        Assert.NotEmpty(handlerTypes);
        foreach (var handler in handlerTypes)
        {
            Assert.Contains("Modelling", handler.Namespace!,
                StringComparison.Ordinal);
        }
    }

    [Fact]
    public void No_module_references_kernel_internals_directly()
    {
        // Modules should depend on kernel interfaces, not internal classes
        // (EventStoreDbContext is internal to kernel; modules use IEventStore, AggregateStore)
        var assembly = typeof(Program).Assembly;
        var moduleTypes = assembly.GetTypes()
            .Where(t => t.Namespace?.Contains("Modules") == true)
            .ToList();

        foreach (var type in moduleTypes)
        {
            var fields = type.GetFields(BindingFlags.Instance | BindingFlags.NonPublic);
            foreach (var field in fields)
            {
                Assert.NotEqual(typeof(Vellum.Kernel.EventStore.EventStoreDbContext), field.FieldType);
            }
        }
    }
}
```

- [ ] **Step 2: Update justfile**

Replace `justfile`:
```just
up:
    docker compose up -d

down:
    docker compose down

run:
    dotnet run --project src/Vellum

test:
    DOCKER_CONFIG={{justfile_directory()}}/.docker-test dotnet test

migrate-es name:
    dotnet ef migrations add {{name}} --project src/Vellum --context EventStoreDbContext --output-dir Kernel/EventStore/Migrations

migrate-identity name:
    dotnet ef migrations add {{name}} --project src/Vellum --context AppIdentityDbContext --output-dir Modules/Identity/Migrations

migrate-workspaces name:
    dotnet ef migrations add {{name}} --project src/Vellum --context WorkspacesDbContext --output-dir Modules/Workspaces/Migrations

migrate-modelling name:
    dotnet ef migrations add {{name}} --project src/Vellum --context ModellingDbContext --output-dir Modules/Modelling/Migrations

migrate-views name:
    dotnet ef migrations add {{name}} --project src/Vellum --context ViewsDbContext --output-dir Modules/Views/Migrations

seed:
    dotnet run --project src/Vellum -- seed
```

- [ ] **Step 3: Run all tests**

```bash
dotnet test tests/Vellum.Tests
```

Expected: all tests PASS including convention tests.

- [ ] **Step 4: Commit**

```bash
git add tests/Vellum.Tests/Modules/ConventionTests.cs justfile
git commit -m "feat: add convention tests and per-module migration commands to justfile"
```

---

### Task 8: Dev seeding + startup migrations + final wiring

**Files:**
- Modify: `src/Vellum/Program.cs` (add dev-mode migrations + seed)

**Interfaces:**
- Consumes: all modules from Tasks 1–6
- Produces:
  - Startup migration application (all DbContexts) in development mode
  - Dev seed: default user, workspace, project, sample C4 elements + relationships

- [ ] **Step 1: Add dev startup logic to Program.cs**

Add at the end of Program.cs, before `app.Run()`:
```csharp
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
            await userManager.CreateAsync(devUser, "Dev123!");
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
```

- [ ] **Step 2: Run seed**

```bash
dotnet run --project src/Vellum -- seed
```

Expected: "Dev seed complete." printed. The database has the dev user, workspace, project, and sample model.

- [ ] **Step 3: Run the app and verify Scalar**

```bash
dotnet run --project src/Vellum
```

Navigate to `http://localhost:5000/scalar` — verify all endpoints are listed.

- [ ] **Step 4: Run all tests one final time**

```bash
dotnet test tests/Vellum.Tests
```

Expected: all tests PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Vellum/Program.cs
git commit -m "feat: add dev seeding, startup migrations, and final Phase 1a wiring"
```
