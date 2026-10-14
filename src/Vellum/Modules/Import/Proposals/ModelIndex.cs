using System.Text.Json;
using Vellum.Modules.Modelling;

namespace Vellum.Modules.Import.Proposals;

/// <summary>
/// One entity of the model as the importer compares it: the values a scan can observe, by
/// field name. Intent fields (description, tags, owner, status, icon, line shape) and the
/// delivery rules a person sets (timeout, retry) are not here, so no proposal can touch them.
/// </summary>
/// <param name="FactStatus">Null for a schema: a schema has no truth label of its own.</param>
internal sealed record ModelEntity(
    Guid Id, string EntityKind, string Name,
    SortedDictionary<string, string?> Values, string? FactStatus, StaleMark? Stale);

internal sealed record SchemaRow(Guid Id, string Name, string Content);

/// <summary>
/// The model as the planner reads it. Every lookup is a dictionary read: by ID, and by the
/// match key of <see cref="MatchKey"/>, which is computed once per entity when the index is
/// built.
/// </summary>
internal sealed class ModelIndex
{
    private readonly Dictionary<(string Kind, Guid Id), ModelEntity> _byId = [];
    private readonly Dictionary<Guid, string> _names = [];
    private readonly Dictionary<(string Kind, string MatchKey), List<ModelEntity>> _byMatchKey = [];
    private readonly Dictionary<(string Kind, Guid Id), string> _matchKeys = [];
    private readonly Dictionary<(Guid SourceId, string Key), Guid> _bound = [];
    private readonly Dictionary<Guid, HashSet<Guid>> _sourcesOf = [];

    public int Count => _byId.Count;

    public static ModelIndex From(ModelState model, IEnumerable<SchemaRow> schemas)
    {
        var index = new ModelIndex();
        foreach (var e in model.Elements.Values)
        {
            index.Add(Of(e));
            foreach (var binding in e.Bindings)
            {
                index._bound.TryAdd((binding.SourceId, binding.ExternalKey), e.Id);
                if (!index._sourcesOf.TryGetValue(e.Id, out var sources))
                    index._sourcesOf[e.Id] = sources = [];
                sources.Add(binding.SourceId);
            }
        }
        foreach (var o in model.Operations.Values)
            index.Add(Of(o));
        foreach (var c in model.Channels.Values)
            index.Add(Of(c));
        foreach (var s in model.Subscriptions.Values)
            index.Add(Of(model, s));
        foreach (var b in model.Bindings.Values)
            index.Add(Of(model, b));
        foreach (var m in model.Messages.Values)
            index.Add(Of(m));
        foreach (var schema in schemas)
            index.Add(Of(schema));
        return index;
    }

    public static ModelEntity Of(ElementState e) => new(e.Id, EntityKinds.Element, e.Name, Values(
        (Field.Kind, e.Kind.ToString().ToLowerInvariant()), (Field.Name, e.Name), (Field.Technology, e.Technology),
        (Field.ParentId, e.ParentId?.ToString())), e.FactStatus.ToApiString(), e.Stale);

    public static ModelEntity Of(OperationState o) => new(o.Id, EntityKinds.Operation, o.Name, Values(
        (Field.Name, o.Name), (Field.Transport, o.Transport.ToString().ToLowerInvariant()), (Field.OwnerId, o.OwnerId.ToString())),
        o.FactStatus.ToApiString(), o.Stale);

    public static ModelEntity Of(ChannelState c) => new(c.Id, EntityKinds.Channel, c.Name, Values(
        (Field.Name, c.Name), (Field.Kind, c.Kind.ToString().ToLowerInvariant())), c.FactStatus.ToApiString(), c.Stale);

    public static ModelEntity Of(ModelState model, SubscriptionState s) => new(
        s.Id, EntityKinds.Subscription, ContractDecider.SubscriptionLabel(model, s), Values(
            (Field.ChannelId, s.ChannelId.ToString()), (Field.ConsumerId, s.ConsumerId.ToString()),
            (Field.DeadLetterChannelId, s.DeadLetterChannelId?.ToString())), s.FactStatus.ToApiString(), s.Stale);

