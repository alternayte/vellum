using System.Text.Json;
using System.Text.Json.Nodes;

namespace Vellum.Modules.Ai.Design;

/// <summary>
/// The flat answer a model gives for a flow. Every reference is an alias; every payload is
/// JSON text. On the wire no member is null: the model writes the empty string for a text
/// member it does not use, 0 for an unused <c>failFirst</c> and -1 for an unused
/// <c>expectedCount</c> (see <see cref="DesignSchemas"/>). <see cref="FlowAssembler.Normalize"/>
/// maps those values back to null, so a nullable member below is null after it.
/// </summary>
public sealed record FlowOutput(
    string Name, string Summary,
    IReadOnlyList<FlowStepOutput> Steps,
    IReadOnlyList<FlowFaultOutput> Faults,
    IReadOnlyList<FlowAssertionOutput> Assertions,
    IReadOnlyList<FlowMissingOutput> Missing,
    IReadOnlyList<string> OpenQuestions,
    string ReasoningSummary,
    IReadOnlyList<FlowEvidenceOutput> Evidence,
    int? StartLine = null, int? EndLine = null);

/// <param name="Slot"><c>root</c>, <c>then</c>, <c>onError</c>, <c>handler</c>, <c>branchThen</c>, <c>branchElse</c> or <c>parallelBranch</c>.</param>
/// <param name="SlotKey">The subscription alias of a handler, or the branch number of a parallel branch.</param>
/// <param name="RoutingKey">For a publish step: the key that the bindings of the channel are matched against. An answer stored before the member existed reads as null.</param>
public sealed record FlowStepOutput(
    string Id, string? ParentStepId, string Slot, string? SlotKey, int Order, string Type,
    string? From, string? Operation, string? Channel, string? Message, string? By, string? Store, string? Dataset,
    string? WhenStep, string? Is,
    string? RequestJson, string? ResponseJson, string? PayloadJson, string? ChangeJson,
    string? RoutingKey = null);

public sealed record FlowFaultOutput(string Target, string Kind, int? FailFirst);

public sealed record FlowAssertionOutput(
    string Id, string Type, string? Step, string? Expected, string? EventKind, string? Target, int? ExpectedCount);

/// <summary>A thing the flow needs that the model does not hold. <paramref name="OwnerAlias"/> is the owner of an operation or the producer of a message.</summary>
public sealed record FlowMissingOutput(
    string Alias, string Kind, string Name,
    string? ElementKind, string? ParentAlias, string? OwnerAlias, string? Transport, string? ChannelKind,
    string? ChannelAlias, string? ConsumerAlias, string Reason, double Confidence);

public sealed record FlowEvidenceOutput(string Alias, string Why);

/// <summary>The flat answer a model gives for additions to the architecture.</summary>
public sealed record ArchitectureOutput(
    string Summary,
    IReadOnlyList<ArchitectureItemOutput> Items,
    IReadOnlyList<string> OpenQuestions,
    string ReasoningSummary);

/// <param name="Action"><c>add</c>. A model that answers <c>change</c> or <c>remove</c> names the existing thing in <paramref name="Target"/>; the item is dropped.</param>
/// <param name="From">The source of a relationship (an element) or of a binding (a channel).</param>
/// <param name="To">The target of a relationship (an element) or of a binding (a channel).</param>
/// <param name="Label">What a relationship carries, or the routing key of a binding.</param>
public sealed record ArchitectureItemOutput(
    string Alias, string Action, string? Target, string EntityKind, string Name,
    string? Description, string? Technology,
    string? ElementKind, string? Parent,
    string? From, string? To, string? Label, string? RelationshipKind,
    string? Owner, string? Transport,
    string? ChannelKind,
    string? Channel, string? Consumer,
    string? Producer, IReadOnlyList<string>? Consumers,
    double Confidence, string Rationale,
    int? StartLine = null, int? EndLine = null);

/// <summary>
/// The JSON Schemas of the structured answers. They sit inside the subset that all three
/// vendors document for their structured-output mode, with margin:
/// <list type="bullet">
/// <item>Anthropic <c>output_config.format</c>: at most 16 parameters with a union type
/// (<c>anyOf</c> or a type array such as <c>["string","null"]</c>) and at most 24 optional
/// parameters in one request; no recursive schema; no length, range or array constraint;
/// every object has <c>additionalProperties: false</c>
/// (https://platform.claude.com/docs/en/build-with-claude/structured-outputs).</item>
/// <item>OpenAI strict mode: every property is required; at most 10 levels of nesting and
/// 5,000 object properties; at most 1,000 enum values
/// (https://developers.openai.com/api/docs/guides/structured-outputs).</item>
/// <item>Gemini: "a subset of the JSON Schema specification"; "very large or deeply nested
/// schemas may be rejected", with no number given
/// (https://ai.google.dev/gemini-api/docs/structured-output).</item>
/// </list>
/// So the schemas use six keywords only (<c>type</c>, <c>properties</c>, <c>required</c>,
/// <c>additionalProperties</c>, <c>items</c>, <c>enum</c>), no union type and no optional
/// property. A value that a step, a fault, an assertion or an item does not use is a
/// sentinel: the empty string for text, 0 for <c>failFirst</c> ("fail every attempt"), -1 for
/// <c>expectedCount</c>, an empty list for <c>consumers</c>. The prompt states the rule and
/// the server maps each sentinel back to null. The schemas are flat: a nested step tree
/// would need a recursive reference. <c>DesignSchemaTests</c> walks the schemas and enforces
/// the limits.
/// </summary>
internal static class DesignSchemas
{
    /// <summary>The value of <c>failFirst</c> that means "no limit": the fault fails every attempt.</summary>
    public const int NoFailFirst = 0;

