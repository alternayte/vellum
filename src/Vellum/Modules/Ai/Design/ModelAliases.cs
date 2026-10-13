using System.Text.Json;
using System.Text.Json.Nodes;
using Vellum.Modules.Modelling.Model;

namespace Vellum.Modules.Ai.Design;

/// <summary>What an alias names. <paramref name="Kind"/> is one of <see cref="AliasKinds"/>.</summary>
public sealed record AliasRef(string Kind, Guid Id, string Name);

internal static class AliasKinds
{
    public const string Element = "element";
    public const string Operation = "operation";
    public const string Channel = "channel";
    public const string Subscription = "subscription";
    public const string Message = "message";
    public const string Relationship = "relationship";

    public static readonly string[] Contracts = [Element, Operation, Channel, Subscription, Message];

    public static string WithArticle(string kind) => kind is Element or Operation ? $"an {kind}" : $"a {kind}";
}

/// <summary>
/// The names a model may use for the things of one branch, built for one request. A provider
/// never receives an ID of the model and never returns one: it reads and writes aliases
/// (<c>E1</c> for an element, <c>O1</c> operation, <c>C1</c> channel, <c>S1</c> subscription,
/// <c>M1</c> message), and the server maps each alias back. An alias that is not in the table
/// names nothing. The order is by name and then by ID, so the same model gives the same table.
/// </summary>
internal sealed class ModelAliases
{
    /// <summary>The most entries of one kind the table holds. A larger model is cut, and the table says so.</summary>
    public const int MaxPerKind = 500;
    private const int MaxSchemaProperties = 30;

    private readonly Dictionary<string, AliasRef> _byAlias = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, string> _byId = [];

    private ModelAliases(string table) => Table = table;

    /// <summary>The table as JSON text. It is model data: it goes into an untrusted part of the prompt.</summary>
    public string Table { get; }

    public IReadOnlyDictionary<string, AliasRef> ByAlias => _byAlias;

    public string? AliasOf(Guid id) => _byId.GetValueOrDefault(id);

    public AliasRef? Find(string? alias) => alias is not null && _byAlias.TryGetValue(alias, out var found) ? found : null;