    public static ModelEntity Of(ModelState model, BindingState b) => new(
        b.Id, EntityKinds.Binding, ContractDecider.BindingLabel(model, b), Values(
            (Field.SourceChannelId, b.SourceChannelId.ToString()), (Field.TargetChannelId, b.TargetChannelId.ToString()),
            (Field.RoutingKey, b.RoutingKey)), b.FactStatus.ToApiString(), b.Stale);

    public static ModelEntity Of(MessageState m) => new(m.Id, EntityKinds.Message, m.Name, Values(
        (Field.Name, m.Name), (Field.ProducerId, m.ProducerId.ToString()),
        (Field.ConsumerIds, JoinIds(m.ConsumerIds.Select(c => c.Value))), (Field.SchemaId, m.SchemaId?.ToString())),
        m.FactStatus.ToApiString(), m.Stale);

    public static ModelEntity Of(SchemaRow s) => new(s.Id, EntityKinds.Schema, s.Name, Values(
        (Field.Name, s.Name), (Field.Content, s.Content)), null, null);

    /// <summary>
    /// A set of IDs as one text: a JSON array in one fixed order, so two sets of the same IDs
    /// compare equal as text. No ID list is ever a string joined with a separator.
    /// </summary>
    public static string JoinIds(IEnumerable<Guid> ids) =>
        JsonSerializer.Serialize(ids.Distinct().Select(id => id.ToString()).OrderBy(id => id, StringComparer.Ordinal));

    public static IEnumerable<Guid> SplitIds(string? joined) =>
        string.IsNullOrEmpty(joined) ? [] : (JsonSerializer.Deserialize<List<string>>(joined) ?? []).Select(Guid.Parse);

    /// <summary>The fields a scan can change on an existing entity of <paramref name="entityKind"/>.</summary>
    public static IReadOnlyList<string> ComparedFields(string entityKind) => entityKind switch
    {
        // Not the kind and not the parent: where a person put an element stays their choice.
        EntityKinds.Element => ElementFields,
        EntityKinds.Operation => OperationFields,
        EntityKinds.Channel => ChannelFields,
        EntityKinds.Subscription => SubscriptionFields,
        EntityKinds.Binding => BindingFields,
        EntityKinds.Message => MessageFields,
        EntityKinds.Schema => SchemaFields,
        _ => [],
    };

    private static readonly string[] ElementFields = [Field.Name, Field.Technology];
    private static readonly string[] OperationFields = [Field.Name, Field.Transport, Field.OwnerId];
    private static readonly string[] ChannelFields = [Field.Name, Field.Kind];
    private static readonly string[] SubscriptionFields = [Field.ChannelId, Field.ConsumerId, Field.DeadLetterChannelId];
    private static readonly string[] BindingFields = [Field.SourceChannelId, Field.TargetChannelId, Field.RoutingKey];
    private static readonly string[] MessageFields = [Field.Name, Field.ProducerId, Field.ConsumerIds, Field.SchemaId];
    private static readonly string[] SchemaFields = [Field.Name, Field.Content];

    /// <summary>
    /// The exact rule that says a fact and an entity are the same thing: the same kind and
    /// the same normalised name or route. For an operation the owner must be the same
    /// element too; a subscription has no name and matches on its channel and consumer; a
    /// binding has none either and matches on its source, its target and its routing key; a
    /// message, a channel and a schema match on the name alone.
    /// It is used for two things: to join a fact to a hand-made entity, and to move an
    /// accepted link to the fact that replaced its key after the extraction rules changed.
    /// </summary>
    public static string MatchKey(string entityKind, IReadOnlyDictionary<string, string?> values) => entityKind switch
    {
        EntityKinds.Element => $"{values.GetValueOrDefault(Field.Kind)}|{NormalName(values.GetValueOrDefault(Field.Name))}",
        EntityKinds.Operation => $"{values.GetValueOrDefault(Field.OwnerId)}|{NormalRoute(values.GetValueOrDefault(Field.Name))}",
        EntityKinds.Subscription => $"{values.GetValueOrDefault(Field.ChannelId)}|{values.GetValueOrDefault(Field.ConsumerId)}",
        EntityKinds.Binding => $"{values.GetValueOrDefault(Field.SourceChannelId)}|{values.GetValueOrDefault(Field.TargetChannelId)}|{values.GetValueOrDefault(Field.RoutingKey)}",
        _ => NormalName(values.GetValueOrDefault(Field.Name)),
    };

