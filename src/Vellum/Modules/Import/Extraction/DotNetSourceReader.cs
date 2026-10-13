namespace Vellum.Modules.Import.Extraction;

internal sealed partial class DotNetExtractor
{
    private enum BuilderKind { NotBuilder, Unknown, ApplicationBuilder, Prefix }

    /// <summary>What an expression is as an endpoint route builder, and the route prefix it has when that is fixed by the text.</summary>
    private readonly record struct BuilderValue(BuilderKind Kind, string Prefix)
    {
        public static readonly BuilderValue NotBuilder = new(BuilderKind.NotBuilder, "");
        public static readonly BuilderValue Unknown = new(BuilderKind.Unknown, "");
        public static readonly BuilderValue ApplicationBuilder = new(BuilderKind.ApplicationBuilder, "");
        public static readonly BuilderValue Root = new(BuilderKind.Prefix, "");
    }

    /// <summary>
    /// Reads one C# file in passes that are each linear in its tokens: the declarations
    /// (<see cref="CSharpFile"/>), the members of each type, and the call sites. Minimal API
    /// routes are complete inside one file and become facts here; everything else is kept for
    /// the rules that need the whole scan.
    /// </summary>
    private sealed class SourceReader(ExtractionContext context, SourceText file, Project project, FileModel model, CSharpSource source, CSharpFile code, WorkMeter meter)
    {
        private readonly TokenList t = source.Tokens;
        private readonly TypeInfo?[] _infos = new TypeInfo?[code.Types.Count];
        private readonly Dictionary<CSharpDecl, BuilderValue> _builders = [];
        private readonly HashSet<CSharpDecl> _resolving = [];
        private readonly Dictionary<CSharpDecl, string?> _declaredTypes = [];
        private bool _massTransit;

        private Site SiteOf(int startToken, int endToken) =>
            new(file.Path, t.Line(startToken), Math.Max(t.Line(startToken), t.EndLine(endToken)));

        private void Undecided(string reason, int line) => context.Note(NoteKind.Undecided, reason, file.Path, line);

        public void Read()
        {
            _massTransit = project.UsesMassTransit || code.Usings.Contains("MassTransit");
            for (var index = 0; index < code.Types.Count; index++)
            {
                if (meter.Exhausted)
                    return;
                var info = ReadType(code.Types[index]);
                _infos[index] = info;
                model.Types.Add(info);
            }
            ReadCalls();
        }

        // ---- types and members ----

        private TypeInfo ReadType(CSharpType type)
        {
            var info = new TypeInfo(
                model, type, SiteOf(type.StartIndex, type.BodyOpen >= 0 ? type.BodyOpen : type.EndIndex),
                SiteOf(type.StartIndex, type.EndIndex), t.Line(type.KeywordIndex), source.Conditional(type.KeywordIndex));

            foreach (var attribute in type.Attributes)
            {
                if (attribute.Name == "ApiController")
                    info.IsApiController = true;
                else if (attribute.Name == "Route" && info.Routes.Count <= MaxRouteAttributes)
                    info.Routes.Add(TemplateOf(attribute));
            }
            info.HasTooManyRoutes = info.Routes.Count > MaxRouteAttributes;

            foreach (var declared in type.Bases)
            {
                if (!meter.Charge())
                    return info;
                if (declared.Name.Length == 0)
                    continue;
                var written = declared.Qualifier.Length == 0 ? declared.Name : declared.Qualifier + "." + declared.Name;
                info.Bases.Add((declared.Name, new TypeRef(written, type.Container, model, declared.Line)));
                if (!_massTransit || declared.Name != "IConsumer" || declared.ArgCount != 1)
                    continue;
                // "class X<T> : IConsumer<T>" consumes no one message type.
                if (HasTypeParameter(declared.ArgsStart, declared.ArgsEnd))
                    continue;
                if (code.DottedName(declared.ArgsStart, declared.ArgsEnd) is { } message)
                    info.Consumes.Add(new TypeRef(message, type.Container, model, t.Line(type.KeywordIndex)));
                else
                    Undecided("dynamic-message-type", t.Line(type.KeywordIndex));
            }
            if (type.Keyword == "class" && type.Bases.Count > 0)
                info.Methods = new HashSet<string>(StringComparer.Ordinal);
            if (type.Keyword == "class" && type.BodyOpen >= 0 && type.Bases.Any(b => b.Name is "BackgroundService" or "IHostedService"))
                info.Hosted = new HostedInfo();

            if (type.Keyword == "record" && type.ParametersOpen >= 0)
                ReadPositionalParameters(type.ParametersOpen, info);
            if (type.BodyOpen >= 0)
                ReadMembers(type, info);
            return info;
        }

