using System.Globalization;
using System.Text.Json;

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

    public static Dictionary<string, string?> ReadValues(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, string?>>(json, Options) ?? [];
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
    /// shows the same lines for as long as the repository exists.
    /// </summary>
    public static string Url(string repository, string commitSha, EvidenceRef evidence)
    {
        var path = string.Join('/', evidence.Path.Split('/').Select(Uri.EscapeDataString));
        var lines = evidence.EndLine > evidence.StartLine
            ? string.Create(CultureInfo.InvariantCulture, $"L{evidence.StartLine}-L{evidence.EndLine}")
            : string.Create(CultureInfo.InvariantCulture, $"L{evidence.StartLine}");
        return $"https://{repository}/blob/{evidence.CommitSha ?? commitSha}/{path}#{lines}";
    }
}
