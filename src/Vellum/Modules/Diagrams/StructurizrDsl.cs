using System.Globalization;
using System.Text;
using Vellum.Modules.Modelling;
using Vellum.Modules.Promises;
using Vellum.Modules.Views;

namespace Vellum.Modules.Diagrams;

/// <summary>
/// The whole model as a Structurizr DSL workspace. The mapping is fixed:
/// <list type="bullet">
/// <item>A top-level actor is a <c>person</c>; any other top-level element is a <c>softwareSystem</c>.</item>
/// <item>An element inside a software system is a <c>container</c>; inside a container, a <c>component</c>.
/// A store gets the tag <c>Database</c>; a behavioural relationship the tag <c>Behavioural</c>.</item>
/// <item>An element that Structurizr cannot nest where it stands (an actor inside anything, an
/// element inside a person or a component) moves to the top level with its own inside.</item>
/// <item>Identifiers come from <see cref="DiagramText.Identifiers"/> and are flat (Structurizr's default).</item>
/// <item>A view whose root is none is a <c>systemLandscape</c>; one whose root is a software system,
/// a <c>container</c> view; one whose root is a container, a <c>component</c> view. Each has
/// <c>include *</c> and <c>autoLayout</c>. Any other view is named in a comment.</item>
/// </list>
/// <item>Each derived line of a written view (<see cref="DerivedLines"/>) is a relationship with
/// the tag <c>Derived</c>, drawn thin and dotted. A derived line that two views share is written once.</item>
/// The export is one-way: the header lists what it drops, and a comment names each relationship
/// that Structurizr refuses.
/// </summary>
public static class StructurizrDsl
{
    private static readonly HashSet<string> Reserved = new(StringComparer.Ordinal)
    {
        "workspace", "model", "views", "person", "softwaresystem", "container", "component", "group", "enterprise",
        "deploymentenvironment", "deploymentnode", "infrastructurenode", "softwaresysteminstance", "containerinstance",
        "element", "relationship", "this", "styles", "theme", "themes", "branding", "terminology", "configuration",
        "include", "exclude", "autolayout", "properties", "perspectives", "tags", "tag", "url", "description",
        "technology", "title", "animation", "extends", "users", "scope", "visibility", "archetypes", "healthcheck",
        "instances", "systemlandscape", "systemcontext", "filtered", "dynamic", "deployment", "custom", "image", "name",
    };

    public const string Dropped =
        "canvas layout and positions, icons, line shapes, truth labels and their provenance, element status, " +
        "owners, stale marks, messages, contracts (operations, channels, subscriptions, bindings), schemas, " +
        "scenarios, promises, obligations, decisions and documents, costs, comments, the technology of an element " +
        "written as a person or a software system, and the nesting that Structurizr cannot express";

    private enum DslType { Person, SoftwareSystem, Container, Component }