        private bool HasTypeParameter(int start, int end)
        {
            for (var k = start; k >= 0 && k <= end; k++)
            {
                if (!meter.Charge())
                    return true;
                if (t.Id(k) && code.DeclAt(k) is { Kind: CSharpDeclKind.TypeParameter })
                    return true;
            }
            return false;
        }

        /// <summary>The route template of an attribute: its first positional argument when that is one string literal.</summary>
        private RouteTemplate TemplateOf(CSharpAttribute attribute)
        {
            var open = attribute.ArgsOpen;
            if (open < 0 || t.Match[open] == open + 1)
                return new RouteTemplate(Template.None, "", file.Path, attribute.Line);
            var close = t.Match[open];
            if (t.StaticString(open + 1) is { } value && (open + 2 == close || t.P(open + 2, ',')))
                return Static(value);
            if (t.Id(open + 1, "template") && t.P(open + 2, ':') && t.StaticString(open + 3) is { } named && (open + 4 == close || t.P(open + 4, ',')))
                return Static(named);
            // "Name = …", "Order = …": a property of the attribute, not a template.
            return new RouteTemplate(t.Id(open + 1) && t.P(open + 2, '=') && !t.P(open + 3, '=') ? Template.None : Template.Dynamic, "", file.Path, attribute.Line);

            RouteTemplate Static(string text) => text.Length > MaxRouteLength
                ? new RouteTemplate(Template.Dynamic, "", file.Path, attribute.Line)
                : new RouteTemplate(Template.Static, text, file.Path, attribute.Line);
        }

        /// <summary>
        /// The members of a type body, one after the other. A member starts with its attributes
        /// and ends at a ';' or after a '{…}' block of the body level. Nested brackets are stepped
        /// over, so each token of the file is read as a member token of one type only.
        /// </summary>
        private void ReadMembers(CSharpType type, TypeInfo info)
        {
            var isInterface = type.Keyword == "interface";
            var attributes = new List<CSharpAttribute>();
            var i = type.BodyOpen + 1;
            while (i < type.EndIndex)
            {
                if (!meter.Charge())
                    return;
                attributes.Clear();
                var first = i;
                while (t.P(i, '[') && t.Match[i] > i && meter.Charge())
                {
                    code.ReadAttributeGroup(i, attributes);
                    i = t.Match[i] + 1;
                }
                var start = i;
                int signature = -1, equals = -1;
                bool isPublic = isInterface, isStatic = false, isDelegate = false;
                while (i < type.EndIndex)
                {
                    if (!meter.Charge())
                        return;
                    if (t.P(i, ';'))
                    {
                        i++;
                        break;
                    }
                    if (t.Match[i] > i)
                    {
                        if (t.P(i, '{'))
                        {
                            if (signature < 0 && equals < 0 && t.Id(i - 1) && (t.Id(i + 1, "get") || t.Id(i + 1, "init") || t.Id(i + 1, "set")))
                                AddProperty(info, start, i, isPublic, isStatic);
                            i = t.Match[i] + 1;
                            // "{ get; } = value;" goes on to its ';'.
                            if (t.P(i, '=') && !t.P(i + 1, '>'))
                            {
                                equals = i++;
                                continue;
                            }
                            break;
                        }
                        if (t.P(i, '(') && signature < 0 && equals < 0)
                            signature = i;
                        i = t.Match[i] + 1;
                        continue;
                    }
                    if (signature < 0 && equals < 0)
                    {
                        if (t.P(i, '='))
                            equals = i;
                        else if (t.Id(i, "public"))
                            isPublic = true;
                        else if (t.Id(i, "static") || t.Id(i, "const"))
                            isStatic = true;
                        else if (t.Id(i, "delegate") || t.Id(i, "event") || t.Id(i, "operator"))
                            isDelegate = true;
                        else if (t.Id(i, "DbSet") && t.P(i + 1, '<'))
                            ReadDbSet(i, info);
                    }
                    i++;
                }

                if (signature < 0 || isDelegate || MethodName(signature) is not { } method)
                    continue;
                info.Methods?.Add(method);
                if (type.Keyword == "class" && isPublic && !isStatic && attributes.Count > 0)
                    AddAction(info, method, attributes, first, signature);
            }
        }

