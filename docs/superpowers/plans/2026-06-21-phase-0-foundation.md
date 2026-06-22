# Phase 0 — Foundation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the event-sourcing kernel that all subsequent phases depend on — streams, events, snapshots, decide/evolve, inline + async projections, outbox, upcasting.

**Architecture:** Single .NET 10 web project with a PostgreSQL event store. The kernel provides generic infrastructure (stream append, snapshot load, projection dispatch, outbox) that feature modules (Phase 1+) build on. No domain-specific code in this phase — a test `Counter` aggregate validates everything end-to-end.

**Tech Stack:** .NET 10 / C# 14, ASP.NET Core minimal APIs, EF Core 10 + Npgsql, Thinktecture.Runtime.Extensions (unions), Scrutor (DI decoration), Testcontainers (PostgreSQL 17), xUnit.

## Global Constraints

- Target framework: `net10.0`
- All dependencies must be MIT or Apache-2.0 licensed
- PostgreSQL 17 is the only external dependency
- `snake_case` naming via `EFCore.NamingConventions` — never manually specify column names
- All DB access via EF Core parameterised queries (raw SQL only for Postgres-specific features like `xid` guards and `NOTIFY`, always parameterised)
- `es` schema for all kernel tables
- Frequent commits — one per task minimum
- Tests use Testcontainers (real Postgres, no mocks)

---

## File Map

```
Vellum.sln
.gitignore
docker-compose.yml
justfile

src/Vellum/
  Vellum.csproj
  Program.cs
  appsettings.json
  appsettings.Development.json
  Kernel/
    Results/
      CommandResult.cs
    EventStore/
      StreamEntity.cs
      EventEntity.cs
      EventStoreDbContext.cs
      IEventStore.cs
      EventStore.cs
      ConcurrencyException.cs
      Migrations/                          (EF Core generated)
    Aggregates/
      IAggregateState.cs
      EventMetadata.cs
      AggregateStore.cs
    EventTypes/
      IEventTypeRegistry.cs
      EventTypeRegistry.cs
    CommandHandling/
      ICommandHandler.cs
      EventCollector.cs
      TransactionBehavior.cs
    Projections/
      IInlineProjection.cs
      IAsyncProjection.cs
      CheckpointEntity.cs
      AsyncProjectionHost.cs
    Outbox/
      OutboxMessageEntity.cs
      DeadLetterEntity.cs
      OutboxDispatcher.cs

tests/Vellum.Tests/
  Vellum.Tests.csproj
  IntegrationFixture.cs
  SmokeTests.cs
  Kernel/
    EventStore/
      EventStoreTests.cs
    Aggregates/
      CounterAggregate.cs
      AggregateStoreTests.cs
    EventTypes/
      EventTypeRegistryTests.cs
      UpcastingTests.cs
    CommandHandling/
      TransactionBehaviorTests.cs
    Projections/
      AsyncProjectionHostTests.cs
    Outbox/
      OutboxDispatcherTests.cs
```

---

### Task 1: Project scaffolding + dev environment

**Files:**
- Create: `Vellum.sln`, `src/Vellum/Vellum.csproj`, `src/Vellum/Program.cs`, `src/Vellum/appsettings.json`, `src/Vellum/appsettings.Development.json`, `tests/Vellum.Tests/Vellum.Tests.csproj`, `tests/Vellum.Tests/IntegrationFixture.cs`, `tests/Vellum.Tests/SmokeTests.cs`, `docker-compose.yml`, `justfile`, `.gitignore`

**Interfaces:**
- Consumes: nothing
- Produces: `IntegrationFixture` (provides `ConnectionString` to a running PostgreSQL 17 container for all integration tests)

- [ ] **Step 1: Create solution and projects**

```bash
cd /path/to/vellum
dotnet new sln -n Vellum
dotnet new web -n Vellum -o src/Vellum --no-https
dotnet new xunit -n Vellum.Tests -o tests/Vellum.Tests
dotnet sln add src/Vellum/Vellum.csproj
dotnet sln add tests/Vellum.Tests/Vellum.Tests.csproj
dotnet add tests/Vellum.Tests reference src/Vellum
```

- [ ] **Step 2: Add NuGet packages**

```bash
# Backend
dotnet add src/Vellum package Npgsql.EntityFrameworkCore.PostgreSQL
dotnet add src/Vellum package EFCore.NamingConventions
dotnet add src/Vellum package Thinktecture.Runtime.Extensions
dotnet add src/Vellum package Scrutor

# Tests
dotnet add tests/Vellum.Tests package Testcontainers.PostgreSql
dotnet add tests/Vellum.Tests package Npgsql
```

- [ ] **Step 3: Create docker-compose.yml**

```yaml
services:
  postgres:
    image: postgres:17
    ports:
      - "5432:5432"
    environment:
      POSTGRES_USER: vellum
      POSTGRES_PASSWORD: vellum
      POSTGRES_DB: vellum
    volumes:
      - pgdata:/var/lib/postgresql/data

volumes:
  pgdata:
```

- [ ] **Step 4: Create justfile**

```just
up:
    docker compose up -d

down:
    docker compose down

run:
    dotnet run --project src/Vellum

test:
    dotnet test

migrate name:
    dotnet ef migrations add {{name}} --project src/Vellum --context EventStoreDbContext --output-dir Kernel/EventStore/Migrations
```

- [ ] **Step 5: Create .gitignore**

```
bin/
obj/
.vs/
*.user
*.suo
appsettings.*.local.json
```

- [ ] **Step 6: Create appsettings files**

`src/Vellum/appsettings.json`:
```json
{
  "ConnectionStrings": {
    "DefaultConnection": "Host=localhost;Database=vellum;Username=vellum;Password=vellum"
  }
}
```

`src/Vellum/appsettings.Development.json`:
```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning"
    }
  }
}
```

- [ ] **Step 7: Create minimal Program.cs**

```csharp
var builder = WebApplication.CreateBuilder(args);

var app = builder.Build();

app.MapGet("/health", () => Results.Ok());

app.Run();
```

- [ ] **Step 8: Create IntegrationFixture**

`tests/Vellum.Tests/IntegrationFixture.cs`:
```csharp
using Testcontainers.PostgreSql;

namespace Vellum.Tests;

public class IntegrationFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("postgres:17")
        .Build();

    public string ConnectionString => _postgres.GetConnectionString();

    public async ValueTask InitializeAsync() => await _postgres.StartAsync();

    public async ValueTask DisposeAsync() => await _postgres.DisposeAsync();
}

[CollectionDefinition("Integration")]
public class IntegrationCollection : ICollectionFixture<IntegrationFixture>;
```

- [ ] **Step 9: Write smoke test**

`tests/Vellum.Tests/SmokeTests.cs`:
```csharp
using Npgsql;

namespace Vellum.Tests;

[Collection("Integration")]
public class SmokeTests
{
    private readonly IntegrationFixture _fixture;

    public SmokeTests(IntegrationFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Postgres_connects_and_reports_version_17()
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();

        Assert.Equal(System.Data.ConnectionState.Open, conn.State);

        await using var cmd = new NpgsqlCommand("SHOW server_version", conn);
        var version = (string)(await cmd.ExecuteScalarAsync())!;
        Assert.StartsWith("17", version);
    }
}
```

- [ ] **Step 10: Run smoke test**

```bash
dotnet test tests/Vellum.Tests --filter "SmokeTests"
```

Expected: PASS — Testcontainers starts a PostgreSQL 17 container and the test connects.

- [ ] **Step 11: Commit**

```bash
git add -A
git commit -m "feat: project scaffolding with dotnet solution, docker-compose, and testcontainers"
```

---

### Task 2: Event store core (raw JSON storage)

**Files:**
- Create: `src/Vellum/Kernel/EventStore/StreamEntity.cs`, `src/Vellum/Kernel/EventStore/EventEntity.cs`, `src/Vellum/Kernel/EventStore/EventStoreDbContext.cs`, `src/Vellum/Kernel/EventStore/IEventStore.cs`, `src/Vellum/Kernel/EventStore/EventStore.cs`, `src/Vellum/Kernel/EventStore/ConcurrencyException.cs`, `tests/Vellum.Tests/Kernel/EventStore/EventStoreTests.cs`
- Modify: `src/Vellum/Program.cs` (add DI registrations), `tests/Vellum.Tests/IntegrationFixture.cs` (apply migrations)

**Interfaces:**
- Consumes: `IntegrationFixture.ConnectionString` (from Task 1)
- Produces:
  - `IEventStore.LoadAsync(Guid streamId, CancellationToken ct) → StreamSnapshot?` — returns `null` if stream doesn't exist
  - `IEventStore.AppendAsync(Guid streamId, string streamType, int expectedVersion, JsonDocument newState, IReadOnlyList<NewEvent> events, CancellationToken ct)` — throws `ConcurrencyException` on version mismatch
  - `StreamSnapshot` record: `(int Version, JsonDocument State)`
  - `NewEvent` record: `(string EventType, JsonDocument Payload, JsonDocument Metadata)`
  - `ConcurrencyException` with `StreamId`, `ExpectedVersion`, `ActualVersion`
  - `EventStoreDbContext` with `DbSet<StreamEntity> Streams` and `DbSet<EventEntity> Events`

- [ ] **Step 1: Create IEventStore interface and DTOs**

`src/Vellum/Kernel/EventStore/IEventStore.cs`:
```csharp
using System.Text.Json;

namespace Vellum.Kernel.EventStore;

public interface IEventStore
{
    Task<StreamSnapshot?> LoadAsync(Guid streamId, CancellationToken ct = default);

    Task AppendAsync(
        Guid streamId,
        string streamType,
        int expectedVersion,
        JsonDocument newState,
        IReadOnlyList<NewEvent> events,
        CancellationToken ct = default);
}

public sealed record StreamSnapshot(int Version, JsonDocument State);

public sealed record NewEvent(string EventType, JsonDocument Payload, JsonDocument Metadata);
```

`src/Vellum/Kernel/EventStore/ConcurrencyException.cs`:
```csharp
namespace Vellum.Kernel.EventStore;

public sealed class ConcurrencyException : Exception
{
    public Guid StreamId { get; }
    public int ExpectedVersion { get; }
    public int ActualVersion { get; }

    public ConcurrencyException(Guid streamId, int expectedVersion, int actualVersion)
        : base($"Stream {streamId}: expected version {expectedVersion}, actual {actualVersion}")
    {
        StreamId = streamId;
        ExpectedVersion = expectedVersion;
        ActualVersion = actualVersion;
    }
}
```

