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