    public static string Write(
        string projectName, PinnedModel model, IEnumerable<ViewEntity> views,
        IReadOnlyDictionary<Guid, IReadOnlyList<LayoutPositionEntity>> positions,
        IReadOnlyDictionary<Guid, IReadOnlyList<LayoutEdgeEntity>>? edges = null,
        IReadOnlyDictionary<Guid, IReadOnlyList<LayoutLineEntity>>? lines = null)
    {
        var state = model.State;
        var ids = DiagramText.Identifiers(state.Elements.Values.Select(e => (e.Id.Value, e.Name)), "element", Reserved);

        var children = state.Elements.Values
            .Where(e => e.ParentId is { } p && state.Elements.ContainsKey(p))
            .GroupBy(e => e.ParentId!.Value)
            .ToDictionary(g => g.Key, g => g.OrderBy(e => e.Name, StringComparer.Ordinal).ThenBy(e => e.Id.Value).ToList());
        var topLevel = new Queue<ElementState>(state.Elements.Values
            .Where(e => e.ParentId is not { } p || !state.Elements.ContainsKey(p))
            .OrderBy(e => e.Name, StringComparer.Ordinal).ThenBy(e => e.Id.Value));

        var text = new StringBuilder();
        text.Append("workspace ").Append(Quote(projectName)).Append(' ').Append(Quote("Exported from Vellum")).Append(" {\n\n");
        text.Append("    model {\n");
        // Vellum draws the relationships it holds and the derived lines of its views, which are
        // written out below. Implied relationships would also make the file fail when two
        // children of one parent point at the same element.
        text.Append("        !impliedRelationships false\n\n");

        var types = new Dictionary<ElementId, DslType>();
        var dslParent = new Dictionary<ElementId, ElementId>();

        // Structurizr wants a unique name among the people and software systems, among the
        // containers of one software system and among the components of one container. A later
        // sibling with a name already used gets " (2)", " (3)" …, in the order of writing.
        var namesInScope = new Dictionary<Guid, HashSet<string>>(); // Guid.Empty: the people and software systems
        var renames = new List<(string Identifier, string From, string To)>();
        string UniqueName(ElementState e, ElementId? scope)
        {
            var key = scope?.Value ?? Guid.Empty;
            if (!namesInScope.TryGetValue(key, out var used))
                namesInScope[key] = used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var name = DiagramText.OneLine(e.Name);
            var candidate = name;
            for (var n = 2; !used.Add(candidate); n++)
                candidate = string.Create(CultureInfo.InvariantCulture, $"{name} ({n})");
            if (candidate != name) renames.Add((ids[e.Id.Value], name, candidate));
            return candidate;
        }

        void Write(ElementState e, DslType type, ElementId? parent, int depth)
        {
            types[e.Id] = type;
            if (parent is { } p) dslParent[e.Id] = p;
            var indent = new string(' ', 4 * depth);
            text.Append(indent).Append(ids[e.Id.Value]).Append(" = ").Append(Keyword(type))
                .Append(' ').Append(Quote(UniqueName(e, parent))).Append(' ').Append(Quote(e.Description));
            if (type is DslType.Container or DslType.Component)
                text.Append(' ').Append(Quote(e.Technology));
            text.Append(' ').Append(Quote(Tags(e)));

            var nested = new List<(ElementState Child, DslType Type)>();
            foreach (var child in children.GetValueOrDefault(e.Id) ?? [])
            {
                if (Nested(type, child.Kind) is { } childType)
                    nested.Add((child, childType));
                else
                    topLevel.Enqueue(child);
            }
            if (nested.Count == 0)
            {
                text.Append('\n');
                return;
            }
            text.Append(" {\n");
            foreach (var (child, childType) in nested)
                Write(child, childType, e.Id, depth + 1);
            text.Append(indent).Append("}\n");
        }

        while (topLevel.TryDequeue(out var element))
            Write(element, element.Kind == ElementKind.Actor ? DslType.Person : DslType.SoftwareSystem, null, 2);

        var relationships = state.Relationships.Values
            .Where(r => types.ContainsKey(r.FromId) && types.ContainsKey(r.ToId))
            .OrderBy(r => r.Id.Value)
            .ToList();
        if (relationships.Count > 0) text.Append('\n');
        var written = new HashSet<(ElementId, ElementId, string)>();
        foreach (var r in relationships)
        {
            var from = ids[r.FromId.Value];
            var to = ids[r.ToId.Value];
            var label = DiagramText.OneLine(r.Label);
            if (r.FromId == r.ToId || IsAncestor(dslParent, r.FromId, r.ToId) || IsAncestor(dslParent, r.ToId, r.FromId))
            {
                text.Append("        // Not written: ").Append(from).Append(" -> ").Append(to)
                    .Append(". Structurizr does not allow a relationship between an element and itself or its parent.\n");
                continue;
            }
            if (!written.Add((r.FromId, r.ToId, label)))
            {
                text.Append("        // Not written: a second ").Append(from).Append(" -> ").Append(to)
                    .Append(" with the same label. Structurizr allows one.\n");
                continue;
            }
            text.Append("        ").Append(from).Append(" -> ").Append(to)
                .Append(' ').Append(Quote(r.Label)).Append(' ').Append(Quote(r.Technology))
                .Append(' ').Append(Quote(r.Kind == RelationshipKind.Behavioural ? "Behavioural" : ""))
                .Append('\n');
        }

        var orderedViews = views.OrderBy(v => v.Name, StringComparer.Ordinal).ThenBy(v => v.Id).ToList();

        // The derived lines of each view that is written, as the canvas draws them with every card
        // closed. The line between two elements is the same in every view that shows both.
        var derived = new Dictionary<(ElementId From, ElementId To), DerivedLine>();
        foreach (var view in orderedViews)
        {
            ElementId? root = view.RootElementId is { } r ? new ElementId(r) : null;
            if (root is { } at && (!types.TryGetValue(at, out var type) || type is not (DslType.SoftwareSystem or DslType.Container)))
                continue;
            var level = state.Elements.Values.Where(e => e.ParentId == root).Select(e => e.Id).ToHashSet();
            foreach (var line in DerivedLines.Project(state.Elements, state.Relationships.Values, level).Derived)
                derived.TryAdd((line.FromId, line.ToId), line);
        }
        if (derived.Count > 0) text.Append('\n');
        foreach (var line in derived.Values
                     .OrderBy(l => l.FromId.Value.ToString(), StringComparer.Ordinal).ThenBy(l => l.ToId.Value.ToString(), StringComparer.Ordinal))
        {
            var from = ids[line.FromId.Value];
            var to = ids[line.ToId.Value];
            var label = DiagramText.OneLine(line.Label);
            if (!types.ContainsKey(line.FromId) || !types.ContainsKey(line.ToId)
                || IsAncestor(dslParent, line.FromId, line.ToId) || IsAncestor(dslParent, line.ToId, line.FromId))
            {
                text.Append("        // Derived line not written: ").Append(from).Append(" -> ").Append(to)
                    .Append(". Structurizr does not allow a relationship between an element and its parent.\n");
                continue;
            }
            if (!written.Add((line.FromId, line.ToId, label)))
            {
                text.Append("        // Derived line not written: ").Append(from).Append(" -> ").Append(to)
                    .Append(" has the label of a relationship between the same two elements. Structurizr allows one.\n");
                continue;
            }
            text.Append("        ").Append(from).Append(" -> ").Append(to)
                .Append(' ').Append(Quote(line.Label)).Append(' ').Append(Quote(null)).Append(' ').Append(Quote("Derived"))
                .Append('\n');
        }
        text.Append("    }\n\n");

        text.Append("    views {\n");
        var keys = DiagramText.Identifiers(orderedViews.Select(v => (v.Id, v.Name)), "view", new HashSet<string>(StringComparer.Ordinal));
        foreach (var view in orderedViews)
        {
            var key = keys[view.Id];
            var name = Quote(view.Name);
            string? opening = null;
            if (view.RootElementId is null)
                opening = $"systemLandscape {Quote(key)} {name}";
            else if (types.TryGetValue(view.RootElementId.Value, out var rootType) && rootType == DslType.SoftwareSystem)
                opening = $"container {ids[view.RootElementId.Value]} {Quote(key)} {name}";
            else if (rootType == DslType.Container)
                opening = $"component {ids[view.RootElementId.Value]} {Quote(key)} {name}";

            if (opening is null)
            {
                text.Append("        // View \"").Append(DiagramText.OneLine(view.Name))
                    .Append("\" is not written: Structurizr has no view of the inside of its root element.\n");
                continue;
            }
            text.Append("        ").Append(opening).Append(" {\n")
                .Append("            include *\n")
                .Append("            autoLayout\n")
                .Append("        }\n");
        }
        text.Append("\n        styles {\n")
            .Append("            element \"Person\" {\n                shape Person\n            }\n")
            .Append("            element \"Database\" {\n                shape Cylinder\n            }\n")
            .Append(derived.Count > 0 ? "            relationship \"Derived\" {\n                thickness 1\n                style dotted\n            }\n" : "")
            .Append("        }\n");
        text.Append("    }\n");
        text.Append("}\n");

        var header = new StringBuilder();
        header.Append("// Vellum model of project \"").Append(DiagramText.OneLine(projectName)).Append("\", Structurizr DSL workspace.\n");
        header.Append("// From ").Append(ViewDiagram.PinText(model.Pin)).Append(".\n");
        foreach (var view in orderedViews)
            header.Append("// View \"").Append(DiagramText.OneLine(view.Name)).Append("\" with layout ")
                .Append(ViewDiagram.LayoutFingerprint(view, positions.GetValueOrDefault(view.Id) ?? [], edges?.GetValueOrDefault(view.Id), lines?.GetValueOrDefault(view.Id))).Append(".\n");
        header.Append("// One-way export. It drops: ").Append(Dropped).Append(".\n");
        if (derived.Count > 0)
            header.Append("// A relationship tagged Derived is a derived line of a view: the relationships between elements inside its two ends, ")
            .Append("labelled with the one relationship's label or \"N relationships\". It is not a relationship of the Vellum model.\n");
        foreach (var (identifier, from, to) in renames)
            header.Append("// Renamed for unique names: ").Append(identifier).Append(" \"").Append(from)
                .Append("\" is \"").Append(to).Append("\".\n");
        return header.Append(text).ToString();
    }