    /// <summary>The value of <c>expectedCount</c> in an assertion that does not count events.</summary>
    public const int NoExpectedCount = -1;

    public static readonly JsonElement Flow = JsonSerializer.SerializeToElement(FlowSchema());

    /// <summary>The flow answer for a flow read from a document: it also names the lines the flow comes from.</summary>
    public static readonly JsonElement FlowFromDocument = JsonSerializer.SerializeToElement(WithLines(FlowSchema()));

    private static JsonObject WithLines(JsonObject schema)
    {
        schema["properties"]!["startLine"] = Type("integer");
        schema["properties"]!["endLine"] = Type("integer");
        schema["required"]!.AsArray().Add("startLine");
        schema["required"]!.AsArray().Add("endLine");
        return schema;
    }

    private static JsonObject FlowSchema() => Object(
        ("name", Text()),
        ("summary", Text()),
        ("steps", Array(Object(
            ("id", Text()),
            ("parentStepId", Text()),
            ("slot", Enum("root", "then", "onError", "handler", "branchThen", "branchElse", "parallelBranch")),
            ("slotKey", Text()),
            ("order", Type("integer")),
            ("type", Enum("call", "publish", "write", "parallel", "branch")),
            ("from", Text()),
            ("operation", Text()),
            ("channel", Text()),
            ("message", Text()),
            ("by", Text()),
            ("store", Text()),
            ("dataset", Text()),
            ("whenStep", Text()),
            ("is", Text()),
            ("requestJson", Text()),
            ("responseJson", Text()),
            ("payloadJson", Text()),
            ("changeJson", Text()),
            ("routingKey", Text())))),
        ("faults", Array(Object(
            ("target", Text()),
            ("kind", Enum("error", "timeout", "outage")),
            ("failFirst", Type("integer"))))),
        ("assertions", Array(Object(
            ("id", Text()),
            ("type", Enum("outcome", "event-count")),
            ("step", Text()),
            ("expected", Text()),
            ("eventKind", Text()),
            ("target", Text()),
            ("expectedCount", Type("integer"))))),
        ("missing", Array(Object(
            ("alias", Text()),
            ("kind", Enum("element", "operation", "channel", "subscription", "message")),
            ("name", Text()),
            ("elementKind", Text()),
            ("parentAlias", Text()),
            ("ownerAlias", Text()),
            ("transport", Text()),
            ("channelKind", Text()),
            ("channelAlias", Text()),
            ("consumerAlias", Text()),
            ("reason", Text()),
            ("confidence", Type("number"))))),
        ("openQuestions", Array(Text())),
        ("reasoningSummary", Text()),
        ("evidence", Array(Object(("alias", Text()), ("why", Text())))));

    public static readonly JsonElement Architecture = JsonSerializer.SerializeToElement(ArchitectureSchema(cited: false));

    /// <summary>The answer for additions read from a document: each item also names the lines it comes from.</summary>
    public static readonly JsonElement ArchitectureFromDocument = JsonSerializer.SerializeToElement(ArchitectureSchema(cited: true));

    private static JsonObject ArchitectureSchema(bool cited) => Object(
        ("summary", Text()),
        ("items", Array(Object([.. ItemProperties(), .. cited ? new[] { ("startLine", Type("integer")), ("endLine", Type("integer")) } : []]))),
        ("openQuestions", Array(Text())),
        ("reasoningSummary", Text()));

    private static (string Name, JsonObject Schema)[] ItemProperties() =>
    [
            ("alias", Text()),
            ("action", Enum("add", "change", "remove")),
            ("target", Text()),
            ("entityKind", Enum("element", "relationship", "operation", "channel", "subscription", "binding", "message")),
            ("name", Text()),
            ("description", Text()),
            ("technology", Text()),
            ("elementKind", Text()),
            ("parent", Text()),
            ("from", Text()),
            ("to", Text()),
            ("label", Text()),
            ("relationshipKind", Text()),
            ("owner", Text()),
            ("transport", Text()),
            ("channelKind", Text()),
            ("channel", Text()),
            ("consumer", Text()),
            ("producer", Text()),
            ("consumers", Array(Text())),
            ("confidence", Type("number")),
            ("rationale", Text()),
    ];

    private static JsonObject Object(params (string Name, JsonObject Schema)[] properties)
    {
        var listed = new JsonObject();
        var required = new JsonArray();
        foreach (var (name, schema) in properties)
        {
            listed[name] = schema;
            required.Add(name);
        }

        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = listed,
            ["required"] = required,
            ["additionalProperties"] = false,
        };
    }

    private static JsonObject Array(JsonObject items) => new() { ["type"] = "array", ["items"] = items };

    private static JsonObject Type(string type) => new() { ["type"] = type };

    private static JsonObject Text() => Type("string");

    private static JsonObject Enum(params string[] values) => new()
    {
        ["type"] = "string",
        ["enum"] = new JsonArray([.. values.Select(v => (JsonNode?)v)]),
    };
}
