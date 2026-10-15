using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Vellum.Shared;

namespace Vellum.Modules.Settings;

public sealed record SettingsDocument(
    AiSettings Ai,
    GithubSettings Github,
    ImportSettings Import,
    CostSettings Cost,
    SimulationSettings Simulation,
    PrivacySettings Privacy,
    UsageSettings Usage,
    NotificationSettings Notifications,
    SharingSettings Sharing,
    GitHostSettings Gitlab,
    GitHostSettings Bitbucket,
    GitHostSettings AzureDevops);

public sealed record AiSettings(IReadOnlyList<AiProviderSettings> Providers, TaskRouting TaskRouting);

/// <summary>
/// One entry in the provider registry. A provider list replaces the default list as a whole,
/// so an entry states its identity and takes the rest from these defaults.
/// </summary>
public sealed record AiProviderSettings(
    Guid Id,
    string Kind,
    string Name,
    string Model,
    string BaseUrl = "",
    bool Enabled = true,
    bool SupportsStructuredOutput = true,
    bool SupportsStreaming = true,
    double? Temperature = null,
    int TimeoutSeconds = 60,
    decimal? MonthlyBudgetUsd = null,
    int? RequestsPerMinute = null);

/// <summary>Which provider serves each AI task. Null means the task has no provider.</summary>
public sealed record TaskRouting(Guid? InferArchitecture, Guid? ProposeFlow, Guid? Critique, Guid? Explain);

public sealed record GithubSettings(string ApiBaseUrl, string DefaultRef);
/// <param name="MaxArchiveBytes">
/// The most bytes the entries of one uploaded archive may expand to, all files together. An
/// archive over it is refused as a whole and nothing of it is stored.
/// </param>
public sealed record ImportSettings(
    int MaxFiles, long MaxFileBytes, long MaxTotalBytes, IReadOnlyList<string> ExcludeGlobs,
    long MaxArchiveBytes = ImportSettings.DefaultMaxArchiveBytes)
{
    public const long DefaultMaxArchiveBytes = 256L * 1024 * 1024;
    public const long MinArchiveBytes = 1024L * 1024;
    public const long MaxArchiveBytesLimit = 4L * 1024 * 1024 * 1024;
}

/// <summary>
/// The REST API of a git host other than GitHub: GitLab (gitlab.com or self-managed),
/// Bitbucket Cloud or Azure DevOps. Each workspace token is bound to the origin of this
/// address and goes nowhere else. Empty turns the host off.
/// </summary>
public sealed record GitHostSettings(string ApiBaseUrl);

/// <summary>The git hosts other than GitHub, with the addresses of their cloud services.</summary>
public static class GitHostDefaults
{
    public const string Gitlab = "https://gitlab.com/api/v4";
    public const string Bitbucket = "https://api.bitbucket.org/2.0";
    public const string AzureDevops = "https://dev.azure.com";
}
/// <param name="AzureRetailPricesBaseUrl">
/// The address of the Azure Retail Prices API, or of a mirror of it. Empty turns the price
/// search off. No credential is sent to it.
/// </param>
/// <param name="StalePriceAfterDays">An estimate flags a price that was entered or fetched more than this many days before its date.</param>
public sealed record CostSettings(
    string Provider, string Region, string Currency,
    string AzureRetailPricesBaseUrl = CostSettings.AzureRetailPrices, int StalePriceAfterDays = 90)
{
    public const string AzureRetailPrices = "https://prices.azure.com";
}
/// <summary>
/// What a new stress profile starts with. A profile keeps its own values after that; a change
/// here does not change a profile that exists.
/// </summary>
/// <param name="WarmUpSeconds">Flows that start before this instant are left out of the statistics. Less than <paramref name="DurationSeconds"/>.</param>
public sealed record SimulationSettings(int Seed, int DurationSeconds, double ArrivalRatePerSecond, int WarmUpSeconds = 5);
public sealed record PrivacySettings(bool SendPrivateCodeToAi);
public sealed record UsageSettings(decimal? MonthlyAiBudgetUsd, int MaxScansPerDay);
public sealed record NotificationSettings(string? WebhookUrl);

/// <summary>
/// Who outside the workspace may read it through a share link, and which sites may frame a
/// shared view. A change applies to the links that exist: a link that the policy no longer
/// allows answers 404 until the policy allows it again.
/// </summary>
/// <param name="LinkPolicy">
/// <c>off</c> (no link works), <c>password-only</c> (a link without a password answers 404) or
/// <c>public</c> (a link may have a password or none).
/// </param>
/// <param name="EmbedHosts">
/// The origins that may show <c>/embed/{token}</c> in a frame, such as
/// <c>https://wiki.example.com</c> or <c>https://*.atlassian.net</c>. Empty: no site may.
/// </param>
public sealed record SharingSettings(string LinkPolicy, IReadOnlyList<string> EmbedHosts)
{
    public const string Off = "off";
    public const string PasswordOnly = "password-only";
    public const string Public = "public";
    public static readonly IReadOnlyList<string> Policies = [Off, PasswordOnly, Public];
    public const int MaxEmbedHosts = 20;
}

