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

    /// <summary>
    /// Returns the current max global_position so each test can seed its checkpoint
    /// to start processing only from the events it appends.
    /// </summary>
    private async Task<long> GetCurrentMaxPositionAsync()
    {
        await using var db = CreateDb();
        return await db.Events.AnyAsync()
            ? await db.Events.MaxAsync(e => e.GlobalPosition)
            : 0L;
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

    private async Task SeedCheckpointAsync(string projectionName, long position)
    {
        await using var db = CreateDb();
        db.Checkpoints.Add(new CheckpointEntity
        {
            ProjectionName = projectionName,
            LastProcessedPosition = position,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Processes_events_in_order()
    {
        var startPosition = await GetCurrentMaxPositionAsync();
        var streamId = Guid.NewGuid();
        await AppendTestEvent(streamId, "test.a.v1", 0);
        await AppendTestEvent(streamId, "test.b.v1", 1);

        var projectionName = $"order-test-{Guid.NewGuid():N}";
        await SeedCheckpointAsync(projectionName, startPosition);

        var projection = new RecordingProjection(projectionName);
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
        var startPosition = await GetCurrentMaxPositionAsync();
        var streamId = Guid.NewGuid();
        await AppendTestEvent(streamId, "test.a.v1", 0);

        var projectionName = $"checkpoint-test-{Guid.NewGuid():N}";
        await SeedCheckpointAsync(projectionName, startPosition);

        var projection = new RecordingProjection(projectionName);
        var registry = new EventTypeRegistry();
        registry.Register<TestEventA>("test.a.v1");

        var host = new AsyncProjectionHost(
            new TestScopeFactory(() => CreateDb()),
            [projection],
            registry,
            NullLogger<AsyncProjectionHost>.Instance);

        await host.ProcessBatchAsync(CancellationToken.None);

        await using var db = CreateDb();
        var checkpoint = await db.Checkpoints.FindAsync(projectionName);
        Assert.NotNull(checkpoint);
        Assert.True(checkpoint!.LastProcessedPosition > startPosition);
    }

    [Fact]
    public async Task Skips_already_processed_events()
    {
        var startPosition = await GetCurrentMaxPositionAsync();
        var streamId = Guid.NewGuid();
        await AppendTestEvent(streamId, "test.a.v1", 0);

        var projectionName = $"skip-test-{Guid.NewGuid():N}";
        await SeedCheckpointAsync(projectionName, startPosition);

        var projection = new RecordingProjection(projectionName);
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
