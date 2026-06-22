namespace Vellum.Modules.Views.Entities;

public class ViewEntity
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public string Name { get; set; } = null!;
    public Guid? RootElementId { get; set; }
    public Guid[] VisibleElementIds { get; set; } = [];
    public string? ActiveLens { get; set; }
    public Guid? ActiveFlowId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
