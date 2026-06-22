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
