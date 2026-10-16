using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;

namespace Vellum.Modules.Mcp.Tools;

/// <summary>
/// The tools that submit a proposal: additions to the architecture, and a flow. Each calls
/// the submit endpoint of the HTTP API, which checks the proposal with the checks of the
/// in-app assistant, stores it as a draft with the token as its origin and writes nothing
/// else. The catalog has no tool for the accept, decide, reject and create-missing
/// endpoints: a person decides in the app.
/// </summary>
internal static class ProposalTools
{
    /// <summary>What every result that holds a proposal says. It is a sentence of the server, not of the model.</summary>
    public const string Approval =
        "The proposal is a draft. It changed nothing in the model and saved no scenario. A person previews it in Vellum and accepts or rejects it: no tool and no agent token accepts a proposal.";

    private const string Request = "What the proposal is for, in plain language, for the person who decides. At most 4,000 characters.";

    private const string Architecture =
        "The additions: { summary, reasoningSummary, openQuestions[], items[] }. An item is { alias (N1, N2, …), action (add), entityKind, name, description, technology, confidence (0 to 1), rationale } "
        + "and the members of its entityKind: element { elementKind (actor, system, app, store or component), parent }, where an actor and a system have no parent, an app and a store have a parent of kind system, and a component has a parent of kind app; relationship { from, to, label, relationshipKind (structural or behavioural) }; "
        + "operation { owner, transport (http, grpc or command) }; channel { channelKind (topic, queue or exchange) }; subscription { channel, consumer }; binding { from, to, label (the routing key) }; "
        + "message { producer, consumers[] }. A reference is the ID of a thing of the model, or the alias of another item. An item that changes or removes a thing is dropped: a proposal only adds.";

    private const string Flow =
        "The flow: { name, summary, reasoningSummary, openQuestions[], steps[], faults[], assertions[], missing[], evidence[] }. "
        + "A step is { id, type (call, publish, write, parallel or branch), slot (root, then, onError, handler, branchThen, branchElse or parallelBranch), parentStepId, slotKey, order } "
        + "and the members of its type: call { from, operation, requestJson, responseJson }; publish { from, channel, message, payloadJson, routingKey }; write { by, store, dataset, changeJson }; "
        + "branch { whenStep, is (succeeded, failed or skipped) }. slotKey is the subscription of a handler, or the branch number of a parallelBranch. A payload is JSON text. "
        + "A fault is { target, kind (error, timeout or outage), failFirst (0 or absent: every attempt) }. "
        + "An assertion is { id, type: outcome, step, expected } or { id, type: event-count, eventKind, target, expectedCount }. "
        + "missing lists what the flow needs and the model lacks: { alias (N1, N2, …), kind (element, operation, channel, subscription or message), name, elementKind, parentAlias, ownerAlias, transport, channelKind, channelAlias, consumerAlias, reason, confidence }. "
        + "evidence is [{ alias, why }]. A reference is the ID of a thing of the model, or the alias of an entry of missing.";

    public static async Task<CallToolResult> ProposeArchitecture(
        McpCall call,
        [Description("The ID of the project.")] Guid projectId,
        [Description(Request)] string request,
        [Description(Architecture)] JsonElement proposal)
    {
        if (proposal.ValueKind != JsonValueKind.Object)
            return ModelTools.Invalid("proposal", "The proposal is a JSON object");

        var reply = await call.SendAsync(body: new JsonObject { ["request"] = request, ["proposal"] = JsonNode.Parse(proposal.GetRawText()) });
        if (!reply.Succeeded)
            return call.Refused(reply);

        var stored = reply.Json();
        var data = Head(stored);
        data["summary"] = stored["summary"]?.DeepClone();
        data["items"] = stored["items"]?.DeepClone();
        data["itemIssues"] = stored["itemIssues"]?.DeepClone();
        data["resolvedToExisting"] = stored["resolvedToExisting"]?.DeepClone();
        data["rejectedReferences"] = stored["rejectedReferences"]?.DeepClone();
        if (McpOutput.Fits(data))
            return call.Ok(data);

        // The values of each item are what the call sent. What the checks made of them stays.
        foreach (var item in data["items"]!.AsArray())
        {
            foreach (var member in new[] { "proposed", "rationale", "action", "confidence" })
                item!.AsObject().Remove(member);
        }
        return Bounded(call, data,
            "The proposal is larger than one result holds, so each item is here without its proposed values and its rationale.",
            "resolvedToExisting", "itemIssues", "rejectedReferences", "items");
    }

