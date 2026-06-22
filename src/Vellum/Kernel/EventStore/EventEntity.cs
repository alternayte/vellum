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
