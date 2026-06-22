namespace Vellum.Modules.Views.Entities;

public class LayoutPositionEntity
{
    public Guid Id { get; set; }
    public Guid ViewId { get; set; }
    public Guid ElementId { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
}
