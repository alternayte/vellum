using System.Reflection;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Vellum.Modules.Drafts;
using Vellum.Modules.Identity.AgentTokens;
using Vellum.Modules.Modelling;
using Vellum.Modules.Trace.Engine;
using Vellum.Modules.Trace;
using Vellum.Modules.Workspaces;
using Vellum.Shared;

namespace Vellum.Modules.Ai.Design;

/// <summary>
/// The answer of an AI endpoint when no answer was generated. It is a state of the instance
/// or of one call, not a transport error, so the status is 200.
/// <c>status</c> is <c>unavailable</c> when AI cannot be used now (<c>not_configured</c>,
/// <c>disabled</c>, <c>budget_exceeded</c>, <c>rate_limited</c>) and <c>failed</c> when the
/// provider was called and gave nothing usable (<c>invalid_output</c>, <c>refused</c>,
/// <c>provider_error</c>, <c>timed_out</c>, <c>cancelled</c>). Nothing is stored in either case.
/// </summary>
public sealed record AiUnavailable(string Status, AiUnavailableReason Reason)
{
    public static AiUnavailable From(AiFailure failure) => failure switch
    {
        AiFailure.NotConfigured => Unavailable("not_configured",
            "No AI provider is configured for this task. An owner sets one in the workspace settings. Modelling, playback and export work without it."),
        AiFailure.Disabled { Setting: { } setting } => Unavailable("disabled",
            $"The setting {setting} does not allow this request to be sent to an AI provider.", setting),
        AiFailure.Disabled => Unavailable("disabled",
            "The AI provider for this task is switched off, or the instance policy does not allow it."),
        AiFailure.BudgetExceeded { Reason: "price_unknown" } => Unavailable("budget_exceeded",
            "A budget is set and the model has no price, so the budget cannot be kept. Add a price for the model or remove the budget."),
        AiFailure.BudgetExceeded budget => Unavailable("budget_exceeded", $"The monthly AI budget of the {budget.Scope} is used up."),
        AiFailure.RateLimited limited => new AiUnavailable("unavailable", new AiUnavailableReason(
            "rate_limited", "The request limit of the AI provider is reached. Try again later.", null,
            limited.RetryAfter is { } wait ? (int)Math.Clamp(Math.Ceiling(wait.TotalSeconds), 1, 86_400) : null)),
        AiFailure.Refused => Failed("refused", "The AI provider declined the request."),
        AiFailure.InvalidOutput => Failed("invalid_output",
            "The AI provider gave an answer that is not valid against the required format, after the repair attempts. Nothing was stored."),
        AiFailure.ProviderError error => Failed("provider_error", error.Message),
        AiFailure.TimedOut => Failed("timed_out", "The AI provider did not answer in time."),
        _ => Failed("cancelled", "The request was cancelled."),
    };

    private static AiUnavailable Unavailable(string code, string message, string? setting = null) =>
        new("unavailable", new AiUnavailableReason(code, message, setting, null));

    private static AiUnavailable Failed(string code, string message) =>
        new("failed", new AiUnavailableReason(code, message, null, null));
}

/// <param name="Setting">The setting that forbids the request, when one does.</param>
public sealed record AiUnavailableReason(string Code, string Message, string? Setting, int? RetryAfterSeconds);

public sealed record ProposalProvider(string Kind, string Model, string? ReportedModel);

/// <summary>
/// A proposal that a caller wrote and submitted, in place of one the in-app assistant
/// generated. It goes through the same checks, is stored as the same draft and is decided by
/// a person in the same way. Its origin is the credential of the call, never a value of the body.
/// </summary>
internal static class SubmittedProposal
{
    /// <summary>The <c>origin</c> of a proposal that the in-app assistant generated.</summary>
    public const string AiOrigin = "ai";

    /// <summary>What the template version member holds: no prompt template made the proposal.</summary>
    public const string NoTemplate = "none";

    /// <summary>The most proposals one caller submits in a minute.</summary>
    public const int PerMinute = 30;

    public static readonly ProposalUsage NoUsage = new(null, null, null);

    /// <summary><c>agent-token:&lt;id&gt;</c> for an agent token, else <c>user:&lt;id&gt;</c>.</summary>
    public static string OriginOf(ClaimsPrincipal user) => AgentPrincipal.Actor(user)!;

    /// <summary>The provider member of a submitted proposal: the kind and the ID of who submitted it, in place of a vendor and a model.</summary>
    public static ProposalProvider Provider(string origin)
    {
        var at = origin.IndexOf(':', StringComparison.Ordinal);
        return new ProposalProvider(origin[..at], origin[(at + 1)..], null);
    }

