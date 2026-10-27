using System.Text;
using System.Text.Json;

namespace Vellum.Modules.Views;

/// <summary>A derived line hidden in a view, by its two cards in one direction.</summary>
public sealed record HiddenDerivedLine(Guid FromId, Guid ToId);

/// <summary>
/// Which lines a view draws. It is layout data, never a model fact: the same rules apply on the
/// canvas (lib/line-visibility.ts), in the exports and on a share link.
/// <list type="bullet">
/// <item>A relationship hidden one at a time, or of a kind that is turned off, is not drawn in
/// this view at all: neither as itself nor inside a derived line.</item>
/// <item>A cross-level line is a direct relationship whose two visible ends sit at different
/// nesting depths. With <see cref="CrossLevel"/> off, those are not drawn.</item>
/// <item>With <see cref="DerivedLines"/> off, no derived line is drawn. A derived line hidden one
/// at a time is keyed by its pair of cards: a line between other cards shows, and the line keeps
/// hidden while the set of relationships under it changes.</item>
/// </list>
/// </summary>
public sealed record LineVisibility(
    bool DerivedLines, bool Structural, bool Behavioural, bool CrossLevel,
    IReadOnlyList<Guid> HiddenRelationshipIds, IReadOnlyList<HiddenDerivedLine> HiddenDerivedLines)
{
    /// <summary>The most relationships or derived lines one view hides one at a time.</summary>
    public const int MaxHidden = 10_000;

    public static readonly LineVisibility AllShown = new(true, true, true, true, [], []);

    /// <summary>True when the view hides at least one line or kind of line.</summary>
    public bool HidesAnything =>
        !DerivedLines || !Structural || !Behavioural || !CrossLevel || HiddenRelationshipIds.Count > 0 || HiddenDerivedLines.Count > 0;

    /// <summary>True when relationships of this kind are drawn.</summary>
    public bool Shows(Modelling.RelationshipKind kind) => kind == Modelling.RelationshipKind.Behavioural ? Behavioural : Structural;

    /// <summary>
    /// The visibility as text for a layout fingerprint: the four switches as 0 or 1, the hidden
    /// relationships in order of ID, then the hidden derived lines in order of from ID and to ID.
    /// </summary>
    public string Text()
    {
        var text = new StringBuilder();
        text.Append(DerivedLines ? '1' : '0').Append(Structural ? '1' : '0').Append(Behavioural ? '1' : '0').Append(CrossLevel ? '1' : '0').Append(';');
        foreach (var id in HiddenRelationshipIds.Distinct().Order()) text.Append(id).Append(',');
        text.Append(';');
        foreach (var line in HiddenDerivedLines.Distinct().OrderBy(l => l.FromId).ThenBy(l => l.ToId))
            text.Append(line.FromId).Append('>').Append(line.ToId).Append(',');
        return text.ToString();
    }

    /// <summary>The visibility a saved row holds; no row shows every line.</summary>
    public static LineVisibility Of(LineVisibilityEntity? row) => row is null
        ? AllShown
        : new LineVisibility(row.DerivedLines, row.Structural, row.Behavioural, row.CrossLevel,
            row.HiddenRelationshipIds, ReadPairs(row.HiddenDerivedLines));

    /// <summary>The pairs of a saved JSON array of <c>{ fromId, toId }</c>. An entry that is not one is skipped.</summary>
    public static IReadOnlyList<HiddenDerivedLine> ReadPairs(JsonDocument? document)
    {
        if (document is null || document.RootElement.ValueKind != JsonValueKind.Array) return [];
        var pairs = new List<HiddenDerivedLine>();
        foreach (var item in document.RootElement.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object
                && item.TryGetProperty("fromId", out var from) && from.TryGetGuid(out var fromId)
                && item.TryGetProperty("toId", out var to) && to.TryGetGuid(out var toId))
                pairs.Add(new HiddenDerivedLine(fromId, toId));
        }
        return pairs;
    }

    /// <summary>The pairs as the JSON array a row saves.</summary>
    public static JsonDocument WritePairs(IEnumerable<HiddenDerivedLine> pairs) =>
        JsonSerializer.SerializeToDocument(pairs.Select(p => new { fromId = p.FromId, toId = p.ToId }));
}
