using System.ComponentModel;
using System.Globalization;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;

namespace Vellum.Modules.Mcp.Tools;

/// <summary>
/// The tools over schemas and scenarios. Each calls the endpoint the HTTP API has for the
/// same thing and bounds what comes back.
/// </summary>
internal static class ScenarioTools
{
    public static async Task<CallToolResult> GetSchema(
        McpCall call,
        [Description("The ID of the project.")] Guid projectId,
        [Description("The ID of the schema.")] Guid schemaId)
    {
        var reply = await call.SendAsync(new Dictionary<string, string> { ["schemaId"] = schemaId.ToString() });
        if (!reply.Succeeded)
            return call.Refused(reply);

        var data = new JsonObject { ["schema"] = reply.Json() };
        if (McpOutput.Fits(data))
            return call.Ok(data);

        data["schema"]!.AsObject().Remove("content");
        return call.Ok(data, notice:
            $"The content of the schema is larger than one result holds, so it is left out. GET /api/projects/{projectId}/schemas/{schemaId} gives it.");
    }

    public static Task<CallToolResult> ListScenarios(
        McpCall call,
        [Description("The ID of the project.")] Guid projectId,
        [Description(Paged.Cursor)] string? cursor = null,
        [Description(Paged.Limit)] int? limit = null) =>
        Paged.ListAsync(call, cursor, limit);

    public static async Task<CallToolResult> GetScenario(
        McpCall call,
        [Description("The ID of the project.")] Guid projectId,
        [Description("The ID of the scenario.")] Guid scenarioId)
    {
        var reply = await call.SendAsync(new Dictionary<string, string> { ["scenarioId"] = scenarioId.ToString() });
        if (!reply.Succeeded)
            return call.Refused(reply);

        var scenario = reply.Json();
        var data = new JsonObject { ["scenario"] = scenario };
        if (McpOutput.Fits(data))
            return call.Ok(data);

        return call.Ok(
            new JsonObject { ["scenario"] = new JsonObject { ["id"] = scenario["id"]?.DeepClone(), ["name"] = scenario["name"]?.DeepClone() } },
            notice: $"The scenario is larger than one result holds, so only its ID and name are here. GET /api/projects/{projectId}/scenarios/{scenarioId} gives it. run_scenario plays it.");
    }

    public static async Task<CallToolResult> RunScenario(
        McpCall call,
        [Description("The ID of the project.")] Guid projectId,
        [Description("The ID of the scenario.")] Guid scenarioId,
        [Description("True to get the payload and the headers of each event. Default false.")] bool includePayloads = false,
        [Description("The nextCursor of the result before this one, to get the events after it. The same scenario on the same model version gives the same events.")] string? cursor = null,
        [Description("The most events to return: 1 to 200. Default 50.")] int? limit = null)
    {
        var after = -1;
        if (cursor is not null && !int.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out after))
            return ModelTools.Invalid("cursor", "The cursor is the nextCursor of a result of this tool");

        var reply = await call.SendAsync(new Dictionary<string, string> { ["scenarioId"] = scenarioId.ToString() });
        if (!reply.Succeeded)
            return call.Refused(reply);

        var run = reply.Json();
        var all = run["events"]!.AsArray();
        var size = McpOutput.PageSize(limit);
        var rest = all.Where(e => e!["sequence"]!.GetValue<int>() > after).ToList();
        var events = new JsonArray([.. rest.Take(size).Select(e =>
        {
            var item = e!.DeepClone().AsObject();
            if (!includePayloads)
            {
                item.Remove("payload");
                item.Remove("headers");
            }
            return item;
        })]);

        var issues = run["issues"]!.DeepClone().AsArray();
        var assertions = run["assertions"]!.DeepClone().AsArray();
        var data = new JsonObject
        {
            ["engineVersion"] = run["engineVersion"]?.DeepClone(),
            ["hash"] = run["hash"]?.DeepClone(),
            ["modelVersion"] = run["modelVersion"]?.DeepClone(),
            ["issues"] = issues,
            ["outcomes"] = run["outcomes"]?.DeepClone(),
            ["assertions"] = assertions,
            ["eventCount"] = all.Count,
            ["events"] = events,
            ["labels"] = new JsonObject(),
        };
        var capped = new[] { ("issues", McpOutput.Cap(issues)), ("assertions", McpOutput.Cap(assertions)) }
            .Where(list => list.Item2 is not null).Select(list => list.Item1).ToList();
        var cut = McpOutput.Fit(data, events);

        // The names of the IDs that the events of this result carry, and no other.
        var names = run["labels"]!.AsObject();
        var labels = data["labels"]!.AsObject();
        foreach (var id in events.SelectMany(e => new[] { e!["from"], e["to"], e["target"], e["message"] }).Select(id => id?.ToString()).OfType<string>().Distinct())
        {
            if (names[id] is { } name && McpOutput.Fits(data))
                labels[id] = name.DeepClone();
        }

        var more = rest.Count > events.Count;
        var notices = new List<string>();
        if (cut > 0)
            notices.Add($"The result was cut to fit the byte bound: {cut} fewer events than the limit. nextCursor gets the next ones.");
        if (capped.Count > 0)
            notices.Add($"These lists have more items than the result holds: {string.Join(", ", capped)}. POST /api/projects/{projectId}/scenarios/{scenarioId}/run gives the whole run.");
        return call.Ok(data,
            more && events.Count > 0 ? events[^1]!["sequence"]!.GetValue<int>().ToString(CultureInfo.InvariantCulture) : null,
            notices.Count > 0 ? string.Join(' ', notices) : null);
    }
}

/// <summary>A list that an endpoint pages: the tool passes the cursor and the limit on, and bounds the page.</summary>
internal static class Paged
{
    public const string Cursor = "The nextCursor of the result before this one. Leave it out for the first page.";
    public const string Limit = "The most items to return: 1 to 200. Default 50.";

    /// <summary>
    /// One page of the list of the tool's endpoint. A page over the byte bound is asked for
    /// again with half the limit, so the cursor is always the one the endpoint gave for the
    /// items in the result.
    /// </summary>
    public static async Task<CallToolResult> ListAsync(McpCall call, string? cursor, int? limit)
    {
        var asked = McpOutput.PageSize(limit);
        for (var size = asked; ; size /= 2)
        {
            var reply = await call.SendAsync(query: new Dictionary<string, string?>
            {
                ["cursor"] = cursor,
                ["limit"] = size.ToString(CultureInfo.InvariantCulture),
            });
            if (!reply.Succeeded)
                return call.Refused(reply);

            var page = reply.Json();
            var data = new JsonObject { ["items"] = page["items"]!.DeepClone() };
            if (McpOutput.Fits(data) || size == 1)
            {
                return call.Ok(data, page["cursor"]?.GetValue<string>(), size < asked
                    ? $"The result was cut to fit the byte bound: {size} items and not {asked}. nextCursor gets the next ones."
                    : null);
            }
        }
    }
}
