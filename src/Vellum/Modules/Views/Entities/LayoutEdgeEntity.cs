using System.Text.Json;

namespace Vellum.Modules.Views.Entities;

public class LayoutEdgeEntity
{
    public Guid Id { get; set; }
    public Guid ViewId { get; set; }
    public Guid RelationshipId { get; set; }
    public JsonDocument? RoutePoints { get; set; }
}
