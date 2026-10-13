using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;

namespace Vellum.Modules.Mcp.Tools;

/// <summary>
/// The tools over promises. Each calls the endpoint the HTTP API has for the same thing, so
/// the origin of a new promise, the validation, the size limit of a revision, the rate limit
/// of runs and of draft writes and the binding rules of evidence are the ones of that
/// endpoint. The catalog has no tool for the approve, revise, delete and prefix endpoints,
/// and none that changes or removes an obligation.
/// </summary>
internal static class PromiseTools
{
    /// <summary>What every result that holds a draft says. It is a sentence of the server, not of the model.</summary>
    public const string Approval =
        "A draft is not a requirement. A workspace owner must approve it in Vellum: no tool and no agent token approves. The draft names this token in lastChangedBy, and the owner sees that an agent token changed it.";

    private const string Links =
        "What the promise is bound to: { scenarioIds[], operationIds[], channelIds[], subscriptionIds[], messageIds[], elementIds[] }. Every ID names a thing of the project.";
    private const string ForbiddenEvents =
        "Trace events that must not occur: [{ kind, target }]. kind is a trace event kind such as Dropped or DeadLettered; target is the ID of an operation, channel or subscription, or null.";
    private const string RequiresPattern =
        "A pattern the source must have: { repository (owner/name), pattern (transactional-outbox or in-memory-outbox), service (an element ID, or null) }.";

    public static Task<CallToolResult> ListPromises(
        McpCall call,
        [Description("The ID of the project.")] Guid projectId,
        [Description(Paged.Cursor)] string? cursor = null,
        [Description(Paged.Limit)] int? limit = null) =>
        Paged.ListAsync(call, cursor, limit);

    public static async Task<CallToolResult> GetPromise(
        McpCall call,
        [Description("The ID of the project.")] Guid projectId,
        [Description("The ID of the promise (a GUID, not the requirement ID).")] Guid promiseId)
    {
        var reply = await call.SendAsync(Promise(promiseId));
        return reply.Succeeded ? Shown(call, new JsonObject(), reply.Json(), projectId) : call.Refused(reply);
    }

    public static Task<CallToolResult> ListPromiseRuns(
        McpCall call,
        [Description("The ID of the project.")] Guid projectId,
        [Description("The ID of the promise (a GUID, not the requirement ID).")] Guid promiseId,
        [Description("Only the runs of this obligation: its ID, or its key such as O2. Leave it out for every run.")] string? obligation = null,
        [Description(Paged.Cursor)] string? cursor = null,
        [Description(Paged.Limit)] int? limit = null) =>
        Paged.ListAsync(call, cursor, limit, Promise(promiseId), new Dictionary<string, string?> { ["obligation"] = obligation },
            $"GET /api/projects/{projectId}/promises/{promiseId}/runs");

    public static async Task<CallToolResult> ProposePromise(
        McpCall call,
        [Description("The ID of the project.")] Guid projectId,
        [Description("A short title.")] string title,
        [Description("What must hold, as one claim that a check can show to be true or false.")] string claim,
        [Description("Where the claim applies.")] string? scope = null,
        [Description("What must be true before the claim applies.")] string? preconditions = null,
        [Description("What must happen.")] string? expectedOutcome = null,
        [Description("What must not happen.")] string? forbiddenOutcome = null,
        [Description("What would count as evidence.")] string? evidenceCriteria = null,
        [Description(ForbiddenEvents)] JsonElement? forbiddenEvents = null,
        [Description(RequiresPattern)] JsonElement? requiresPattern = null,
        [Description(Links)] JsonElement? links = null)
    {
        // The body has no origin: the endpoint takes it from the caller, and the caller is an agent token.
        var reply = await call.SendAsync(body: Content(
            title, claim, scope, preconditions, expectedOutcome, forbiddenOutcome, evidenceCriteria, forbiddenEvents, requiresPattern, links));
        return reply.Succeeded ? Shown(call, new JsonObject(), reply.Json(), projectId, draft: true) : call.Refused(reply);
    }

    public static async Task<CallToolResult> UpdatePromiseDraft(
        McpCall call,
        [Description("The ID of the project.")] Guid projectId,
        [Description("The ID of the promise (a GUID, not the requirement ID).")] Guid promiseId,
        [Description("A short title.")] string title,
        [Description("What must hold, as one claim that a check can show to be true or false.")] string claim,
        [Description("Where the claim applies.")] string? scope = null,
        [Description("What must be true before the claim applies.")] string? preconditions = null,
        [Description("What must happen.")] string? expectedOutcome = null,
        [Description("What must not happen.")] string? forbiddenOutcome = null,
        [Description("What would count as evidence.")] string? evidenceCriteria = null,
        [Description(ForbiddenEvents)] JsonElement? forbiddenEvents = null,
        [Description(RequiresPattern)] JsonElement? requiresPattern = null,
        [Description(Links)] JsonElement? links = null)
    {
        var reply = await call.SendAsync(Promise(promiseId), body: Content(
            title, claim, scope, preconditions, expectedOutcome, forbiddenOutcome, evidenceCriteria, forbiddenEvents, requiresPattern, links));
        return reply.Succeeded ? Shown(call, new JsonObject(), reply.Json(), projectId, draft: true) : call.Refused(reply);
    }