        /// <summary>The name before a parameter list: "Name(" or "Name&lt;T&gt;(".</summary>
        private string? MethodName(int signature)
        {
            var k = signature - 1;
            if (t.P(k, '>'))
                k = code.AngleOpen(k) - 1;
            return k >= 0 ? t.IdText(k) : null;
        }

        private void AddProperty(TypeInfo info, int start, int brace, bool isPublic, bool isStatic)
        {
            if (!isPublic || isStatic || info.Fields.Count > context.Limits.MaxListItems)
                return;
            var typeStart = start;
            while (typeStart < brace - 1 && t.Id(typeStart) && MemberModifiers.Contains(t[typeStart].Text) && meter.Charge())
                typeStart++;
            if (typeStart < brace - 1 && meter.Charge(brace - typeStart))
                info.Fields.Add($"{t[brace - 1].Text}:{t.Text(typeStart, brace - 2)}");
        }

        private void ReadPositionalParameters(int open, TypeInfo info)
        {
            var close = t.Match[open];
            int depth = 0, start = open + 1, equals = -1;
            for (var i = open + 1; i <= close; i++)
            {
                if (!meter.Charge() || info.Fields.Count > context.Limits.MaxListItems)
                    return;
                if (i < close && t.Match[i] > i)
                {
                    i = t.Match[i];
                    continue;
                }
                if (i < close && t.P(i, '<'))
                    depth++;
                else if (i < close && t.P(i, '>'))
                    depth = Math.Max(0, depth - 1);
                else if (i < close && t.P(i, '=') && !t.P(i + 1, '>') && equals < 0)
                    equals = i;
                else if (i == close || (t.P(i, ',') && depth == 0 && equals < 0) || (t.P(i, ',') && equals >= 0))
                {
                    // "[attributes] Type Name" or "Type Name = default".
                    var first = start;
                    while (t.P(first, '[') && t.Match[first] > first && meter.Charge())
                        first = t.Match[first] + 1;
                    var name = (equals >= 0 ? equals : i) - 1;
                    if (t.Id(name) && name > first && meter.Charge(name - first))
                        info.Fields.Add($"{t[name].Text}:{t.Text(first, name - 1)}");
                    start = i + 1;
                    depth = 0;
                    equals = -1;
                }
            }
        }

        private void ReadDbSet(int i, TypeInfo info)
        {
            var close = code.CloseAngle(i + 1);
            if (close < 0 || !t.Id(close + 1))
                return;
            info.DbSets.Add(new DbSetInfo(t[close + 1].Text, t.Text(i + 2, close - 1), new Site(file.Path, t.Line(i), t.Line(close + 1))));
        }