/// <summary>
/// The form of an origin that may frame an embedded view. It goes into a response header, so
/// it is a scheme, a host name that may start with <c>*.</c> and an optional port: no path,
/// no quote, no space and no semicolon can pass.
/// </summary>
public static partial class EmbedOrigin
{
    [System.Text.RegularExpressions.GeneratedRegex(
        @"^(?<scheme>https?)://(\*\.)?[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?(?:\.[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?)*(?::[0-9]{1,5})?\z",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex Pattern();

    /// <param name="httpsOnly">True when only <c>https</c> passes.</param>
    public static bool IsValid(string? origin, bool httpsOnly) =>
        origin is { Length: > 0 and <= 300 }
        && Pattern().Match(origin) is { Success: true } match
        && (!httpsOnly || match.Groups["scheme"].Value == "https");
}

/// <summary>What the instance admin allows every workspace to choose.</summary>
/// <param name="MaxConcurrentStressRuns">
/// How many stress runs of all workspaces together one app process does at the same time. One
/// run holds a thread and memory until it ends, so this number bounds what stress runs take
/// from the instance. A body that leaves it out gets <see cref="DefaultMaxConcurrentStressRuns"/>.
/// </param>
public sealed record InstancePolicy(
    bool AllowSelfRegistration,
    bool AllowPrivateNetworkUrls,
    IReadOnlyList<string> AllowedAiProviderKinds,
    bool AllowSendingPrivateCodeToAi,
    int MaxConcurrentStressRuns = InstancePolicy.DefaultMaxConcurrentStressRuns)
{
    public const int DefaultMaxConcurrentStressRuns = 4;
    public const int MinConcurrentStressRuns = 1;
    public const int MaxConcurrentStressRunsLimit = 64;
}

public static class AiProviderKinds
{
    public static readonly IReadOnlyList<string> All = ["openai", "anthropic", "gemini", "openai-compatible"];

    /// <summary>
    /// The address of a provider that has an empty base URL, or null for a kind that has no
    /// address of its own. A stored API key is bound to the origin of this address.
    /// </summary>
    public static string? DefaultBaseUrl(string kind) => kind switch
    {
        "openai" => "https://api.openai.com/v1",
        "anthropic" => "https://api.anthropic.com",
        "gemini" => "https://generativelanguage.googleapis.com",
        _ => null,
    };
}

public static class SettingsDefaults
{
    /// <summary>The defaults of a new local instance, before the admin changes them.</summary>
    public static readonly SettingsDocument BuiltIn = new(
        new AiSettings([], new TaskRouting(null, null, null, null)),
        new GithubSettings("https://api.github.com", ""),
        new ImportSettings(2000, 262144, 20971520,
            ["**/node_modules/**", "**/vendor/**", "**/bin/**", "**/obj/**", "**/*.min.*"]),
        new CostSettings("", "", "USD"),
        new SimulationSettings(1, 300, 10),
        new PrivacySettings(false),
        new UsageSettings(null, 20),
        new NotificationSettings(null),
        new SharingSettings(SharingSettings.Public, []),
        new GitHostSettings(GitHostDefaults.Gitlab),
        new GitHostSettings(GitHostDefaults.Bitbucket),
        new GitHostSettings(GitHostDefaults.AzureDevops));

    /// <summary>
    /// The defaults of a new instance of <paramref name="profile"/>. A hosted instance is open
    /// to the internet, so a share link there needs a password until an Owner allows public links.
    /// </summary>
    public static SettingsDocument For(OperatingProfile profile) => profile == OperatingProfile.Hosted
        ? BuiltIn with { Sharing = new SharingSettings(SharingSettings.PasswordOnly, []) }
        : BuiltIn;

    /// <summary>
    /// A local instance trusts its own network and its one operator. A hosted instance
    /// starts closed to private addresses and to forwarding private code.
    /// </summary>
    public static InstancePolicy Policy(OperatingProfile profile) => profile == OperatingProfile.Hosted
        ? new InstancePolicy(AllowSelfRegistration: true, AllowPrivateNetworkUrls: false, AiProviderKinds.All, AllowSendingPrivateCodeToAi: false)
        : new InstancePolicy(AllowSelfRegistration: false, AllowPrivateNetworkUrls: true, AiProviderKinds.All, AllowSendingPrivateCodeToAi: true);
}

/// <summary>
/// Settings are stored as JSON documents. Overrides are a partial document; the effective
/// document is the defaults with the overrides laid over them.
/// </summary>
public static class SettingsJson
{
    /// <summary>Strict: an unknown field, a missing identity field, a null or a wrong type is an error.</summary>
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        NumberHandling = JsonNumberHandling.Strict,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
    };

    /// <summary>The largest request body of a settings endpoint, in bytes.</summary>
    public const int MaxBodyBytes = 64 * 1024;
    public const string NotADocument = "Must be a settings document in which no field repeats.";
    public const string HasControlCharacter = "Must not contain control characters.";

    /// <summary>
    /// The request body as a document, or null when it is not a JSON object or repeats a
    /// field. A repeated field has no single meaning, so it is refused. The size of the body
    /// is limited before it is parsed.
    /// </summary>
    public static JsonObject? ToDocument(JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object)
            return null;

        try
        {
            var document = JsonObject.Create(body)!;
            Visit(document);
            return document;
        }
        catch (ArgumentException)
        {
            return null;
        }

        // A JsonObject reads its fields on first use, and that is when a repeat is found.
        static void Visit(JsonNode? node)
        {
            if (node is JsonObject obj)
            {
                foreach (var (_, value) in obj)
                    Visit(value);
            }
            else if (node is JsonArray array)
            {
                foreach (var item in array)
                    Visit(item);
            }
        }
    }

