using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Vellum.Modules.Import.Extraction;
using Vellum.Modules.Import.Proposals;
using Vellum.Shared;

namespace Vellum.Modules.Import.Runtime;

/// <summary>A service that a runtime Source shows. <paramref name="Key"/> is its fact key and the external key of its source binding.</summary>
internal sealed record RuntimeService(string Key, string Name, string TruthLabel, IReadOnlyDictionary<string, string> Attributes);

/// <summary>
/// One runtime edge between two services of the graph: calls, or messages from a producer to a
/// consumer. <paramref name="Label"/> is the relationship label it proposes.
/// </summary>
internal sealed record RuntimeEdge(
    string Key, string FromKey, string ToKey, string Label, string TruthLabel, IReadOnlyDictionary<string, string> Attributes);

/// <summary>
/// What a runtime Source showed once: services and the edges between them, nothing else. It is
/// derived, so it holds no span, no attribute value outside the allow-list and no metric.
/// <para>
/// It is stored as one blob, the Snapshot's only file: a JSON object per line. The first line
/// is the header (what was read, the window, the counts); then the services and then the
/// edges, each ordered by key. A citation names the line of its fact, so the source view
/// shows exactly what was kept. The Snapshot ID is the SHA-256 of the lines after the header:
/// the sorted set of services and edges.
/// </para>
/// </summary>
internal sealed partial record RuntimeGraph(
    IReadOnlyDictionary<string, string> Header, IReadOnlyList<RuntimeService> Services, IReadOnlyList<RuntimeEdge> Edges, bool IsComplete)
{
    public const string Format = "vellum.runtime.v1";
    /// <summary>The longest name (of a service, an operation or a peer) the graph keeps.</summary>
    public const int MaxName = 200;

    // The document is data shown as text; "->" in a key stays readable.
    private static readonly JsonSerializerOptions Compact = new() { WriteIndented = false, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>The stored document: the header line, then one line per service and per edge, in key order.</summary>
    public byte[] Write()
    {
        var text = new StringBuilder();
        var header = new JsonObject { ["format"] = Format, ["complete"] = IsComplete };
        foreach (var (name, value) in Header.OrderBy(h => h.Key, StringComparer.Ordinal))
            header[name] = value;
        text.Append(header.ToJsonString(Compact)).Append('\n');
        foreach (var line in Lines())
            text.Append(line).Append('\n');
        return Encoding.UTF8.GetBytes(text.ToString());
    }

    /// <summary>The SHA-256 of the sorted service and edge lines: the Snapshot ID of a runtime Source.</summary>
    public string SnapshotId()
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var line in Lines())
            sha.AppendData(Encoding.UTF8.GetBytes(line + "\n"));
        return Convert.ToHexStringLower(sha.GetHashAndReset());
    }

    private IEnumerable<string> Lines()
    {
        foreach (var service in Services.OrderBy(s => s.Key, StringComparer.Ordinal))
        {
            yield return new JsonObject
            {
                ["type"] = "service", ["key"] = service.Key, ["name"] = service.Name, ["truthLabel"] = service.TruthLabel,
                ["attributes"] = Sorted(service.Attributes),
            }.ToJsonString(Compact);
        }
        foreach (var edge in Edges.OrderBy(e => e.Key, StringComparer.Ordinal))
        {
            yield return new JsonObject
            {
                ["type"] = "interaction", ["key"] = edge.Key, ["from"] = edge.FromKey, ["to"] = edge.ToKey,
                ["label"] = edge.Label, ["truthLabel"] = edge.TruthLabel, ["attributes"] = Sorted(edge.Attributes),
            }.ToJsonString(Compact);
        }
    }

    private static JsonObject Sorted(IReadOnlyDictionary<string, string> values)
    {
        var json = new JsonObject();
        foreach (var (name, value) in values.OrderBy(v => v.Key, StringComparer.Ordinal))
            json[name] = value;
        return json;
    }

    /// <summary>
    /// The facts of a stored document, each cited at its own line. The header's sample values
    /// (the window, the span count, the file) ride on every fact as <c>sample.*</c>
    /// attributes, so the provenance of a fact names them. Null when the bytes are not such a
    /// document.
    /// </summary>
    public static RuntimeFacts? Read(byte[] document, string path, string rule)
    {
        var lines = Encoding.UTF8.GetString(document).Split('\n');
        if (lines.Length < 1 || ParseLine(lines[0]) is not { } header || header["format"]?.GetValue<string>() != Format)
            return null;

        var sample = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, value) in header)
        {
            if (name is not ("format" or "complete") && value is JsonValue text && text.TryGetValue<string>(out var s))
                sample[FactMapping.SamplePrefix + name] = s;
        }

        var facts = new List<ExtractedFact>();
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 1; i < lines.Length; i++)
        {
            if (lines[i].Length == 0 || ParseLine(lines[i]) is not { } line)
                continue;
            var attributes = new Dictionary<string, string>(sample, StringComparer.Ordinal);
            foreach (var (name, value) in line["attributes"] as JsonObject ?? [])
            {
                if (value is JsonValue v && v.TryGetValue<string>(out var s))
                    attributes[name] = s;
            }
            attributes[FactMapping.TruthLabelAttribute] = line["truthLabel"]?.GetValue<string>() ?? "measured";
            var key = line["key"]?.GetValue<string>();
            if (key is null)
                continue;
            var evidence = new SourceEvidence(path, i + 1, i + 1, rule, 1);
            switch (line["type"]?.GetValue<string>())
            {
                case "service":
                    var name = line["name"]?.GetValue<string>() ?? key;
                    names[key] = name;
                    facts.Add(new ExtractedFact(FactKind.Service, key, name, attributes, [evidence]));
                    break;
                case "interaction":
                    var from = line["from"]?.GetValue<string>();
                    var to = line["to"]?.GetValue<string>();
                    if (from is null || to is null)
                        continue;
                    attributes["from"] = from;
                    attributes["to"] = to;
                    attributes["label"] = line["label"]?.GetValue<string>() ?? "calls";
                    facts.Add(new ExtractedFact(FactKind.Interaction, key,
                        $"{names.GetValueOrDefault(from, from)} → {names.GetValueOrDefault(to, to)}", attributes, [evidence]));
                    break;
            }
        }
        return new RuntimeFacts(facts, header["complete"]?.GetValue<bool>() == true);
    }

    private static JsonObject? ParseLine(string line)
    {
        try
        {
            return JsonNode.Parse(line) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// A name that a runtime Source supplies (a service, an operation, a peer), fit to keep, or
    /// null. Text of a trace is untrusted: a name with a control character, an e-mail address
    /// or the shape of a credential is not kept, and a long one is cut.
    /// </summary>
    public static string? CleanName(string? value)
    {
        if (value is null)
            return null;
        var name = value.Trim();
        if (name.Length == 0 || TextRule.Line("name", name, int.MaxValue) is not null)
            return null;
        if (name.Length > MaxName)
            name = char.IsHighSurrogate(name[MaxName - 1]) ? name[..(MaxName - 1)] : name[..MaxName];
        if (Email().IsMatch(name) || CredentialGuard.LooksSecret(name))
            return null;
        return name;
    }

    public static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);

    [GeneratedRegex(@"[^\s@/]+@[^\s@/]+\.[A-Za-z]{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex Email();
}

/// <param name="IsComplete">False when the Source was read only in part: no absence counts then.</param>
internal sealed record RuntimeFacts(IReadOnlyList<ExtractedFact> Facts, bool IsComplete);