        private void AddAction(TypeInfo info, string method, List<CSharpAttribute> attributes, int first, int signature)
        {
            List<VerbUse>? verbs = null;
            List<RouteTemplate>? routes = null;
            foreach (var attribute in attributes)
            {
                if (attribute.Name == "NonAction")
                    return;
                if (attribute.Name == "Route")
                {
                    (routes ??= []).Add(TemplateOf(attribute));
                }
                else if (HttpVerbs.TryGetValue(attribute.Name, out var verb))
                {
                    var template = TemplateOf(attribute);
                    (verbs ??= []).Add(new VerbUse(verb, template.Kind, template.Value, attribute.Line));
                }
                if ((verbs?.Count ?? 0) > MaxRouteAttributes || (routes?.Count ?? 0) > MaxRouteAttributes)
                {
                    Undecided("route-attribute-count", t.Line(first));
                    return;
                }
            }
            if (verbs is not null)
                info.Actions.Add(new ActionInfo(method, SiteOf(first, t.Match[signature]), verbs, routes ?? [], source.Conditional(signature)));
        }

        // ---- call sites ----

        private void ReadCalls()
        {
            if (!meter.Charge(t.Count))
                return;
            // The one receive endpoint that is open, and the end of the endpoints nested in it.
            EndpointSite? endpoint = null;
            int endpointEnd = -1, nestedEnd = -1;
            for (var i = 1; i + 1 < t.Count; i++)
            {
                if (!t.P(i - 1, '.') || !t.Id(i))
                    continue;
                if (meter.Exhausted)
                    return;
                var name = t[i].Text;
                if (MapVerbs.TryGetValue(name, out var verb))
                {
                    if (t.P(i + 1, '(') && t.Match[i + 1] > i)
                        ReadMinimalRoute(i, verb);
                    continue;
                }
                if (code.TypeOf(i) is >= 0 and var typeIndex && _infos[typeIndex]?.Hosted is { } hosted)
                    ReadHostedAccess(i, name, hosted);
                if (!_massTransit)
                    continue;
                switch (name)
                {
                    case "Publish" or "Send":
                        ReadPublish(i, name == "Publish" ? "publish" : "send");
                        break;
                    case "ReceiveEndpoint" when t.P(i + 1, '(') && t.Match[i + 1] > i:
                    {
                        var open = i + 1;
                        var close = t.Match[open];
                        if (i < endpointEnd)
                        {
                            // MassTransit has no endpoint inside an endpoint. Which queue the inner consumers get is not read.
                            Undecided("nested-receive-endpoint", t.Line(i));
                            nestedEnd = Math.Max(nestedEnd, close);
                            break;
                        }
                        endpoint = null;
                        endpointEnd = close;
                        nestedEnd = -1;
                        if (t.StaticString(open + 1) is { } queue && t.P(open + 2, ','))
                        {
                            if (queue.Length > CSharpFile.MaxNameLength)
                                context.Note(NoteKind.Redacted, "value-too-long", file.Path, t.Line(i));
                            else
                                project.Endpoints.Add(endpoint = new EndpointSite(queue, SiteOf(i, close), source.Conditional(i), model));
                        }
                        else if (close > open + 1 && code.NextComma(open + 1) >= 0)
                        {
                            Undecided("dynamic-queue-name", t.Line(i));
                        }
                        break;
                    }
                    case "ConfigureConsumer" or "Consumer" when endpoint is not null && i < endpointEnd && i > nestedEnd && t.P(i + 1, '<'):
                    {
                        var close = code.CloseAngle(i + 1);
                        if (close > 0 && code.DottedName(i + 2, close - 1) is { } consumer)
                            endpoint.Consumers.Add(new TypeRef(consumer, ContainerAt(i), model, t.Line(i)));
                        break;
                    }
                    case "SetEntityName" when t.P(i + 1, '(') && t.P(i + 3, ')') && t.StaticString(i + 2) is { } entity:
                        ReadEntityName(i, entity);
                        break;
                    case "AddEntityFrameworkOutbox" when t.P(i + 1, '<') && code.CloseAngle(i + 1) is > 0 and var close && t.P(close + 1, '(') && t.Match[close + 1] > close:
                        project.OutboxCalls.Add(new OutboxCall(
                            name, code.DottedName(i + 2, close - 1) is { } dbContext ? dbContext[(dbContext.LastIndexOf('.') + 1)..] : null,
                            SiteOf(i, t.Match[close + 1]), source.Conditional(i)));
                        break;
                    case "UseBusOutbox" or "AddOutboxMessageEntity" or "AddOutboxStateEntity" or "AddInboxStateEntity"
                        or "AddInMemoryOutbox" or "UseInMemoryOutbox" when t.P(i + 1, '(') && t.Match[i + 1] > i:
                        project.OutboxCalls.Add(new OutboxCall(name, null, SiteOf(i, t.Match[i + 1]), source.Conditional(i)));
                        break;
                }
            }
        }