    public static async Task<CallToolResult> ProposeObligation(
        McpCall call,
        [Description("The ID of the project.")] Guid projectId,
        [Description("The ID of the promise (a GUID, not the requirement ID).")] Guid promiseId,
        [Description("scenario-check or source-pattern-check (Vellum runs these), or contract-test, integration-test or failure-test (a test plan that runs outside Vellum).")] string kind,
        [Description("A short title.")] string title,
        [Description("{ text, and the part that fits the kind: scenarioCheck { scenarioId, faults[] { target, kind, failFirst }, expect[] assertions }, sourcePattern { repository, pattern, service, requireUnconditional }, or plan { steps[], inputs[], expectedObservations[], forbiddenObservations[], contractIds[], evidenceSource { system: github-actions, repository, workflow, job } } }.")] JsonElement recipe)
    {
        var reply = await call.SendAsync(Promise(promiseId),
            body: new JsonObject { ["kind"] = kind, ["title"] = title, ["recipe"] = Node(recipe) });
        return reply.Succeeded ? Shown(call, new JsonObject(), reply.Json(), projectId, draft: true) : call.Refused(reply);
    }

    public static async Task<CallToolResult> GenerateObligations(
        McpCall call,
        [Description("The ID of the project.")] Guid projectId,
        [Description("The ID of the promise (a GUID, not the requirement ID).")] Guid promiseId)
    {
        var reply = await call.SendAsync(Promise(promiseId));
        if (!reply.Succeeded)
            return call.Refused(reply);

        var generated = reply.Json();
        return Shown(call,
            new JsonObject { ["added"] = generated["added"]?.DeepClone(), ["skipped"] = generated["skipped"]?.DeepClone() },
            generated["promise"]!.AsObject(), projectId, draft: true);
    }

    public static async Task<CallToolResult> RunObligation(
        McpCall call,
        [Description("The ID of the project.")] Guid projectId,
        [Description("The ID of the promise (a GUID, not the requirement ID).")] Guid promiseId,
        [Description("The key of the obligation: the last part of its ID, such as O2.")] string obligationKey,
        [Description("The promise revision. Default: the approved revision in force. A draft runs only when it is named here; such a run is labelled draft and does not count after approval.")] int? revision = null,
        [Description("For a scenario-check: the named model revision to play on.")] Guid? modelRevisionId = null,
        [Description("For a scenario-check: a draft or variant branch to play on. Default: the main model.")] Guid? branchId = null,
        [Description("For a source-pattern-check: the completed scan whose snapshot is looked at.")] Guid? scanId = null)
    {
        var reply = await call.SendAsync(Obligation(promiseId, obligationKey), body: new JsonObject
        {
            ["revision"] = revision, ["modelRevisionId"] = modelRevisionId, ["branchId"] = branchId, ["scanId"] = scanId,
        });
        return reply.Succeeded ? call.Ok(new JsonObject { ["run"] = reply.Json() }) : call.Refused(reply);
    }

    public static async Task<CallToolResult> RecordEvidence(
        McpCall call,
        [Description("The ID of the project.")] Guid projectId,
        [Description("The ID of the promise (a GUID, not the requirement ID).")] Guid promiseId,
        [Description("The key of the test-plan obligation: the last part of its ID, such as O2.")] string obligationKey,
        [Description("passed or failed: what the external test showed.")] string result,
        [Description("The URL of the run in the system that holds it: an https address, such as a GitHub Actions run.")] string runUrl,
        [Description("github-actions or other. Vellum can check only github-actions.")] string system = "github-actions",
        [Description("The commit the result is for: 40 or 64 lower-case hex digits. Default: the commit the model follows.")] string? commitSha = null,
        [Description("What the run concluded with, as one lower-case word such as success or failure. Vellum reads the conclusion itself.")] string? conclusion = null,
        [Description("sha256: and 64 lower-case hex digits of a test report. It is recorded as claimed.")] string? artifactDigest = null,
        [Description("The promise revision. Default: the approved revision in force.")] int? revision = null)
    {
        var reply = await call.SendAsync(Obligation(promiseId, obligationKey), body: new JsonObject
        {
            ["result"] = result,
            ["source"] = new JsonObject { ["system"] = system, ["runUrl"] = runUrl, ["commitSha"] = commitSha, ["conclusion"] = conclusion },
            ["artifactDigest"] = artifactDigest,
            ["revision"] = revision,
        });
        return reply.Succeeded ? call.Ok(new JsonObject { ["run"] = reply.Json() }) : call.Refused(reply);
    }