    private static DslType? Nested(DslType parent, ElementKind child) => child == ElementKind.Actor ? null : parent switch
    {
        DslType.SoftwareSystem => DslType.Container,
        DslType.Container => DslType.Component,
        _ => null,
    };

    private static bool IsAncestor(Dictionary<ElementId, ElementId> parents, ElementId ancestor, ElementId of)
    {
        for (var current = of; parents.TryGetValue(current, out var parent); current = parent)
            if (parent == ancestor) return true;
        return false;
    }

    private static string Keyword(DslType type) => type switch
    {
        DslType.Person => "person",
        DslType.SoftwareSystem => "softwareSystem",
        DslType.Container => "container",
        _ => "component",
    };

    /// <summary>The tags of an element, comma-separated: Database for a store, then its own tags without commas.</summary>
    private static string Tags(ElementState e)
    {
        var tags = new List<string>();
        if (e.Kind == ElementKind.Store) tags.Add("Database");
        foreach (var tag in e.Tags)
        {
            var clean = DiagramText.OneLine(tag).Replace(',', ' ').Trim();
            if (clean.Length > 0 && !tags.Contains(clean, StringComparer.Ordinal)) tags.Add(clean);
        }
        return string.Join(',', tags);
    }

    /// <summary>
    /// A DSL string: in double quotes and on one line. Structurizr knows one escape, <c>\"</c>, so
    /// a double quote gets a backslash in front and a backslash stays as it is; a backslash at
    /// the end becomes U+29F5 (reverse solidus operator), so it cannot escape the closing quote.
    /// Structurizr replaces <c>${NAME}</c> with a constant or an environment variable, so every
    /// <c>${</c> gets U+200B (zero width space) between its two characters.
    /// </summary>
    public static string Quote(string? text)
    {
        var line = DiagramText.OneLine(text).Replace("${", "$\u200B{", StringComparison.Ordinal);
        if (line.EndsWith('\\')) line = line[..^1] + "\u29F5";
        return "\"" + line.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    }
}
