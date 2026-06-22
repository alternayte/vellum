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
