namespace Vellum.Modules.Modelling.Entities;

public class RelationshipEntity
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public Guid Branch { get; set; }
    public Guid FromId { get; set; }
    public Guid ToId { get; set; }
    public string? Label { get; set; }
    public string? Technology { get; set; }
    public Guid? MessageId { get; set; }
    public string? LineShape { get; set; }
}
