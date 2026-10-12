namespace Vellum.Modules.Modelling.Entities;

/// <summary>
/// A forked model stream used to compare architecture alternatives. Forking copies the
/// pinned state into a new event stream and materialises complete read-model rows for the
/// variant branch.
/// </summary>
public class VariantEntity
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public string Name { get; set; } = null!;
    public Guid StreamId { get; set; }
    public Guid BaseStreamId { get; set; }
    public Guid? BaseRevisionId { get; set; }
    public int ForkVersion { get; set; }
    public string Status { get; set; } = "open";
    public string CreatedBy { get; set; } = null!;
    public DateTimeOffset CreatedAt { get; set; }
}
