namespace Vellum.Modules.Import.Extraction;

/// <summary>
/// Backstage catalog files: <c>catalog-info.y[a]ml</c>, and any YAML file whose documents have an
/// <c>apiVersion</c> of <c>backstage.io/…</c>. Every document of a multi-document file is read.
/// Rule <c>backstage.catalog</c>, version 1. The facts are <c>declared</c>.
/// <para>
/// <b>Keys.</b> The key of an entity is its entity ref, in lower case:
/// <c>component:default/orders</c>. A ref in a relation that leaves out the kind takes the
/// default kind of the field (<c>System</c> for <c>system</c>, <c>API</c> for
/// <c>providesApis</c> and <c>consumesApis</c>, <c>Domain</c> for <c>domain</c>,
/// <c>Component</c> for <c>subcomponentOf</c>); one that leaves out the namespace takes the
/// namespace of the entity. The ref is the external key of the source binding of the element.
/// </para>
/// <para>
/// <b>Mapping.</b>
/// <list type="bullet">
/// <item><c>System</c> is a <c>system</c>. Its <c>domain</c> is kept as the tag <c>domain:&lt;name&gt;</c>.</item>
/// <item><c>Component</c> is an <c>app</c> inside its <c>system</c>; with <c>subcomponentOf</c> it is a
/// <c>component</c> inside that component.</item>
/// <item><c>Resource</c> is a <c>store</c> inside its <c>system</c>; <c>spec.type</c> is its technology.</item>
/// <item><c>API</c> is a <c>component</c> inside the one component that lists it in
/// <c>providesApis</c>; <c>spec.type</c> is its technology. With no provider, or several, it
/// stays evidence.</item>
/// <item><c>Domain</c> is evidence only: the model has no level above a system.</item>
/// <item><c>dependsOn</c> is a relationship "depends on"; <c>consumesApis</c> is a relationship
/// "consumes" to the API.</item>
/// </list>
/// <c>metadata.title</c> (else <c>metadata.name</c>) is the name, <c>metadata.description</c> the
/// description and <c>metadata.tags</c> the tags. <c>spec.type</c>, <c>spec.lifecycle</c> and
/// <c>spec.owner</c> stay as observed attributes. A <c>Location</c> is not followed: its targets
/// are other files or URLs. Other kinds (<c>Group</c>, <c>User</c>, <c>Template</c>) are not read.
/// </para>
/// </summary>
internal sealed class BackstageExtractor : IExtractor
{
    public static readonly ExtractionRule Catalog = new("backstage.catalog", 1);

    public string Id => "backstage";
    public IReadOnlyList<ExtractionRule> Rules { get; } = [Catalog];

    private sealed record Entity(string Kind, string Ref, string Name, DocNode Node, DocNode? Spec, string Namespace);

    public void Run(ExtractionContext context)
    {
        // All catalog files of the scan are one catalog: a relation can name an entity of another file.
        var entities = new List<(SourceText File, Entity Entity)>();
        foreach (var file in context.Files)
        {
            if (context.Stopped)
                return;
            if (!file.IsYaml)
                continue;
            var named = file.Name.Equals("catalog-info.yaml", StringComparison.OrdinalIgnoreCase)
                || file.Name.Equals("catalog-info.yml", StringComparison.OrdinalIgnoreCase);
            if (!named && !file.Content.Contains("backstage.io/", StringComparison.Ordinal))
                continue;
            context.Guarded(Id, file.Path, () =>
            {
                foreach (var root in context.Documents(file))
                {
                    if (!context.Meter(file).Charge())
                        return;
                    if (root.Kind != DocKind.Map || root.Str("apiVersion") is not { } apiVersion
                        || !apiVersion.StartsWith("backstage.io/", StringComparison.Ordinal))
                        continue;
                    var kind = (root.Str("kind") ?? "").ToLowerInvariant();
                    var metadata = root.Map("metadata");
                    if (kind == "location")
                    {
                        context.Note(NoteKind.Undecided, "backstage:location-not-followed", file.Path, root.StartLine);
                        continue;
                    }
                    if (kind is not ("component" or "api" or "system" or "resource" or "domain"))
                        continue;
                    if (metadata?.Str("name") is not { Length: > 0 } name || !IsName(name))
                    {
                        context.Note(NoteKind.Undecided, "backstage:invalid-entity-name", file.Path, root.StartLine);
                        continue;
                    }
                    var ns = metadata.Str("namespace") is { Length: > 0 } given && IsName(given) ? given.ToLowerInvariant() : "default";
                    var title = metadata.Str("title") is { Length: > 0 } t ? t : name;
                    entities.Add((file, new Entity(kind, $"{kind}:{ns}/{name.ToLowerInvariant()}", title, root, root.Map("spec"), ns)));
                }
            });
        }
        if (entities.Count == 0)
            return;

        var byRef = new Dictionary<string, (SourceText File, Entity Entity)>(StringComparer.Ordinal);
        foreach (var item in entities)
        {
            if (!byRef.TryAdd(item.Entity.Ref, item))
                context.Note(NoteKind.Conflict, "backstage:duplicate-entity", item.File.Path, item.Entity.Node.StartLine);
        }

        // The one component that provides each API.
        var providers = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (_, entity) in byRef.Values.Where(e => e.Entity.Kind == "component"))
        {
            foreach (var api in Refs(entity.Spec?.Seq("providesApis"), "api", entity.Namespace))
            {
                if (!providers.TryGetValue(api, out var list))
                    providers[api] = list = [];
                list.Add(entity.Ref);
            }
        }

