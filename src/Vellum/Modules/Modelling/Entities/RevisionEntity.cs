namespace Vellum.Modules.Modelling.Entities;

/// <summary>
/// A named, immutable snapshot of a model stream at a pinned version. The state is stored
/// with the revision so downstream features (diff, impact, agent briefs) can compare
/// pinned revisions without replaying the event log.
/// </summary>
public class RevisionEntity
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public string Name { get; set; } = null!;
    public string? Note { get; set; }
    public Guid StreamId { get; set; }
    public int Version { get; set; }
    public string State { get; set; } = null!;
    public string CreatedBy { get; set; } = null!;
    public DateTimeOffset CreatedAt { get; set; }
}
