namespace Vellum.Modules.Modelling.Entities;

public class ElementEntity
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public Guid Branch { get; set; }
    public string Kind { get; set; } = null!;
    public string Name { get; set; } = null!;
    public string? Description { get; set; }
    public string? Technology { get; set; }
    public Guid? OwnerId { get; set; }
    public string Status { get; set; } = "current";
    public Guid? ParentId { get; set; }
    public string[] Tags { get; set; } = [];
    public string? Icon { get; set; }
}