- [ ] **Step 2: Write failing tests**

`tests/Vellum.Tests/Kernel/EventStore/EventStoreTests.cs`:
```csharp
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Vellum.Kernel.EventStore;

namespace Vellum.Tests.Kernel.EventStore;

[Collection("Integration")]
public class EventStoreTests
{
    private readonly IntegrationFixture _fixture;

    public EventStoreTests(IntegrationFixture fixture) => _fixture = fixture;

    private (EventStoreDbContext Db, Vellum.Kernel.EventStore.EventStore Store) CreateStore()
    {
        var options = new DbContextOptionsBuilder<EventStoreDbContext>()
            .UseNpgsql(_fixture.ConnectionString)
            .UseSnakeCaseNamingConvention()
            .Options;
        var db = new EventStoreDbContext(options);
        return (db, new Vellum.Kernel.EventStore.EventStore(db));
    }

    private static NewEvent MakeEvent(string type, object payload) => new(
        type,
        JsonSerializer.SerializeToDocument(payload),
        JsonSerializer.SerializeToDocument(new { actorId = Guid.NewGuid() }));

    private static JsonDocument MakeState(object state) =>
        JsonSerializer.SerializeToDocument(state);

    [Fact]
    public async Task Append_to_new_stream_creates_stream_and_events()
    {
        var (db, store) = CreateStore();
        await using var _ = db;
        var streamId = Guid.NewGuid();

        await store.AppendAsync(
            streamId, "test", 0,
            MakeState(new { count = 1 }),
            [MakeEvent("test.incremented.v1", new { })]);

        var stream = await db.Streams.FindAsync(streamId);
        Assert.NotNull(stream);
        Assert.Equal(1, stream!.Version);

        var events = await db.Events.Where(e => e.StreamId == streamId).ToListAsync();
        Assert.Single(events);
        Assert.Equal(1, events[0].Version);
        Assert.Equal("test.incremented.v1", events[0].EventType);
    }

    [Fact]
    public async Task Load_returns_null_for_nonexistent_stream()
    {
        var (db, store) = CreateStore();
        await using var _ = db;

        var result = await store.LoadAsync(Guid.NewGuid());

        Assert.Null(result);
    }

    [Fact]
    public async Task Load_returns_snapshot_after_append()
    {
        var (db, store) = CreateStore();
        await using var _ = db;
        var streamId = Guid.NewGuid();
        var state = new { count = 42 };

        await store.AppendAsync(
            streamId, "test", 0,
            MakeState(state),
            [MakeEvent("test.set.v1", new { value = 42 })]);

        var snapshot = await store.LoadAsync(streamId);

        Assert.NotNull(snapshot);
        Assert.Equal(1, snapshot!.Version);
        Assert.Equal(42, snapshot.State.RootElement.GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task Append_increments_version_and_updates_state()
    {
        var (db, store) = CreateStore();
        await using var _ = db;
        var streamId = Guid.NewGuid();

        await store.AppendAsync(
            streamId, "test", 0,
            MakeState(new { count = 1 }),
            [MakeEvent("test.inc.v1", new { })]);

        await store.AppendAsync(
            streamId, "test", 1,
            MakeState(new { count = 2 }),
            [MakeEvent("test.inc.v1", new { })]);

        var snapshot = await store.LoadAsync(streamId);
        Assert.Equal(2, snapshot!.Version);
        Assert.Equal(2, snapshot.State.RootElement.GetProperty("count").GetInt32());

        var events = await db.Events.Where(e => e.StreamId == streamId).OrderBy(e => e.Version).ToListAsync();
        Assert.Equal(2, events.Count);
        Assert.Equal(1, events[0].Version);
        Assert.Equal(2, events[1].Version);
    }

    [Fact]
    public async Task Append_with_wrong_version_throws_ConcurrencyException()
    {
        var (db, store) = CreateStore();
        await using var _ = db;
        var streamId = Guid.NewGuid();

        await store.AppendAsync(
            streamId, "test", 0,
            MakeState(new { count = 1 }),
            [MakeEvent("test.inc.v1", new { })]);

        var ex = await Assert.ThrowsAsync<ConcurrencyException>(() =>
            store.AppendAsync(
                streamId, "test", 0,
                MakeState(new { count = 2 }),
                [MakeEvent("test.inc.v1", new { })]));

        Assert.Equal(streamId, ex.StreamId);
        Assert.Equal(0, ex.ExpectedVersion);
        Assert.Equal(1, ex.ActualVersion);
    }

    [Fact]
    public async Task Append_multiple_events_atomically()
    {
        var (db, store) = CreateStore();
        await using var _ = db;
        var streamId = Guid.NewGuid();

        await store.AppendAsync(
            streamId, "test", 0,
            MakeState(new { count = 3 }),
            [
                MakeEvent("test.inc.v1", new { }),
                MakeEvent("test.inc.v1", new { }),
                MakeEvent("test.inc.v1", new { }),
            ]);

        var snapshot = await store.LoadAsync(streamId);
        Assert.Equal(3, snapshot!.Version);

        var events = await db.Events.Where(e => e.StreamId == streamId).OrderBy(e => e.Version).ToListAsync();
        Assert.Equal(3, events.Count);
        Assert.Equal(1, events[0].Version);
        Assert.Equal(2, events[1].Version);
        Assert.Equal(3, events[2].Version);
    }

    [Fact]
    public async Task Events_have_ascending_global_positions()
    {
        var (db, store) = CreateStore();
        await using var _ = db;
        var streamA = Guid.NewGuid();
        var streamB = Guid.NewGuid();

        await store.AppendAsync(streamA, "test", 0, MakeState(new { }), [MakeEvent("a.v1", new { })]);
        await store.AppendAsync(streamB, "test", 0, MakeState(new { }), [MakeEvent("b.v1", new { })]);
        await store.AppendAsync(streamA, "test", 1, MakeState(new { }), [MakeEvent("a.v1", new { })]);

        var allEvents = await db.Events.OrderBy(e => e.GlobalPosition).ToListAsync();
        var positions = allEvents.Select(e => e.GlobalPosition).ToList();

        Assert.Equal(positions.OrderBy(p => p), positions);
        Assert.Equal(3, positions.Distinct().Count());
    }
}
```

- [ ] **Step 3: Run tests to verify they fail**

```bash
dotnet test tests/Vellum.Tests --filter "EventStoreTests"
```

Expected: compilation errors — `EventStoreDbContext`, `EventStore`, entity classes don't exist yet.

- [ ] **Step 4: Create entities**

`src/Vellum/Kernel/EventStore/StreamEntity.cs`:
```csharp
using System.Text.Json;

namespace Vellum.Kernel.EventStore;

public class StreamEntity
{
    public Guid StreamId { get; set; }
    public string StreamType { get; set; } = null!;
    public int Version { get; set; }
    public JsonDocument State { get; set; } = null!;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
```

`src/Vellum/Kernel/EventStore/EventEntity.cs`:
```csharp
using System.Text.Json;

namespace Vellum.Kernel.EventStore;

public class EventEntity
{
    public Guid StreamId { get; set; }
    public int Version { get; set; }
    public long GlobalPosition { get; set; }
    public string EventType { get; set; } = null!;
    public JsonDocument Payload { get; set; } = null!;
    public JsonDocument Metadata { get; set; } = null!;
    public DateTimeOffset OccurredAt { get; set; }
}
```

- [ ] **Step 5: Create DbContext**

`src/Vellum/Kernel/EventStore/EventStoreDbContext.cs`:
```csharp
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace Vellum.Kernel.EventStore;

public class EventStoreDbContext : DbContext
{
    public EventStoreDbContext(DbContextOptions<EventStoreDbContext> options)
        : base(options) { }

    public DbSet<StreamEntity> Streams => Set<StreamEntity>();
    public DbSet<EventEntity> Events => Set<EventEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("es");

        modelBuilder.Entity<StreamEntity>(b =>
        {
            b.ToTable("streams");
            b.HasKey(s => s.StreamId);
            b.Property(s => s.Version).IsConcurrencyToken();
            b.Property(s => s.State).HasColumnType("jsonb");
            b.Property(s => s.CreatedAt).HasDefaultValueSql("now()");
            b.Property(s => s.UpdatedAt).HasDefaultValueSql("now()");
        });

        modelBuilder.Entity<EventEntity>(b =>
        {
            b.ToTable("events");
            b.HasKey(e => new { e.StreamId, e.Version });
            b.Property(e => e.GlobalPosition).UseIdentityAlwaysColumn();
            b.Property(e => e.Payload).HasColumnType("jsonb");
            b.Property(e => e.Metadata).HasColumnType("jsonb");
            b.Property(e => e.OccurredAt).HasDefaultValueSql("now()");
            b.HasIndex(e => e.GlobalPosition).IsUnique();
        });
    }
}
```

- [ ] **Step 6: Generate EF Core migration and add xid column**

```bash
dotnet ef migrations add InitialEventStore --project src/Vellum --context EventStoreDbContext --output-dir Kernel/EventStore/Migrations
```

Then edit the generated migration file to add the `xid` column after the `events` table creation:

```csharp
// Add inside the Up() method, after the CreateTable for "events":
migrationBuilder.Sql(
    "ALTER TABLE es.events ADD COLUMN xid xid8 NOT NULL DEFAULT pg_current_xact_id();");
```

- [ ] **Step 7: Implement EventStore**

`src/Vellum/Kernel/EventStore/EventStore.cs`:
```csharp
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace Vellum.Kernel.EventStore;

public sealed class EventStore : IEventStore
{
    private readonly EventStoreDbContext _db;

    public EventStore(EventStoreDbContext db) => _db = db;

    public async Task<StreamSnapshot?> LoadAsync(Guid streamId, CancellationToken ct = default)
    {
        var stream = await _db.Streams
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.StreamId == streamId, ct);

        if (stream is null) return null;
        return new StreamSnapshot(stream.Version, stream.State);
    }

    public async Task AppendAsync(
        Guid streamId,
        string streamType,
        int expectedVersion,
        JsonDocument newState,
        IReadOnlyList<NewEvent> events,
        CancellationToken ct = default)
    {
        var newVersion = expectedVersion + events.Count;

        if (expectedVersion == 0)
        {
            _db.Streams.Add(new StreamEntity
            {
                StreamId = streamId,
                StreamType = streamType,
                Version = newVersion,
                State = newState,
            });
        }
        else
        {
            var stream = await _db.Streams.FindAsync([streamId], ct)
                ?? throw new ConcurrencyException(streamId, expectedVersion, -1);

            if (stream.Version != expectedVersion)
                throw new ConcurrencyException(streamId, expectedVersion, stream.Version);

            stream.Version = newVersion;
            stream.State = newState;
            stream.UpdatedAt = DateTimeOffset.UtcNow;
        }

        for (var i = 0; i < events.Count; i++)
        {
            _db.Events.Add(new EventEntity
            {
                StreamId = streamId,
                Version = expectedVersion + i + 1,
                EventType = events[i].EventType,
                Payload = events[i].Payload,
                Metadata = events[i].Metadata,
            });
        }

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ConcurrencyException(streamId, expectedVersion, -1);
        }
    }
}
```

