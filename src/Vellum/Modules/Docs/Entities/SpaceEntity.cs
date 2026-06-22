// src/Vellum/Modules/Docs/Entities/SpaceEntity.cs
namespace Vellum.Modules.Docs.Entities;

public class SpaceEntity
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public string Name { get; set; } = null!;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
