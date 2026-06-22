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
