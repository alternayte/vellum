using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace Vellum.Modules.Views;

/// <summary>The tables of the module, in the Postgres schema <c>views</c>.</summary>
public static class ViewsTables
{
    public const string Schema = "views";

    public static void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ViewEntity>(b =>
        {
            b.ToTable("views", Schema);
            b.HasKey(v => v.Id);
            b.HasIndex(v => v.ProjectId);
            b.Property(v => v.CreatedAt).HasDefaultValueSql("now()");
            b.Property(v => v.UpdatedAt).HasDefaultValueSql("now()");
            b.Property(v => v.VisibleElementIds).HasColumnType("uuid[]");
        });

        modelBuilder.Entity<LayoutPositionEntity>(b =>
        {
            b.ToTable("layout_positions", Schema);
            b.HasKey(l => l.Id);
            b.HasIndex(l => new { l.ViewId, l.ElementId }).IsUnique();
            b.HasOne<ViewEntity>()
                .WithMany()
                .HasForeignKey(p => p.ViewId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<LayoutEdgeEntity>(b =>
        {
            b.ToTable("layout_edges", Schema);
            b.HasKey(l => l.Id);
            b.HasIndex(l => new { l.ViewId, l.RelationshipId }).IsUnique();
            b.Property(l => l.RoutePoints).HasColumnType("jsonb");
            b.HasOne<ViewEntity>()
                .WithMany()
                .HasForeignKey(e => e.ViewId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<LayoutLineEntity>(b =>
        {
            b.ToTable("layout_lines", Schema);
            b.HasKey(l => l.Id);
            b.HasIndex(l => new { l.ViewId, l.FromId, l.ToId }).IsUnique();
            b.Property(l => l.Route).HasColumnType("jsonb");
            b.HasOne<ViewEntity>()
                .WithMany()
                .HasForeignKey(l => l.ViewId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<LineVisibilityEntity>(b =>
        {
            b.ToTable("line_visibility", Schema);
            b.HasKey(l => l.ViewId);
            b.Property(l => l.HiddenRelationshipIds).HasColumnType("uuid[]");
            b.Property(l => l.HiddenDerivedLines).HasColumnType("jsonb");
            b.HasOne<ViewEntity>()
                .WithMany()
                .HasForeignKey(l => l.ViewId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}

/// <summary>
/// Which lines a view draws: layout data, never a model fact. A view without a row draws every
/// line. Hiding a line removes it from this view only; the model and the other views keep it.
/// </summary>
public class LineVisibilityEntity
{
    public Guid ViewId { get; set; }
    public bool DerivedLines { get; set; } = true;
    public bool Structural { get; set; } = true;
    public bool Behavioural { get; set; } = true;
    public bool CrossLevel { get; set; } = true;

    /// <summary>The relationships hidden one at a time in this view.</summary>
    public Guid[] HiddenRelationshipIds { get; set; } = [];

    /// <summary>The derived lines hidden one at a time, as a JSON array of <c>{ fromId, toId }</c>: a derived line has no ID of its own.</summary>
    public JsonDocument HiddenDerivedLines { get; set; } = null!;
}

/// <summary>
/// The route of a derived line in a view, from the auto layout: the line between two cards, in
/// one direction, for the relationships hidden inside them. The derived line itself is never
/// stored: it is drawn again from the model each time. Only the path the layout gave it is kept
/// here, as presentation, like the route of a relationship in <see cref="LayoutEdgeEntity"/>.
/// </summary>
public class LayoutLineEntity
{
    public Guid Id { get; set; }
    public Guid ViewId { get; set; }

    /// <summary>The card the line starts at: an element of the view.</summary>
    public Guid FromId { get; set; }

    /// <summary>The card the line ends at.</summary>
    public Guid ToId { get; set; }

    /// <summary>A <see cref="LayoutRoute"/> as JSON.</summary>
    public JsonDocument Route { get; set; } = null!;
}

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

public class LayoutPositionEntity
{
    public Guid Id { get; set; }
    public Guid ViewId { get; set; }
    public Guid ElementId { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double? Width { get; set; }
    public double? Height { get; set; }
}

public class LayoutEdgeEntity
{
    public Guid Id { get; set; }
    public Guid ViewId { get; set; }
    public Guid RelationshipId { get; set; }
    public JsonDocument? RoutePoints { get; set; }

    /// <summary>The side of the source card the relationship leaves from in this view: one of <see cref="EdgeSides.All"/>, or null for auto.</summary>
    public string? SourceSide { get; set; }

    /// <summary>The side of the target card the relationship enters in this view: one of <see cref="EdgeSides.All"/>, or null for auto.</summary>
    public string? TargetSide { get; set; }
}

/// <summary>
/// The sides of a card a relationship can attach to in a view. A relationship without a saved
/// side is auto: its end takes the side that faces the other card, as the cards move.
/// </summary>
public static class EdgeSides
{
    public const string Top = "top";
    public const string Right = "right";
    public const string Bottom = "bottom";
    public const string Left = "left";

    public static readonly IReadOnlyList<string> All = [Top, Right, Bottom, Left];

    /// <summary>True for null (auto) and for each word of <see cref="All"/>.</summary>
    public static bool IsValid(string? side) => side is null or Top or Right or Bottom or Left;
}
