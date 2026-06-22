using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Vellum.Kernel.CommandHandling;
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
        return (db, new Vellum.Kernel.EventStore.EventStore(db, new EventCollector()));
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

        var streamIds = new[] { streamA, streamB };
        var streamEvents = await db.Events
            .Where(e => streamIds.Contains(e.StreamId))
            .OrderBy(e => e.GlobalPosition)
            .ToListAsync();
        var positions = streamEvents.Select(e => e.GlobalPosition).ToList();

        Assert.Equal(3, positions.Count);
        Assert.Equal(positions.OrderBy(p => p), positions);
        Assert.Equal(3, positions.Distinct().Count());
    }
}
