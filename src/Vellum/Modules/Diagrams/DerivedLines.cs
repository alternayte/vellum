using System.Globalization;
using Vellum.Modules.Modelling;
using Vellum.Modules.Views;

namespace Vellum.Modules.Diagrams;

/// <summary>
/// One derived line: the line a view draws between two visible cards for the relationships whose
/// ends are hidden inside them. It is a projection of the view and is never stored.
/// </summary>
public sealed record DerivedLine(ElementId FromId, ElementId ToId, IReadOnlyList<RelationshipState> Relationships)
{
    /// <summary>The label of the one relationship, or "N relationships" for more.</summary>
    public string? Label => Relationships.Count == 1
        ? Relationships[0].Label
        : string.Create(CultureInfo.InvariantCulture, $"{Relationships.Count} relationships");
}

/// <summary>
/// The rules of derived lines, the same as <c>projectLines</c> in the web client
/// (lib/derived-lines.ts), so an export draws what the canvas draws:
/// <list type="bullet">
/// <item>A relationship whose two ends are visible is direct: it draws as itself.</item>
/// <item>Otherwise each end attaches to its nearest visible ancestor. An end with none is not in
/// the view, and the relationship draws nothing.</item>
/// <item>Two ends that attach to the same card draw nothing.</item>
/// <item>The relationships between the same two visible cards, in one direction, merge into one
/// derived line. Its relationships are in ordinal order of ID text, and the lines are in ordinal
/// order of the ID text of the source card, then of the target card.</item>
/// </list>
/// </summary>
public static class DerivedLines
{
    public static (IReadOnlyList<RelationshipState> Direct, IReadOnlyList<DerivedLine> Derived) Project(
        IReadOnlyDictionary<ElementId, ElementState> elements, IEnumerable<RelationshipState> relationships, IReadOnlySet<ElementId> visible)
    {
        var resolved = new Dictionary<ElementId, ElementId?>();
        ElementId? NearestVisible(ElementId id)
        {
            if (resolved.TryGetValue(id, out var known)) return known;
            ElementId? found = null;
            // The walk stops at a card it saw before, so a cycle of parents cannot hold it.
            var seen = new HashSet<ElementId>();
            for (ElementId? at = id; at is { } current && elements.TryGetValue(current, out var element) && seen.Add(current); at = element.ParentId)
            {
                if (visible.Contains(current))
                {
                    found = current;
                    break;
                }
            }
            resolved[id] = found;
            return found;
        }

        var direct = new List<RelationshipState>();
        var groups = new Dictionary<(ElementId From, ElementId To), List<RelationshipState>>();
        foreach (var r in relationships)
        {
            if (visible.Contains(r.FromId) && visible.Contains(r.ToId))
            {
                direct.Add(r);
                continue;
            }
            if (NearestVisible(r.FromId) is not { } from || NearestVisible(r.ToId) is not { } to || from == to) continue;
            if (!groups.TryGetValue((from, to), out var group)) groups[(from, to)] = group = [];
            group.Add(r);
        }

        var derived = groups
            .OrderBy(g => g.Key.From.Value.ToString(), StringComparer.Ordinal)
            .ThenBy(g => g.Key.To.Value.ToString(), StringComparer.Ordinal)
            .Select(g => new DerivedLine(g.Key.From, g.Key.To,
                [.. g.Value.OrderBy(r => r.Id.Value.ToString(), StringComparer.Ordinal)]))
            .ToList();
        return (direct, derived);
    }

    /// <summary>
    /// The lines a view draws under its <see cref="LineVisibility"/>, the same as
    /// <c>applyLineVisibility</c> in the web client (lib/line-visibility.ts): a relationship hidden
    /// by ID or by kind is left out before the lines are projected, then a cross-level direct line
    /// and a hidden derived line are left out.
    /// </summary>
    public static (IReadOnlyList<RelationshipState> Direct, IReadOnlyList<DerivedLine> Derived) Project(
        IReadOnlyDictionary<ElementId, ElementState> elements, IEnumerable<RelationshipState> relationships, IReadOnlySet<ElementId> visible,
        LineVisibility visibility)
    {
        if (!visibility.HidesAnything) return Project(elements, relationships, visible);
        var hiddenIds = visibility.HiddenRelationshipIds.ToHashSet();
        var (direct, derived) = Project(elements,
            relationships.Where(r => !hiddenIds.Contains(r.Id.Value) && visibility.Shows(r.Kind)), visible);
        var hiddenPairs = visibility.HiddenDerivedLines.Select(l => (l.FromId, l.ToId)).ToHashSet();
        return (
            visibility.CrossLevel ? direct : [.. direct.Where(r => Depth(elements, r.FromId) == Depth(elements, r.ToId))],
            visibility.DerivedLines ? [.. derived.Where(l => !hiddenPairs.Contains((l.FromId.Value, l.ToId.Value)))] : []);
    }

    /// <summary>How many ancestors an element has in the model. A cycle of parents stops the count.</summary>
    public static int Depth(IReadOnlyDictionary<ElementId, ElementState> elements, ElementId id)
    {
        var depth = 0;
        var seen = new HashSet<ElementId> { id };
        for (var at = elements.GetValueOrDefault(id)?.ParentId; at is { } parent && elements.ContainsKey(parent) && seen.Add(parent); at = elements[parent].ParentId)
            depth++;
        return depth;
    }
}
