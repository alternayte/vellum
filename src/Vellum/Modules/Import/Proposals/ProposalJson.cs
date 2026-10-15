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
    /// The permanent link to the lines at the commit, in the form of the Source's git host. A
    /// commit SHA never moves, so the link shows the same lines for as long as the repository
    /// exists. Null for an agent-push, model-file, directory or archive Source: none of them has
    /// a web page. Empty for runtime evidence, whose line Vellum keeps itself.
    /// <list type="bullet">
    /// <item>GitHub: <c>https://github.com/acme/orders/blob/&lt;sha&gt;/src/a.cs#L4-L9</c></item>
    /// <item>GitLab: <c>https://gitlab.com/acme/orders/-/blob/&lt;sha&gt;/src/a.cs#L4-9</c></item>
    /// <item>Bitbucket Cloud: <c>https://bitbucket.org/acme/orders/src/&lt;sha&gt;/src/a.cs#lines-4:9</c></item>
    /// <item>Azure DevOps: <c>https://dev.azure.com/org/project/_git/repo?path=/src/a.cs&amp;version=GC&lt;sha&gt;&amp;line=4&amp;lineEnd=10&amp;lineStartColumn=1&amp;lineEndColumn=1&amp;lineStyle=plain</c>
    /// (the selection ends at the start of the line after the last one)</item>
    /// </list>
    /// </summary>
    /// <param name="repository">With the host, for example <c>github.com/acme/orders</c>.</param>
    public static string? Url(SourceKind kind, string repository, string commitSha, EvidenceRef evidence)
    {
        // A runtime Source has no page to link to: its evidence is the derived line Vellum keeps.
        if (evidence.Rule.StartsWith("runtime.", StringComparison.Ordinal))
            return "";
        var sha = evidence.CommitSha ?? commitSha;
        var (start, end) = (evidence.StartLine, Math.Max(evidence.StartLine, evidence.EndLine));
        var path = string.Join('/', evidence.Path.Split('/').Select(Uri.EscapeDataString));
        var page = "https://" + string.Join('/', repository.Split('/').Select(Uri.EscapeDataString));
        return kind switch
        {
            SourceKind.Github => end > start
                ? string.Create(CultureInfo.InvariantCulture, $"{page}/blob/{sha}/{path}#L{start}-L{end}")
                : string.Create(CultureInfo.InvariantCulture, $"{page}/blob/{sha}/{path}#L{start}"),
            SourceKind.Gitlab => end > start
                ? string.Create(CultureInfo.InvariantCulture, $"{page}/-/blob/{sha}/{path}#L{start}-{end}")
                : string.Create(CultureInfo.InvariantCulture, $"{page}/-/blob/{sha}/{path}#L{start}"),
            SourceKind.Bitbucket => end > start
                ? string.Create(CultureInfo.InvariantCulture, $"{page}/src/{sha}/{path}#lines-{start}:{end}")
                : string.Create(CultureInfo.InvariantCulture, $"{page}/src/{sha}/{path}#lines-{start}"),
            SourceKind.AzureDevops => AzureUrl(repository, sha, evidence.Path, start, end),
            _ => null,
        };
    }

    /// <summary><c>dev.azure.com/org/project/repo</c> as the page of the repository, <c>…/org/project/_git/repo</c>.</summary>
    private static string? AzureUrl(string repository, string sha, string filePath, int start, int end)
    {
        var parts = repository.Split('/');
        if (parts.Length < 4)
            return null;
        var host = string.Join('/', parts[..^3]);
        var (organization, project, name) = (parts[^3], parts[^2], parts[^1]);
        return string.Create(CultureInfo.InvariantCulture,
            $"https://{host}/{Uri.EscapeDataString(organization)}/{Uri.EscapeDataString(project)}/_git/{Uri.EscapeDataString(name)}" +
            $"?path={Uri.EscapeDataString("/" + filePath)}&version=GC{sha}&line={start}&lineEnd={end + 1}&lineStartColumn=1&lineEndColumn=1&lineStyle=plain");
    }
}