- [ ] **Step 8: Register in DI and update IntegrationFixture**

Add to `src/Vellum/Program.cs`:
```csharp
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
```

Update `tests/Vellum.Tests/IntegrationFixture.cs` to apply migrations:
```csharp
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using Vellum.Kernel.EventStore;

namespace Vellum.Tests;

public class IntegrationFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("postgres:17")
        .Build();

    public string ConnectionString => _postgres.GetConnectionString();

    public async ValueTask InitializeAsync()
    {
        await _postgres.StartAsync();

        var options = new DbContextOptionsBuilder<EventStoreDbContext>()
            .UseNpgsql(ConnectionString)
            .UseSnakeCaseNamingConvention()
            .Options;
        await using var db = new EventStoreDbContext(options);
        await db.Database.MigrateAsync();
    }

    public async ValueTask DisposeAsync() => await _postgres.DisposeAsync();
}

[CollectionDefinition("Integration")]
public class IntegrationCollection : ICollectionFixture<IntegrationFixture>;
```

- [ ] **Step 9: Run tests**

```bash
dotnet test tests/Vellum.Tests --filter "EventStoreTests"
```

Expected: all 6 tests PASS.

- [ ] **Step 10: Commit**

```bash
git add -A
git commit -m "feat(kernel): add event store with streams, events, snapshots, and optimistic concurrency"
```

---

### Task 3: Event type registry + aggregate pattern

**Files:**
- Create: `src/Vellum/Kernel/EventTypes/IEventTypeRegistry.cs`, `src/Vellum/Kernel/EventTypes/EventTypeRegistry.cs`, `src/Vellum/Kernel/Aggregates/IAggregateState.cs`, `src/Vellum/Kernel/Aggregates/EventMetadata.cs`, `src/Vellum/Kernel/Aggregates/AggregateStore.cs`, `tests/Vellum.Tests/Kernel/Aggregates/CounterAggregate.cs`, `tests/Vellum.Tests/Kernel/Aggregates/AggregateStoreTests.cs`, `tests/Vellum.Tests/Kernel/EventTypes/EventTypeRegistryTests.cs`
- Modify: `src/Vellum/Program.cs` (register services)

**Interfaces:**
- Consumes: `IEventStore` (from Task 2)
- Produces:
  - `IAggregateState<TSelf, TEvent>` — interface with `static abstract TSelf Initial` and `TSelf Evolve(TEvent @event)`
  - `AggregateStore.LoadAsync<TState, TEvent>(Guid streamId, CancellationToken ct) → (TState State, int Version)` — returns `TState.Initial` + version 0 if stream doesn't exist
  - `AggregateStore.SaveAsync<TState, TEvent>(Guid streamId, string streamType, int expectedVersion, TState newState, IReadOnlyList<TEvent> events, EventMetadata metadata, CancellationToken ct)`
  - `EventMetadata` record: `(Guid ActorId, Guid CorrelationId, Guid? DraftId, Guid? MergeCorrelationId)`
  - `IEventTypeRegistry.GetTypeName(Type clrType) → string`, `.GetClrType(string typeName) → Type`, `.Register<T>(string typeName)`

- [ ] **Step 1: Create IEventTypeRegistry and implementation**

`src/Vellum/Kernel/EventTypes/IEventTypeRegistry.cs`:
```csharp
namespace Vellum.Kernel.EventTypes;

public interface IEventTypeRegistry
{
    string GetTypeName(Type clrType);
    Type GetClrType(string typeName);
    void Register<T>(string typeName) where T : class;
}
```

`src/Vellum/Kernel/EventTypes/EventTypeRegistry.cs`:
```csharp
namespace Vellum.Kernel.EventTypes;

public sealed class EventTypeRegistry : IEventTypeRegistry
{
    private readonly Dictionary<Type, string> _clrToName = new();
    private readonly Dictionary<string, Type> _nameToClr = new();

    public void Register<T>(string typeName) where T : class
    {
        _clrToName[typeof(T)] = typeName;
        _nameToClr[typeName] = typeof(T);
    }

    public string GetTypeName(Type clrType) =>
        _clrToName.TryGetValue(clrType, out var name)
            ? name
            : throw new InvalidOperationException($"No type name registered for {clrType.FullName}");

    public Type GetClrType(string typeName) =>
        _nameToClr.TryGetValue(typeName, out var type)
            ? type
            : throw new InvalidOperationException($"No CLR type registered for '{typeName}'");
}
```

- [ ] **Step 2: Write failing registry tests**

`tests/Vellum.Tests/Kernel/EventTypes/EventTypeRegistryTests.cs`:
```csharp
using Vellum.Kernel.EventTypes;

namespace Vellum.Tests.Kernel.EventTypes;

public class EventTypeRegistryTests
{
    private sealed record SomeEvent(string Value);

    [Fact]
    public void Register_and_resolve_by_clr_type()
    {
        var registry = new EventTypeRegistry();
        registry.Register<SomeEvent>("test.some.v1");

        Assert.Equal("test.some.v1", registry.GetTypeName(typeof(SomeEvent)));
    }

    [Fact]
    public void Register_and_resolve_by_type_name()
    {
        var registry = new EventTypeRegistry();
        registry.Register<SomeEvent>("test.some.v1");

        Assert.Equal(typeof(SomeEvent), registry.GetClrType("test.some.v1"));
    }

    [Fact]
    public void GetTypeName_throws_for_unregistered_type()
    {
        var registry = new EventTypeRegistry();

        Assert.Throws<InvalidOperationException>(() => registry.GetTypeName(typeof(SomeEvent)));
    }

    [Fact]
    public void GetClrType_throws_for_unregistered_name()
    {
        var registry = new EventTypeRegistry();

        Assert.Throws<InvalidOperationException>(() => registry.GetClrType("unknown.v1"));
    }
}
```

- [ ] **Step 3: Run registry tests**

```bash
dotnet test tests/Vellum.Tests --filter "EventTypeRegistryTests"
```

Expected: all 4 PASS.

- [ ] **Step 4: Create IAggregateState and EventMetadata**

`src/Vellum/Kernel/Aggregates/IAggregateState.cs`:
```csharp
namespace Vellum.Kernel.Aggregates;

public interface IAggregateState<TSelf, TEvent>
    where TSelf : IAggregateState<TSelf, TEvent>
{
    static abstract TSelf Initial { get; }
    TSelf Evolve(TEvent @event);
}
```

`src/Vellum/Kernel/Aggregates/EventMetadata.cs`:
```csharp
namespace Vellum.Kernel.Aggregates;

public sealed record EventMetadata
{
    public required Guid ActorId { get; init; }
    public required Guid CorrelationId { get; init; }
    public Guid? DraftId { get; init; }
    public Guid? MergeCorrelationId { get; init; }
}
```

- [ ] **Step 5: Create test Counter aggregate**

`tests/Vellum.Tests/Kernel/Aggregates/CounterAggregate.cs`:
```csharp
using Vellum.Kernel.Aggregates;

namespace Vellum.Tests.Kernel.Aggregates;

public abstract record CounterEvent
{
    public sealed record Incremented : CounterEvent;
    public sealed record Decremented(int Amount) : CounterEvent;

    private CounterEvent() { }
}

public sealed record CounterState(int Value) : IAggregateState<CounterState, CounterEvent>
{
    public static CounterState Initial => new(0);

    public CounterState Evolve(CounterEvent @event) => @event switch
    {
        CounterEvent.Incremented => this with { Value = Value + 1 },
        CounterEvent.Decremented d => this with { Value = Value - d.Amount },
        _ => throw new InvalidOperationException($"Unknown event: {@event.GetType().Name}")
    };
}

public static class CounterDecider
{
    public static IReadOnlyList<CounterEvent> Decide(CounterState state, CounterCommand command) =>
        command switch
        {
            CounterCommand.Increment => [new CounterEvent.Incremented()],
            CounterCommand.Decrement d when state.Value >= d.Amount => [new CounterEvent.Decremented(d.Amount)],
            CounterCommand.Decrement => throw new InvalidOperationException("Cannot decrement below zero"),
            _ => throw new InvalidOperationException($"Unknown command: {command.GetType().Name}")
        };
}

public abstract record CounterCommand
{
    public sealed record Increment : CounterCommand;
    public sealed record Decrement(int Amount) : CounterCommand;

    private CounterCommand() { }
}
```

- [ ] **Step 6: Write failing AggregateStore tests**

