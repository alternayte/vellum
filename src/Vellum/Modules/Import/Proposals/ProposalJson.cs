using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Vellum.Modules.Import.Extraction;

namespace Vellum.Modules.Import.Proposals;

/// <summary>The JSON columns of the import tables: one set of options, so what is written reads back.</summary>
internal static class ProposalJson
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static string Write<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static List<EvidenceRef> ReadEvidence(string json) =>
        JsonSerializer.Deserialize<List<EvidenceRef>>(json, Options) ?? [];

    public static List<FieldChange> ReadChanges(string json) =>
        JsonSerializer.Deserialize<List<FieldChange>>(json, Options) ?? [];

    public static Dictionary<string, string> ReadAttributes(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(json, Options) ?? [];

    public static List<OtherSourceFact> ReadOtherSources(string json) =>
        JsonSerializer.Deserialize<List<OtherSourceFact>>(json, Options) ?? [];

    public static List<string> ReadKeys(string json) =>
        JsonSerializer.Deserialize<List<string>>(json, Options) ?? [];

    /// <summary>
    /// An entity's values by field name. A list of IDs is a JSON array in the column and the
    /// one canonical text of <see cref="ModelIndex.JoinIds"/> in memory, so two sets of the
    /// same IDs compare equal.
    /// </summary>
    public static Dictionary<string, string?> ReadValues(string json)
    {
        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (JsonNode.Parse(json) is not JsonObject fields)
            return values;
        foreach (var (field, node) in fields)
        {
            values[field] = node switch
            {
                null => null,
                JsonArray ids => ModelIndex.JoinIds(ids.Select(id => Guid.Parse(id!.GetValue<string>()))),
                _ => node.GetValue<string>(),
            };
        }
        return values;
    }

    public static string WriteValues(IReadOnlyDictionary<string, string?> values)
    {
        var fields = new JsonObject();
        foreach (var (field, value) in values.OrderBy(v => v.Key, StringComparer.Ordinal))
        {
            fields[field] = field == Field.ConsumerIds
                ? new JsonArray([.. ModelIndex.SplitIds(value).Select(id => (JsonNode)id.ToString())])
                : value;
        }
        return fields.ToJsonString(Options);
    }
}

/// <summary>A rule of an extractor and the version of it that made a snapshot.</summary>
public sealed record RuleVersion(string Rule, int Version);

/// <summary>The extraction rules as one value: a fact key means the same thing only under the same set of rules.</summary>
public static class RuleSet
{
    /// <summary>The rules this build extracts with.</summary>
    public static IReadOnlyList<RuleVersion> Current { get; } =
        [.. EvidenceExtractor.Rules.Select(r => new RuleVersion(r.Id, r.Version))];

    public static string CurrentFingerprint { get; } = Fingerprint(Current);

    /// <summary>
    /// SHA-256 over every rule ID with its version, in the order of the IDs. Another version
    /// of one rule, a new rule or a removed rule gives another fingerprint.
    /// </summary>
    public static string Fingerprint(IEnumerable<RuleVersion> rules)
    {
        var text = new StringBuilder();
        foreach (var rule in rules.OrderBy(r => r.Rule, StringComparer.Ordinal).ThenBy(r => r.Version))
            text.Append(rule.Rule.Length).Append(':').Append(rule.Rule).Append('=').Append(rule.Version.ToString(CultureInfo.InvariantCulture)).Append('\n');
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }
}

/// <summary>The two ways a reader follows evidence: the pointer stored on a fact, and the page that shows the lines.</summary>
internal static class EvidenceLinks
{
    /// <summary><c>github.com/acme/orders@&lt;sha&gt;:src/Api/Program.cs:41-44</c></summary>
    /// <param name="repository">With the host, for example <c>github.com/acme/orders</c>.</param>
    public static string Pointer(string repository, string commitSha, EvidenceRef evidence) => string.Create(
        CultureInfo.InvariantCulture,
        $"{repository}@{evidence.CommitSha ?? commitSha}:{evidence.Path}:{evidence.StartLine}-{evidence.EndLine}");

    /// <summary>
    /// The permanent link to the lines at the commit. A commit SHA never moves, so the link
    /// shows the same lines for as long as the repository exists. Null for an agent-push
    /// Source: it has no web page, and the files stay on the machine that pushed them.
    /// </summary>
    public static string? Url(string repository, string commitSha, EvidenceRef evidence)
    {
        // An agent push and an uploaded model file have no web page.
        if (repository.StartsWith(Sources.AgentPush.Host + "/", StringComparison.Ordinal)
            || repository.StartsWith(Sources.ModelFileUpload.Host + "/", StringComparison.Ordinal))
            return null;
        // A runtime Source has no page to link to: its evidence is the derived line Vellum keeps.
        if (evidence.Rule.StartsWith("runtime.", StringComparison.Ordinal))
            return "";
        var path = string.Join('/', evidence.Path.Split('/').Select(Uri.EscapeDataString));
        var lines = evidence.EndLine > evidence.StartLine
            ? string.Create(CultureInfo.InvariantCulture, $"L{evidence.StartLine}-L{evidence.EndLine}")
            : string.Create(CultureInfo.InvariantCulture, $"L{evidence.StartLine}");
        return $"https://{repository}/blob/{evidence.CommitSha ?? commitSha}/{path}#{lines}";
    }
}
