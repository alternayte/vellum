namespace Vellum.Modules.Import.Extraction;

/// <summary>The controller rule: it needs every part and every base class of a controller, so it runs after all files are read.</summary>
internal sealed partial class DotNetExtractor
{
    private static void EmitControllers(ExtractionContext context, TypeIndex index)
    {
        foreach (var logical in index.All)
        {
            if (logical.Keyword != "class")
                continue;
            var hasActions = logical.Parts.Exists(p => p.Actions.Count > 0);
            var isMarked = logical.Parts.Exists(p => p.IsApiController);
            if (!hasActions && !isMarked && !logical.Parts.Exists(p => p.Bases.Count > 0))
                continue;
            var first = logical.Parts[0];
            var meter = context.Meter(first.File.File);
            if (!meter.Charge())
                continue;

            // The class and its base classes, nearest first.
            var chain = new List<LogicalType>();
            string? broken = null;
            var isController = false;
            var current = logical;
            while (broken is null)
            {
                if (!current.IsConsistent)
                    broken = "ambiguous-type-declaration";
                else if (current.IsIncomplete)
                    broken = "partial-part-missing";
                else
                {
                    chain.Add(current);
                    isController |= current.Parts.Exists(p => p.IsApiController);
                    var next = BaseClassOf(current, index, meter, out var endsAtControllerBase, out var isResolved);
                    if (!isResolved)
                        broken = "unresolved-base-class";
                    else if (endsAtControllerBase)
                        isController = true;
                    else if (next is not null && (chain.Count >= MaxBaseDepth || chain.Contains(next)))
                        broken = "base-class-depth";
                    if (next is null || endsAtControllerBase)
                        break;
                    current = next;
                }
            }

            if (broken is not null)
            {
                // The class route can stand on what is not known. Only a class that shows it is a controller is noted.
                if (hasActions || isMarked)
                    context.Note(NoteKind.Undecided, broken, first.File.File.Path, first.Line);
                continue;
            }
            if (isController && !logical.Parts.Exists(p => p.IsAbstract || p.IsStatic))
                EmitRoutes(context, logical, chain, meter);
        }
    }

    /// <summary>
    /// The base class of a class: the first type of the base list of each part, when it is a
    /// class of the scan. A first type that is an interface of the scan means no base class.
    /// <paramref name="isResolved"/> is false when a first type is not in the scan (it can be a
    /// class or an interface) or the parts name different base classes.
    /// </summary>
    private static LogicalType? BaseClassOf(LogicalType type, TypeIndex index, WorkMeter meter, out bool endsAtControllerBase, out bool isResolved)
    {
        endsAtControllerBase = false;
        isResolved = true;
        LogicalType? found = null;
        foreach (var part in type.Parts)
        {
            if (part.Bases.Count == 0 || !meter.Charge())
                continue;
            var (name, reference) = part.Bases[0];
            if (name is "ControllerBase" or "Controller" && reference.Written is "ControllerBase" or "Controller" or "Microsoft.AspNetCore.Mvc.ControllerBase" or "Microsoft.AspNetCore.Mvc.Controller")
            {
                endsAtControllerBase = true;
                continue;
            }
            var resolved = index.Resolve(reference, meter);
            if (resolved is { Keyword: "interface" })
                continue;
            if (resolved is not { Keyword: "class" } || (found is not null && found != resolved))
            {
                isResolved = false;
                return null;
            }
            found = resolved;
        }
        if (endsAtControllerBase && found is not null)
            isResolved = false;
        return isResolved ? found : null;
    }

