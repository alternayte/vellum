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