`tests/Vellum.Tests/Kernel/Aggregates/AggregateStoreTests.cs`:
```csharp
using Microsoft.EntityFrameworkCore;
using Vellum.Kernel.Aggregates;
using Vellum.Kernel.EventStore;
using Vellum.Kernel.EventTypes;

namespace Vellum.Tests.Kernel.Aggregates;

[Collection("Integration")]
public class AggregateStoreTests
{
    private readonly IntegrationFixture _fixture;

    public AggregateStoreTests(IntegrationFixture fixture) => _fixture = fixture;

    private (EventStoreDbContext Db, AggregateStore Store) CreateStore()
    {
        var options = new DbContextOptionsBuilder<EventStoreDbContext>()
            .UseNpgsql(_fixture.ConnectionString)
            .UseSnakeCaseNamingConvention()
            .Options;
        var db = new EventStoreDbContext(options);
        var eventStore = new Vellum.Kernel.EventStore.EventStore(db);

        var registry = new EventTypeRegistry();
        registry.Register<CounterEvent.Incremented>("counter.incremented.v1");
        registry.Register<CounterEvent.Decremented>("counter.decremented.v1");

        var aggregateStore = new AggregateStore(eventStore, registry);
        return (db, aggregateStore);
    }

    [Fact]
    public async Task Load_returns_initial_state_for_new_stream()
    {
        var (db, store) = CreateStore();
        await using var _ = db;

        var (state, version) = await store.LoadAsync<CounterState, CounterEvent>(Guid.NewGuid());

        Assert.Equal(CounterState.Initial, state);
        Assert.Equal(0, version);
    }

    [Fact]
    public async Task Save_and_load_roundtrips_state()
    {
        var (db, store) = CreateStore();
        await using var _ = db;
        var streamId = Guid.NewGuid();
        var metadata = new EventMetadata
        {
            ActorId = Guid.NewGuid(),
            CorrelationId = Guid.NewGuid()
        };

        var initial = CounterState.Initial;
        var events = CounterDecider.Decide(initial, new CounterCommand.Increment());
        var newState = events.Aggregate(initial, (s, e) => s.Evolve(e));

        await store.SaveAsync(streamId, "counter", 0, newState, events, metadata);

        var (loaded, version) = await store.LoadAsync<CounterState, CounterEvent>(streamId);

        Assert.Equal(1, loaded.Value);
        Assert.Equal(1, version);
    }

    [Fact]
    public async Task Multiple_appends_accumulate_state()
    {
        var (db, store) = CreateStore();
        await using var _ = db;
        var streamId = Guid.NewGuid();
        var metadata = new EventMetadata
        {
            ActorId = Guid.NewGuid(),
            CorrelationId = Guid.NewGuid()
        };

        // First append: increment
        var state = CounterState.Initial;
        var events1 = CounterDecider.Decide(state, new CounterCommand.Increment());
        state = events1.Aggregate(state, (s, e) => s.Evolve(e));
        await store.SaveAsync(streamId, "counter", 0, state, events1, metadata);

        // Second append: increment again
        var events2 = CounterDecider.Decide(state, new CounterCommand.Increment());
        state = events2.Aggregate(state, (s, e) => s.Evolve(e));
        await store.SaveAsync(streamId, "counter", 1, state, events2, metadata);

        // Third append: decrement by 1
        var events3 = CounterDecider.Decide(state, new CounterCommand.Decrement(1));
        state = events3.Aggregate(state, (s, e) => s.Evolve(e));
        await store.SaveAsync(streamId, "counter", 2, state, events3, metadata);

        var (loaded, version) = await store.LoadAsync<CounterState, CounterEvent>(streamId);

        Assert.Equal(1, loaded.Value); // 0 + 1 + 1 - 1 = 1
        Assert.Equal(3, version);
    }

    [Fact]
    public async Task Decide_rejects_invalid_command()
    {
        var state = CounterState.Initial; // Value = 0

        Assert.Throws<InvalidOperationException>(() =>
            CounterDecider.Decide(state, new CounterCommand.Decrement(1)));
    }
}
```

- [ ] **Step 7: Run tests to verify they fail**

```bash
dotnet test tests/Vellum.Tests --filter "AggregateStoreTests"
```

Expected: compilation error — `AggregateStore` class doesn't exist yet.

- [ ] **Step 8: Implement AggregateStore**

`src/Vellum/Kernel/Aggregates/AggregateStore.cs`:
```csharp
using System.Text.Json;
using Vellum.Kernel.EventStore;
using Vellum.Kernel.EventTypes;

namespace Vellum.Kernel.Aggregates;

public sealed class AggregateStore
{
    private readonly IEventStore _eventStore;
    private readonly IEventTypeRegistry _registry;

    public AggregateStore(IEventStore eventStore, IEventTypeRegistry registry)
    {
        _eventStore = eventStore;
        _registry = registry;
    }

    public async Task<(TState State, int Version)> LoadAsync<TState, TEvent>(
        Guid streamId,
        CancellationToken ct = default)
        where TState : IAggregateState<TState, TEvent>
    {
        var snapshot = await _eventStore.LoadAsync(streamId, ct);
        if (snapshot is null)
            return (TState.Initial, 0);

        var state = JsonSerializer.Deserialize<TState>(snapshot.State, JsonOptions)!;
        return (state, snapshot.Version);
    }

    public async Task SaveAsync<TState, TEvent>(
        Guid streamId,
        string streamType,
        int expectedVersion,
        TState newState,
        IReadOnlyList<TEvent> events,
        EventMetadata metadata,
        CancellationToken ct = default)
        where TState : IAggregateState<TState, TEvent>
        where TEvent : notnull
    {
        var stateJson = JsonSerializer.SerializeToDocument(newState, JsonOptions);
        var metadataJson = JsonSerializer.SerializeToDocument(metadata, JsonOptions);

        var newEvents = events.Select(e => new NewEvent(
            _registry.GetTypeName(e.GetType()),
            JsonSerializer.SerializeToDocument(e, e.GetType(), JsonOptions),
            metadataJson
        )).ToList();

        await _eventStore.AppendAsync(streamId, streamType, expectedVersion, stateJson, newEvents, ct);
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };
}
```

- [ ] **Step 9: Register in DI**

Add to `src/Vellum/Program.cs` after the existing registrations:
```csharp
using Vellum.Kernel.Aggregates;
using Vellum.Kernel.EventTypes;

// ... existing builder.Services lines ...
builder.Services.AddSingleton<EventTypeRegistry>();
builder.Services.AddSingleton<IEventTypeRegistry>(sp => sp.GetRequiredService<EventTypeRegistry>());
builder.Services.AddScoped<AggregateStore>();
```

- [ ] **Step 10: Run tests**

```bash
dotnet test tests/Vellum.Tests --filter "AggregateStoreTests|EventTypeRegistryTests"
```

Expected: all tests PASS.

- [ ] **Step 11: Commit**

```bash
git add -A
git commit -m "feat(kernel): add event type registry and typed aggregate store with decide/evolve pattern"
```

---

### Task 4: Upcasting

**Files:**
- Modify: `src/Vellum/Kernel/EventTypes/IEventTypeRegistry.cs` (add upcasting methods), `src/Vellum/Kernel/EventTypes/EventTypeRegistry.cs` (implement upcasting)
- Create: `tests/Vellum.Tests/Kernel/EventTypes/UpcastingTests.cs`

**Interfaces:**
- Consumes: `IEventTypeRegistry` (from Task 3)
- Produces:
  - `IEventTypeRegistry.RegisterUpcast(string fromTypeName, string toTypeName, Func<JsonNode, JsonNode> transform)` — register a version migration
  - `IEventTypeRegistry.DeserializeEvent(string storedTypeName, JsonDocument payload) → object` — resolves type, applies upcasters, deserializes

- [ ] **Step 1: Write failing upcasting tests**

`tests/Vellum.Tests/Kernel/EventTypes/UpcastingTests.cs`:
```csharp
using System.Text.Json;
using System.Text.Json.Nodes;
using Vellum.Kernel.EventTypes;

namespace Vellum.Tests.Kernel.EventTypes;

public class UpcastingTests
{
    private sealed record ItemAddedV2(string Name, int Quantity);

    [Fact]
    public void Deserialize_current_version_without_upcasting()
    {
        var registry = new EventTypeRegistry();
        registry.Register<ItemAddedV2>("item.added.v2");

        var payload = JsonSerializer.SerializeToDocument(new { name = "Widget", quantity = 5 });

        var result = registry.DeserializeEvent("item.added.v2", payload);

        var typed = Assert.IsType<ItemAddedV2>(result);
        Assert.Equal("Widget", typed.Name);
        Assert.Equal(5, typed.Quantity);
    }

    [Fact]
    public void Upcast_v1_to_v2_adds_missing_field()
    {
        var registry = new EventTypeRegistry();
        registry.Register<ItemAddedV2>("item.added.v2");

        registry.RegisterUpcast("item.added.v1", "item.added.v2", node =>
        {
            node["quantity"] = 1;
            return node;
        });

        var v1Payload = JsonSerializer.SerializeToDocument(new { name = "Widget" });

        var result = registry.DeserializeEvent("item.added.v1", v1Payload);

        var typed = Assert.IsType<ItemAddedV2>(result);
        Assert.Equal("Widget", typed.Name);
        Assert.Equal(1, typed.Quantity);
    }

    [Fact]
    public void Upcast_chain_v1_to_v2_to_v3()
    {
        var registry = new EventTypeRegistry();

        // v3 is the current type
        registry.Register<ItemAddedV3>("item.added.v3");

        registry.RegisterUpcast("item.added.v1", "item.added.v2", node =>
        {
            node["quantity"] = 1;
            return node;
        });
        registry.RegisterUpcast("item.added.v2", "item.added.v3", node =>
        {
            node["category"] = "default";
            return node;
        });

        var v1Payload = JsonSerializer.SerializeToDocument(new { name = "Widget" });

        var result = registry.DeserializeEvent("item.added.v1", v1Payload);

        var typed = Assert.IsType<ItemAddedV3>(result);
        Assert.Equal("Widget", typed.Name);
        Assert.Equal(1, typed.Quantity);
        Assert.Equal("default", typed.Category);
    }

    [Fact]
    public void Deserialize_unknown_type_throws()
    {
        var registry = new EventTypeRegistry();

        var payload = JsonSerializer.SerializeToDocument(new { });

        Assert.Throws<InvalidOperationException>(() =>
            registry.DeserializeEvent("totally.unknown.v1", payload));
    }

    private sealed record ItemAddedV3(string Name, int Quantity, string Category);
}
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
dotnet test tests/Vellum.Tests --filter "UpcastingTests"
```

Expected: compilation error — `RegisterUpcast` and `DeserializeEvent` methods don't exist.

- [ ] **Step 3: Extend IEventTypeRegistry**

Update `src/Vellum/Kernel/EventTypes/IEventTypeRegistry.cs`:
```csharp
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Vellum.Kernel.EventTypes;

public interface IEventTypeRegistry
{
    string GetTypeName(Type clrType);
    Type GetClrType(string typeName);
    void Register<T>(string typeName) where T : class;
    void RegisterUpcast(string fromTypeName, string toTypeName, Func<JsonNode, JsonNode> transform);
    object DeserializeEvent(string storedTypeName, JsonDocument payload);
}
```

- [ ] **Step 4: Implement upcasting in EventTypeRegistry**