    private static Dictionary<string, string> Promise(Guid promiseId) => new() { ["promiseId"] = promiseId.ToString() };

    private static Dictionary<string, string> Obligation(Guid promiseId, string obligationKey) =>
        new() { ["promiseId"] = promiseId.ToString(), ["obligationKey"] = obligationKey };

    private static JsonNode? Node(JsonElement? element) =>
        element is { ValueKind: not (JsonValueKind.Undefined or JsonValueKind.Null) } value ? JsonNode.Parse(value.GetRawText()) : null;

    /// <summary>The body of a create and of a change of a draft. The endpoint checks every member.</summary>
    private static JsonObject Content(
        string title, string claim, string? scope, string? preconditions, string? expectedOutcome, string? forbiddenOutcome,
        string? evidenceCriteria, JsonElement? forbiddenEvents, JsonElement? requiresPattern, JsonElement? links) => new()
    {
        ["title"] = title,
        ["claim"] = claim,
        ["scope"] = scope,
        ["preconditions"] = preconditions,
        ["expectedOutcome"] = expectedOutcome,
        ["forbiddenOutcome"] = forbiddenOutcome,
        ["evidenceCriteria"] = evidenceCriteria,
        ["forbiddenEvents"] = Node(forbiddenEvents),
        ["requiresPattern"] = Node(requiresPattern),
        ["links"] = Node(links),
    };

    /// <summary>
    /// A promise as a tool shows it: the revision in force and the draft, each with its
    /// obligations and its evidence status, and no older revision. Each obligation status has
    /// <c>stale</c> beside <c>current</c>. Nothing here says that a promise is proven: the
    /// counts and the gaps of the evidence status are the answer.
    /// </summary>
    private static CallToolResult Shown(McpCall call, JsonObject data, JsonObject promise, Guid projectId, bool draft = false)
    {
        var approved = promise["inForceRevision"] is not null;
        var inForce = approved ? Revision(promise["current"]) : null;
        var proposed = approved ? Revision(promise["draft"]) : Revision(promise["current"]);

        if (draft)
        {
            data["promiseId"] = promise["id"]?.DeepClone();
            data["requirementId"] = promise["requirementId"]?.DeepClone();
            data["origin"] = promise["origin"]?.DeepClone();
            data["state"] = promise["state"]?.DeepClone();
            data["revision"] = proposed?["revision"]?.DeepClone();
            data["contentHash"] = proposed?["contentHash"]?.DeepClone();
            data["approval"] = Approval;
        }
        data["promise"] = new JsonObject
        {
            ["id"] = promise["id"]?.DeepClone(),
            ["requirementId"] = promise["requirementId"]?.DeepClone(),
            ["origin"] = promise["origin"]?.DeepClone(),
            ["state"] = promise["state"]?.DeepClone(),
            ["latestRevision"] = promise["latestRevision"]?.DeepClone(),
            ["inForceRevision"] = promise["inForceRevision"]?.DeepClone(),
            ["needsReview"] = promise["needsReview"]?.DeepClone(),
            ["missingLinks"] = promise["missingLinks"]?.DeepClone(),
            ["inForce"] = inForce,
            ["draft"] = proposed,
        };
        if (McpOutput.Fits(data))
            return call.Ok(data);

        // A revision holds up to 256 KiB. The recipes and the details of the runs are the large part.
        foreach (var revision in new[] { inForce, proposed }.OfType<JsonObject>())
        {
            foreach (var obligation in revision["obligations"]!.AsArray())
                obligation!.AsObject().Remove("recipe");
            foreach (var status in revision["evidenceStatus"]!["obligations"]!.AsArray())
            {
                foreach (var detail in new[] { "inputs", "assertions", "effectiveFaults", "evidence" })
                    status!.AsObject().Remove(detail);
            }
        }
        return call.Ok(data, notice:
            $"The promise is larger than one result holds. The recipe of each obligation and the inputs, assertions, faults and evidence of each run are left out. GET /api/projects/{projectId}/promises/{promise["id"]} gives them.");
    }

    private static JsonObject? Revision(JsonNode? revision)
    {
        if (revision is not JsonObject source)
            return null;

        var copy = source.DeepClone().AsObject();
        foreach (var status in copy["evidenceStatus"]!["obligations"]!.AsArray())
        {
            // A result on inputs that moved is stale. A status with no result has nothing to be stale.
            var current = status!["current"];
            status["stale"] = current is null ? null : !current.GetValue<bool>();
        }
        return copy;
    }
}
