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

/// <summary>How many things of each kind the alias table of a request left out to stay inside its size.</summary>
public sealed record AliasOmitted(int Elements, int Operations, int Channels, int Subscriptions, int Messages, int Relationships)
{
    public static readonly AliasOmitted None = new(0, 0, 0, 0, 0, 0);

    internal int Total => Elements + Operations + Channels + Subscriptions + Messages + Relationships;
}

/// <summary>What a request needs from the alias table, in the order in which the table is filled.</summary>
/// <param name="Required">Things the request cannot do without: every reference of the scenario that is revised. Always listed.</param>
/// <param name="Selected">The canvas selection that the request names.</param>
/// <param name="RequestText">The person's request. A thing whose name shares a whole word with it comes before the rest.</param>
internal sealed record AliasNeeds(IReadOnlySet<Guid> Required, IReadOnlySet<Guid> Selected, string RequestText)
{
    public static AliasNeeds For(string requestText, IEnumerable<Guid>? required = null, IEnumerable<Guid>? selected = null) =>
        new((required ?? []).ToHashSet(), (selected ?? []).ToHashSet(), requestText);
}

/// <summary>
/// The names a model may use for the things of one branch, built for one request. A provider
/// never receives an ID of the model and never returns one: it reads and writes aliases
/// (<c>E1</c> for an element, <c>O1</c> operation, <c>C1</c> channel, <c>S1</c> subscription,
/// <c>M1</c> message), and the server maps each alias back. An alias that is not in the table
/// names nothing. The aliases are numbered by name and then by ID among the listed things,
/// so the same model and the same request give the same table.
/// <para>
/// <b>Size.</b> The table holds at most <see cref="MaxTableBytes"/> bytes of entries. It is
/// filled in this order: what the request requires (always, whatever the size), the canvas
/// selection, the things whose name shares a whole word with the request text (more shared
/// words first), then the rest by name. The owner, channel, consumer, producer or parent of a
/// listed thing of the first three groups is listed with it. What does not fit is counted in
/// <see cref="Omitted"/>, and the table says so in <c>omitted</c>.
/// </para>
/// <para>
/// <b>Private sources.</b> A thing that <see cref="PrivateGate.Hides"/> is not listed, whatever
/// the request requires, and the summary of a hidden schema is left out. The count is
/// <see cref="OmittedPrivateCount"/>, and the table holds it as <c>withheld</c>.
/// </para>
/// </summary>
internal sealed class ModelAliases
{
    /// <summary>The most bytes of entries in one table. An entry is measured as it is written, with the longest alias.</summary>
    public const int MaxTableBytes = 192 * 1024;
    private const int MaxSchemaProperties = 30;
    private const int MaxDescriptionChars = 300;
    private const int MinWordChars = 3;
    // Longer than any alias the table gives, so an entry measured with it is not larger when written.
    private const string WidestAlias = "E9999999";

    private static readonly (string Name, string Kind, string Prefix)[] Sections =
    [
        ("elements", AliasKinds.Element, "E"),
        ("operations", AliasKinds.Operation, "O"),
        ("channels", AliasKinds.Channel, "C"),
        ("subscriptions", AliasKinds.Subscription, "S"),
        ("messages", AliasKinds.Message, "M"),
    ];

    private readonly Dictionary<string, AliasRef> _byAlias = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, string> _byId = [];

    private ModelAliases(string table) => Table = table;

    /// <summary>The table as JSON text. It is model data: it goes into an untrusted part of the prompt.</summary>
    public string Table { get; }

    public IReadOnlyDictionary<string, AliasRef> ByAlias => _byAlias;

    public AliasOmitted Omitted { get; private init; } = AliasOmitted.None;

    public int OmittedPrivateCount { get; private init; }

    /// <summary>The class of the prompt part that holds the table: private when a private-sourced thing is listed.</summary>
    public AiDataClass DataClass { get; private init; } = AiDataClass.Public;

    public string? AliasOf(Guid id) => _byId.GetValueOrDefault(id);

    public AliasRef? Find(string? alias) => alias is not null && _byAlias.TryGetValue(alias, out var found) ? found : null;

    /// <param name="Write">Writes the entry. The first argument gives the alias of a referenced thing, the second is the entry's own alias.</param>
    private sealed record Entry(string Kind, Guid Id, string Name, Guid[] Related, Func<Func<Guid?, JsonNode?>, string, JsonObject> Write);

