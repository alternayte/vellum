using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Vellum.Modules.Import.Extraction;

/// <summary>
/// One fixed text form of a result: the same result always gives the same bytes. It is what
/// the determinism tests compare and what the committed expected-facts files hold.
/// </summary>
public static class ExtractionJson
{
    private static readonly JsonWriterOptions Options = new()
    {
        Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, NewLine = "\n",
    };

    public static string Serialize(ExtractionResult result)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, Options))
        {
            writer.WriteStartObject();
            writer.WriteString("repository", result.Repository);
            writer.WriteString("commitSha", result.CommitSha);
            writer.WriteBoolean("isComplete", result.IsComplete);
            writer.WriteStartArray("facts");
            foreach (var fact in result.Facts)
            {
                writer.WriteStartObject();
                writer.WriteString("kind", fact.Kind.ToString());
                writer.WriteString("key", fact.Key);
                writer.WriteString("name", fact.Name);
                writer.WriteStartObject("attributes");
                foreach (var (name, value) in fact.Attributes.OrderBy(a => a.Key, StringComparer.Ordinal))
                    writer.WriteString(name, value);
                writer.WriteEndObject();
                writer.WriteStartArray("evidence");
                foreach (var evidence in fact.Evidence)
                {
                    writer.WriteStartObject();
                    writer.WriteString("pointer", evidence.Pointer(result.Repository, result.CommitSha));
                    writer.WriteString("rule", evidence.Rule);
                    writer.WriteNumber("ruleVersion", evidence.RuleVersion);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteStartArray("notes");
            foreach (var note in result.Notes)
            {
                writer.WriteStartObject();
                writer.WriteString("kind", note.Kind.ToString());
                writer.WriteString("reason", note.Reason);
                writer.WriteString("path", note.Path);
                writer.WriteNumber("line", note.Line);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.GetBuffer(), 0, (int)stream.Length) + "\n";
    }
}

/// <param name="Attributes">The names of the attributes whose value differs, was added or was removed.</param>
public sealed record ChangedFact(FactKind Kind, string Key, IReadOnlyList<string> Attributes);

public sealed record FactDelta(
    IReadOnlyList<ExtractedFact> Added, IReadOnlyList<ExtractedFact> Removed, IReadOnlyList<ChangedFact> Changed);

/// <summary>
/// What differs between the facts of two commits. A fact is the same fact when kind and key
/// are equal, and it changed when its name or an attribute differs. Evidence is not
/// compared: lines move when a file is edited, and that alone changes nothing a fact says.
/// </summary>
public static class FactDiff
{
    public static FactDelta Compare(IReadOnlyList<ExtractedFact> baseFacts, IReadOnlyList<ExtractedFact> headFacts)
    {
        var before = Index(baseFacts);
        var after = Index(headFacts);
        var changed = new List<ChangedFact>();
        foreach (var (id, head) in after)
        {
            if (!before.TryGetValue(id, out var old))
                continue;
            var names = old.Attributes.Keys.Union(head.Attributes.Keys, StringComparer.Ordinal)
                .Where(name => !string.Equals(old.Attributes.GetValueOrDefault(name), head.Attributes.GetValueOrDefault(name), StringComparison.Ordinal))
                .ToList();
            if (!string.Equals(old.Name, head.Name, StringComparison.Ordinal))
                names.Add("name");
            if (names.Count > 0)
                changed.Add(new ChangedFact(head.Kind, head.Key, [.. names.OrderBy(n => n, StringComparer.Ordinal)]));
        }
        return new FactDelta(
            Ordered(after.Where(f => !before.ContainsKey(f.Key)).Select(f => f.Value)),
            Ordered(before.Where(f => !after.ContainsKey(f.Key)).Select(f => f.Value)),
            [.. changed.OrderBy(c => c.Kind.ToString(), StringComparer.Ordinal).ThenBy(c => c.Key, StringComparer.Ordinal)]);
    }

    private static Dictionary<(FactKind, string), ExtractedFact> Index(IReadOnlyList<ExtractedFact> facts)
    {
        var index = new Dictionary<(FactKind, string), ExtractedFact>();
        foreach (var fact in facts)
            index[(fact.Kind, fact.Key)] = fact;
        return index;
    }

    private static List<ExtractedFact> Ordered(IEnumerable<ExtractedFact> facts) =>
        [.. facts.OrderBy(f => f.Kind.ToString(), StringComparer.Ordinal).ThenBy(f => f.Key, StringComparer.Ordinal)];
}