    /// <summary>
    /// The reference table of a submitted proposal. A caller that submits has read the model,
    /// so it references a thing by its ID, where the assistant uses an alias. Each reference
    /// that is the ID of a thing of the model is listed under the text as it was written. A
    /// reference that is not listed names nothing: the checks reject it and list it, as they
    /// do for an alias that the assistant invented.
    /// </summary>
    public static Dictionary<string, AliasRef> References(IEnumerable<string?> references, ModelState model)
    {
        var table = new Dictionary<string, AliasRef>(StringComparer.Ordinal);
        foreach (var reference in references)
        {
            if (reference is not null && !table.ContainsKey(reference) && Guid.TryParse(reference, out var id) && Find(model, id) is { } found)
                table[reference] = found;
        }

        return table;
    }

    private static AliasRef? Find(ModelState model, Guid id) =>
        model.Elements.TryGetValue(id, out var element) ? new AliasRef(AliasKinds.Element, id, element.Name)
        : model.Operations.TryGetValue(id, out var operation) ? new AliasRef(AliasKinds.Operation, id, operation.Name)
        : model.Channels.TryGetValue(id, out var channel) ? new AliasRef(AliasKinds.Channel, id, channel.Name)
        : model.Subscriptions.TryGetValue(id, out var subscription) ? new AliasRef(AliasKinds.Subscription, id, ContractDecider.SubscriptionLabel(model, subscription))
        : model.Messages.TryGetValue(id, out var message) ? new AliasRef(AliasKinds.Message, id, message.Name)
        : null;
}

/// <summary>The rate limit of submitted proposals. A proposal is a stored document that a person must read, so one caller submits a bounded number in a minute.</summary>
internal sealed class ProposalSubmitRate() : CallerRate(SubmittedProposal.PerMinute);

/// <summary>What the provider reported. A member is null when the provider did not report it or the model has no price.</summary>
public sealed record ProposalUsage(int? InputTokens, int? OutputTokens, decimal? CostUsd)
{
    public static ProposalUsage From(AiUsage usage) => new(usage.InputTokens, usage.OutputTokens, usage.CostUsd);
}

/// <summary>
/// A reference the model gave that names nothing it was allowed to name. The thing that held
/// it was dropped from the proposal.
/// </summary>
/// <param name="Where">For example <c>step:place.operation</c>, <c>fault:0.target</c>, <c>missing:N2.ownerAlias</c>, <c>item:N3.parent</c>.</param>
public sealed record RejectedReference(string Where, string Alias, string Reason);

/// <summary>The caller, the workspace and the model stream of one request to this feature.</summary>
internal sealed record DesignCaller(Guid WorkspaceId, string UserId, Guid StreamId);

internal static class DesignAccess
{
    /// <summary>Authorizes the caller for the project. A caller with no role in the workspace gets 403.</summary>
    public static async Task<(Guid WorkspaceId, string UserId)> RequireAsync(
        HttpContext http, Guid projectId, WorkspaceRole role, CancellationToken ct)
    {
        var userId = http.User.UserId();
        var auth = http.RequestServices.GetRequiredService<WorkspaceAuthorizationService>();
        await auth.RequireProjectRoleAsync(projectId, userId, role, ct);
        return (await auth.GetProjectWorkspaceIdAsync(projectId, ct), userId);
    }

    /// <summary>Authorizes the caller and resolves the branch to a model stream, as the modelling endpoints do.</summary>
    public static async Task<(DesignCaller? Caller, IResult? Refusal)> ResolveAsync(
        HttpContext http, Guid projectId, Guid? branchId, WorkspaceRole role, bool forWrite, CancellationToken ct)
    {
        var (workspaceId, userId) = await RequireAsync(http, projectId, role, ct);
        var services = http.RequestServices;
        var mainStreamId = await services.GetRequiredService<AppDbContext>().Projects.AsNoTracking()
            .Where(p => p.Id == projectId).Select(p => p.StreamId).FirstAsync(ct);
        var (streamId, refusal) = await ModelBranches.ResolveAsync(
            branchId, projectId, mainStreamId,
            services.GetRequiredService<AppDbContext>(), forWrite, ct);
        return refusal is null ? (new DesignCaller(workspaceId, userId, streamId), null) : (null, refusal);
    }

    /// <summary>
    /// The model stream of a branch, for a read. The ID of a draft or a variant is the ID of
    /// its stream, and a stream outlives its branch, so a proposal that was made on a branch
    /// can still be read after the branch was merged or abandoned. The caller took the branch
    /// ID from a proposal of the project, which checked it when the proposal was made.
    /// </summary>
    public static async Task<Guid> StreamOfAsync(HttpContext http, Guid projectId, Guid? branchId, CancellationToken ct) =>
        branchId ?? await http.RequestServices.GetRequiredService<AppDbContext>().Projects.AsNoTracking()
            .Where(p => p.Id == projectId).Select(p => p.StreamId).FirstAsync(ct);

}