    private static void EmitRoutes(ExtractionContext context, LogicalType controller, List<LogicalType> chain, WorkMeter meter)
    {
        var first = controller.Parts[0];
        void Undecided(string reason, string path, int line) => context.Note(NoteKind.Undecided, reason, path, line);

        // The class route: the [Route] attributes of the nearest class of the chain that has any.
        var classRoutes = new List<string>();
        foreach (var type in chain)
        {
            foreach (var part in type.Parts)
            {
                if (part.HasTooManyRoutes || classRoutes.Count + part.Routes.Count > MaxRouteAttributes)
                {
                    Undecided("route-attribute-count", part.File.File.Path, part.Line);
                    return;
                }
                foreach (var route in part.Routes)
                {
                    if (route.Kind != Template.Static)
                    {
                        Undecided("dynamic-route", route.Path, route.Line);
                        return;
                    }
                    classRoutes.Add(route.Value);
                }
            }
            if (classRoutes.Count > 0)
                break;
        }
        var hasClassRoute = classRoutes.Count > 0;
        if (!hasClassRoute)
            classRoutes.Add("");

        var token = controller.Name.EndsWith("Controller", StringComparison.Ordinal) && controller.Name.Length > "Controller".Length
            ? controller.Name[..^"Controller".Length] : controller.Name;

        for (var level = 0; level < chain.Count; level++)
            foreach (var part in chain[level].Parts)
                foreach (var action in part.Actions)
                {
                    if (!meter.Charge())
                        return;
                    if (level > 0 && IsDeclaredBelow(chain, level, action.Name))
                    {
                        // Which attributes an override or a hiding method has is not read.
                        Undecided("overridden-action", first.File.File.Path, first.Line);
                        continue;
                    }
                    var hasDynamicRoute = action.Routes.Exists(r => r.Kind != Template.Static);
                    foreach (var verb in action.Verbs)
                    {
                        if (verb.Kind == Template.Dynamic || hasDynamicRoute)
                        {
                            Undecided("dynamic-route", action.Site.Path, verb.Line);
                            continue;
                        }
                        // A verb attribute with no template anywhere only limits the verb. The path
                        // then comes from conventional routing, which is set up in other code.
                        if (verb.Kind == Template.None && action.Routes.Count == 0 && !hasClassRoute)
                        {
                            Undecided("conventional-route", action.Site.Path, verb.Line);
                            continue;
                        }
                        foreach (var classRoute in classRoutes)
                        {
                            if (verb.Kind == Template.Static || action.Routes.Count == 0)
                                AddRoute(verb, classRoute, verb.Kind == Template.Static ? verb.Value : "");
                            else
                                foreach (var route in action.Routes)
                                    AddRoute(verb, classRoute, route.Value);
                        }
                    }

                    void AddRoute(VerbUse verb, string classRoute, string template)
                    {
                        if (classRoute.Length + template.Length > MaxRouteLength)
                        {
                            context.Note(NoteKind.Redacted, "value-too-long", action.Site.Path, verb.Line);
                            return;
                        }
                        // A template that starts with '/' or '~/' does not take the class route.
                        var declared = template.StartsWith('/') ? template
                            : template.StartsWith("~/", StringComparison.Ordinal) ? template[1..]
                            : RoutePath.Join(classRoute, template);
                        var raw = declared.Replace("[controller]", token, StringComparison.OrdinalIgnoreCase)
                            .Replace("[action]", action.Name, StringComparison.OrdinalIgnoreCase);
                        if (raw.Contains('['))
                        {
                            Undecided("dynamic-route", action.Site.Path, verb.Line);
                            return;
                        }
                        if (!meter.Charge(FactCost))
                            return;
                        var path = RoutePath.Normalize(raw);
                        var evidence = new List<SourceEvidence> { ControllerRoute.At(action.Site.Path, action.Site.Start, action.Site.End) };
                        if (level > 0)
                            evidence.Add(ControllerRoute.At(first.Header.Path, first.Header.Start, first.Header.End));
                        context.Add(FactKind.Operation, $"operation:{controller.Project.Id}:{verb.Verb} {path}", $"{verb.Verb} {path}", evidence,
                        [
                            .. controller.Project.Owner, ("transport", "http"), ("method", verb.Verb), .. RoutePath.Attributes(path, declared),
                            ("framework", "aspnetcore-controller"), ("controller", controller.Name), ("action", action.Name),
                            ("conditional", level > 0 ? Both(first.Conditional, action.Conditional) : action.Conditional),
                        ]);
                    }
                }
    }

    /// <summary>True when a class nearer to the controller than <paramref name="level"/> declares a method of that name.</summary>
    private static bool IsDeclaredBelow(List<LogicalType> chain, int level, string method)
    {
        for (var k = 0; k < level; k++)
            foreach (var part in chain[k].Parts)
                if (part.Methods is { } methods && methods.Contains(method))
                    return true;
        return false;
    }
}