    /// <param name="schemas">The content of the project's schemas that the model names.</param>
    public static ModelAliases Build(
        ModelState model, IReadOnlyDictionary<SchemaId, string> schemas, AliasNeeds needs, PrivateGate gate)
    {
        var hiddenSchemas = new HashSet<Guid>();
        var shownSchemas = new HashSet<Guid>();
        JsonNode? Summary(SchemaId? id)
        {
            if (id is not { } value || !schemas.TryGetValue(value, out var content))
                return null;
            if (gate.Hides(value))
            {
                hiddenSchemas.Add(value);
                return null;
            }

            shownSchemas.Add(value);
            return SchemaSummary(content);
        }

        var all = new List<Entry>();
        all.AddRange(model.Elements.Values.Select(e => new Entry(AliasKinds.Element, e.Id, e.Name,
            e.ParentId is { } parent ? [parent] : [],
            (alias, own) => new JsonObject
            {
                ["alias"] = own,
                ["kind"] = e.Kind.ToString().ToLowerInvariant(),
                ["name"] = e.Name,
                ["parent"] = alias(e.ParentId?.Value),
                ["technology"] = e.Technology,
                ["description"] = e.Description is { Length: > MaxDescriptionChars } text ? text[..MaxDescriptionChars] : e.Description,
            })));
        all.AddRange(model.Operations.Values.Select(o => new Entry(AliasKinds.Operation, o.Id, o.Name, [o.OwnerId],
            (alias, own) => new JsonObject
            {
                ["alias"] = own,
                ["name"] = o.Name,
                ["owner"] = alias(o.OwnerId),
                ["transport"] = o.Transport.ToString().ToLowerInvariant(),
                ["timeoutMs"] = o.TimeoutMs,
                ["maxAttempts"] = o.Retry.MaxAttempts,
                ["requestSchema"] = Summary(o.RequestSchemaId),
                ["responseSchema"] = Summary(o.ResponseSchemaId),
            })));
        all.AddRange(model.Channels.Values.Select(c => new Entry(AliasKinds.Channel, c.Id, c.Name, [],
            (_, own) => new JsonObject
            {
                ["alias"] = own,
                ["name"] = c.Name,
                ["kind"] = c.Kind.ToString().ToLowerInvariant(),
            })));
        all.AddRange(model.Subscriptions.Values.Select(s => new Entry(
            AliasKinds.Subscription, s.Id, ContractDecider.SubscriptionLabel(model, s), [s.ChannelId, s.ConsumerId],
            (alias, own) => new JsonObject
            {
                ["alias"] = own,
                ["channel"] = alias(s.ChannelId),
                ["consumer"] = alias(s.ConsumerId),
                ["maxAttempts"] = s.Retry.MaxAttempts,
                ["deadLetterChannel"] = alias(s.DeadLetterChannelId?.Value),
            })));
        all.AddRange(model.Messages.Values.Select(m => new Entry(AliasKinds.Message, m.Id, m.Name, [m.ProducerId],
            (alias, own) => new JsonObject
            {
                ["alias"] = own,
                ["name"] = m.Name,
                ["producer"] = alias(m.ProducerId),
                ["consumers"] = new JsonArray([.. m.ConsumerIds.Select(c => alias(c)).Where(c => c is not null)]),
                ["schema"] = Summary(m.SchemaId),
            })));

        var hidden = all.Where(e => gate.Hides(e.Id)).Select(e => e.Id).ToHashSet();
        var open = all.Where(e => !hidden.Contains(e.Id)).ToDictionary(e => e.Id);

        // The order in which the table is filled. A lower group comes first; in a group, more shared words come first.
        var words = Words(needs.RequestText);
        var rank = new Dictionary<Guid, (int Group, int Shared)>();
        void Rank(Guid id, int group, int shared)
        {
            if (open.ContainsKey(id) && (!rank.TryGetValue(id, out var had) || (group, -shared).CompareTo((had.Group, -had.Shared)) < 0))
                rank[id] = (group, shared);
        }
        foreach (var entry in open.Values)
        {
            var shared = words.Count == 0 ? 0 : Words(entry.Name).Count(words.Contains);
            Rank(entry.Id, needs.Required.Contains(entry.Id) ? 0 : needs.Selected.Contains(entry.Id) ? 1 : shared > 0 ? 2 : 3, shared);
        }
        foreach (var entry in open.Values.Where(e => rank[e.Id].Group < 3).ToList())
        {
            foreach (var related in entry.Related)
                Rank(related, rank[entry.Id].Group, 0);
        }

        var listed = new HashSet<Guid>();
        var bytes = 0;
        foreach (var entry in open.Values
                     .OrderBy(e => rank[e.Id].Group).ThenByDescending(e => rank[e.Id].Shared)
                     .ThenBy(e => e.Name, StringComparer.Ordinal).ThenBy(e => e.Id))
        {
            var size = entry.Write(id => id is null ? null : WidestAlias, WidestAlias).ToJsonString().Length + 1;
            if (rank[entry.Id].Group > 0 && bytes + size > MaxTableBytes)
                continue;
            bytes += size;
            listed.Add(entry.Id);
        }

        // The measuring pass asked for every summary. Only the summaries of listed things count.
        hiddenSchemas.Clear();
        shownSchemas.Clear();

        // The aliases of every kind exist before an entry is written, so an entry can name any of them.
        var aliases = new Dictionary<string, AliasRef>(StringComparer.Ordinal);
        var ids = new Dictionary<Guid, string>();
        var bySection = Sections.ToDictionary(s => s.Kind, s => open.Values
            .Where(e => e.Kind == s.Kind && listed.Contains(e.Id))
            .OrderBy(e => e.Name, StringComparer.Ordinal).ThenBy(e => e.Id).ToList());
        foreach (var (_, kind, prefix) in Sections)
        {
            for (var i = 0; i < bySection[kind].Count; i++)
            {
                var alias = $"{prefix}{i + 1}";
                aliases[alias] = new AliasRef(kind, bySection[kind][i].Id, bySection[kind][i].Name);
                ids[bySection[kind][i].Id] = alias;
            }
        }

        // A reference to a thing that is not listed has no alias and is left out.
        JsonNode? Ref(Guid? id) => id is { } value && ids.TryGetValue(value, out var alias) ? alias : null;
        var table = new JsonObject();
        foreach (var (name, kind, _) in Sections)
            table[name] = new JsonArray([.. bySection[kind].Select(e => (JsonNode?)e.Write(Ref, ids[e.Id]))]);

        var relationships = new JsonArray();
        var relationshipsLeftOut = 0;
        foreach (var r in model.Relationships.Values
                     .Where(r => ids.ContainsKey(r.FromId) && ids.ContainsKey(r.ToId))
                     .OrderBy(r => ids[r.FromId], StringComparer.Ordinal).ThenBy(r => ids[r.ToId], StringComparer.Ordinal).ThenBy(r => r.Id.Value))
        {
            var node = new JsonObject
            {
                ["from"] = ids[r.FromId],
                ["to"] = ids[r.ToId],
                ["label"] = r.Label,
                ["kind"] = r.Kind.ToString().ToLowerInvariant(),
            };
            var size = node.ToJsonString().Length + 1;
            if (bytes + size > MaxTableBytes)
            {
                relationshipsLeftOut++;
                continue;
            }

            bytes += size;
            relationships.Add(node);
        }
        table["relationships"] = relationships;

        int LeftOut(string kind) => open.Values.Count(e => e.Kind == kind && !listed.Contains(e.Id));
        var omitted = new AliasOmitted(
            LeftOut(AliasKinds.Element), LeftOut(AliasKinds.Operation), LeftOut(AliasKinds.Channel),
            LeftOut(AliasKinds.Subscription), LeftOut(AliasKinds.Message), relationshipsLeftOut);
        if (omitted.Total > 0)
        {
            table["omitted"] = new JsonObject
            {
                ["elements"] = omitted.Elements, ["operations"] = omitted.Operations, ["channels"] = omitted.Channels,
                ["subscriptions"] = omitted.Subscriptions, ["messages"] = omitted.Messages, ["relationships"] = omitted.Relationships,
            };
        }

        var omittedPrivate = hidden.Count + hiddenSchemas.Count;
        if (omittedPrivate > 0)
            table["withheld"] = omittedPrivate;

        var result = new ModelAliases(table.ToJsonString())
        {
            Omitted = omitted,
            OmittedPrivateCount = omittedPrivate,
            DataClass = gate.ClassOf(listed.Concat(shownSchemas)),
        };
        foreach (var (alias, named) in aliases) result._byAlias[alias] = named;
        foreach (var (id, alias) in ids) result._byId[id] = alias;
        return result;
    }

    /// <summary>The whole words of a text in lower case: runs of letters and digits of at least <see cref="MinWordChars"/> characters.</summary>
    private static HashSet<string> Words(string text)
    {
        var words = new HashSet<string>(StringComparer.Ordinal);
        var start = -1;
        for (var i = 0; i <= text.Length; i++)
        {
            if (i < text.Length && char.IsLetterOrDigit(text[i]))
            {
                if (start < 0)
                    start = i;
            }
            else if (start >= 0)
            {
                if (i - start >= MinWordChars)
                    words.Add(text[start..i].ToLowerInvariant());
                start = -1;
            }
        }

        return words;
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