        /// <summary>The namespace and enclosing types of a token: what is visible to a type name written there.</summary>
        private string ContainerAt(int token) =>
            code.TypeOf(token) is >= 0 and var index && _infos[index] is { } info ? info.QualifiedName : code.FileNamespace;

        // ---- minimal API ----

        private void ReadMinimalRoute(int method, string verb)
        {
            var open = method + 1;
            var close = t.Match[open];
            if (close == open + 1)
                return;
            var receiver = Evaluate(method - 2, 0);
            if (receiver.Kind is BuilderKind.NotBuilder or BuilderKind.ApplicationBuilder)
                return;
            var line = t.Line(method);
            if (t.StaticString(open + 1) is not { } template || !t.P(open + 2, ','))
            {
                // One argument is no route with a handler. Two or more with a first one that is not one literal: undecided.
                if (code.NextComma(open + 1) >= 0)
                    Undecided("dynamic-route", line);
                return;
            }
            if (receiver.Kind != BuilderKind.Prefix)
            {
                Undecided("unresolved-route-prefix", line);
                return;
            }
            if (receiver.Prefix.Length + template.Length > MaxRouteLength)
            {
                context.Note(NoteKind.Redacted, "value-too-long", file.Path, line);
                return;
            }
            var declared = RoutePath.Join(receiver.Prefix, template);
            var path = RoutePath.Normalize(declared);
            if (!meter.Charge(FactCost))
                return;
            context.Add(FactKind.Operation, $"operation:{project.Id}:{verb} {path}", $"{verb} {path}",
                [MinimalApi.At(file.Path, line, t.EndLine(close))],
                [
                    .. project.Owner, ("transport", "http"), ("method", verb), .. RoutePath.Attributes(path, declared),
                    ("framework", "aspnetcore-minimal-api"), ("conditional", source.Conditional(method)),
                ]);
        }

