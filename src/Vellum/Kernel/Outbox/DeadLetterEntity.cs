using System.Text.Json;

namespace Vellum.Kernel.Outbox;

public class DeadLetterEntity
{
    public long Id { get; set; }
    public string EventType { get; set; } = null!;
    public JsonDocument Payload { get; set; } = null!;
    public string Error { get; set; } = null!;
    public DateTimeOffset FailedAt { get; set; }
}