    public static async Task<CallToolResult> ProposeFlow(
        McpCall call,
        [Description("The ID of the project.")] Guid projectId,
        [Description(Request)] string request,
        [Description(Flow)] JsonElement flow,
        [Description("The ID of a scenario that the flow replaces when a person accepts the proposal. Leave it out to propose a new scenario.")] Guid? reviseScenarioId = null)
    {
        if (flow.ValueKind != JsonValueKind.Object)
            return ModelTools.Invalid("flow", "The flow is a JSON object");

        var reply = await call.SendAsync(body: new JsonObject
        {
            ["request"] = request, ["reviseScenarioId"] = reviseScenarioId, ["flow"] = JsonNode.Parse(flow.GetRawText()),
        });
        if (!reply.Succeeded)
            return call.Refused(reply);

        var stored = reply.Json();
        var data = Head(stored);
        data["name"] = stored["name"]?.DeepClone();
        data["reviseScenarioId"] = stored["reviseScenarioId"]?.DeepClone();
        data["preview"] = stored["preview"]?.DeepClone();
        data["diff"] = stored["diff"]?.DeepClone();
        data["blockedSteps"] = stored["blockedSteps"]?.DeepClone();
        data["missing"] = stored["missing"]?.DeepClone();
        data["resolvedToExisting"] = stored["resolvedToExisting"]?.DeepClone();
        data["rejectedReferences"] = stored["rejectedReferences"]?.DeepClone();
        data["issues"] = stored["issues"]?.DeepClone();
        data["scenario"] = stored["scenario"]?.DeepClone();
        if (McpOutput.Fits(data))
            return call.Ok(data);

        // The scenario holds the payloads that the call sent. Everything the checks found stays.
        var scenario = stored["scenario"]!;
        data["scenario"] = new JsonObject { ["id"] = scenario["id"]?.DeepClone(), ["name"] = scenario["name"]?.DeepClone() };
        return Bounded(call, data,
            "The proposed scenario is larger than one result holds, so only its ID and name are here.",
            "blockedSteps", "resolvedToExisting", "issues", "rejectedReferences", "missing");
    }

    /// <summary>
    /// The result of a proposal that is stored and is larger than one result holds. The lists
    /// give up items from their ends, in the order given, until the result fits: the answer
    /// to a call that stored a proposal is never an error.
    /// </summary>
    private static CallToolResult Bounded(McpCall call, JsonObject data, string notice, params string[] lists)
    {
        var cut = lists.Where(name => data[name] is JsonArray items && McpOutput.Fit(data, items) > 0).ToList();
        if (cut.Count > 0)
            notice += $" These lists were cut to fit the byte bound: {string.Join(", ", cut)}.";
        return call.Ok(data, notice: $"{notice} The proposal is stored whole, as the checks left it. A person sees it in Vellum.");
    }

    /// <summary>What both results start with: the proposal, its origin and what a person must do.</summary>
    private static JsonObject Head(JsonObject stored) => new()
    {
        ["proposalId"] = stored["id"]?.DeepClone(),
        ["status"] = stored["status"]?.DeepClone(),
        ["origin"] = stored["origin"]?.DeepClone(),
        ["modelVersion"] = stored["modelVersion"]?.DeepClone(),
        ["approval"] = Approval,
    };
}