Replace `src/Vellum/Kernel/EventTypes/EventTypeRegistry.cs`:
```csharp
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Vellum.Kernel.EventTypes;

public sealed class EventTypeRegistry : IEventTypeRegistry
{
    private readonly Dictionary<Type, string> _clrToName = new();
    private readonly Dictionary<string, Type> _nameToClr = new();
    private readonly Dictionary<string, (string TargetTypeName, Func<JsonNode, JsonNode> Transform)> _upcasts = new();

    private static readonly JsonSerializerOptions DeserializeOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public void Register<T>(string typeName) where T : class
    {
        _clrToName[typeof(T)] = typeName;
        _nameToClr[typeName] = typeof(T);
    }

    public string GetTypeName(Type clrType) =>
        _clrToName.TryGetValue(clrType, out var name)
            ? name
            : throw new InvalidOperationException($"No type name registered for {clrType.FullName}");

    public Type GetClrType(string typeName) =>
        _nameToClr.TryGetValue(typeName, out var type)
            ? type
            : throw new InvalidOperationException($"No CLR type registered for '{typeName}'");

    public void RegisterUpcast(
        string fromTypeName,
        string toTypeName,
        Func<JsonNode, JsonNode> transform)
    {
        _upcasts[fromTypeName] = (toTypeName, transform);
    }

    public object DeserializeEvent(string storedTypeName, JsonDocument payload)
    {
        var currentTypeName = storedTypeName;
        var node = JsonNode.Parse(payload.RootElement.GetRawText())!;

        while (_upcasts.TryGetValue(currentTypeName, out var upcast))
        {
            node = upcast.Transform(node);
            currentTypeName = upcast.TargetTypeName;
        }

        var clrType = GetClrType(currentTypeName);
        return node.Deserialize(clrType, DeserializeOptions)
               ?? throw new InvalidOperationException($"Failed to deserialize event '{currentTypeName}'");
    }
}
```

- [ ] **Step 5: Run tests**

```bash
dotnet test tests/Vellum.Tests --filter "UpcastingTests|EventTypeRegistryTests"
```

Expected: all tests PASS.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat(kernel): add event upcasting with chained version transforms"
```

---

### Task 5: Command pipeline + inline projections + Result type

**Files:**
- Create: `src/Vellum/Kernel/Results/CommandResult.cs`, `src/Vellum/Kernel/CommandHandling/ICommandHandler.cs`, `src/Vellum/Kernel/CommandHandling/EventCollector.cs`, `src/Vellum/Kernel/Projections/IInlineProjection.cs`, `src/Vellum/Kernel/CommandHandling/TransactionBehavior.cs`, `tests/Vellum.Tests/Kernel/CommandHandling/TransactionBehaviorTests.cs`
- Modify: `src/Vellum/Program.cs` (register services, Scrutor decorator)

**Interfaces:**
- Consumes: `IEventStore` (Task 2), `AggregateStore` (Task 3), `EventStoreDbContext` (Task 2)
- Produces:
  - `ICommandHandler<TCommand, TResult>` — `Task<TResult> HandleAsync(TCommand command, CancellationToken ct)`
  - `TransactionBehavior<TCommand, TResult>` — Scrutor decorator: begins transaction → runs handler → runs inline projections → commits → sends NOTIFY
  - `EventCollector` — scoped service; `Add(Guid streamId, string eventType, JsonDocument payload)`, `IReadOnlyList<CollectedEvent> Events`
  - `IInlineProjection` — `Task ProjectAsync(IReadOnlyList<CollectedEvent> events, CancellationToken ct)`
  - `CommandResult` — sealed hierarchy: `Success`, `Invalid(errors)`, `Conflict(message)`, `NotFound(message)`

- [ ] **Step 1: Create CommandResult type**

`src/Vellum/Kernel/Results/CommandResult.cs`:
```csharp
namespace Vellum.Kernel.Results;

public abstract record CommandResult
{
    public sealed record Success : CommandResult;
    public sealed record Invalid(IReadOnlyList<ValidationError> Errors) : CommandResult;
    public sealed record Conflict(string Message) : CommandResult;
    public sealed record NotFound(string Message) : CommandResult;

    private CommandResult() { }
}

public abstract record CommandResult<T>
{
    public sealed record Success(T Value) : CommandResult<T>;
    public sealed record Invalid(IReadOnlyList<ValidationError> Errors) : CommandResult<T>;
    public sealed record Conflict(string Message) : CommandResult<T>;
    public sealed record NotFound(string Message) : CommandResult<T>;

    private CommandResult() { }
}

public sealed record ValidationError(string Field, string Message);
```

- [ ] **Step 2: Create ICommandHandler, EventCollector, and IInlineProjection**

`src/Vellum/Kernel/CommandHandling/ICommandHandler.cs`:
```csharp
namespace Vellum.Kernel.CommandHandling;

public interface ICommandHandler<in TCommand, TResult>
{
    Task<TResult> HandleAsync(TCommand command, CancellationToken ct = default);
}
```

`src/Vellum/Kernel/CommandHandling/EventCollector.cs`:
```csharp
using System.Text.Json;

namespace Vellum.Kernel.CommandHandling;

public sealed class EventCollector
{
    private readonly List<CollectedEvent> _events = [];

    public IReadOnlyList<CollectedEvent> Events => _events;

    public void Add(Guid streamId, string eventType, JsonDocument payload)
    {
        _events.Add(new CollectedEvent(streamId, eventType, payload));
    }

    public void Clear() => _events.Clear();
}

public sealed record CollectedEvent(Guid StreamId, string EventType, JsonDocument Payload);
```

`src/Vellum/Kernel/Projections/IInlineProjection.cs`:
```csharp
using Vellum.Kernel.CommandHandling;

namespace Vellum.Kernel.Projections;

public interface IInlineProjection
{
    Task ProjectAsync(IReadOnlyList<CollectedEvent> events, CancellationToken ct = default);
}
```

- [ ] **Step 3: Wire EventCollector into EventStore**

The `EventStore.AppendAsync` must add events to the collector so the transaction decorator can feed them to inline projections.

Update `src/Vellum/Kernel/EventStore/EventStore.cs` — add `EventCollector` as a constructor dependency:
```csharp
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Vellum.Kernel.CommandHandling;

namespace Vellum.Kernel.EventStore;

public sealed class EventStore : IEventStore
{
    private readonly EventStoreDbContext _db;
    private readonly EventCollector _collector;

    public EventStore(EventStoreDbContext db, EventCollector collector)
    {
        _db = db;
        _collector = collector;
    }

    public async Task<StreamSnapshot?> LoadAsync(Guid streamId, CancellationToken ct = default)
    {
        var stream = await _db.Streams
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.StreamId == streamId, ct);

        if (stream is null) return null;
        return new StreamSnapshot(stream.Version, stream.State);
    }

    public async Task AppendAsync(
        Guid streamId,
        string streamType,
        int expectedVersion,
        JsonDocument newState,
        IReadOnlyList<NewEvent> events,
        CancellationToken ct = default)
    {
        var newVersion = expectedVersion + events.Count;

        if (expectedVersion == 0)
        {
            _db.Streams.Add(new StreamEntity
            {
                StreamId = streamId,
                StreamType = streamType,
                Version = newVersion,
                State = newState,
            });
        }
        else
        {
            var stream = await _db.Streams.FindAsync([streamId], ct)
                ?? throw new ConcurrencyException(streamId, expectedVersion, -1);

            if (stream.Version != expectedVersion)
                throw new ConcurrencyException(streamId, expectedVersion, stream.Version);

            stream.Version = newVersion;
            stream.State = newState;
            stream.UpdatedAt = DateTimeOffset.UtcNow;
        }

        for (var i = 0; i < events.Count; i++)
        {
            _db.Events.Add(new EventEntity
            {
                StreamId = streamId,
                Version = expectedVersion + i + 1,
                EventType = events[i].EventType,
                Payload = events[i].Payload,
                Metadata = events[i].Metadata,
            });

            _collector.Add(streamId, events[i].EventType, events[i].Payload);
        }

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ConcurrencyException(streamId, expectedVersion, -1);
        }
    }
}
```

- [ ] **Step 4: Write failing TransactionBehavior tests**

`tests/Vellum.Tests/Kernel/CommandHandling/TransactionBehaviorTests.cs`:
```csharp
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Vellum.Kernel.Aggregates;
using Vellum.Kernel.CommandHandling;
using Vellum.Kernel.EventStore;
using Vellum.Kernel.EventTypes;
using Vellum.Kernel.Projections;
using Vellum.Kernel.Results;

namespace Vellum.Tests.Kernel.CommandHandling;

[Collection("Integration")]
public class TransactionBehaviorTests
{
    private readonly IntegrationFixture _fixture;

    public TransactionBehaviorTests(IntegrationFixture fixture) => _fixture = fixture;

    private record IncrementCommand(Guid StreamId);

    private sealed class IncrementHandler : ICommandHandler<IncrementCommand, CommandResult>
    {
        private readonly AggregateStore _store;

        public IncrementHandler(AggregateStore store) => _store = store;

        public async Task<CommandResult> HandleAsync(IncrementCommand command, CancellationToken ct = default)
        {
            var (state, version) = await _store.LoadAsync<Aggregates.CounterState, Aggregates.CounterEvent>(command.StreamId, ct);
            var events = new Aggregates.CounterEvent[] { new Aggregates.CounterEvent.Incremented() };
            var newState = events.Aggregate(state, (s, e) => s.Evolve(e));
            var metadata = new EventMetadata { ActorId = Guid.NewGuid(), CorrelationId = Guid.NewGuid() };
            await _store.SaveAsync(command.StreamId, "counter", version, newState, events, metadata, ct);
            return new CommandResult.Success();
        }
    }

    private sealed class CountingProjection : IInlineProjection
    {
        public int EventCount { get; private set; }

        public Task ProjectAsync(IReadOnlyList<CollectedEvent> events, CancellationToken ct = default)
        {
            EventCount += events.Count;
            return Task.CompletedTask;
        }
    }

    private (EventStoreDbContext Db, TransactionBehavior<IncrementCommand, CommandResult> Pipeline, CountingProjection Projection) CreatePipeline()
    {
        var options = new DbContextOptionsBuilder<EventStoreDbContext>()
            .UseNpgsql(_fixture.ConnectionString)
            .UseSnakeCaseNamingConvention()
            .Options;
        var db = new EventStoreDbContext(options);

        var collector = new EventCollector();
        var eventStore = new Vellum.Kernel.EventStore.EventStore(db, collector);

        var registry = new EventTypeRegistry();
        registry.Register<Aggregates.CounterEvent.Incremented>("counter.incremented.v1");
        registry.Register<Aggregates.CounterEvent.Decremented>("counter.decremented.v1");

        var aggregateStore = new AggregateStore(eventStore, registry);
        var handler = new IncrementHandler(aggregateStore);
        var projection = new CountingProjection();

        var behavior = new TransactionBehavior<IncrementCommand, CommandResult>(
            handler, db, collector, [projection]);

        return (db, behavior, projection);
    }