        /// <summary>
        /// What the expression that ends at <paramref name="end"/> is. The walk goes from the last
        /// call of a chain to its root: <c>MapGroup("literal")</c> adds a segment, the calls of
        /// <see cref="GroupPassThrough"/> change nothing, and the root is a name (looked up in the
        /// scope of the token), <c>….Build()</c> on a web application builder, or <c>WebApplication.Create(…)</c>.
        /// </summary>
        private BuilderValue Evaluate(int end, int depth)
        {
            List<string>? segments = null;
            var length = 0;
            BuilderValue root;
            var current = end;
            while (true)
            {
                if (!meter.Charge())
                    return BuilderValue.Unknown;
                if (t.Id(current))
                {
                    if (t.P(current - 1, '.'))
                        return BuilderValue.Unknown;
                    root = code.DeclAt(current) is { } decl ? ValueOf(decl, depth) : BuilderValue.Unknown;
                    break;
                }
                if (current >= 0 && current < t.Count && t[current].Kind is TokKind.Number or TokKind.String or TokKind.Char)
                    return BuilderValue.NotBuilder;
                if (t.P(current, '}') && t.Match[current] is > 0 and var brace)
                {
                    // "new T { … }" and "new T(…) { … }" construct an object, which is no endpoint builder.
                    var before = t.P(brace - 1, ')') && t.Match[brace - 1] > 0 ? t.Match[brace - 1] - 1 : brace - 1;
                    return t.Id(before) && t.Id(before - 1, "new") ? BuilderValue.NotBuilder : BuilderValue.Unknown;
                }
                if (!t.P(current, ')'))
                    return BuilderValue.Unknown;
                var open = t.Match[current];
                var method = open - 1;
                if (open < 1 || !t.Id(method))
                    return BuilderValue.Unknown;
                var name = t[method].Text;
                if (!t.P(method - 1, '.'))
                    return t.Id(method - 1, "new") ? BuilderValue.NotBuilder : BuilderValue.Unknown;
                if (name == "MapGroup")
                {
                    if (open + 2 != current || t.StaticString(open + 1) is not { } segment)
                        return BuilderValue.Unknown;
                    length += segment.Length;
                    if (length > MaxRouteLength || (segments ??= []).Count >= MaxGroupSegments)
                        return BuilderValue.Unknown;
                    segments.Add(segment);
                    current = method - 2;
                    continue;
                }
                if (GroupPassThrough.Contains(name))
                {
                    current = method - 2;
                    continue;
                }
                var onWebApplication = t.Id(method - 2, "WebApplication") && !t.P(method - 3, '.') && code.DeclAt(method - 2) is null;
                if (name == "Build")
                    root = BuildReceiver(method - 2, depth);
                else if (onWebApplication && name == "Create")
                    root = BuilderValue.Root;
                else if (onWebApplication && ApplicationBuilderFactories.Contains(name))
                    root = BuilderValue.ApplicationBuilder;
                else
                    return BuilderValue.Unknown;
                break;
            }

            if (root.Kind != BuilderKind.Prefix || segments is null)
                return root.Kind == BuilderKind.ApplicationBuilder && segments is not null ? BuilderValue.Unknown : root;
            var prefix = root.Prefix;
            for (var k = segments.Count - 1; k >= 0; k--)
            {
                if (!meter.Charge(prefix.Length + 1) || prefix.Length + segments[k].Length > MaxRouteLength)
                    return BuilderValue.Unknown;
                prefix = RoutePath.Join(prefix, segments[k]);
            }
            return new BuilderValue(BuilderKind.Prefix, prefix);
        }

        /// <summary>"x.Build()" is the application when x is a web application builder.</summary>
        private BuilderValue BuildReceiver(int receiver, int depth)
        {
            if (t.Id(receiver) && !t.P(receiver - 1, '.'))
            {
                var value = code.DeclAt(receiver) is { } decl ? ValueOf(decl, depth) : BuilderValue.Unknown;
                return value.Kind switch
                {
                    BuilderKind.ApplicationBuilder => BuilderValue.Root,
                    BuilderKind.NotBuilder => BuilderValue.NotBuilder,
                    _ => BuilderValue.Unknown,
                };
            }
            // "WebApplication.CreateBuilder(args).Build()".
            if (t.P(receiver, ')') && t.Match[receiver] is > 2 and var open && t.Id(open - 1) && t.P(open - 2, '.')
                && ApplicationBuilderFactories.Contains(t[open - 1].Text)
                && t.Id(open - 3, "WebApplication") && !t.P(open - 4, '.') && code.DeclAt(open - 3) is null)
                return BuilderValue.Root;
            return BuilderValue.Unknown;
        }

        private BuilderValue ValueOf(CSharpDecl decl, int depth)
        {
            if (_builders.TryGetValue(decl, out var known))
                return known;
            if (depth >= MaxPrefixDepth)
            {
                // A chain of variables longer than the bound is not followed. The value is not kept, so a shorter way to it still works.
                Undecided("route-prefix-depth", t.Line(decl.NameIndex));
                return BuilderValue.Unknown;
            }
            if (!_resolving.Add(decl))
                return BuilderValue.Unknown;
            var value = Compute(decl, depth);
            _resolving.Remove(decl);
            _builders[decl] = value;
            return value;
        }