/// <summary>How this feature writes and reads its stored documents.</summary>
internal static class DesignJson
{
    public static readonly JsonSerializerOptions Options = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            MaxDepth = ScenarioShape.MaxJsonDepth,
            AllowOutOfOrderMetadataProperties = true,
        };
        options.Converters.Add(new JsonStringEnumConverter<StepOutcome>());
        options.Converters.Add(new JsonStringEnumConverter<FaultKind>());
        options.Converters.Add(new JsonStringEnumConverter<TraceEventKind>());
        return options;
    }
}

/// <summary>
/// Text that a model wrote is shown to a person and stored. It is bounded, and it holds no
/// control character and no half of a surrogate pair, so it fits the store and every list.
/// </summary>
internal static class DesignText
{
    public const int MaxNote = 2_000;

    /// <summary>One line: control characters are removed.</summary>
    public static string Line(string? text, int max = TextRule.MaxName) => Clean(text, max, multiline: false);

    /// <summary>Several lines: tab and line breaks stay.</summary>
    public static string Lines(string? text, int max = MaxNote) => Clean(text, max, multiline: true);

    public static List<string> List(IEnumerable<string?>? items, int maxItems, int maxChars = MaxNote) =>
        (items ?? []).Select(item => Lines(item, maxChars)).Where(item => item.Length > 0).Take(maxItems).ToList();

    private static string Clean(string? text, int max, bool multiline)
    {
        if (string.IsNullOrEmpty(text))
            return "";

        var clean = new StringBuilder(Math.Min(text.Length, max));
        for (var i = 0; i < text.Length && clean.Length < max; i++)
        {
            var c = text[i];
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                if (clean.Length + 2 > max)
                    break;
                clean.Append(c).Append(text[++i]);
            }
            else if (char.IsSurrogate(c) || (char.IsControl(c) && !(multiline && c is '\t' or '\n' or '\r')))
            {
                // Dropped: it is not text a list can show or the store can keep.
            }
            else
            {
                clean.Append(c);
            }
        }

        return clean.ToString().Trim();
    }
}

/// <summary>
/// The prompt templates of this feature. Each is an embedded Markdown file with sections
/// that start with a line <c># name</c>. A change to a template changes its version constant.
/// </summary>
internal static class DesignPrompts
{
    public const string ProposeFlowVersion = "design/propose-flow/3";
    public const string InferArchitectureVersion = "design/infer-architecture/3";
    public const string ExplainVersion = "design/explain/2";

    /// <summary>
    /// SDD guarantee 8. The flow and the explanation templates both hold this sentence, and a
    /// test checks that it is in the outgoing request.
    /// </summary>
    public const string DeclaredBehaviourRule =
        "A scenario, a trace and a proposal are declared behaviour: a description of what the design says must happen. " +
        "They are not evidence about a running system. Never state or imply that anything was verified, tested, measured, observed or proven.";

    public static readonly IReadOnlyDictionary<string, string> ProposeFlow = Load("propose-flow.md");
    public static readonly IReadOnlyDictionary<string, string> InferArchitecture = Load("infer-architecture.md");
    public const string InferFromDocumentVersion = "design/infer-from-document/1";
    public static readonly IReadOnlyDictionary<string, string> InferFromDocument = Load("infer-from-document.md");
    public static readonly IReadOnlyDictionary<string, string> Explain = Load("explain.md");

    /// <param name="folder">The end of the resource name before the file name. Another feature gives its own prompt folder.</param>
    internal static Dictionary<string, string> Load(string file, string folder = ".Design.Prompts.")
    {
        var assembly = typeof(DesignPrompts).Assembly;
        var name = assembly.GetManifestResourceNames().Single(n => n.EndsWith(folder + file, StringComparison.Ordinal));
        using var reader = new StreamReader(assembly.GetManifestResourceStream(name)!, Encoding.UTF8);

        var sections = new Dictionary<string, string>(StringComparer.Ordinal);
        string? current = null;
        var text = new StringBuilder();
        while (reader.ReadLine() is { } line)
        {
            if (line.StartsWith("# ", StringComparison.Ordinal))
            {
                if (current is not null)
                    sections[current] = text.ToString().Trim();
                current = line[2..].Trim();
                text.Clear();
            }
            else
            {
                text.Append(line).Append('\n');
            }
        }

        if (current is not null)
            sections[current] = text.ToString().Trim();

        // The rule is one constant, so the templates and the test cannot drift apart.
        foreach (var key in sections.Keys.ToList())
            sections[key] = sections[key].Replace("{{declared-behaviour-rule}}", DeclaredBehaviourRule, StringComparison.Ordinal);
        return sections;
    }
}
