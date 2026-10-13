using System.Text;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;

namespace Vellum.Modules.Mcp;

/// <summary>
/// The bounds and the two shapes of every tool result. A result is one JSON document as
/// text: the result of <see cref="McpCall.Ok"/>, or <c>{ "error": { type, title, detail,
/// status, … } }</c> with <c>isError</c> set.
/// </summary>
public static class McpOutput
{
    /// <summary>The most bytes of UTF-8 one tool result holds. A larger result is cut, with a notice, or refused.</summary>
    public const int MaxBytes = 100_000;

    /// <summary>What the members around <c>data</c> may take of <see cref="MaxBytes"/>.</summary>
    private const int EnvelopeBytes = 2_000;

    /// <summary>The page sizes of the HTTP lists.</summary>
    public const int DefaultPage = 50;
    public const int MaxPage = 200;

    /// <summary>The most items of a list that has no cursor, such as the relationships of one element.</summary>
    public const int MaxListed = 200;

    public const string ResultTooLarge = "result_too_large";

    public static int PageSize(int? limit) => Math.Clamp(limit ?? DefaultPage, 1, MaxPage);

    public static int Bytes(JsonNode node) => Encoding.UTF8.GetByteCount(node.ToJsonString());

    /// <summary>True when <paramref name="data"/> fits in one result.</summary>
    public static bool Fits(JsonNode data) => Bytes(data) <= MaxBytes - EnvelopeBytes;

    /// <summary>
    /// Removes items from the end of <paramref name="items"/>, a list inside
    /// <paramref name="data"/>, until <paramref name="data"/> fits in one result. Returns how
    /// many it removed. The caller gives the cursor of the last item that stayed.
    /// </summary>
    public static int Fit(JsonObject data, JsonArray items)
    {
        var over = Bytes(data) - (MaxBytes - EnvelopeBytes);
        var removed = 0;
        while (over > 0 && items.Count > 0)
        {
            // An item and the comma before it.
            over -= Bytes(items[^1]!) + 1;
            items.RemoveAt(items.Count - 1);
            removed++;
        }
        return removed;
    }

    /// <summary>
    /// Keeps the first <paramref name="keep"/> items of a list that has no cursor. Returns the
    /// number the list had when it was longer, else null.
    /// </summary>
    public static int? Cap(JsonArray items, int keep = MaxListed)
    {
        if (items.Count <= keep)
            return null;
        var total = items.Count;
        while (items.Count > keep)
            items.RemoveAt(items.Count - 1);
        return total;
    }

    /// <summary>One JSON document as the text of a result that worked. A document over the bound becomes <see cref="TooLarge"/>.</summary>
    public static CallToolResult Text(string text) =>
        Encoding.UTF8.GetByteCount(text) > MaxBytes
            ? TooLarge()
            : new CallToolResult { Content = [new TextContentBlock { Text = text }] };

    public static CallToolResult TooLarge() => Error(ResultTooLarge, $"The result is larger than {MaxBytes} bytes",
        "Ask for less: a smaller limit, one item by its ID, or the HTTP endpoint the agent reference names for this tool.");

    /// <summary>
    /// A tool error. <paramref name="document"/> is the error document of an endpoint, whose
    /// other members (the field errors, the current revision of a draft) are kept while the
    /// error stays inside the bound.
    /// </summary>
    public static CallToolResult Error(string type, string title, string? detail = null, int? status = null, JsonObject? document = null)
    {
        JsonObject Write(JsonObject error)
        {
            error["type"] = type;
            error["title"] = title;
            if (detail is not null)
                error["detail"] = detail;
            if (status is not null)
                error["status"] = status;
            return new JsonObject { ["error"] = error };
        }

        var text = Write(document?.DeepClone().AsObject() ?? new JsonObject()).ToJsonString();
        if (Encoding.UTF8.GetByteCount(text) > MaxBytes)
            text = Write(new JsonObject()).ToJsonString();
        return new CallToolResult { IsError = true, Content = [new TextContentBlock { Text = text }] };
    }

    /// <summary>The error type of a status that came with no type.</summary>
    public static string TypeOf(int status) => status switch
    {
        StatusCodes.Status400BadRequest => "invalid_request",
        StatusCodes.Status403Forbidden => "forbidden",
        StatusCodes.Status404NotFound => "not_found",
        StatusCodes.Status409Conflict => "conflict",
        StatusCodes.Status413PayloadTooLarge => "payload_too_large",
        StatusCodes.Status429TooManyRequests => "rate_limited",
        _ => "failed",
    };

    /// <summary>
    /// The error types a tool result can hold that are not the answer of one endpoint. An
    /// endpoint adds its own (<c>conflict</c>, <c>no_approved_revision</c>, …): a tool passes
    /// them on unchanged.
    /// </summary>
    public static readonly IReadOnlyList<(string Type, string Meaning)> ErrorTypes =
    [
        ("agent_not_allowed", "The token lacks the scope of the tool, is restricted to another project, or its user left the workspace."),
        ("not_found", "The project, or the thing the call names, does not exist or is outside the workspace of the token. The two cases get the same answer."),
        ("validation_error", "An argument is missing or is not valid. `errors` names each field."),
        ("invalid_request", "The arguments are not what the endpoint takes."),
        ("forbidden", "The user the token acts for has a role in the workspace that is too low for this."),
        ("conflict", "The state does not allow the call. `title` and `detail` say why."),
        ("payload_too_large", "An argument is larger than the endpoint takes."),
        ("rate_limited", "Too many calls in one minute. `detail` says how long to wait."),
        (ResultTooLarge, $"The result would be larger than {MaxBytes} bytes and cannot be cut. Ask for less, or use the HTTP endpoint."),
        ("failed", "The server failed. The call changed nothing that the result does not name."),
    ];
}