    /// <summary>
    /// The path of the first field name or text value that holds a control character, or
    /// null. No setting needs one, and the database cannot store U+0000 in a document.
    /// </summary>
    public static string? FindControlCharacter(JsonNode? node, string path = "")
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (name, value) in obj)
                {
                    if (name.Any(char.IsControl))
                        return path;
                    if (FindControlCharacter(value, path.Length == 0 ? name : $"{path}.{name}") is { } found)
                        return found;
                }

                return null;
            case JsonArray array:
                for (var i = 0; i < array.Count; i++)
                {
                    if (FindControlCharacter(array[i], $"{path}[{i}]") is { } found)
                        return found;
                }

                return null;
            case JsonValue value when value.GetValueKind() == JsonValueKind.String:
                return value.GetValue<string>().Any(char.IsControl) ? path : null;
            default:
                return null;
        }
    }

    private static bool IsUrlField(string path) =>
        path.EndsWith("apiBaseUrl", StringComparison.Ordinal) || path.EndsWith("baseUrl", StringComparison.Ordinal)
        || path.EndsWith("webhookUrl", StringComparison.Ordinal) || path.EndsWith("azureRetailPricesBaseUrl", StringComparison.Ordinal);

    /// <summary>
    /// The paths of the values that differ between two documents, for the audit log. A path
    /// carries no value, with one exception: a changed URL field carries its new origin
    /// (scheme, host and port), which shows an auditor where a credential can now go.
    /// </summary>
    public static IReadOnlyList<string> ChangedPaths(JsonNode? before, JsonNode? after, string path = "")
    {
        var changed = new List<string>();
        Compare(before, after, path);
        return changed;

        void Compare(JsonNode? a, JsonNode? b, string at)
        {
            if (a is JsonObject || b is JsonObject)
            {
                var left = a as JsonObject;
                var right = b as JsonObject;
                foreach (var name in (left?.Select(p => p.Key) ?? []).Union(right?.Select(p => p.Key) ?? []).Order(StringComparer.Ordinal))
                    Compare(left?[name], right?[name], at.Length == 0 ? name : $"{at}.{name}");
            }
            else if (a is JsonArray || b is JsonArray)
            {
                var left = a as JsonArray;
                var right = b as JsonArray;
                for (var i = 0; i < Math.Max(left?.Count ?? 0, right?.Count ?? 0); i++)
                    Compare(i < (left?.Count ?? 0) ? left![i] : null, i < (right?.Count ?? 0) ? right![i] : null, $"{at}[{i}]");
            }
            else if (!JsonNode.DeepEquals(a, b))
            {
                var origin = IsUrlField(at) && b is JsonValue value && value.GetValueKind() == JsonValueKind.String
                    ? OutboundUrlPolicy.OriginOf(value.GetValue<string>())
                    : null;
                changed.Add(origin is null ? at : $"{at}={origin}");
            }
        }
    }

    public static JsonObject ToJson(SettingsDocument document) =>
        JsonSerializer.SerializeToNode(document, Options)!.AsObject();

    /// <summary>
    /// Objects merge field by field. A list, a value or an explicit null replaces what is
    /// under it, so a workspace can clear a nullable default.
    /// </summary>
    public static JsonObject Merge(JsonObject defaults, JsonObject overrides)
    {
        var merged = defaults.DeepClone().AsObject();
        foreach (var (name, value) in overrides)
        {
            merged[name] = value is JsonObject over && merged[name] is JsonObject under
                ? Merge(under, over)
                : value?.DeepClone();
        }

        return merged;
    }

    /// <summary>Reads a complete document. On failure the error names the field.</summary>
    public static SettingsDocument? Read(JsonObject document, out FieldError? error)
    {
        try
        {
            error = null;
            return document.Deserialize<SettingsDocument>(Options)!;
        }
        catch (JsonException ex)
        {
            // The message of a JsonException can quote the input, so it is not passed on.
            var path = ex.Path is { Length: > 2 } p && p.StartsWith("$.", StringComparison.Ordinal) ? p[2..] : "";
            error = new FieldError(path, "The value is missing, has the wrong type or is not a known field.");
            return null;
        }
    }
}
