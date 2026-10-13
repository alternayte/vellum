using Vellum.Modules.Modelling.Model;

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

internal sealed class ModelIndex
{
    private readonly Dictionary<(string Kind, Guid Id), ModelEntity> _byId = [];
    private readonly Dictionary<string, List<ModelEntity>> _byKind = EntityKinds.All.ToDictionary(k => k, _ => new List<ModelEntity>());

    public static ModelIndex From(ModelState model, IEnumerable<SchemaRow> schemas)
    {
        var index = new ModelIndex();
        foreach (var e in model.Elements.Values)
            index.Add(Of(e));
        foreach (var o in model.Operations.Values)
            index.Add(Of(o));
        foreach (var c in model.Channels.Values)
            index.Add(Of(c));
        foreach (var s in model.Subscriptions.Values)
            index.Add(Of(model, s));
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

    public static ModelEntity Of(MessageState m) => new(m.Id, EntityKinds.Message, m.Name, Values(
        (Field.Name, m.Name), (Field.ProducerId, m.ProducerId.ToString()),
        (Field.ConsumerIds, JoinIds(m.ConsumerIds.Select(c => c.Value))), (Field.SchemaId, m.SchemaId?.ToString())),
        m.FactStatus.ToApiString(), m.Stale);

    public static ModelEntity Of(SchemaRow s) => new(s.Id, EntityKinds.Schema, s.Name, Values(
        (Field.Name, s.Name), (Field.Content, s.Content)), null, null);

    /// <summary>IDs in one fixed order, so two sets of the same IDs compare equal as text.</summary>
    public static string JoinIds(IEnumerable<Guid> ids) =>
        string.Join(",", ids.Distinct().Select(id => id.ToString()).OrderBy(id => id, StringComparer.Ordinal));

    public static IEnumerable<Guid> SplitIds(string? joined) =>
        string.IsNullOrEmpty(joined) ? [] : joined.Split(',').Select(Guid.Parse);

    /// <summary>The fields a scan can change on an existing entity of <paramref name="entityKind"/>.</summary>
    public static IReadOnlyList<string> ComparedFields(string entityKind) => entityKind switch
    {
        // Not the kind and not the parent: where a person put an element stays their choice.
        EntityKinds.Element => [Field.Name, Field.Technology],
        EntityKinds.Operation => [Field.Name, Field.Transport, Field.OwnerId],
        EntityKinds.Channel => [Field.Name, Field.Kind],
        EntityKinds.Subscription => [Field.ChannelId, Field.ConsumerId, Field.DeadLetterChannelId],
        EntityKinds.Message => [Field.Name, Field.ProducerId, Field.ConsumerIds, Field.SchemaId],
        EntityKinds.Schema => [Field.Name, Field.Content],
        _ => [],
    };

    /// <summary>
    /// The exact rule that says a fact and a hand-made entity are the same thing: the same
    /// kind and the same normalised name or route. For an operation the owner must be the
    /// same element too; a subscription has no name and matches on its channel and consumer.
    /// </summary>
    public static string MatchKey(string entityKind, IReadOnlyDictionary<string, string?> values) => entityKind switch
    {
        EntityKinds.Element => $"{values.GetValueOrDefault(Field.Kind)}|{NormalName(values.GetValueOrDefault(Field.Name))}",
        EntityKinds.Operation => $"{values.GetValueOrDefault(Field.OwnerId)}|{NormalRoute(values.GetValueOrDefault(Field.Name))}",
        EntityKinds.Subscription => $"{values.GetValueOrDefault(Field.ChannelId)}|{values.GetValueOrDefault(Field.ConsumerId)}",
        _ => NormalName(values.GetValueOrDefault(Field.Name)),
    };

    /// <summary>Surrounding white space removed, inner runs of white space as one space, upper case by the invariant rules.</summary>
    public static string NormalName(string? name) =>
        string.Join(' ', (name ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();

    /// <summary>
    /// <c>post  /orders/</c> and <c>POST /orders</c> are one route: the method in upper case,
    /// one space, the path as written without a trailing slash. The path keeps its case.
    /// </summary>
    public static string NormalRoute(string? name)
    {
        var parts = (name ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2)
            return string.Join(' ', parts);
        var path = parts[1].Length > 1 ? parts[1].TrimEnd('/') : parts[1];
        return $"{parts[0].ToUpperInvariant()} {(path.Length == 0 ? "/" : path)}";
    }

    /// <summary>One entity of the model stream, read without building an index. A schema is not in the stream.</summary>
    public static ModelEntity? Read(ModelState model, string entityKind, Guid id) => entityKind switch
    {
        EntityKinds.Element => model.Elements.TryGetValue(id, out var element) ? Of(element) : null,
        EntityKinds.Operation => model.Operations.TryGetValue(id, out var operation) ? Of(operation) : null,
        EntityKinds.Channel => model.Channels.TryGetValue(id, out var channel) ? Of(channel) : null,
        EntityKinds.Subscription => model.Subscriptions.TryGetValue(id, out var subscription) ? Of(model, subscription) : null,
        EntityKinds.Message => model.Messages.TryGetValue(id, out var message) ? Of(message) : null,
        _ => null,
    };

    public ModelEntity? Find(string entityKind, Guid id) => _byId.GetValueOrDefault((entityKind, id));

    public IReadOnlyList<ModelEntity> OfKind(string entityKind) => _byKind[entityKind];

    /// <summary>The name of any entity, for showing next to an ID.</summary>
    public string? NameOf(Guid id)
    {
        foreach (var kind in EntityKinds.All)
        {
            if (_byId.TryGetValue((kind, id), out var entity))
                return entity.Name;
        }
        return null;
    }

    private void Add(ModelEntity entity)
    {
        _byId[(entity.EntityKind, entity.Id)] = entity;
        _byKind[entity.EntityKind].Add(entity);
    }

    private static SortedDictionary<string, string?> Values(params (string Name, string? Value)[] values)
    {
        var result = new SortedDictionary<string, string?>(StringComparer.Ordinal);
        foreach (var (name, value) in values)
            result[name] = value;
        return result;
    }
}
