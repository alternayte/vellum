using System.Text.Json;
using System.Text.Json.Nodes;

namespace Vellum.Modules.Ai.Design;

/// <summary>The flat answer a model gives for a flow. Every reference is an alias; every payload is JSON text.</summary>
public sealed record FlowOutput(
    string Name, string Summary,
    IReadOnlyList<FlowStepOutput> Steps,
    IReadOnlyList<FlowFaultOutput> Faults,
    IReadOnlyList<FlowAssertionOutput> Assertions,
    IReadOnlyList<FlowMissingOutput> Missing,
    IReadOnlyList<string> OpenQuestions,
    string ReasoningSummary,
    IReadOnlyList<FlowEvidenceOutput> Evidence);

/// <param name="Slot"><c>root</c>, <c>then</c>, <c>onError</c>, <c>handler</c>, <c>branchThen</c>, <c>branchElse</c> or <c>parallelBranch</c>.</param>
/// <param name="SlotKey">The subscription alias of a handler, or the branch number of a parallel branch.</param>
public sealed record FlowStepOutput(
    string Id, string? ParentStepId, string Slot, string? SlotKey, int Order, string Type,
    string? From, string? Operation, string? Channel, string? Message, string? By, string? Store, string? Dataset,
    string? WhenStep, string? Is,
    string? RequestJson, string? ResponseJson, string? PayloadJson, string? ChangeJson);

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
public sealed record ArchitectureItemOutput(
    string Alias, string Action, string? Target, string EntityKind, string Name,
    string? Description, string? Technology,
    string? ElementKind, string? Parent,
    string? From, string? To, string? Label, string? RelationshipKind,
    string? Owner, string? Transport,
    string? ChannelKind,
    string? Channel, string? Consumer,
    string? Producer, IReadOnlyList<string>? Consumers,
    double Confidence, string Rationale);

/// <summary>
/// The JSON Schemas of the two structured answers. They stay inside the subset that every
/// provider accepts: each object is closed and lists all its properties as required, an
/// optional value is a nullable type, and no length, range, pattern or format keyword is used.
/// The schemas are flat: a nested step tree would need a recursive reference.
/// </summary>
internal static class DesignSchemas
{
    public static readonly JsonElement Flow = JsonSerializer.SerializeToElement(Object(
        ("name", Text()),
        ("summary", Text()),
        ("steps", Array(Object(
            ("id", Text()),
            ("parentStepId", Optional("string")),
            ("slot", Enum("root", "then", "onError", "handler", "branchThen", "branchElse", "parallelBranch")),
            ("slotKey", Optional("string")),
            ("order", Type("integer")),
            ("type", Enum("call", "publish", "write", "parallel", "branch")),
            ("from", Optional("string")),
            ("operation", Optional("string")),
            ("channel", Optional("string")),
            ("message", Optional("string")),
            ("by", Optional("string")),
            ("store", Optional("string")),
            ("dataset", Optional("string")),
            ("whenStep", Optional("string")),
            ("is", Optional("string")),
            ("requestJson", Optional("string")),
            ("responseJson", Optional("string")),
            ("payloadJson", Optional("string")),
            ("changeJson", Optional("string"))))),
        ("faults", Array(Object(
            ("target", Text()),
            ("kind", Enum("error", "timeout", "outage")),
            ("failFirst", Optional("integer"))))),
        ("assertions", Array(Object(
            ("id", Text()),
            ("type", Enum("outcome", "event-count")),
            ("step", Optional("string")),
            ("expected", Optional("string")),
            ("eventKind", Optional("string")),
            ("target", Optional("string")),
            ("expectedCount", Optional("integer"))))),
        ("missing", Array(Object(
            ("alias", Text()),
            ("kind", Enum("element", "operation", "channel", "subscription", "message")),
            ("name", Text()),
            ("elementKind", Optional("string")),
            ("parentAlias", Optional("string")),
            ("ownerAlias", Optional("string")),
            ("transport", Optional("string")),
            ("channelKind", Optional("string")),
            ("channelAlias", Optional("string")),
            ("consumerAlias", Optional("string")),
            ("reason", Text()),
            ("confidence", Type("number"))))),
        ("openQuestions", Array(Text())),
        ("reasoningSummary", Text()),
        ("evidence", Array(Object(("alias", Text()), ("why", Text()))))));

    public static readonly JsonElement Architecture = JsonSerializer.SerializeToElement(Object(
        ("summary", Text()),
        ("items", Array(Object(
            ("alias", Text()),
            ("action", Enum("add", "change", "remove")),
            ("target", Optional("string")),
            ("entityKind", Enum("element", "relationship", "operation", "channel", "subscription", "message")),
            ("name", Text()),
            ("description", Optional("string")),
            ("technology", Optional("string")),
            ("elementKind", Optional("string")),
            ("parent", Optional("string")),
            ("from", Optional("string")),
            ("to", Optional("string")),
            ("label", Optional("string")),
            ("relationshipKind", Optional("string")),
            ("owner", Optional("string")),
            ("transport", Optional("string")),
            ("channelKind", Optional("string")),
            ("channel", Optional("string")),
            ("consumer", Optional("string")),
            ("producer", Optional("string")),
            ("consumers", new JsonObject { ["type"] = new JsonArray("array", "null"), ["items"] = Text() }),
            ("confidence", Type("number")),
            ("rationale", Text())))),
        ("openQuestions", Array(Text())),
        ("reasoningSummary", Text())));

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

    private static JsonObject Optional(string type) => new() { ["type"] = new JsonArray(type, "null") };

    private static JsonObject Enum(params string[] values) => new()
    {
        ["type"] = "string",
        ["enum"] = new JsonArray([.. values.Select(v => (JsonNode?)v)]),
    };
}