    [Fact]
    public async Task Command_commits_events_and_runs_inline_projection()
    {
        var (db, pipeline, projection) = CreatePipeline();
        await using var _ = db;
        var streamId = Guid.NewGuid();

        var result = await pipeline.HandleAsync(new IncrementCommand(streamId));

        Assert.IsType<CommandResult.Success>(result);
        Assert.Equal(1, projection.EventCount);

        var stream = await db.Streams.AsNoTracking().FirstOrDefaultAsync(s => s.StreamId == streamId);
        Assert.NotNull(stream);
        Assert.Equal(1, stream!.Version);
    }

    [Fact]
    public async Task Failed_handler_does_not_commit()
    {
        var options = new DbContextOptionsBuilder<EventStoreDbContext>()
            .UseNpgsql(_fixture.ConnectionString)
            .UseSnakeCaseNamingConvention()
            .Options;
        var db = new EventStoreDbContext(options);
        var collector = new EventCollector();

        var failingHandler = new FailingHandler();
        var behavior = new TransactionBehavior<IncrementCommand, CommandResult>(
            failingHandler, db, collector, []);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => behavior.HandleAsync(new IncrementCommand(Guid.NewGuid())));
    }

    private sealed class FailingHandler : ICommandHandler<IncrementCommand, CommandResult>
    {
        public Task<CommandResult> HandleAsync(IncrementCommand command, CancellationToken ct = default)
            => throw new InvalidOperationException("Simulated failure");
    }
}
```

- [ ] **Step 5: Run tests to verify they fail**

```bash
dotnet test tests/Vellum.Tests --filter "TransactionBehaviorTests"
```

Expected: compilation error — `TransactionBehavior` doesn't exist.

- [ ] **Step 6: Implement TransactionBehavior**

`src/Vellum/Kernel/CommandHandling/TransactionBehavior.cs`:
```csharp
using Vellum.Kernel.EventStore;
using Vellum.Kernel.Projections;

namespace Vellum.Kernel.CommandHandling;

public sealed class TransactionBehavior<TCommand, TResult> : ICommandHandler<TCommand, TResult>
{
    private readonly ICommandHandler<TCommand, TResult> _inner;
    private readonly EventStoreDbContext _db;
    private readonly EventCollector _collector;
    private readonly IEnumerable<IInlineProjection> _projections;

    public TransactionBehavior(
        ICommandHandler<TCommand, TResult> inner,
        EventStoreDbContext db,
        EventCollector collector,
        IEnumerable<IInlineProjection> projections)
    {
        _inner = inner;
        _db = db;
        _collector = collector;
        _projections = projections;
    }

    public async Task<TResult> HandleAsync(TCommand command, CancellationToken ct = default)
    {
        _collector.Clear();

        await using var transaction = await _db.Database.BeginTransactionAsync(ct);
        try
        {
            var result = await _inner.HandleAsync(command, ct);

            var events = _collector.Events;
            foreach (var projection in _projections)
                await projection.ProjectAsync(events, ct);

            await transaction.CommitAsync(ct);

            if (events.Count > 0)
                await _db.Database.ExecuteSqlRawAsync("NOTIFY new_events", ct);

            return result;
        }
        catch
        {
            await transaction.RollbackAsync(ct);
            throw;
        }
    }
}
```

- [ ] **Step 7: Fix EventStoreTests and AggregateStoreTests to pass EventCollector**

The `EventStore` constructor now requires an `EventCollector`. Update the test helper in `EventStoreTests`:

```csharp
private (EventStoreDbContext Db, Vellum.Kernel.EventStore.EventStore Store) CreateStore()
{
    var options = new DbContextOptionsBuilder<EventStoreDbContext>()
        .UseNpgsql(_fixture.ConnectionString)
        .UseSnakeCaseNamingConvention()
        .Options;
    var db = new EventStoreDbContext(options);
    return (db, new Vellum.Kernel.EventStore.EventStore(db, new EventCollector()));
}
```

Add `using Vellum.Kernel.CommandHandling;` to the imports.

Also update the test helper in `AggregateStoreTests`:

```csharp
private (EventStoreDbContext Db, AggregateStore Store) CreateStore()
{
    var options = new DbContextOptionsBuilder<EventStoreDbContext>()
        .UseNpgsql(_fixture.ConnectionString)
        .UseSnakeCaseNamingConvention()
        .Options;
    var db = new EventStoreDbContext(options);
    var eventStore = new Vellum.Kernel.EventStore.EventStore(db, new EventCollector());

    var registry = new EventTypeRegistry();
    registry.Register<CounterEvent.Incremented>("counter.incremented.v1");
    registry.Register<CounterEvent.Decremented>("counter.decremented.v1");

    var aggregateStore = new AggregateStore(eventStore, registry);
    return (db, aggregateStore);
}
```

Add `using Vellum.Kernel.CommandHandling;` to the imports in `AggregateStoreTests`.

- [ ] **Step 8: Update Program.cs with DI registrations**

```csharp
using Microsoft.EntityFrameworkCore;
using Vellum.Kernel.Aggregates;
using Vellum.Kernel.CommandHandling;
using Vellum.Kernel.EventStore;
using Vellum.Kernel.EventTypes;
using Vellum.Kernel.Projections;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")!;

builder.Services.AddDbContext<EventStoreDbContext>(options =>
    options.UseNpgsql(connectionString)
           .UseSnakeCaseNamingConvention());

builder.Services.AddScoped<IEventStore, EventStore>();
builder.Services.AddScoped<EventCollector>();
builder.Services.AddScoped<AggregateStore>();
builder.Services.AddSingleton<EventTypeRegistry>();
builder.Services.AddSingleton<IEventTypeRegistry>(sp => sp.GetRequiredService<EventTypeRegistry>());

var app = builder.Build();

app.MapGet("/health", () => Results.Ok());

app.Run();
```

Note: Scrutor decoration of `ICommandHandler<,>` is registered per-feature (e.g., in Phase 1's modelling module), not globally here. The `TransactionBehavior` wraps specific command handlers using Scrutor's `Decorate<>` at registration time:

```csharp
// Example for Phase 1 (not added now):
// builder.Services.AddScoped<ICommandHandler<AddElement, CommandResult>, AddElementHandler>();
// builder.Services.Decorate<ICommandHandler<AddElement, CommandResult>, TransactionBehavior<AddElement, CommandResult>>();
```

- [ ] **Step 9: Run all tests**

```bash
dotnet test tests/Vellum.Tests
```

Expected: all tests PASS (smoke, event store, registry, upcasting, aggregate store, transaction behavior).

- [ ] **Step 10: Commit**

```bash
git add -A
git commit -m "feat(kernel): add command pipeline with transaction behavior, inline projections, and result type"
```

---

### Task 6: Outbox + dispatcher

**Files:**
- Create: `src/Vellum/Kernel/Outbox/OutboxMessageEntity.cs`, `src/Vellum/Kernel/Outbox/DeadLetterEntity.cs`, `src/Vellum/Kernel/Outbox/OutboxDispatcher.cs`, `tests/Vellum.Tests/Kernel/Outbox/OutboxDispatcherTests.cs`
- Modify: `src/Vellum/Kernel/EventStore/EventStoreDbContext.cs` (add outbox entities), `src/Vellum/Program.cs` (register dispatcher)

**Interfaces:**
- Consumes: `EventStoreDbContext` (Task 2)
- Produces:
  - `OutboxMessageEntity` — `Id`, `EventType`, `Payload` (jsonb), `CreatedAt`, `RetryCount`, `NextRetryAt`, `ProcessedAt`
  - `DeadLetterEntity` — `Id`, `EventType`, `Payload`, `Error`, `FailedAt`
  - `OutboxDispatcher` — `BackgroundService` that polls outbox, dispatches, retries with exponential backoff (1s, 5s, 25s, 2m, 10m), moves to dead letters after 5 failures

- [ ] **Step 1: Create outbox entities**

`src/Vellum/Kernel/Outbox/OutboxMessageEntity.cs`:
```csharp
using System.Text.Json;

namespace Vellum.Kernel.Outbox;

public class OutboxMessageEntity
{
    public long Id { get; set; }
    public string EventType { get; set; } = null!;
    public JsonDocument Payload { get; set; } = null!;
    public DateTimeOffset CreatedAt { get; set; }
    public int RetryCount { get; set; }
    public DateTimeOffset? NextRetryAt { get; set; }
    public DateTimeOffset? ProcessedAt { get; set; }
}
```

`src/Vellum/Kernel/Outbox/DeadLetterEntity.cs`:
```csharp
using System.Text.Json;

namespace Vellum.Kernel.Outbox;

public class DeadLetterEntity
{
    public long Id { get; set; }
    public string EventType { get; set; } = null!;
    public JsonDocument Payload { get; set; } = null!;
    public string Error { get; set; } = null!;
    public DateTimeOffset FailedAt { get; set; }
}
```

- [ ] **Step 2: Add outbox entities to EventStoreDbContext**

Add to the end of `OnModelCreating` in `EventStoreDbContext`:
```csharp
using Vellum.Kernel.Outbox;

// Add DbSet properties:
public DbSet<OutboxMessageEntity> OutboxMessages => Set<OutboxMessageEntity>();
public DbSet<DeadLetterEntity> DeadLetters => Set<DeadLetterEntity>();

// Add to OnModelCreating:
modelBuilder.Entity<OutboxMessageEntity>(b =>
{
    b.ToTable("outbox_messages");
    b.HasKey(o => o.Id);
    b.Property(o => o.Id).UseIdentityAlwaysColumn();
    b.Property(o => o.Payload).HasColumnType("jsonb");
    b.Property(o => o.CreatedAt).HasDefaultValueSql("now()");
    b.HasIndex(o => new { o.ProcessedAt, o.NextRetryAt });
});

modelBuilder.Entity<DeadLetterEntity>(b =>
{
    b.ToTable("dead_letters");
    b.HasKey(d => d.Id);
    b.Property(d => d.Id).UseIdentityAlwaysColumn();
    b.Property(d => d.Payload).HasColumnType("jsonb");
    b.Property(d => d.FailedAt).HasDefaultValueSql("now()");
});
```

- [ ] **Step 3: Generate migration**

```bash
dotnet ef migrations add AddOutbox --project src/Vellum --context EventStoreDbContext --output-dir Kernel/EventStore/Migrations
```

- [ ] **Step 4: Write failing OutboxDispatcher tests**

`tests/Vellum.Tests/Kernel/Outbox/OutboxDispatcherTests.cs`:
```csharp
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Vellum.Kernel.EventStore;
using Vellum.Kernel.Outbox;

