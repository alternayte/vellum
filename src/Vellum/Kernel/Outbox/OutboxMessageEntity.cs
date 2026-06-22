using System.Text.Json;

namespace Vellum.Kernel.Outbox;

public class OutboxMessageEntity
{
    public long Id { get; set; }
    public string EventType { get; set; } = null!;
    public JsonDocument Payload { get; set; } = null!;
    public DateTimeOffset CreatedAt { get; set; }
    public int RetryCount { get; set; }
    public DateTimeOffset? NextRetryAt { get; set; }
    public DateTimeOffset? ProcessedAt { get; set; }
}