    /// <param name="schemas">The content of the project's schemas that the model names.</param>
    public static ModelAliases Build(ModelState model, IReadOnlyDictionary<SchemaId, string> schemas)
    {
        var elements = model.Elements.Values.OrderBy(e => e.Name, StringComparer.Ordinal).ThenBy(e => e.Id.Value).Take(MaxPerKind).ToList();
        var operations = model.Operations.Values.OrderBy(o => o.Name, StringComparer.Ordinal).ThenBy(o => o.Id.Value).Take(MaxPerKind).ToList();
        var channels = model.Channels.Values.OrderBy(c => c.Name, StringComparer.Ordinal).ThenBy(c => c.Id.Value).Take(MaxPerKind).ToList();
        var subscriptions = model.Subscriptions.Values
            .Select(s => (Subscription: s, Label: ContractDecider.SubscriptionLabel(model, s)))
            .OrderBy(s => s.Label, StringComparer.Ordinal).ThenBy(s => s.Subscription.Id.Value).Take(MaxPerKind).ToList();
        var messages = model.Messages.Values.OrderBy(m => m.Name, StringComparer.Ordinal).ThenBy(m => m.Id.Value).Take(MaxPerKind).ToList();

        var aliases = new Dictionary<string, AliasRef>(StringComparer.Ordinal);
        var ids = new Dictionary<Guid, string>();
        void Name(string prefix, int index, string kind, Guid id, string name)
        {
            var alias = $"{prefix}{index + 1}";
            aliases[alias] = new AliasRef(kind, id, name);
            ids[id] = alias;
        }

        for (var i = 0; i < elements.Count; i++) Name("E", i, AliasKinds.Element, elements[i].Id, elements[i].Name);
        for (var i = 0; i < operations.Count; i++) Name("O", i, AliasKinds.Operation, operations[i].Id, operations[i].Name);
        for (var i = 0; i < channels.Count; i++) Name("C", i, AliasKinds.Channel, channels[i].Id, channels[i].Name);
        for (var i = 0; i < subscriptions.Count; i++) Name("S", i, AliasKinds.Subscription, subscriptions[i].Subscription.Id, subscriptions[i].Label);
        for (var i = 0; i < messages.Count; i++) Name("M", i, AliasKinds.Message, messages[i].Id, messages[i].Name);

        // A reference to a thing that the table was cut before has no alias and is left out.
        JsonNode? Ref(Guid? id) => id is { } value && ids.TryGetValue(value, out var alias) ? alias : null;
        JsonNode? Summary(SchemaId? id) => id is { } value && schemas.TryGetValue(value, out var content) ? SchemaSummary(content) : null;

        var table = new JsonObject
        {
            ["elements"] = new JsonArray([.. elements.Select(e => (JsonNode)new JsonObject
            {
                ["alias"] = ids[e.Id],
                ["kind"] = e.Kind.ToString().ToLowerInvariant(),
                ["name"] = e.Name,
                ["parent"] = Ref(e.ParentId?.Value),
                ["technology"] = e.Technology,
                ["description"] = e.Description is { Length: > 300 } long_ ? long_[..300] : e.Description,
            })]),
            ["operations"] = new JsonArray([.. operations.Select(o => (JsonNode)new JsonObject
            {
                ["alias"] = ids[o.Id],
                ["name"] = o.Name,
                ["owner"] = Ref(o.OwnerId),
                ["transport"] = o.Transport.ToString().ToLowerInvariant(),
                ["timeoutMs"] = o.TimeoutMs,
                ["maxAttempts"] = o.Retry.MaxAttempts,
                ["requestSchema"] = Summary(o.RequestSchemaId),
                ["responseSchema"] = Summary(o.ResponseSchemaId),
            })]),
            ["channels"] = new JsonArray([.. channels.Select(c => (JsonNode)new JsonObject
            {
                ["alias"] = ids[c.Id],
                ["name"] = c.Name,
                ["kind"] = c.Kind.ToString().ToLowerInvariant(),
            })]),
            ["subscriptions"] = new JsonArray([.. subscriptions.Select(s => (JsonNode)new JsonObject
            {
                ["alias"] = ids[s.Subscription.Id],
                ["channel"] = Ref(s.Subscription.ChannelId),
                ["consumer"] = Ref(s.Subscription.ConsumerId),
                ["maxAttempts"] = s.Subscription.Retry.MaxAttempts,
                ["deadLetterChannel"] = Ref(s.Subscription.DeadLetterChannelId?.Value),
            })]),
            ["messages"] = new JsonArray([.. messages.Select(m => (JsonNode)new JsonObject
            {
                ["alias"] = ids[m.Id],
                ["name"] = m.Name,
                ["producer"] = Ref(m.ProducerId),
                ["consumers"] = new JsonArray([.. m.ConsumerIds.Select(c => Ref(c)).Where(c => c is not null)]),
                ["schema"] = Summary(m.SchemaId),
            })]),
            ["relationships"] = new JsonArray([.. model.Relationships.Values
                .Where(r => ids.ContainsKey(r.FromId) && ids.ContainsKey(r.ToId))
                .OrderBy(r => ids[r.FromId], StringComparer.Ordinal).ThenBy(r => ids[r.ToId], StringComparer.Ordinal).ThenBy(r => r.Id.Value)
                .Take(MaxPerKind)
                .Select(r => (JsonNode)new JsonObject
                {
                    ["from"] = ids[r.FromId],
                    ["to"] = ids[r.ToId],
                    ["label"] = r.Label,
                    ["kind"] = r.Kind.ToString().ToLowerInvariant(),
                })]),
        };

        var omitted = new JsonObject();
        void Omitted(string kind, int total, int listed)
        {
            if (total > listed) omitted[kind] = total - listed;
        }
        Omitted("elements", model.Elements.Count, elements.Count);
        Omitted("operations", model.Operations.Count, operations.Count);
        Omitted("channels", model.Channels.Count, channels.Count);
        Omitted("subscriptions", model.Subscriptions.Count, subscriptions.Count);
        Omitted("messages", model.Messages.Count, messages.Count);
        if (omitted.Count > 0)
            table["omittedBecauseTheModelIsLarge"] = omitted;

        var result = new ModelAliases(table.ToJsonString());
        foreach (var (alias, named) in aliases) result._byAlias[alias] = named;
        foreach (var (id, alias) in ids) result._byId[id] = alias;
        return result;
    }

    /// <summary>
    /// The top level of a JSON Schema: its type, its required properties and the type of each
    /// property. Enough for a model to write a payload that fits. Null when the text is not a
    /// JSON object.
    /// </summary>
    private static JsonObject? SchemaSummary(string content)
    {
        try
        {
            using var document = JsonDocument.Parse(content, new JsonDocumentOptions { MaxDepth = 64 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;

            var summary = new JsonObject();
            if (root.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String)
                summary["type"] = type.GetString();
            if (root.TryGetProperty("required", out var required) && required.ValueKind == JsonValueKind.Array)
            {
                summary["required"] = new JsonArray([.. required.EnumerateArray()
                    .Where(r => r.ValueKind == JsonValueKind.String)
                    .Take(MaxSchemaProperties)
                    .Select(r => (JsonNode?)DesignText.Line(r.GetString(), 100))]);
            }
            if (root.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object)
            {
                var listed = new JsonObject();
                foreach (var property in properties.EnumerateObject().Take(MaxSchemaProperties))
                {
                    var name = DesignText.Line(property.Name, 100);
                    if (name.Length == 0 || listed.ContainsKey(name))
                        continue;
                    listed[name] = property.Value.ValueKind == JsonValueKind.Object
                        && property.Value.TryGetProperty("type", out var propertyType) && propertyType.ValueKind == JsonValueKind.String
                        ? DesignText.Line(propertyType.GetString(), 40)
                        : "any";
                }
                summary["properties"] = listed;
            }

            return summary;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return null;
        }
    }
}
