using System.ComponentModel;
using System.Globalization;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using Vellum.Shared;

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

        var notices = new List<string>();
        if (events.Count == 0 && rest.Count > 0)
        {
            // The first event alone is over the bound. It comes without its payload and its
            // headers, so that the result holds an event and a cursor that goes past it.
            var first = rest[0]!.DeepClone().AsObject();
            first.Remove("payload");
            first.Remove("headers");
            events.Add(first);
            if (!McpOutput.Fits(data))
                first.Remove("detail");
            notices.Add(
                $"Event {first["sequence"]} is larger than one result holds, so it is here without its payload, its headers{(first.ContainsKey("detail") ? "" : " and its detail")}. POST /api/projects/{projectId}/scenarios/{scenarioId}/run gives the whole event; the same token works there. nextCursor gets the events after it.");
        }
        else if (cut > 0)
        {
            notices.Add($"The result was cut to fit the byte bound: {cut} fewer events than the limit. nextCursor gets the next ones.");
        }

        // The names of the IDs that the events of this result carry, and no other.
        var names = run["labels"]!.AsObject();
        var labels = data["labels"]!.AsObject();
        foreach (var id in events.SelectMany(e => new[] { e!["from"], e["to"], e["target"], e["message"] }).Select(id => id?.ToString()).OfType<string>().Distinct())
        {
            if (names[id] is { } name && McpOutput.Fits(data))
                labels[id] = name.DeepClone();
        }

        var more = rest.Count > events.Count;
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

    /// <summary>The longest text member that stays in an item that is over the byte bound by itself.</summary>
    private const int ShortMember = 500;

    /// <summary>
    /// One page of the list of the tool's endpoint. A page over the byte bound is asked for
    /// again with half the limit, so the cursor is always the one the endpoint gave for the
    /// items in the result. One item that is over the bound by itself comes with its short
    /// members only (<see cref="Reduce"/>), so the cursor still goes past it.
    /// </summary>
    /// <param name="whole">The HTTP request that gives an item whole, for the notice.</param>
    public static async Task<CallToolResult> ListAsync(
        McpCall call, string? cursor, int? limit,
        IReadOnlyDictionary<string, string>? values = null, IReadOnlyDictionary<string, string?>? filter = null, string? whole = null)
    {
        var asked = McpOutput.PageSize(limit);
        for (var size = asked; ; size /= 2)
        {
            var query = new Dictionary<string, string?>(filter ?? new Dictionary<string, string?>())
            {
                ["cursor"] = cursor,
                ["limit"] = size.ToString(CultureInfo.InvariantCulture),
            };
            var reply = await call.SendAsync(values, query);
            if (!reply.Succeeded)
                return call.Refused(reply);

            var page = reply.Json();
            var items = page["items"]!.DeepClone().AsArray();
            var data = new JsonObject { ["items"] = items };
            var next = page["cursor"]?.GetValue<string>();
            if (McpOutput.Fits(data))
            {
                return call.Ok(data, next, size < asked
                    ? $"The result was cut to fit the byte bound: {size} items and not {asked}. nextCursor gets the next ones."
                    : null);
            }
            if (size == 1)
                return call.Ok(data, next, Reduce(items, 0, whole));
        }
    }

    /// <summary>
    /// One page of a list that the endpoint gives whole: the tool pages it by position. The
    /// cursor is the position of the first item of the next page. <c>total</c> is the number
    /// of items the list has.
    /// </summary>
    /// <param name="data">The other members of the result. The page goes into <paramref name="member"/>.</param>
    /// <param name="whole">The HTTP request that gives an item whole, for the notice.</param>
    public static CallToolResult Local(McpCall call, JsonObject data, string member, JsonArray all, string? cursor, int? limit, string whole)
    {
        var start = 0;
        if (cursor is not null && (!int.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out start) || start > all.Count))
            return ModelTools.Invalid(InvalidCursorException.Field, InvalidCursorException.Text);

        var size = McpOutput.PageSize(limit);
        var items = new JsonArray([.. all.Skip(start).Take(size).Select(item => item!.DeepClone())]);
        data["total"] = all.Count;
        data[member] = items;
        var asked = items.Count;
        var cut = McpOutput.Fit(data, items);

        string? notice = null;
        if (items.Count == 0 && asked > 0)
        {
            items.Add(all[start]!.DeepClone());
            notice = Reduce(items, start, whole);
        }
        else if (cut > 0)
        {
            notice = $"The result was cut to fit the byte bound: {cut} fewer {member} than the limit. nextCursor gets the next ones.";
        }

        var end = start + items.Count;
        return call.Ok(data, end < all.Count ? end.ToString(CultureInfo.InvariantCulture) : null, notice);
    }

    /// <summary>
    /// Makes the one item of <paramref name="items"/> fit: its lists, its objects and its long
    /// texts are removed, and its short members stay, so that a caller can name it and ask
    /// for it over HTTP. Returns the notice, which says so.
    /// </summary>
    private static string Reduce(JsonArray items, int position, string? whole)
    {
        if (items[0] is JsonObject item)
        {
            foreach (var (name, value) in item.ToList())
            {
                if (value is JsonObject or JsonArray || (value is JsonValue text && text.TryGetValue<string>(out var s) && s.Length > ShortMember))
                    item.Remove(name);
            }
        }

        return $"Item {position} is larger than one result holds, so only its short members are here: its lists, its objects and its long texts are left out. "
            + $"{(whole is null ? "The HTTP endpoint of the tool" : whole)} gives it whole; the same token works there. nextCursor gets the items after it.";
    }
}