    /// <summary>Surrounding white space removed, inner runs of white space as one space, upper case by the invariant rules.</summary>
    public static string NormalName(string? name) =>
        string.Join(' ', (name ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();

    /// <summary>
    /// <c>post  /orders/{id:guid}/</c> and <c>POST /orders/{orderId}</c> are one route: the
    /// method in upper case, one space, and the path without a trailing slash, with every
    /// parameter as <c>{}</c>. The name of a parameter and its constraint say nothing about
    /// which route it is. The rest of the path keeps its case.
    /// </summary>
    public static string NormalRoute(string? name)
    {
        var parts = (name ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2)
            return string.Join(' ', parts);

        var path = new System.Text.StringBuilder();
        var depth = 0;
        foreach (var c in parts[1])
        {
            if (c == '{')
            {
                if (depth++ == 0)
                    path.Append("{}");
            }
            else if (c == '}')
                depth = Math.Max(0, depth - 1);
            else if (depth == 0)
                path.Append(c);
        }
        var route = path.Length > 1 ? path.ToString().TrimEnd('/') : path.ToString();
        return $"{parts[0].ToUpperInvariant()} {(route.Length == 0 ? "/" : route)}";
    }

    /// <summary>One entity of the model stream, read without building an index. A schema is not in the stream.</summary>
    public static ModelEntity? Read(ModelState model, string entityKind, Guid id) => entityKind switch
    {
        EntityKinds.Element => model.Elements.TryGetValue(id, out var element) ? Of(element) : null,
        EntityKinds.Operation => model.Operations.TryGetValue(id, out var operation) ? Of(operation) : null,
        EntityKinds.Channel => model.Channels.TryGetValue(id, out var channel) ? Of(channel) : null,
        EntityKinds.Subscription => model.Subscriptions.TryGetValue(id, out var subscription) ? Of(model, subscription) : null,
        EntityKinds.Binding => model.Bindings.TryGetValue(id, out var binding) ? Of(model, binding) : null,
        EntityKinds.Message => model.Messages.TryGetValue(id, out var message) ? Of(message) : null,
        _ => null,
    };

    public ModelEntity? Find(string entityKind, Guid id) => _byId.GetValueOrDefault((entityKind, id));

    /// <summary>The element that a person bound to this key of this Source, or null.</summary>
    public Guid? BoundElement(Guid sourceId, string key) => _bound.TryGetValue((sourceId, key), out var id) ? id : null;

    /// <summary>True when the element has a binding to this Source, under any key.</summary>
    public bool IsBoundTo(Guid elementId, Guid sourceId) => _sourcesOf.TryGetValue(elementId, out var sources) && sources.Contains(sourceId);

    /// <summary>True when the element has a binding to a Source other than this one.</summary>
    public bool IsBoundToOther(Guid elementId, Guid sourceId) => _sourcesOf.TryGetValue(elementId, out var sources) && sources.Any(s => s != sourceId);

    /// <summary>The entities of a kind with this match key. The list is empty when there is none.</summary>
    public IReadOnlyList<ModelEntity> Matching(string entityKind, string matchKey) =>
        _byMatchKey.TryGetValue((entityKind, matchKey), out var found) ? found : [];

    /// <summary>The match key of an entity of this index, as computed when the index was built.</summary>
    public string MatchKeyOf(ModelEntity entity) => _matchKeys[(entity.EntityKind, entity.Id)];

    /// <summary>The name of any entity, for showing next to an ID.</summary>
    public string? NameOf(Guid id) => _names.GetValueOrDefault(id);

    private void Add(ModelEntity entity)
    {
        _byId[(entity.EntityKind, entity.Id)] = entity;
        _names.TryAdd(entity.Id, entity.Name);

        var key = MatchKey(entity.EntityKind, entity.Values);
        _matchKeys[(entity.EntityKind, entity.Id)] = key;
        if (!_byMatchKey.TryGetValue((entity.EntityKind, key), out var list))
            _byMatchKey[(entity.EntityKind, key)] = list = [];
        list.Add(entity);
    }

    private static SortedDictionary<string, string?> Values(params (string Name, string? Value)[] values)
    {
        var result = new SortedDictionary<string, string?>(StringComparer.Ordinal);
        foreach (var (name, value) in values)
            result[name] = value;
        return result;
    }
}
