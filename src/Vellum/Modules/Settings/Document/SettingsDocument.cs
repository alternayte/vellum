using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Vellum.Shared;

namespace Vellum.Modules.Settings.Document;

public sealed record SettingsDocument(
    AiSettings Ai,
    GithubSettings Github,
    ImportSettings Import,
    CostSettings Cost,
    SimulationSettings Simulation,
    PrivacySettings Privacy,
    UsageSettings Usage,
    NotificationSettings Notifications);

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
public sealed record ImportSettings(int MaxFiles, long MaxFileBytes, long MaxTotalBytes, IReadOnlyList<string> ExcludeGlobs);
public sealed record CostSettings(string Provider, string Region, string Currency);
public sealed record SimulationSettings(int Seed, int DurationSeconds, double ArrivalRatePerSecond);
public sealed record PrivacySettings(bool SendPrivateCodeToAi);
public sealed record UsageSettings(decimal? MonthlyAiBudgetUsd, int MaxScansPerDay);
public sealed record NotificationSettings(string? WebhookUrl);

/// <summary>What the instance admin allows every workspace to choose.</summary>
public sealed record InstancePolicy(
    bool AllowSelfRegistration,
    bool AllowPrivateNetworkUrls,
    IReadOnlyList<string> AllowedAiProviderKinds,
    bool AllowSendingPrivateCodeToAi);

public static class AiProviderKinds
{
    public static readonly IReadOnlyList<string> All = ["openai", "anthropic", "gemini", "openai-compatible"];
}

public static class SettingsDefaults
{
    /// <summary>The defaults of a new instance, before the admin changes them.</summary>
    public static readonly SettingsDocument BuiltIn = new(
        new AiSettings([], new TaskRouting(null, null, null, null)),
        new GithubSettings("https://api.github.com", ""),
        new ImportSettings(2000, 262144, 20971520,
            ["**/node_modules/**", "**/vendor/**", "**/bin/**", "**/obj/**", "**/*.min.*"]),
        new CostSettings("", "", "USD"),
        new SimulationSettings(1, 300, 10),
        new PrivacySettings(false),
        new UsageSettings(null, 20),
        new NotificationSettings(null));

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

    public const int MaxDocumentLength = 64 * 1024;
    public const string NotADocument = "Must be a settings document of at most 64 KB in which no field repeats.";

    /// <summary>
    /// The request body as a document, or null when it is not a JSON object, is too large or
    /// repeats a field. A repeated field has no single meaning, so it is refused.
    /// </summary>
    public static JsonObject? ToDocument(JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object || body.GetRawText().Length > MaxDocumentLength)
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