        private BuilderValue Compute(CSharpDecl decl, int depth)
        {
            if (decl.Kind == CSharpDeclKind.TypeParameter || decl.IsArray)
                return BuilderValue.NotBuilder;
            if (decl.Kind == CSharpDeclKind.LambdaParameter)
                return BuilderValue.Unknown;
            if (!decl.IsVar)
            {
                switch (decl.TypeName)
                {
                    case null: return BuilderValue.Unknown;
                    case "WebApplication": return BuilderValue.Root;
                    case "WebApplicationBuilder": return BuilderValue.ApplicationBuilder;
                    case "IEndpointRouteBuilder" or "RouteGroupBuilder": break;
                    default: return BuilderValue.NotBuilder;
                }
            }
            if (decl.Assignments is not { Count: > 0 } assignments)
                return BuilderValue.Unknown;
            // Every assignment to the name must give the same value.
            BuilderValue? agreed = null;
            foreach (var assignment in assignments)
            {
                if (!meter.Charge())
                    return BuilderValue.Unknown;
                var end = code.ExpressionEnd(assignment);
                var value = end < 0 ? BuilderValue.Unknown : Evaluate(end, depth + 1);
                if (value.Kind == BuilderKind.Unknown || (agreed is { } other && other != value))
                    return BuilderValue.Unknown;
                agreed = value;
            }
            return agreed ?? BuilderValue.Unknown;
        }

        // ---- declared types of receivers ----

        /// <summary>
        /// The declared type of the name at <paramref name="receiver"/> in the scope of that token,
        /// or null. The name stands alone or after <c>this.</c>.
        /// </summary>
        private string? ReceiverType(int receiver)
        {
            if (!t.Id(receiver) || (t.P(receiver - 1, '.') && !(t.Id(receiver - 2, "this") && !t.P(receiver - 3, '.'))))
                return null;
            return code.DeclAt(receiver) is { } decl ? DeclaredType(decl) : null;
        }

        /// <summary>
        /// The type a declaration states: its type name; or, for <c>var</c>, the type that every
        /// assignment fixes by its text: <c>new T(…)</c>, <c>….GetRequiredService&lt;T&gt;()</c> and the like, and
        /// <c>factory.CreateDbContext[Async](…)</c> on an <c>IDbContextFactory&lt;T&gt;</c>.
        /// </summary>
        private string? DeclaredType(CSharpDecl decl)
        {
            if (_declaredTypes.TryGetValue(decl, out var known))
                return known;
            string? type = null;
            if (decl.Kind == CSharpDeclKind.Variable && !decl.IsArray)
            {
                if (!decl.IsVar)
                {
                    type = decl.TypeName;
                }
                else if (decl.Assignments is { Count: > 0 } assignments)
                {
                    foreach (var assignment in assignments)
                    {
                        var assigned = meter.Charge() ? AssignedType(assignment) : null;
                        if (assigned is null || (type is not null && type != assigned))
                        {
                            type = null;
                            break;
                        }
                        type = assigned;
                    }
                }
            }
            _declaredTypes[decl] = type;
            return type;
        }

        private string? AssignedType(int nameIndex)
        {
            var end = code.ExpressionEnd(nameIndex);
            if (end < 0 || !t.P(end, ')'))
                return null;
            var open = t.Match[end];
            if (open < 2)
                return null;
            var start = nameIndex + 2;
            if (t.Id(start, "new"))
            {
                // The whole expression is "new A.B.T(…)".
                return open > start + 1 && code.DottedName(start + 1, open - 1) is { } constructed
                    ? constructed[(constructed.LastIndexOf('.') + 1)..] : null;
            }
            if (t.P(open - 1, '>'))
            {
                var angle = code.AngleOpen(open - 1);
                return angle > 1 && t.Id(angle - 1) && t.P(angle - 2, '.') && ServiceGetters.Contains(t[angle - 1].Text)
                    && code.DottedName(angle + 1, open - 2) is { } service
                    ? service[(service.LastIndexOf('.') + 1)..] : null;
            }
            if (t.Id(open - 1) && t.P(open - 2, '.') && t[open - 1].Text is "CreateDbContext" or "CreateDbContextAsync"
                && t.Id(open - 3) && !t.P(open - 4, '.') && code.DeclAt(open - 3) is { IsVar: false, TypeName: "IDbContextFactory" or "PooledDbContextFactory" } factory)
                return factory.TypeArgument;
            return null;
        }