namespace Vellum.Tests.Kernel.Outbox;

[Collection("Integration")]
public class OutboxDispatcherTests
{
    private readonly IntegrationFixture _fixture;

    public OutboxDispatcherTests(IntegrationFixture fixture) => _fixture = fixture;

    private EventStoreDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<EventStoreDbContext>()
            .UseNpgsql(_fixture.ConnectionString)
            .UseSnakeCaseNamingConvention()
            .Options;
        return new EventStoreDbContext(options);
    }

    private async Task SeedMessage(string eventType = "test.event.v1", int retryCount = 0)
    {
        await using var db = CreateDb();
        db.OutboxMessages.Add(new OutboxMessageEntity
        {
            EventType = eventType,
            Payload = JsonSerializer.SerializeToDocument(new { value = 1 }),
            RetryCount = retryCount,
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Dispatches_pending_message_and_marks_processed()
    {
        await SeedMessage();
        var dispatched = new List<string>();

        await using var db = CreateDb();
        var dispatcher = new OutboxDispatcher(
            new TestServiceScopeFactory(() => CreateDb()),
            NullLogger<OutboxDispatcher>.Instance);

        await dispatcher.ProcessBatchAsync(
            msg => { dispatched.Add(msg.EventType); return Task.CompletedTask; },
            CancellationToken.None);

        Assert.Contains("test.event.v1", dispatched);

        await using var verifyDb = CreateDb();
        var remaining = await verifyDb.OutboxMessages
            .Where(m => m.ProcessedAt == null)
            .CountAsync();
        Assert.Equal(0, remaining);
    }

    [Fact]
    public async Task Failed_dispatch_increments_retry_and_sets_next_retry()
    {
        await SeedMessage();

        await using var db = CreateDb();
        var dispatcher = new OutboxDispatcher(
            new TestServiceScopeFactory(() => CreateDb()),
            NullLogger<OutboxDispatcher>.Instance);

        await dispatcher.ProcessBatchAsync(
            _ => throw new Exception("Dispatch failed"),
            CancellationToken.None);

        await using var verifyDb = CreateDb();
        var msg = await verifyDb.OutboxMessages
            .Where(m => m.ProcessedAt == null)
            .FirstAsync();
        Assert.Equal(1, msg.RetryCount);
        Assert.NotNull(msg.NextRetryAt);
    }

    [Fact]
    public async Task Message_moved_to_dead_letters_after_max_retries()
    {
        await SeedMessage(retryCount: 4); // Will become 5th failure

        await using var db = CreateDb();
        var dispatcher = new OutboxDispatcher(
            new TestServiceScopeFactory(() => CreateDb()),
            NullLogger<OutboxDispatcher>.Instance);

        await dispatcher.ProcessBatchAsync(
            _ => throw new Exception("Still failing"),
            CancellationToken.None);

        await using var verifyDb = CreateDb();
        var deadLetters = await verifyDb.DeadLetters.CountAsync();
        Assert.True(deadLetters > 0);

        var remaining = await verifyDb.OutboxMessages
            .Where(m => m.ProcessedAt == null)
            .CountAsync();
        Assert.Equal(0, remaining);
    }

    private sealed class TestServiceScopeFactory : IServiceScopeFactory
    {
        private readonly Func<EventStoreDbContext> _dbFactory;

        public TestServiceScopeFactory(Func<EventStoreDbContext> dbFactory) => _dbFactory = dbFactory;

        public IServiceScope CreateScope() => new TestScope(_dbFactory);

        private sealed class TestScope : IServiceScope
        {
            private readonly EventStoreDbContext _db;
            public IServiceProvider ServiceProvider { get; }

            public TestScope(Func<EventStoreDbContext> dbFactory)
            {
                _db = dbFactory();
                ServiceProvider = new TestServiceProvider(_db);
            }

            public void Dispose() => _db.Dispose();
        }

        private sealed class TestServiceProvider : IServiceProvider
        {
            private readonly EventStoreDbContext _db;
            public TestServiceProvider(EventStoreDbContext db) => _db = db;
            public object? GetService(Type serviceType) =>
                serviceType == typeof(EventStoreDbContext) ? _db : null;
        }
    }
}
```

- [ ] **Step 5: Run tests to verify they fail**

```bash
dotnet test tests/Vellum.Tests --filter "OutboxDispatcherTests"
```

Expected: compilation error — `OutboxDispatcher` and its `ProcessBatchAsync` method don't exist.

- [ ] **Step 6: Implement OutboxDispatcher**

`src/Vellum/Kernel/Outbox/OutboxDispatcher.cs`:
```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Vellum.Kernel.EventStore;

namespace Vellum.Kernel.Outbox;

public sealed class OutboxDispatcher : BackgroundService
{
    private static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(25),
        TimeSpan.FromMinutes(2),
        TimeSpan.FromMinutes(10),
    ];

    private const int MaxRetries = 5;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<OutboxDispatcher> _logger;

    public OutboxDispatcher(IServiceScopeFactory scopeFactory, ILogger<OutboxDispatcher> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await ProcessBatchAsync(DefaultDispatch, stoppingToken);
            await Task.Delay(PollInterval, stoppingToken);
        }
    }

    private static Task DefaultDispatch(OutboxMessageEntity message) => Task.CompletedTask;

    public async Task ProcessBatchAsync(
        Func<OutboxMessageEntity, Task> dispatch,
        CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EventStoreDbContext>();

        var now = DateTimeOffset.UtcNow;
        var messages = await db.OutboxMessages
            .FromSqlInterpolated(
                $"""
                SELECT *
                FROM es.outbox_messages
                WHERE processed_at IS NULL
                  AND (next_retry_at IS NULL OR next_retry_at <= {now})
                ORDER BY id
                LIMIT 100
                FOR UPDATE SKIP LOCKED
                """)
            .ToListAsync(ct);

        foreach (var message in messages)
        {
            try
            {
                await dispatch(message);
                message.ProcessedAt = DateTimeOffset.UtcNow;
            }
            catch (Exception ex)
            {
                message.RetryCount++;

                if (message.RetryCount >= MaxRetries)
                {
                    db.DeadLetters.Add(new DeadLetterEntity
                    {
                        EventType = message.EventType,
                        Payload = message.Payload,
                        Error = ex.ToString(),
                    });
                    db.OutboxMessages.Remove(message);
                    _logger.LogError(ex, "Outbox message {Id} moved to dead letters after {MaxRetries} retries",
                        message.Id, MaxRetries);
                }
                else
                {
                    var delay = RetryDelays[Math.Min(message.RetryCount - 1, RetryDelays.Length - 1)];
                    message.NextRetryAt = DateTimeOffset.UtcNow + delay;
                    _logger.LogWarning(ex, "Outbox message {Id} failed (retry {Count}/{Max}), next retry at {NextRetry}",
                        message.Id, message.RetryCount, MaxRetries, message.NextRetryAt);
                }
            }
        }

        await db.SaveChangesAsync(ct);
    }
}
```

- [ ] **Step 7: Register in Program.cs**

Add to `src/Vellum/Program.cs`:
```csharp
using Vellum.Kernel.Outbox;

// ... existing registrations ...
builder.Services.AddHostedService<OutboxDispatcher>();
```

- [ ] **Step 8: Run tests**

```bash
dotnet test tests/Vellum.Tests --filter "OutboxDispatcherTests"
```

Expected: all 3 tests PASS.

- [ ] **Step 9: Commit**

```bash
git add -A
git commit -m "feat(kernel): add outbox dispatcher with exponential backoff retry and dead letters"
```

---

### Task 7: Async projection host

**Files:**
- Create: `src/Vellum/Kernel/Projections/CheckpointEntity.cs`, `src/Vellum/Kernel/Projections/IAsyncProjection.cs`, `src/Vellum/Kernel/Projections/AsyncProjectionHost.cs`, `tests/Vellum.Tests/Kernel/Projections/AsyncProjectionHostTests.cs`
- Modify: `src/Vellum/Kernel/EventStore/EventStoreDbContext.cs` (add checkpoint entity), `src/Vellum/Program.cs` (register host)

**Interfaces:**
- Consumes: `EventStoreDbContext` (Task 2), `IEventTypeRegistry` (Task 3/4)
- Produces:
  - `IAsyncProjection` — `string Name`, `Task ProjectAsync(PersistedEvent @event, CancellationToken ct)`
  - `PersistedEvent` record: `(Guid StreamId, int Version, long GlobalPosition, string EventType, object Data, DateTimeOffset OccurredAt)`
  - `AsyncProjectionHost` — `BackgroundService` that reads events by `global_position`, uses `xid` guard for gap safety, tracks checkpoints, wakes on `NOTIFY new_events`

- [ ] **Step 1: Create checkpoint entity and IAsyncProjection**

`src/Vellum/Kernel/Projections/CheckpointEntity.cs`:
```csharp
namespace Vellum.Kernel.Projections;

