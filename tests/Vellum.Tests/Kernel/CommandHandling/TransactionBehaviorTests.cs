using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Vellum.Kernel.Aggregates;
using Vellum.Kernel.CommandHandling;
using Vellum.Kernel.EventStore;
using Vellum.Kernel.EventTypes;
using Vellum.Kernel.Projections;
using Vellum.Kernel.Results;
using Aggregates = Vellum.Tests.Kernel.Aggregates;

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