        // ---- hosted services ----

        private void ReadHostedAccess(int member, string name, HostedInfo hosted)
        {
            if (ReceiverType(member - 2) is not { } type)
                return;
            var line = t.Line(member);
            var isCall = t.P(member + 1, '(');
            if ((isCall || t.P(member + 1, '<')) && PublishApis.TryGetValue(name, out var receivers) && receivers.Contains(type))
            {
                if (hosted.PublishLine == 0)
                    hosted.PublishLine = line;
                return;
            }
            if (name == "Set" && t.P(member + 1, '<'))
            {
                var close = code.CloseAngle(member + 1);
                if (close > 0 && t.P(close + 1, '(') && t.Match[close + 1] > close && !IsWrite(t.Match[close + 1] + 1)
                    && code.DottedName(member + 2, close - 1) is { } entity)
                    hosted.SetCalls.TryAdd((type, entity[(entity.LastIndexOf('.') + 1)..]), line);
                return;
            }
            if (!isCall && !IsWrite(member + 1) && !(t.P(member + 1, '=') && !t.P(member + 2, '=')))
                hosted.Reads.TryAdd((type, name), line);
        }

        /// <summary>".Add(" and the other calls that change a set without reading it.</summary>
        private bool IsWrite(int dot) => t.P(dot, '.') && t.Id(dot + 1) && SetWrites.Contains(t[dot + 1].Text);

        // ---- MassTransit ----

        private void ReadPublish(int method, string mode)
        {
            int open, first, last;
            var after = method + 1;
            if (t.P(after, '<'))
            {
                var close = code.CloseAngle(after);
                if (close < 0 || !t.P(close + 1, '('))
                    return;
                open = close + 1;
                (first, last) = (after + 1, close - 1);
            }
            else if (t.P(after, '('))
            {
                open = after;
                (first, last) = (-1, -1);
                if (t.Id(open + 1, "new") && t.Id(open + 2))
                {
                    first = last = open + 2;
                    while (t.P(last + 1, '.') && t.Id(last + 2) && meter.Charge())
                        last += 2;
                    if (!(t.P(last + 1, '(') || t.P(last + 1, '{')))
                        first = -1;
                }
            }
            else
            {
                return;
            }
            if (t.Match[open] < 0 || ReceiverType(method - 2) is not { } receiver || !BusTypes.Contains(receiver))
                return;
            // "Publish<T>" with T a type parameter publishes no one message type.
            if (first >= 0 && HasTypeParameter(first, last))
                return;
            if (first < 0 || code.DottedName(first, last) is not { } message)
            {
                Undecided("dynamic-message-type", t.Line(method));
                return;
            }
            project.Publishes.Add(new PublishSite(
                new TypeRef(message, ContainerAt(method), model, t.Line(method)), mode, SiteOf(method, t.Match[open]), source.Conditional(method)));
        }

        /// <summary>"Message&lt;T&gt;(m =&gt; m.SetEntityName("literal"))": the call that holds the lambda, at most three brackets up.</summary>
        private void ReadEntityName(int method, string entity)
        {
            var parent = t.Parent[method];
            for (var level = 0; level < 3 && parent > 2; level++, parent = t.Parent[parent])
            {
                if (!t.P(parent, '('))
                    continue;
                var angle = code.AngleOpen(parent - 1);
                if (angle < 2 || !t.Id(angle - 1, "Message") || !t.P(angle - 2, '.') || t.Match[parent] < 0)
                    return;
                if (HasTypeParameter(angle + 1, parent - 2))
                    return;
                if (entity.Length > CSharpFile.MaxNameLength)
                    context.Note(NoteKind.Redacted, "value-too-long", file.Path, t.Line(method));
                else if (code.DottedName(angle + 1, parent - 2) is { } message)
                    project.EntityNames.Add(new EntityNameSite(
                        new TypeRef(message, ContainerAt(method), model, t.Line(angle - 1)), entity, SiteOf(angle - 1, t.Match[parent]), source.Conditional(method)));
                return;
            }
        }
    }
}
