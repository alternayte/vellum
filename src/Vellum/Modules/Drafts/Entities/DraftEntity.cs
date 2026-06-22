namespace Vellum.Modules.Drafts.Entities;

public class DraftEntity
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public string Name { get; set; } = null!;
    public Guid StreamId { get; set; }
    public Guid BaseStreamId { get; set; }
    public int ForkVersion { get; set; }
    public string Status { get; set; } = "open";
    public string CreatedBy { get; set; } = null!;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? MergedAt { get; set; }
    public DateTimeOffset? AbandonedAt { get; set; }
    public string BaseSnapshot { get; set; } = null!;
}
