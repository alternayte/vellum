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
