namespace Vellum.Modules.Schemas.Entities;

public class SchemaEntity
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public string Name { get; set; } = null!;
    public string? Description { get; set; }
    public string Content { get; set; } = null!;
    public int Version { get; set; }
}