public class CheckpointEntity
{
    public string ProjectionName { get; set; } = null!;
    public long LastProcessedPosition { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
```

`src/Vellum/Kernel/Projections/IAsyncProjection.cs`:
```csharp
using System.Text.Json;

namespace Vellum.Kernel.Projections;

public interface IAsyncProjection
{
    string Name { get; }
    Task ProjectAsync(PersistedEvent @event, CancellationToken ct = default);
}

public sealed record PersistedEvent(
    Guid StreamId,
    int Version,
    long GlobalPosition,
    string EventType,
    object Data,
    DateTimeOffset OccurredAt);
```

- [ ] **Step 2: Add checkpoint to EventStoreDbContext**

Add to `EventStoreDbContext`:
```csharp
using Vellum.Kernel.Projections;

// Add DbSet:
public DbSet<CheckpointEntity> Checkpoints => Set<CheckpointEntity>();

// Add to OnModelCreating:
modelBuilder.Entity<CheckpointEntity>(b =>
{
    b.ToTable("checkpoints");
    b.HasKey(c => c.ProjectionName);
    b.Property(c => c.UpdatedAt).HasDefaultValueSql("now()");
});
```

- [ ] **Step 3: Generate migration**

```bash
dotnet ef migrations add AddCheckpoints --project src/Vellum --context EventStoreDbContext --output-dir Kernel/EventStore/Migrations
```

- [ ] **Step 4: Write failing AsyncProjectionHost tests**

`tests/Vellum.Tests/Kernel/Projections/AsyncProjectionHostTests.cs`:
```csharp
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Vellum.Kernel.CommandHandling;
using Vellum.Kernel.EventStore;
using Vellum.Kernel.EventTypes;
using Vellum.Kernel.Projections;

namespace Vellum.Tests.Kernel.Projections;

[Collection("Integration")]
public class AsyncProjectionHostTests
{
    private readonly IntegrationFixture _fixture;

    public AsyncProjectionHostTests(IntegrationFixture fixture) => _fixture = fixture;

    private EventStoreDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<EventStoreDbContext>()
            .UseNpgsql(_fixture.ConnectionString)
            .UseSnakeCaseNamingConvention()
            .Options;
        return new EventStoreDbContext(options);
    }

    private async Task AppendTestEvent(Guid streamId, string eventType, int expectedVersion)
    {
        await using var db = CreateDb();
        var store = new Vellum.Kernel.EventStore.EventStore(db, new EventCollector());
        await store.AppendAsync(
            streamId, "test", expectedVersion,
            JsonSerializer.SerializeToDocument(new { v = expectedVersion + 1 }),
            [new NewEvent(eventType, JsonSerializer.SerializeToDocument(new { }), JsonSerializer.SerializeToDocument(new { }))]);
    }

    [Fact]
    public async Task Processes_events_in_order()
    {
        var streamId = Guid.NewGuid();
        await AppendTestEvent(streamId, "test.a.v1", 0);
        await AppendTestEvent(streamId, "test.b.v1", 1);

        var projection = new RecordingProjection("order-test");
        var registry = new EventTypeRegistry();
        registry.Register<TestEventA>("test.a.v1");
        registry.Register<TestEventB>("test.b.v1");

        var host = new AsyncProjectionHost(
            new TestScopeFactory(() => CreateDb()),
            [projection],
            registry,
            NullLogger<AsyncProjectionHost>.Instance);

        await host.ProcessBatchAsync(CancellationToken.None);

        Assert.Equal(2, projection.Received.Count);
        Assert.True(projection.Received[0].GlobalPosition < projection.Received[1].GlobalPosition);
    }

    [Fact]
    public async Task Checkpoint_advances_after_processing()
    {
        var streamId = Guid.NewGuid();
        await AppendTestEvent(streamId, "test.a.v1", 0);

        var projection = new RecordingProjection("checkpoint-test");
        var registry = new EventTypeRegistry();
        registry.Register<TestEventA>("test.a.v1");

        var host = new AsyncProjectionHost(
            new TestScopeFactory(() => CreateDb()),
            [projection],
            registry,
            NullLogger<AsyncProjectionHost>.Instance);

        await host.ProcessBatchAsync(CancellationToken.None);

        await using var db = CreateDb();
        var checkpoint = await db.Checkpoints.FindAsync("checkpoint-test");
        Assert.NotNull(checkpoint);
        Assert.True(checkpoint!.LastProcessedPosition > 0);
    }

    [Fact]
    public async Task Skips_already_processed_events()
    {
        var streamId = Guid.NewGuid();
        await AppendTestEvent(streamId, "test.a.v1", 0);

        var projection = new RecordingProjection("skip-test");
        var registry = new EventTypeRegistry();
        registry.Register<TestEventA>("test.a.v1");

        var host = new AsyncProjectionHost(
            new TestScopeFactory(() => CreateDb()),
            [projection],
            registry,
            NullLogger<AsyncProjectionHost>.Instance);

        // Process once
        await host.ProcessBatchAsync(CancellationToken.None);
        Assert.Single(projection.Received);

        // Process again — should not re-process
        await host.ProcessBatchAsync(CancellationToken.None);
        Assert.Single(projection.Received);
    }

    private sealed record TestEventA;
    private sealed record TestEventB;

    private sealed class RecordingProjection : IAsyncProjection
    {
        public string Name { get; }
        public List<PersistedEvent> Received { get; } = [];

        public RecordingProjection(string name) => Name = name;

        public Task ProjectAsync(PersistedEvent @event, CancellationToken ct = default)
        {
            Received.Add(@event);
            return Task.CompletedTask;
        }
    }

    private sealed class TestScopeFactory : IServiceScopeFactory
    {
        private readonly Func<EventStoreDbContext> _dbFactory;
        public TestScopeFactory(Func<EventStoreDbContext> dbFactory) => _dbFactory = dbFactory;

        public IServiceScope CreateScope() => new TestScope(_dbFactory);

        private sealed class TestScope : IServiceScope
        {
            private readonly EventStoreDbContext _db;
            public IServiceProvider ServiceProvider { get; }

            public TestScope(Func<EventStoreDbContext> dbFactory)
            {
                _db = dbFactory();
                ServiceProvider = new TestServiceProvider(_db);
            }

            public void Dispose() => _db.Dispose();
        }

        private sealed class TestServiceProvider : IServiceProvider
        {
            private readonly EventStoreDbContext _db;
            public TestServiceProvider(EventStoreDbContext db) => _db = db;
            public object? GetService(Type serviceType) =>
                serviceType == typeof(EventStoreDbContext) ? _db : null;
        }
    }
}
```

- [ ] **Step 5: Run tests to verify they fail**

```bash
dotnet test tests/Vellum.Tests --filter "AsyncProjectionHostTests"
```

Expected: compilation error — `AsyncProjectionHost` doesn't exist.

- [ ] **Step 6: Implement AsyncProjectionHost**

`src/Vellum/Kernel/Projections/AsyncProjectionHost.cs`:
```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Vellum.Kernel.EventStore;
using Vellum.Kernel.EventTypes;

namespace Vellum.Kernel.Projections;

public sealed class AsyncProjectionHost : BackgroundService
{
    private const int BatchSize = 200;
    private const int MaxRetriesPerEvent = 3;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IReadOnlyList<IAsyncProjection> _projections;
    private readonly IEventTypeRegistry _registry;
    private readonly ILogger<AsyncProjectionHost> _logger;

    public AsyncProjectionHost(
        IServiceScopeFactory scopeFactory,
        IEnumerable<IAsyncProjection> projections,
        IEventTypeRegistry registry,
        ILogger<AsyncProjectionHost> logger)
    {
        _scopeFactory = scopeFactory;
        _projections = projections.ToList();
        _registry = registry;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var processed = await ProcessBatchAsync(stoppingToken);
            if (processed == 0)
                await Task.Delay(PollInterval, stoppingToken);
        }
    }

    public async Task<int> ProcessBatchAsync(CancellationToken ct)
    {
        var totalProcessed = 0;

        foreach (var projection in _projections)
        {
            totalProcessed += await ProcessProjectionAsync(projection, ct);
        }

        return totalProcessed;
    }

    private async Task<int> ProcessProjectionAsync(IAsyncProjection projection, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EventStoreDbContext>();

        var checkpoint = await db.Checkpoints.FindAsync([projection.Name], ct);
        var lastPosition = checkpoint?.LastProcessedPosition ?? 0;

        // Gap-safe query: only read events from committed transactions
        var events = await db.Events
            .FromSqlInterpolated(
                $"""
                SELECT stream_id, version, global_position, event_type, payload, metadata, occurred_at
                FROM es.events
                WHERE global_position > {lastPosition}
                  AND xid < pg_snapshot_xmin(pg_current_snapshot())
                ORDER BY global_position
                LIMIT {BatchSize}
                """)
            .AsNoTracking()
            .ToListAsync(ct);

        if (events.Count == 0)
            return 0;

        foreach (var eventEntity in events)
        {
            var data = _registry.DeserializeEvent(eventEntity.EventType, eventEntity.Payload);
            var persisted = new PersistedEvent(
                eventEntity.StreamId,
                eventEntity.Version,
                eventEntity.GlobalPosition,
                eventEntity.EventType,
                data,
                eventEntity.OccurredAt);

            var retries = 0;
            while (true)
            {
                try
                {
                    await projection.ProjectAsync(persisted, ct);
                    break;
                }
                catch (Exception ex) when (++retries <= MaxRetriesPerEvent)
                {
                    _logger.LogWarning(ex,
                        "Projection {Name} failed on event at position {Position} (attempt {Attempt}/{Max})",
                        projection.Name, eventEntity.GlobalPosition, retries, MaxRetriesPerEvent);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "Projection {Name} skipping event at position {Position} after {Max} retries",
                        projection.Name, eventEntity.GlobalPosition, MaxRetriesPerEvent);
                    break;
                }
            }

            // Advance checkpoint even if the event was skipped (to not block the projection)
            if (checkpoint is null)
            {
                checkpoint = new CheckpointEntity
                {
                    ProjectionName = projection.Name,
                    LastProcessedPosition = eventEntity.GlobalPosition,
                };
                db.Checkpoints.Add(checkpoint);
            }
            else
            {
                checkpoint.LastProcessedPosition = eventEntity.GlobalPosition;
                checkpoint.UpdatedAt = DateTimeOffset.UtcNow;
            }
        }

        await db.SaveChangesAsync(ct);
        return events.Count;
    }
}
```

- [ ] **Step 7: Register in Program.cs**

Add to `src/Vellum/Program.cs`:
```csharp
using Vellum.Kernel.Projections;

// ... existing registrations ...
builder.Services.AddHostedService<AsyncProjectionHost>();
```

- [ ] **Step 8: Run tests**

```bash
dotnet test tests/Vellum.Tests --filter "AsyncProjectionHostTests"
```

Expected: all 3 tests PASS.

- [ ] **Step 9: Run full test suite**

```bash
dotnet test tests/Vellum.Tests
```

Expected: ALL tests pass — smoke, event store, registry, upcasting, aggregate store, transaction behavior, outbox dispatcher, async projection host.

- [ ] **Step 10: Commit**

```bash
git add -A
git commit -m "feat(kernel): add async projection host with gap-safe event processing and checkpoints"
```

---

## Validation Gate

Phase 0 is complete when:
1. ✅ Event store passes aggregate tests (Task 2 + Task 3)
2. ✅ Snapshot load/save round-trips (Task 3: `AggregateStoreTests.Save_and_load_roundtrips_state`)
3. ✅ Projection host processes events without gaps (Task 7: `AsyncProjectionHostTests.Processes_events_in_order`)
4. ✅ Full `dotnet test` passes

Next: Phase 1 — Modelling (C4 elements, relationships, canvas, semantic zoom).
