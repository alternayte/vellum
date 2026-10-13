using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;

namespace Vellum.Modules.Mcp.Tools;

/// <summary>
/// The tools over the review: the deterministic findings, the decisions and the open
/// questions. Each calls the endpoint the HTTP API has for the same thing. The review calls
/// no AI. A token proposes a decision and a question; the catalog has no tool for the
/// accept, reject, answer, drop and disposition endpoints, which are for a person.
/// </summary>
internal static class ReviewTools
{
    private static readonly string[] Results = ["concern", "unknown", "verified", "not-applicable"];

    /// <summary>What every result that holds a proposed decision says. It is a sentence of the server, not of the model.</summary>
    public const string DecisionApproval =
        "A proposed decision settles no finding. A workspace owner must accept it in Vellum: no tool and no agent token accepts or rejects a decision.";

    public const string QuestionApproval =
        "The question is open. A person answers or drops it in Vellum: no tool and no agent token does.";

    public static async Task<CallToolResult> GetReviewFindings(
        McpCall call,
        [Description("The ID of the project.")] Guid projectId,
        [Description("Only the findings with this result: concern, unknown, verified or not-applicable. Leave it out for every finding.")] string? result = null,
        [Description("A named model revision. Default: the branch at its version now.")] Guid? revisionId = null,
        [Description("A draft or variant branch. Default: the main model.")] Guid? branchId = null,
        [Description(Paged.Cursor)] string? cursor = null,
        [Description(Paged.Limit)] int? limit = null)
    {
        if (result is not null && !Results.Contains(result, StringComparer.Ordinal))
            return ModelTools.Invalid("result", $"A result is one of: {string.Join(", ", Results)}");

        var reply = await call.SendAsync(query: new Dictionary<string, string?>
        {
            ["revisionId"] = revisionId?.ToString(), ["branchId"] = branchId?.ToString(),
        });
        if (!reply.Succeeded)
            return call.Refused(reply);

        var review = reply.Json();
        var findings = review["findings"]!.AsArray();
        var wanted = result is null
            ? findings
            : new JsonArray([.. findings.Where(f => f!["finding"]?["result"]?.GetValue<string>() == result).Select(f => f!.DeepClone())]);
        var data = new JsonObject
        {
            ["branch"] = review["branch"]?.DeepClone(),
            ["modelVersion"] = review["modelVersion"]?.DeepClone(),
            ["revisionId"] = review["revisionId"]?.DeepClone(),
            ["inputsFingerprint"] = review["inputsFingerprint"]?.DeepClone(),
            ["ruleSetVersion"] = review["ruleSetVersion"]?.DeepClone(),
            ["hash"] = review["hash"]?.DeepClone(),
            // The counts are of every finding of the state, whatever the filter and the page.
            ["counts"] = review["counts"]?.DeepClone(),
        };
        return Paged.Local(call, data, "findings", wanted, cursor, limit, $"GET /api/projects/{projectId}/review/findings");
    }

    public static async Task<CallToolResult> ListDecisions(
        McpCall call,
        [Description("The ID of the project.")] Guid projectId,
        [Description(Paged.Cursor)] string? cursor = null,
        [Description(Paged.Limit)] int? limit = null)
    {
        var reply = await call.SendAsync();
        return reply.Succeeded
            ? Paged.Local(call, new JsonObject(), "decisions", reply.Json()["items"]!.AsArray(), cursor, limit, $"GET /api/projects/{projectId}/decisions/{{decisionId}}")
            : call.Refused(reply);
    }

    public static async Task<CallToolResult> ProposeDecision(
        McpCall call,
        [Description("The ID of the project.")] Guid projectId,
        [Description("A short title.")] string title,
        [Description("What is decided.")] string decision,
        [Description("The situation that makes the decision necessary.")] string? context = null,
        [Description("The options that were not chosen: [{ title, whyNot }].")] JsonElement? alternatives = null,
        [Description("What follows from the decision.")] string? consequences = null,
        [Description("The IDs of the things of the model the decision is about.")] Guid[]? affectedIds = null,
        [Description("The findings the decision answers: [{ ruleId, subjectId }], as get_review_findings names them. The decision settles them only after a person accepts it.")] JsonElement? settles = null,
        [Description("The ID of the decision that this one replaces when a person accepts it.")] Guid? supersedes = null)
    {
        // The body has no origin: the endpoint takes it from the caller, and the caller is an agent token.
        var reply = await call.SendAsync(body: new JsonObject
        {
            ["title"] = title,
            ["decision"] = decision,
            ["context"] = context,
            ["alternatives"] = Node(alternatives),
            ["consequences"] = consequences,
            ["affectedIds"] = Ids(affectedIds),
            ["settles"] = Node(settles),
            ["supersedes"] = supersedes,
        });
        if (!reply.Succeeded)
            return call.Refused(reply);

        var created = reply.Json();
        return call.Ok(new JsonObject
        {
            ["decisionId"] = created["id"]?.DeepClone(),
            ["number"] = created["number"]?.DeepClone(),
            ["status"] = created["status"]?.DeepClone(),
            ["origin"] = created["origin"]?.DeepClone(),
            ["approval"] = DecisionApproval,
            ["decision"] = created,
        });
    }

    public static async Task<CallToolResult> ListQuestions(
        McpCall call,
        [Description("The ID of the project.")] Guid projectId,
        [Description(Paged.Cursor)] string? cursor = null,
        [Description(Paged.Limit)] int? limit = null)
    {
        var reply = await call.SendAsync();
        return reply.Succeeded
            ? Paged.Local(call, new JsonObject(), "questions", reply.Json()["items"]!.AsArray(), cursor, limit, $"GET /api/projects/{projectId}/questions")
            : call.Refused(reply);
    }

    public static async Task<CallToolResult> ProposeQuestion(
        McpCall call,
        [Description("The ID of the project.")] Guid projectId,
        [Description("The question: what the design leaves undecided.")] string text,
        [Description("The IDs of the things of the model the question is about.")] Guid[]? about = null)
    {
        var reply = await call.SendAsync(body: new JsonObject { ["text"] = text, ["about"] = Ids(about) });
        if (!reply.Succeeded)
            return call.Refused(reply);

        var created = reply.Json();
        return call.Ok(new JsonObject
        {
            ["questionId"] = created["id"]?.DeepClone(),
            ["status"] = created["status"]?.DeepClone(),
            ["origin"] = created["origin"]?.DeepClone(),
            ["approval"] = QuestionApproval,
            ["question"] = created,
        });
    }

    private static JsonArray Ids(Guid[]? ids) => new([.. (ids ?? []).Select(id => (JsonNode)id)]);

    internal static JsonNode? Node(JsonElement? element) =>
        element is { ValueKind: not (JsonValueKind.Undefined or JsonValueKind.Null) } value ? JsonNode.Parse(value.GetRawText()) : null;
}