        foreach (var (file, entity) in byRef.Values)
        {
            if (context.Stopped)
                return;
            context.Guarded(Id, file.Path, () => AddEntity(context, file, entity, byRef, providers));
        }
    }

    private static void AddEntity(
        ExtractionContext context, SourceText file, Entity entity,
        Dictionary<string, (SourceText File, Entity Entity)> catalog, Dictionary<string, List<string>> providers)
    {
        var node = entity.Node;
        var spec = entity.Spec;
        var metadata = node.Map("metadata");
        var evidence = Catalog.At(file.Path, node.StartLine, node.EndLine);
        var tags = (metadata?.Seq("tags")?.Items ?? []).Select(i => i.AsText()).Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t!).ToList();
        string? parent = null;
        string? elementKind;
        string? technology = null;

        switch (entity.Kind)
        {
            case "system":
                elementKind = "system";
                if (Ref(spec?.Str("domain"), "domain", entity.Namespace) is { } domain)
                    tags.Add("domain:" + domain[(domain.IndexOf('/') + 1)..]);
                break;
            case "component":
                if (Ref(spec?.Str("subcomponentOf"), "component", entity.Namespace) is { } owner)
                {
                    elementKind = "component";
                    parent = owner;
                }
                else
                {
                    elementKind = "app";
                    parent = Ref(spec?.Str("system"), "system", entity.Namespace);
                }
                break;
            case "resource":
                elementKind = "store";
                parent = Ref(spec?.Str("system"), "system", entity.Namespace);
                technology = spec?.Str("type");
                break;
            case "api":
                elementKind = "component";
                technology = spec?.Str("type");
                var provided = providers.GetValueOrDefault(entity.Ref) ?? [];
                if (provided.Count == 1)
                    parent = provided[0];
                else
                    context.Note(NoteKind.Undecided, provided.Count == 0 ? "backstage:api-without-provider" : "backstage:api-with-several-providers", file.Path, node.StartLine);
                break;
            default:
                elementKind = "domain";
                break;
        }

        // A parent that the catalog does not hold leaves the element without a place in the model.
        if (parent is not null && !catalog.ContainsKey(parent))
        {
            context.Note(NoteKind.Undecided, "backstage:unresolved-ref", file.Path, node.StartLine);
            parent = null;
        }
        if (elementKind is "app" or "store" or "component" && parent is null)
            context.Note(NoteKind.Undecided, "backstage:no-parent", file.Path, node.StartLine);

        // A title the model refuses falls back to the name, which is always a short identifier.
        var name = ModelFileText.Line(context, file, node.StartLine, "name", entity.Name) ?? entity.Ref[(entity.Ref.IndexOf('/') + 1)..];
        context.Add(FactKind.Element, entity.Ref, name, evidence,
            ("elementKind", elementKind), ("parent", parent), ("backstage.kind", entity.Kind),
            ("description", ModelFileText.Lines(context, file, node.StartLine, "description", metadata?.Str("description"))),
            ("technology", ModelFileText.Line(context, file, node.StartLine, "technology", technology)),
            ("tags", ModelFileText.Tags(context, file, node.StartLine, tags)),
            ("type", entity.Kind is "component" ? spec?.Str("type") : null),
            ("lifecycle", spec?.Str("lifecycle")), ("owner", spec?.Str("owner")));

        foreach (var target in Refs(spec?.Seq("dependsOn"), null, entity.Namespace))
            Relationship(context, file, entity, target, "depends on", "dependsOn", catalog);
        foreach (var target in Refs(spec?.Seq("consumesApis"), "api", entity.Namespace))
            Relationship(context, file, entity, target, "consumes", "consumesApis", catalog);
    }

    private static void Relationship(
        ExtractionContext context, SourceText file, Entity from, string to, string label, string relation,
        Dictionary<string, (SourceText File, Entity Entity)> catalog)
    {
        var node = from.Spec?.Seq(relation);
        if (!catalog.ContainsKey(to))
        {
            context.Note(NoteKind.Undecided, "backstage:unresolved-ref", file.Path, node?.StartLine ?? from.Node.StartLine);
            return;
        }
        var line = node?.Items.FirstOrDefault(i => Ref(i.AsText(), null, from.Namespace) == to)?.StartLine ?? node?.StartLine ?? from.Node.StartLine;
        context.Add(FactKind.Relationship, $"backstage:{relation}:{from.Ref}->{to}", label, Catalog.At(file.Path, line, line),
            ("from", from.Ref), ("to", to), ("label", label), ("relationshipKind", "structural"));
    }

    private static IEnumerable<string> Refs(DocNode? list, string? defaultKind, string ns)
    {
        foreach (var item in list?.Items ?? [])
        {
            if (Ref(item.AsText(), defaultKind, ns) is { } reference)
                yield return reference;
        }
    }

    /// <summary><c>[kind:][namespace/]name</c> in lower case, with the defaults filled in. Null when the text is not a ref.</summary>
    public static string? Ref(string? text, string? defaultKind, string ns)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var value = text.Trim().ToLowerInvariant();
        var colon = value.IndexOf(':');
        var kind = colon > 0 ? value[..colon] : defaultKind;
        var rest = colon > 0 ? value[(colon + 1)..] : value;
        var slash = rest.IndexOf('/');
        var space = slash > 0 ? rest[..slash] : ns;
        var name = slash > 0 ? rest[(slash + 1)..] : rest;
        return kind is null || !IsName(kind) || !IsName(space) || !IsName(name) ? null : $"{kind}:{space}/{name}";
    }

    /// <summary>Backstage names: letters, digits and <c>-_.</c>, at most 63 characters.</summary>
    private static bool IsName(string value) =>
        value.Length is > 0 and <= 63 && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');
}
