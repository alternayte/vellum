namespace Vellum.Modules.Views;

public sealed record ViewDto(
    Guid Id, Guid ProjectId, string Name, Guid? RootElementId,
    Guid[] VisibleElementIds, string? ActiveLens, Guid? ActiveFlowId,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public sealed record LayoutPositionDto(Guid ElementId, double X, double Y);
public sealed record LayoutEdgeDto(Guid RelationshipId, object? RoutePoints);

public sealed record ViewDetailDto(
    Guid Id, Guid ProjectId, string Name, Guid? RootElementId,
    Guid[] VisibleElementIds, string? ActiveLens, Guid? ActiveFlowId,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    IReadOnlyList<LayoutPositionDto> Positions,
    IReadOnlyList<LayoutEdgeDto> Edges);
