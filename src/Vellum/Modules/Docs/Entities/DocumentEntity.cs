// src/Vellum/Modules/Docs/Entities/DocumentEntity.cs
namespace Vellum.Modules.Docs.Entities;

public class DocumentEntity
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public Guid? SpaceId { get; set; }
    public Guid? ElementId { get; set; }
    public string Title { get; set; } = null!;
    public string Content { get; set; } = string.Empty;
    public string CreatedBy { get; set; } = null!;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
