using System.Xml;

namespace Vellum.Modules.Import.Extraction;

/// <summary>
/// .NET projects and C# source. Nothing is compiled, restored or loaded: a project file is
/// read as XML, and source is read as tokens (see <see cref="CSharpLexer"/>). A C# file
/// belongs to the nearest project file at or above its directory; a file with no project is
/// noted and not read. <c>&lt;slug&gt;</c> is the project file name in lower case with other
/// characters as '-' (<c>Orders.Api.csproj</c> gives <c>orders-api</c>).
/// <para>Rules, and what each does not recognise:</para>
/// <list type="bullet">
/// <item><c>dotnet.project</c> — <c>service:&lt;slug&gt;</c> when the SDK is Web or Worker, or the
/// output type is Exe. Not: <c>Directory.Build.props</c>, imports, conditions, old-style
/// project files without an <c>Sdk</c> attribute that are not Exe.</item>
/// <item><c>dotnet.package-reference</c> — <c>dependency:&lt;slug&gt;:nuget:&lt;package&gt;</c> and
/// <c>dependency:&lt;slug&gt;:project:&lt;name&gt;</c>. Not: central package versions, transitive packages.</item>
/// <item><c>dotnet.minimal-api-route</c> — <c>MapGet/Post/Put/Patch/Delete("literal", …)</c> on a
/// receiver whose prefix is known from this file: a variable assigned from <c>….Build()</c>,
/// a <c>WebApplication</c> parameter, or a chain of <c>MapGroup("literal")</c> from one of
/// those. Not: <c>MapMethods</c>, <c>MapFallback</c>, a route that is not one string literal,
/// a receiver that is an <c>IEndpointRouteBuilder</c> or <c>RouteGroupBuilder</c> parameter
/// (the prefix is set by a caller this rule does not follow), a prefix from another file.</item>
/// <item><c>dotnet.controller-route</c> — <c>[HttpGet]</c> and the other verb attributes on
/// members of a class with <c>[ApiController]</c> or a <c>ControllerBase</c>/<c>Controller</c>
/// base, joined with the class <c>[Route]</c>; <c>[controller]</c> and <c>[action]</c>
/// replaced. Not: <c>[AcceptVerbs]</c>, conventional routing (a verb attribute with no template
/// on a class with no route is noted), <c>[area]</c>, a template that
/// is a constant or an expression, a route inherited from a base class.</item>
/// <item><c>dotnet.masstransit-consumer</c> — <c>class X : IConsumer&lt;T&gt;</c>. Not: consumers
/// registered by convention without the interface, sagas, routing slips.</item>
/// <item><c>dotnet.masstransit-receive-endpoint</c> — <c>ReceiveEndpoint("literal", e =&gt; … ConfigureConsumer&lt;X&gt; …)</c>
/// gives the queue of consumer X. Not: endpoint name formatters, <c>ConsumerDefinition</c>,
/// <c>ConfigureEndpoints</c> conventions (the queue is then unknown and left out).</item>
/// <item><c>dotnet.masstransit-publish</c> — <c>.Publish&lt;T&gt;(…)</c>, <c>.Publish(new T…)</c>,
/// <c>.Send&lt;T&gt;(…)</c>, <c>.Send(new T…)</c> on a receiver declared in the same file as
/// <c>IPublishEndpoint</c>, <c>IBus</c>, <c>IBusControl</c>, <c>ISendEndpoint</c>,
/// <c>ISendEndpointProvider</c> or <c>ConsumeContext</c>. A message passed as a variable is
/// noted, not guessed. Not: any other receiver (silently, because <c>Send</c> and
/// <c>Publish</c> are common names), <c>PublishBatch</c>, scheduled messages.</item>
/// <item><c>dotnet.masstransit-entity-name</c> — <c>Message&lt;T&gt;(m =&gt; m.SetEntityName("literal"))</c>
/// gives the channel of T.</item>
/// <item><c>dotnet.masstransit-outbox</c> — <c>AddEntityFrameworkOutbox&lt;TContext&gt;</c>, with
/// <c>UseBusOutbox</c> and the outbox entity mappings as more evidence; and
/// <c>AddInMemoryOutbox</c>/<c>UseInMemoryOutbox</c> as a separate, weaker fact.</item>
/// <item><c>dotnet.outbox-handwritten</c> — both of: a <c>DbSet&lt;T&gt;</c> whose entity name starts
/// with <c>Outbox</c>, and in the same project a class derived from <c>BackgroundService</c>
/// or implementing <c>IHostedService</c> whose body names that set or entity. Not: an outbox
/// table without a dispatcher in the same project, a dispatcher in another process, other
/// libraries (CAP, NServiceBus, Wolverine).</item>
/// <item><c>dotnet.efcore-dbcontext</c> — a class derived from <c>DbContext</c> or an Identity
/// context, with its <c>DbSet&lt;T&gt;</c> members. The store kind comes from the one EF Core
/// provider package of the project, else <c>unknown</c>. Not: entities mapped only in
/// <c>OnModelCreating</c>, Dapper or raw SQL access, a context derived from a custom base.</item>
/// <item><c>dotnet.message-contract</c> — the <c>record</c>/<c>class</c>/<c>interface</c>
/// declaration of a type that a consumer, publish or send names: positional parameters and
/// public properties as fields. Not: inherited members, a type declared outside the scan.</item>
/// </list>
/// </summary>
internal sealed class DotNetExtractor : IExtractor
{
    private static readonly ExtractionRule ProjectRule = new("dotnet.project", 1);
    private static readonly ExtractionRule PackageRule = new("dotnet.package-reference", 1);
    private static readonly ExtractionRule MinimalApi = new("dotnet.minimal-api-route", 1);
    private static readonly ExtractionRule ControllerRoute = new("dotnet.controller-route", 1);
    private static readonly ExtractionRule Consumer = new("dotnet.masstransit-consumer", 1);
    private static readonly ExtractionRule ReceiveEndpoint = new("dotnet.masstransit-receive-endpoint", 1);
    private static readonly ExtractionRule Publish = new("dotnet.masstransit-publish", 1);
    private static readonly ExtractionRule EntityName = new("dotnet.masstransit-entity-name", 1);
    private static readonly ExtractionRule Outbox = new("dotnet.masstransit-outbox", 1);
    private static readonly ExtractionRule HandwrittenOutbox = new("dotnet.outbox-handwritten", 1);
    private static readonly ExtractionRule DbContextRule = new("dotnet.efcore-dbcontext", 1);
    private static readonly ExtractionRule Contract = new("dotnet.message-contract", 1);

    public string Id => "dotnet";
    public IReadOnlyList<ExtractionRule> Rules =>
    [
        ProjectRule, PackageRule, MinimalApi, ControllerRoute, Consumer, ReceiveEndpoint, Publish,
        EntityName, Outbox, HandwrittenOutbox, DbContextRule, Contract,
    ];

    /// <summary>Package id, or a prefix of it up to a dot, to what the package is.</summary>
    private static readonly (string Package, string Technology, string Role, string? Detail)[] KnownPackages =
    [
        ("MassTransit.RabbitMQ", "masstransit", "messaging", "rabbitmq"),
        ("MassTransit.Kafka", "masstransit", "messaging", "kafka"),
        ("MassTransit.Azure.ServiceBus.Core", "masstransit", "messaging", "azure-service-bus"),
        ("MassTransit.AmazonSQS", "masstransit", "messaging", "amazon-sqs"),
        ("MassTransit.EntityFrameworkCore", "masstransit", "messaging-persistence", null),
        ("MassTransit", "masstransit", "messaging", null),
        ("RabbitMQ.Client", "rabbitmq", "messaging", "rabbitmq"),
        ("Confluent.Kafka", "kafka", "messaging", "kafka"),
        ("Npgsql.EntityFrameworkCore.PostgreSQL", "efcore", "data", "postgres"),
        ("Microsoft.EntityFrameworkCore.SqlServer", "efcore", "data", "sqlserver"),
        ("Microsoft.EntityFrameworkCore.Sqlite", "efcore", "data", "sqlite"),
        ("Microsoft.EntityFrameworkCore.InMemory", "efcore", "data", "in-memory"),
        ("Microsoft.EntityFrameworkCore.Cosmos", "efcore", "data", "cosmosdb"),
        ("Pomelo.EntityFrameworkCore.MySql", "efcore", "data", "mysql"),
        ("MySql.EntityFrameworkCore", "efcore", "data", "mysql"),
        ("Oracle.EntityFrameworkCore", "efcore", "data", "oracle"),
        ("Microsoft.EntityFrameworkCore", "efcore", "data", null),
        ("Npgsql", "npgsql", "data", "postgres"),
        ("Microsoft.Data.SqlClient", "sqlclient", "data", "sqlserver"),
        ("MongoDB.Driver", "mongodb", "data", "mongodb"),
        ("StackExchange.Redis", "redis", "data", "redis"),
        ("Dapper", "dapper", "data", null),
        ("Refit", "refit", "http-client", null),
    ];

    private static readonly Dictionary<string, string> HttpVerbs = new(StringComparer.Ordinal)
    {
        ["HttpGet"] = "GET", ["HttpPost"] = "POST", ["HttpPut"] = "PUT", ["HttpPatch"] = "PATCH",
        ["HttpDelete"] = "DELETE", ["HttpHead"] = "HEAD", ["HttpOptions"] = "OPTIONS",
    };

    private static readonly Dictionary<string, string> MapVerbs = new(StringComparer.Ordinal)
    {
        ["MapGet"] = "GET", ["MapPost"] = "POST", ["MapPut"] = "PUT", ["MapPatch"] = "PATCH", ["MapDelete"] = "DELETE",
    };

    private static readonly HashSet<string> Modifiers = new(StringComparer.Ordinal)
    {
        "public", "private", "protected", "internal", "static", "sealed", "abstract", "partial", "readonly",
        "unsafe", "new", "file", "ref", "virtual", "override", "async", "extern", "required", "record",
    };

    private static readonly HashSet<string> BusTypes = new(StringComparer.Ordinal)
    {
        "IPublishEndpoint", "IBus", "IBusControl", "ISendEndpoint", "ISendEndpointProvider", "ConsumeContext",
    };

    private static readonly HashSet<string> DbContextBases = new(StringComparer.Ordinal)
    {
        "DbContext", "IdentityDbContext", "IdentityUserContext", "ApiAuthorizationDbContext",
    };

    // Calls that return the route group they were called on.
    private static readonly HashSet<string> GroupPassThrough = new(StringComparer.Ordinal)
    {
        "WithTags", "RequireAuthorization", "AllowAnonymous", "WithOpenApi", "AddEndpointFilter", "WithMetadata",
        "RequireCors", "RequireRateLimiting", "WithGroupName", "DisableAntiforgery", "ProducesProblem",
        "ProducesValidationProblem", "CacheOutput", "ExcludeFromDescription", "DisableRateLimiting",
    };

    // Bounds that keep the work linear in the size of a file that was written to be slow.
    private const int MaxStatementTokens = 256;
    private const int MaxCallTokens = 4096;
    private const int MaxTypeNesting = 8;

    private sealed record Site(string Path, int Start, int End);

    private sealed class Project(string path, string directory, string name)
    {
        public string Path { get; } = path;
        public string Directory { get; } = directory;
        public string Name { get; } = name;
        public string Slug { get; } = TextUtil.Slug(name);
        public string? ServiceKey { get; set; }
        public bool UsesMassTransit { get; set; }
        public HashSet<string> EfStores { get; } = new(StringComparer.Ordinal);

        public List<(string Class, string Message, Site Site)> Consumers { get; } = [];
        public List<(string Consumer, string Queue, Site Site)> Endpoints { get; } = [];
        public List<(string Message, string Mode, Site Site)> Publishes { get; } = [];
        public List<(string Message, string Entity, Site Site)> EntityNames { get; } = [];
        public List<(string Call, string? DbContext, Site Site)> OutboxCalls { get; } = [];
        public List<(string Context, string Set, string Entity, Site Site)> DbSets { get; } = [];
        public List<(string Class, HashSet<string> Names, Site Site)> HostedServices { get; } = [];

        public IEnumerable<(string, string?)> Owner => [("service", ServiceKey), ("project", Path)];
    }

    private sealed record AttributeUse(string Name, int ArgsOpen, int Line);
    private sealed record BaseType(string Name, List<string> TypeArguments);
    private sealed record TypeDecl(
        string Keyword, string Name, int KeywordIndex, int StartIndex, List<AttributeUse> Attributes,
        int ParametersOpen, List<BaseType> Bases, int BodyOpen, int EndIndex, string Namespace);

    public void Run(ExtractionContext context)
    {
        var projects = new List<Project>();
        foreach (var file in context.Files)
            if (file.Extension == ".csproj" && ReadProject(context, file) is { } project)
                projects.Add(project);

        var sources = new List<(SourceText File, Project Project, TokenList Tokens, List<TypeDecl> Types)>();
        foreach (var file in context.Files)
        {
            if (context.Stopped)
                return;
            if (file.Extension != ".cs")
                continue;
            // The nearest project: the longest directory that holds the file.
            var owner = projects.Where(p => file.IsUnder(p.Directory))
                .OrderByDescending(p => p.Directory.Length).ThenBy(p => p.Path, StringComparer.Ordinal).FirstOrDefault();
            if (owner is null)
            {
                context.Note(NoteKind.Undecided, "no-owning-project", file.Path);
                continue;
            }
            var tokens = CSharpLexer.Tokenize(file.Content);
            var types = ReadTypes(tokens);
            sources.Add((file, owner, tokens, types));
            new SourceReader(context, file, owner, tokens, types).Read();
        }

        var messages = new HashSet<string>(StringComparer.Ordinal);
        foreach (var project in projects)
        {
            Emit(context, project);
            messages.UnionWith(project.Consumers.Select(c => c.Message));
            messages.UnionWith(project.Publishes.Select(p => p.Message));
        }
        foreach (var (file, project, tokens, types) in sources)
            foreach (var type in types.Where(t => t.Keyword != "struct" && messages.Contains(t.Name)))
                AddContract(context, file, project, tokens, type);
    }

    // ---- project files ----------------------------------------------------------------

    private static Project? ReadProject(ExtractionContext context, SourceText file)
    {
        var project = new Project(file.Path, file.Directory, file.Name[..^".csproj".Length]);
        string? sdk = null, outputType = null, framework = null;
        var projectLine = 1;
        var packages = new List<(string Name, string? Version, int Line)>();
        var references = new List<(string Path, int Line)>();
        try
        {
            using var reader = XmlReader.Create(new StringReader(file.Content), new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, IgnoreComments = true,
                IgnoreProcessingInstructions = true, MaxCharactersFromEntities = 1024,
            });
            var lines = (IXmlLineInfo)reader;
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element)
                    continue;
                var line = lines.LineNumber;
                switch (reader.LocalName)
                {
                    case "Project" when reader.Depth == 0:
                        sdk = reader.GetAttribute("Sdk");
                        projectLine = line;
                        break;
                    case "OutputType":
                        outputType = reader.ReadElementContentAsString().Trim();
                        break;
                    case "TargetFramework" or "TargetFrameworks":
                        framework = reader.ReadElementContentAsString().Trim();
                        break;
                    case "PackageReference" when (reader.GetAttribute("Include") ?? reader.GetAttribute("Update")) is { Length: > 0 } package:
                        packages.Add((package.Trim(), reader.GetAttribute("Version")?.Trim(), line));
                        break;
                    case "ProjectReference" when reader.GetAttribute("Include") is { Length: > 0 } reference:
                        references.Add((reference.Replace('\\', '/'), line));
                        break;
                }
            }
        }
        catch (XmlException exception)
        {
            context.Note(NoteKind.ParseFailure, "xml-syntax", file.Path, Math.Max(1, exception.LineNumber));
            return null;
        }
        catch (InvalidOperationException)
        {
            context.Note(NoteKind.ParseFailure, "xml-syntax", file.Path);
            return null;
        }

        var kind = sdk is null ? null
            : sdk.StartsWith("Microsoft.NET.Sdk.Web", StringComparison.OrdinalIgnoreCase) ? "web"
            : sdk.StartsWith("Microsoft.NET.Sdk.Worker", StringComparison.OrdinalIgnoreCase) ? "worker"
            : null;
        if (kind is null && outputType is not null && (outputType.Equals("Exe", StringComparison.OrdinalIgnoreCase) || outputType.Equals("WinExe", StringComparison.OrdinalIgnoreCase)))
            kind = "executable";
        if (kind is not null)
        {
            project.ServiceKey = "service:" + project.Slug;
            context.Add(FactKind.Service, project.ServiceKey, project.Name, ProjectRule.At(file.Path, projectLine, projectLine),
                ("language", "dotnet"), ("kind", kind), ("project", file.Path), ("sdk", sdk),
                ("outputType", outputType), ("targetFramework", framework));
        }

        foreach (var (name, version, line) in packages)
        {
            var known = Known(name);
            if (known?.Technology == "masstransit")
                project.UsesMassTransit = true;
            if (known is { Technology: "efcore", Detail: { } store })
                project.EfStores.Add(store);
            context.Add(FactKind.Dependency, $"dependency:{project.Slug}:nuget:{name}", name, PackageRule.At(file.Path, line, line),
                ("ecosystem", "nuget"), ("package", name), ("version", version), ("project", file.Path),
                ("service", project.ServiceKey), ("technology", known?.Technology), ("role", known?.Role),
                (known?.Role == "data" ? "store" : "broker", known?.Detail));
        }
        foreach (var (reference, line) in references)
        {
            var slash = reference.LastIndexOf('/');
            var name = reference[(slash + 1)..];
            if (name.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                name = name[..^".csproj".Length];
            context.Add(FactKind.Dependency, $"dependency:{project.Slug}:project:{name}", name, PackageRule.At(file.Path, line, line),
                ("ecosystem", "project"), ("reference", reference), ("project", file.Path), ("service", project.ServiceKey));
        }
        return project;
    }

    private static (string Package, string Technology, string Role, string? Detail)? Known(string package)
    {
        foreach (var known in KnownPackages)
            if (package.Equals(known.Package, StringComparison.OrdinalIgnoreCase)
                || (package.Length > known.Package.Length && package[known.Package.Length] == '.'
                    && package.StartsWith(known.Package, StringComparison.OrdinalIgnoreCase)))
                return known;
        return null;
    }

    // ---- type declarations ------------------------------------------------------------

    private static List<TypeDecl> ReadTypes(TokenList t)
    {
        var types = new List<TypeDecl>();
        var enclosing = new Stack<int>();   // end index of each type around the current token
        var ns = "";
        for (var i = 0; i < t.Count; i++)
        {
            if (!t.Id(i))
                continue;
            var keyword = t[i].Text;
            if (keyword == "namespace" && t.Id(i + 1))
            {
                var end = i + 1;
                while (t.P(end + 1, '.') && t.Id(end + 2))
                    end += 2;
                ns = t.Text(i + 1, end);
                continue;
            }
            if (keyword is not ("class" or "record" or "interface" or "struct"))
                continue;
            // "where T : class", "x.record", "typeof(struct)".
            if (t.P(i - 1, ':') || t.P(i - 1, ',') || t.P(i - 1, '.') || t.P(i - 1, '('))
                continue;
            var nameIndex = i + 1;
            if (keyword == "record" && (t.Id(nameIndex, "class") || t.Id(nameIndex, "struct")))
                nameIndex++;
            if (!t.Id(nameIndex))
                continue;

            var j = nameIndex + 1;
            if (t.P(j, '<'))
            {
                var close = t.CloseAngle(j);
                if (close < 0)
                    continue;
                j = close + 1;
            }
            if (!(t.P(j, '(') || t.P(j, ':') || t.P(j, '{') || t.P(j, ';') || t.Id(j, "where")))
                continue;
            var parametersOpen = -1;
            if (t.P(j, '('))
            {
                if (t.Match[j] < 0)
                    continue;
                parametersOpen = j;
                j = t.Match[j] + 1;
            }

            var bases = new List<BaseType>();
            if (t.P(j, ':'))
            {
                j++;
                while (t.Id(j) && !t.Id(j, "where"))
                {
                    while (t.P(j + 1, '.') && t.Id(j + 2))
                        j += 2;
                    var baseName = t[j].Text;
                    j++;
                    var arguments = new List<string>();
                    if (t.P(j, '<'))
                    {
                        var close = t.CloseAngle(j);
                        if (close < 0)
                            break;
                        arguments = SplitTypeArguments(t, j + 1, close - 1);
                        j = close + 1;
                    }
                    if (t.P(j, '(') && t.Match[j] > j)
                        j = t.Match[j] + 1;
                    bases.Add(new BaseType(baseName, arguments));
                    if (!t.P(j, ','))
                        break;
                    j++;
                }
            }

            // Constraints, up to the body or the ';' of a declaration without one.
            var limit = j + 200;
            while (j < t.Count && j < limit && !t.P(j, '{') && !t.P(j, ';'))
                j = t.Match[j] > j ? t.Match[j] + 1 : j + 1;
            if (j >= t.Count || j >= limit)
                continue;
            var bodyOpen = t.P(j, '{') && t.Match[j] > j ? j : -1;
            var endIndex = bodyOpen >= 0 ? t.Match[j] : j;
            // A type nested deeper than any real code nests is not read: each level reads its body again.
            while (enclosing.Count > 0 && enclosing.Peek() < i)
                enclosing.Pop();
            if (enclosing.Count > MaxTypeNesting)
                continue;
            if (bodyOpen >= 0)
                enclosing.Push(endIndex);
            var (attributes, start) = AttributesBefore(t, i);
            types.Add(new TypeDecl(keyword, t[nameIndex].Text, i, start, attributes, parametersOpen, bases, bodyOpen, endIndex, ns));
        }
        return types;
    }

    private static List<string> SplitTypeArguments(TokenList t, int start, int end)
    {
        var result = new List<string>();
        int depth = 0, from = start;
        for (var i = start; i <= end; i++)
        {
            if (t.P(i, '<')) depth++;
            else if (t.P(i, '>')) depth--;
            else if (t.P(i, ',') && depth == 0)
            {
                result.Add(t.Text(from, i - 1));
                from = i + 1;
            }
        }
        result.Add(t.Text(from, end));
        return result;
    }

    /// <summary>The attributes written before a declaration, and the index of its first token.</summary>
    private static (List<AttributeUse> Attributes, int Start) AttributesBefore(TokenList t, int index)
    {
        var attributes = new List<AttributeUse>();
        var j = index - 1;
        while (j >= 0)
        {
            if (t.Id(j) && Modifiers.Contains(t[j].Text))
            {
                j--;
            }
            else if (t.P(j, ']') && t.Match[j] >= 0)
            {
                ReadAttributeGroup(t, t.Match[j], attributes);
                j = t.Match[j] - 1;
            }
            else
            {
                break;
            }
        }
        return (attributes, j + 1);
    }

    private static void ReadAttributeGroup(TokenList t, int open, List<AttributeUse> attributes)
    {
        foreach (var (start, end) in t.Args(open))
        {
            var s = start;
            if (t.Id(s) && t.P(s + 1, ':'))
                s += 2;
            while (t.Id(s) && t.P(s + 1, '.') && s + 2 <= end)
                s += 2;
            if (!t.Id(s) || s > end)
                continue;
            var name = t[s].Text;
            if (name.Length > "Attribute".Length && name.EndsWith("Attribute", StringComparison.Ordinal))
                name = name[..^"Attribute".Length];
            attributes.Add(new AttributeUse(name, t.P(s + 1, '(') && t.Match[s + 1] > s ? s + 1 : -1, t.Line(open)));
        }
    }

    private enum Template { None, Static, Dynamic }

    /// <summary>The route template of an attribute: its first positional argument when that is one string literal.</summary>
    private static (Template Kind, string Value) TemplateOf(TokenList t, AttributeUse attribute)
    {
        if (attribute.ArgsOpen < 0)
            return (Template.None, "");
        var args = t.Args(attribute.ArgsOpen);
        if (args.Count == 0)
            return (Template.None, "");
        var (start, end) = args[0];
        if (start == end && t.StaticString(start) is { } value)
            return (Template.Static, value);
        if (t.Id(start) && t.P(start + 1, ':') && end == start + 2 && t.StaticString(end) is { } named)
            return (Template.Static, named);
        // "Name = …", "Order = …": a property of the attribute, not a template.
        return t.Id(start) && t.P(start + 1, '=') ? (Template.None, "") : (Template.Dynamic, "");
    }

    // ---- one source file --------------------------------------------------------------

    private sealed class SourceReader(ExtractionContext context, SourceText file, Project project, TokenList t, List<TypeDecl> types)
    {
        private readonly Dictionary<string, string?> _builders = new(StringComparer.Ordinal);
        private readonly HashSet<string> _resolving = new(StringComparer.Ordinal);
        private readonly Dictionary<string, bool> _busReceivers = new(StringComparer.Ordinal);
        private Dictionary<string, List<int>>? _positions;

        /// <summary>Where an identifier stands in the file. Built once, so a lookup does not scan the file again.</summary>
        private List<int> PositionsOf(string name)
        {
            if (_positions is null)
            {
                _positions = new Dictionary<string, List<int>>(StringComparer.Ordinal);
                for (var i = 0; i < t.Count; i++)
                {
                    if (!t.Id(i))
                        continue;
                    if (!_positions.TryGetValue(t[i].Text, out var list))
                        _positions[t[i].Text] = list = [];
                    list.Add(i);
                }
            }
            return _positions.TryGetValue(name, out var found) ? found : [];
        }

        private Site SiteOf(int startToken, int endToken) =>
            new(file.Path, t.Line(startToken), Math.Max(t.Line(startToken), t.EndLine(endToken)));

        public void Read()
        {
            var massTransit = project.UsesMassTransit;
            for (var i = 0; i + 1 < t.Count && !massTransit; i++)
                massTransit = t.Id(i, "using") && t.Id(i + 1, "MassTransit");

            foreach (var type in types)
            {
                if (type.Keyword == "class" && IsController(type))
                    ReadController(type);
                if (type.Keyword == "class" && type.Bases.Any(b => DbContextBases.Contains(b.Name)))
                    ReadDbContext(type);
                if (type.Keyword == "class" && type.BodyOpen >= 0 && type.Bases.Any(b => b.Name is "BackgroundService" or "IHostedService"))
                {
                    var names = new HashSet<string>(StringComparer.Ordinal);
                    for (var i = type.BodyOpen; i < type.EndIndex; i++)
                        if (t.Id(i))
                            names.Add(t[i].Text);
                    project.HostedServices.Add((type.Name, names, SiteOf(type.StartIndex, type.BodyOpen)));
                }
                if (!massTransit)
                    continue;
                foreach (var consumer in type.Bases.Where(b => b.Name == "IConsumer" && b.TypeArguments.Count == 1))
                {
                    if (SimpleName(consumer.TypeArguments[0]) is { } message)
                        project.Consumers.Add((type.Name, message, SiteOf(type.StartIndex, type.BodyOpen >= 0 ? type.BodyOpen : type.EndIndex)));
                    else
                        context.Note(NoteKind.Undecided, "dynamic-message-type", file.Path, t.Line(type.KeywordIndex));
                }
            }

            for (var i = 1; i + 1 < t.Count; i++)
            {
                if (!t.P(i - 1, '.') || !t.Id(i))
                    continue;
                var name = t[i].Text;
                if (MapVerbs.TryGetValue(name, out var verb) && t.P(i + 1, '(') && t.Match[i + 1] > i)
                    ReadMinimalRoute(i, verb);
                else if (massTransit)
                    ReadMassTransitCall(i, name);
            }
        }

        // ---- minimal API ----

        private void ReadMinimalRoute(int method, string verb)
        {
            var args = t.Args(method + 1);
            if (args.Count < 2)
                return;
            var line = t.Line(method);
            if (args[0].Start != args[0].End || t.StaticString(args[0].Start) is not { } template)
            {
                context.Note(NoteKind.Undecided, "dynamic-route", file.Path, line);
                return;
            }
            if (ResolveBuilder(method - 2, 0) is not { } prefix)
            {
                context.Note(NoteKind.Undecided, "unresolved-route-prefix", file.Path, line);
                return;
            }
            var raw = RoutePath.Join(prefix, template);
            var path = RoutePath.Normalize(raw);
            context.Add(FactKind.Operation, $"operation:{project.Slug}:{verb} {path}", $"{verb} {path}",
                [MinimalApi.At(file.Path, line, t.EndLine(t.Match[method + 1]))],
                [
                    .. project.Owner, ("transport", "http"), ("method", verb), ("path", path),
                    ("routeTemplate", raw), ("framework", "aspnetcore-minimal-api"),
                ]);
        }

        /// <summary>
        /// The route prefix of the endpoint builder that an expression ending at
        /// <paramref name="end"/> gives, or null when the text of this file does not fix it.
        /// </summary>
        private string? ResolveBuilder(int end, int depth)
        {
            if (depth > 16 || end < 0)
                return null;
            if (t.Id(end))
                return t.P(end - 1, '.') ? null : ResolveBuilderName(t[end].Text, depth);
            if (!t.P(end, ')'))
                return null;
            var open = t.Match[end];
            var method = open - 1;
            if (open < 2 || !t.Id(method) || !t.P(method - 1, '.'))
                return null;
            var name = t[method].Text;
            if (name == "Build")
                return "";
            if (name == "MapGroup")
            {
                var args = t.Args(open);
                if (args.Count != 1 || args[0].Start != args[0].End || t.StaticString(args[0].Start) is not { } segment)
                    return null;
                return ResolveBuilder(method - 2, depth + 1) is { } parent ? RoutePath.Join(parent, segment) : null;
            }
            return GroupPassThrough.Contains(name) ? ResolveBuilder(method - 2, depth + 1) : null;
        }

        private string? ResolveBuilderName(string name, int depth)
        {
            if (_builders.TryGetValue(name, out var known))
                return known;
            if (!_resolving.Add(name))
                return null;

            var values = new HashSet<string?>();
            foreach (var i in PositionsOf(name))
            {
                if (t.P(i - 1, '.'))
                    continue;
                if (t.Id(i - 1, "WebApplication") && (t.P(i + 1, ',') || t.P(i + 1, ')')))
                {
                    values.Add("");
                }
                else if (t.P(i + 1, '=') && !t.P(i + 2, '=') && !t.P(i + 2, '>'))
                {
                    // Every assignment to the name must give the same prefix.
                    var end = -1;
                    for (int k = i + 2, steps = 0; k < t.Count && steps < MaxStatementTokens; k++, steps++)
                    {
                        if (t.Match[k] > k)
                            k = t.Match[k];
                        else if (t.P(k, ';'))
                        {
                            end = k - 1;
                            break;
                        }
                        else if (t.P(k, ',') || t.P(k, ')') || t.P(k, '}') || t.P(k, ']'))
                            break;
                    }
                    values.Add(end < 0 ? null : ResolveBuilder(end, depth + 1));
                }
            }
            _resolving.Remove(name);
            var result = values.Count == 1 ? values.First() : null;
            _builders[name] = result;
            return result;
        }

        // ---- controllers ----

        private static bool IsController(TypeDecl type) =>
            type.BodyOpen >= 0 && (type.Attributes.Any(a => a.Name == "ApiController")
                || type.Bases.Any(b => b.Name is "ControllerBase" or "Controller"));

        private void ReadController(TypeDecl type)
        {
            var classRoutes = new List<string>();
            foreach (var route in type.Attributes.Where(a => a.Name == "Route"))
            {
                var (kind, value) = TemplateOf(t, route);
                if (kind != Template.Static)
                {
                    context.Note(NoteKind.Undecided, "dynamic-route", file.Path, route.Line);
                    return;
                }
                classRoutes.Add(value);
            }
            var hasClassRoute = classRoutes.Count > 0;
            if (!hasClassRoute)
                classRoutes.Add("");

            var controller = type.Name.EndsWith("Controller", StringComparison.Ordinal) && type.Name.Length > "Controller".Length
                ? type.Name[..^"Controller".Length] : type.Name;

            for (var i = type.BodyOpen + 1; i < type.EndIndex; i++)
            {
                if (!t.P(i, '[') || t.Parent[i] != type.BodyOpen || t.Match[i] < 0)
                    continue;
                var attributes = new List<AttributeUse>();
                var first = i;
                while (t.P(i, '[') && t.Match[i] > i)
                {
                    ReadAttributeGroup(t, i, attributes);
                    i = t.Match[i] + 1;
                }
                // The member: the first '(' before anything that says this is not a method.
                var signature = -1;
                for (var k = i; k < type.EndIndex; k++)
                {
                    if (t.P(k, '('))
                    {
                        signature = k;
                        break;
                    }
                    if (t.P(k, ';') || t.P(k, '{') || t.P(k, '=') || t.P(k, '}'))
                        break;
                }
                i--;
                if (signature < 0 || t.Match[signature] < 0 || !attributes.Any(a => HttpVerbs.ContainsKey(a.Name)))
                    continue;
                var action = t.IdText(signature - 1);
                var site = SiteOf(first, t.Match[signature]);

                var memberRoutes = new List<string>();
                var undecided = false;
                foreach (var route in attributes.Where(a => a.Name == "Route"))
                {
                    var (kind, value) = TemplateOf(t, route);
                    undecided |= kind != Template.Static;
                    memberRoutes.Add(value);
                }
                foreach (var attribute in attributes.Where(a => HttpVerbs.ContainsKey(a.Name)))
                {
                    var (kind, value) = TemplateOf(t, attribute);
                    if (kind == Template.Dynamic || undecided)
                    {
                        context.Note(NoteKind.Undecided, "dynamic-route", file.Path, attribute.Line);
                        continue;
                    }
                    // A verb attribute with no template anywhere only limits the verb. The path
                    // then comes from conventional routing, which is set up in other code.
                    if (kind == Template.None && memberRoutes.Count == 0 && !hasClassRoute)
                    {
                        context.Note(NoteKind.Undecided, "conventional-route", file.Path, attribute.Line);
                        continue;
                    }
                    var templates = kind == Template.Static ? [value] : memberRoutes.Count > 0 ? memberRoutes : [""];
                    foreach (var template in templates)
                        foreach (var classRoute in classRoutes)
                            AddControllerRoute(HttpVerbs[attribute.Name], classRoute, template, controller, action, type.Name, site, attribute.Line);
                }
            }
        }

        private void AddControllerRoute(
            string verb, string classRoute, string template, string controller, string? action, string className, Site site, int line)
        {
            // A template that starts with '/' or '~/' does not take the class route.
            var raw = template.StartsWith('/') ? template
                : template.StartsWith("~/", StringComparison.Ordinal) ? template[1..]
                : RoutePath.Join(classRoute, template);
            raw = raw.Replace("[controller]", controller, StringComparison.OrdinalIgnoreCase);
            if (action is not null)
                raw = raw.Replace("[action]", action, StringComparison.OrdinalIgnoreCase);
            if (raw.Contains('['))
            {
                context.Note(NoteKind.Undecided, "dynamic-route", file.Path, line);
                return;
            }
            var path = RoutePath.Normalize(raw);
            context.Add(FactKind.Operation, $"operation:{project.Slug}:{verb} {path}", $"{verb} {path}",
                [ControllerRoute.At(site.Path, site.Start, site.End)],
                [
                    .. project.Owner, ("transport", "http"), ("method", verb), ("path", path), ("routeTemplate", raw),
                    ("framework", "aspnetcore-controller"), ("controller", className), ("action", action),
                ]);
        }

        // ---- EF Core ----

        private void ReadDbContext(TypeDecl type)
        {
            var evidence = new List<SourceEvidence>
            {
                DbContextRule.At(file.Path, t.Line(type.StartIndex), t.Line(type.BodyOpen >= 0 ? type.BodyOpen : type.EndIndex)),
            };
            var entities = new List<string>();
            for (var i = type.BodyOpen + 1; type.BodyOpen >= 0 && i < type.EndIndex; i++)
            {
                if (!t.Id(i, "DbSet") || !t.P(i + 1, '<'))
                    continue;
                var close = t.CloseAngle(i + 1);
                if (close < 0 || !t.Id(close + 1))
                    continue;
                var entity = t.Text(i + 2, close - 1);
                entities.Add(entity);
                evidence.Add(DbContextRule.At(file.Path, t.Line(i), t.Line(close + 1)));
                project.DbSets.Add((type.Name, t[close + 1].Text, entity, new Site(file.Path, t.Line(i), t.Line(close + 1))));
                i = close + 1;
            }
            context.Add(FactKind.DataAccess, $"dataaccess:{project.Slug}:{type.Name}", type.Name, evidence,
            [
                .. project.Owner, ("technology", "efcore"), ("dbContext", type.Name),
                ("entities", TextUtil.JoinSorted(entities)),
                ("storeKind", project.EfStores.Count == 1 ? project.EfStores.First() : "unknown"),
            ]);
        }

        // ---- MassTransit ----

        private void ReadMassTransitCall(int method, string name)
        {
            switch (name)
            {
                case "Publish" or "Send":
                    ReadPublish(method, name == "Publish" ? "publish" : "send");
                    break;
                case "ReceiveEndpoint" when t.P(method + 1, '(') && t.Match[method + 1] > method:
                {
                    var open = method + 1;
                    var args = t.Args(open);
                    if (args.Count < 2)
                        break;
                    if (args[0].Start != args[0].End || t.StaticString(args[0].Start) is not { } queue)
                    {
                        context.Note(NoteKind.Undecided, "dynamic-queue-name", file.Path, t.Line(method));
                        break;
                    }
                    var site = SiteOf(method, t.Match[open]);
                    for (var i = args[1].Start; i < t.Match[open] && i < open + MaxCallTokens; i++)
                        if ((t.Id(i, "ConfigureConsumer") || t.Id(i, "Consumer")) && t.P(i - 1, '.') && t.P(i + 1, '<')
                            && t.CloseAngle(i + 1) is > 0 and var close && SimpleName(t.Text(i + 2, close - 1)) is { } consumer)
                            project.Endpoints.Add((consumer, queue, site));
                    break;
                }
                case "Message" when t.P(method + 1, '<') && t.CloseAngle(method + 1) is > 0 and var close && t.P(close + 1, '(') && t.Match[close + 1] > close:
                {
                    if (SimpleName(t.Text(method + 2, close - 1)) is not { } message)
                        break;
                    for (var i = close + 2; i < t.Match[close + 1] && i < close + MaxStatementTokens; i++)
                        if (t.Id(i, "SetEntityName") && t.P(i + 1, '(') && t.P(i + 3, ')') && t.StaticString(i + 2) is { } entity)
                            project.EntityNames.Add((message, entity, SiteOf(method, t.Match[close + 1])));
                    break;
                }
                case "AddEntityFrameworkOutbox" when t.P(method + 1, '<') && t.CloseAngle(method + 1) is > 0 and var close && t.P(close + 1, '(') && t.Match[close + 1] > close:
                    project.OutboxCalls.Add((name, SimpleName(t.Text(method + 2, close - 1)), SiteOf(method, t.Match[close + 1])));
                    break;
                case "UseBusOutbox" or "AddOutboxMessageEntity" or "AddOutboxStateEntity" or "AddInboxStateEntity"
                    or "AddInMemoryOutbox" or "UseInMemoryOutbox" when t.P(method + 1, '(') && t.Match[method + 1] > method:
                    project.OutboxCalls.Add((name, null, SiteOf(method, t.Match[method + 1])));
                    break;
            }
        }

        private void ReadPublish(int method, string mode)
        {
            string? message = null;
            int open;
            var after = method + 1;
            if (t.P(after, '<'))
            {
                var close = t.CloseAngle(after);
                if (close < 0 || !t.P(close + 1, '('))
                    return;
                open = close + 1;
                message = SimpleName(t.Text(after + 1, close - 1));
            }
            else if (t.P(after, '('))
            {
                open = after;
                if (t.Id(open + 1, "new") && t.Id(open + 2))
                {
                    var last = open + 2;
                    while (t.P(last + 1, '.') && t.Id(last + 2))
                        last += 2;
                    if (t.P(last + 1, '(') || t.P(last + 1, '{'))
                        message = t[last].Text;
                }
            }
            else
            {
                return;
            }
            if (t.Match[open] < 0 || !IsBusReceiver(method - 2))
                return;
            if (message is null)
            {
                context.Note(NoteKind.Undecided, "dynamic-message-type", file.Path, t.Line(method));
                return;
            }
            project.Publishes.Add((message, mode, SiteOf(method, t.Match[open])));
        }

        /// <summary>True when the receiver is a name that this file declares with a MassTransit bus type.</summary>
        private bool IsBusReceiver(int receiver)
        {
            if (!t.Id(receiver) || (t.P(receiver - 1, '.') && !t.Id(receiver - 2, "this")))
                return false;
            var name = t[receiver].Text;
            if (_busReceivers.TryGetValue(name, out var known))
                return known;
            var found = false;
            foreach (var i in PositionsOf(name))
            {
                if (found)
                    break;
                if (t.Id(i - 1))
                {
                    found = BusTypes.Contains(t[i - 1].Text);
                }
                else if (t.P(i - 1, '>'))
                {
                    // "ConsumeContext<OrderCreated> context": back to the '<' of this '>'.
                    var depth = 0;
                    for (var k = i - 1; k > 0 && k > i - 32; k--)
                    {
                        if (t.P(k, '>')) depth++;
                        else if (t.P(k, '<') && --depth == 0)
                        {
                            found = t.Id(k - 1) && BusTypes.Contains(t[k - 1].Text);
                            break;
                        }
                    }
                }
            }
            _busReceivers[name] = found;
            return found;
        }
    }

    /// <summary>"Orders.Contracts.OrderCreated" gives "OrderCreated". Null for a generic or array type.</summary>
    private static string? SimpleName(string type)
    {
        if (type.Length == 0)
            return null;
        foreach (var c in type)
            if (!(char.IsLetterOrDigit(c) || c is '_' or '.'))
                return null;
        var name = type[(type.LastIndexOf('.') + 1)..];
        return name.Length == 0 ? null : name;
    }

    // ---- facts of one project ---------------------------------------------------------

    private static void Emit(ExtractionContext context, Project project)
    {
        var entityNames = project.EntityNames.GroupBy(e => e.Message, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(e => e.Entity).Distinct(StringComparer.Ordinal).ToList(), StringComparer.Ordinal);

        foreach (var (message, entity, site) in project.EntityNames)
            context.Add(FactKind.Channel, "channel:" + entity, entity, EntityName.At(site.Path, site.Start, site.End),
                ("entityOf", "message:" + message));

        foreach (var (message, mode, site) in project.Publishes)
            context.Add(FactKind.Publication, $"publication:{project.Slug}:{message}", message,
                [Publish.At(site.Path, site.Start, site.End)],
                [
                    .. project.Owner, ("transport", "masstransit"), ("mode", mode), ("message", "message:" + message),
                    ("channel", entityNames.TryGetValue(message, out var entities) && entities.Count == 1 ? "channel:" + entities[0] : null),
                ]);

        var endpointsOf = project.Endpoints.ToLookup(e => e.Consumer, StringComparer.Ordinal);
        foreach (var (consumer, message, site) in project.Consumers)
        {
            var endpoints = endpointsOf[consumer].ToList();
            var queues = endpoints.Select(e => e.Queue).Distinct(StringComparer.Ordinal).ToList();
            var evidence = new List<SourceEvidence> { Consumer.At(site.Path, site.Start, site.End) };
            evidence.AddRange(endpoints.Select(e => ReceiveEndpoint.At(e.Site.Path, e.Site.Start, e.Site.End)));
            context.Add(FactKind.Subscription, $"subscription:{project.Slug}:{consumer}:{message}", $"{consumer} consumes {message}", evidence,
            [
                .. project.Owner, ("transport", "masstransit"), ("message", "message:" + message), ("consumer", consumer),
                ("queue", queues.Count == 1 ? "channel:" + queues[0] : null),
            ]);
        }
        foreach (var (_, queue, site) in project.Endpoints)
            context.Add(FactKind.Channel, "channel:" + queue, queue, ReceiveEndpoint.At(site.Path, site.Start, site.End),
                ("kind", "queue"), ("transport", "masstransit"));

        // MassTransit outbox. The fact needs the registration; the other calls are more evidence.
        var registrations = project.OutboxCalls.Where(c => c.Call == "AddEntityFrameworkOutbox").ToList();
        if (registrations.Count > 0)
        {
            var related = project.OutboxCalls.Where(c => c.Call is not ("AddInMemoryOutbox" or "UseInMemoryOutbox")).ToList();
            var contexts = registrations.Select(r => r.DbContext).OfType<string>().Distinct(StringComparer.Ordinal).ToList();
            context.Add(FactKind.Publication, $"outbox:{project.Slug}:masstransit-entity-framework", "Transactional outbox",
                related.Select(c => Outbox.At(c.Site.Path, c.Site.Start, c.Site.End)).ToList(),
                [
                    .. project.Owner, ("pattern", "transactional-outbox"), ("mechanism", "masstransit-entity-framework"),
                    ("dbContext", contexts.Count == 1 ? contexts[0] : null),
                    ("busOutbox", related.Any(c => c.Call == "UseBusOutbox") ? "true" : "false"),
                    ("outboxEntities", related.Any(c => c.Call == "AddOutboxMessageEntity") ? "true" : "false"),
                ]);
        }
        var inMemory = project.OutboxCalls.Where(c => c.Call is "AddInMemoryOutbox" or "UseInMemoryOutbox").ToList();
        if (inMemory.Count > 0)
            context.Add(FactKind.Publication, $"outbox:{project.Slug}:masstransit-in-memory", "In-memory outbox",
                inMemory.Select(c => Outbox.At(c.Site.Path, c.Site.Start, c.Site.End)).ToList(),
                [.. project.Owner, ("pattern", "in-memory-outbox"), ("mechanism", "masstransit-in-memory")]);

        // Hand-written outbox: a table and a dispatcher that reads it, both in this project.
        var tables = project.DbSets.Where(s => s.Entity.StartsWith("Outbox", StringComparison.Ordinal)).ToList();
        var wanted = tables.SelectMany(s => (string[])[s.Set, s.Entity]).ToHashSet(StringComparer.Ordinal);
        var readers = new Dictionary<string, List<(string Class, Site Site)>>(StringComparer.Ordinal);
        foreach (var service in tables.Count == 0 ? [] : project.HostedServices)
            foreach (var name in service.Names.Where(wanted.Contains))
            {
                if (!readers.TryGetValue(name, out var list))
                    readers[name] = list = [];
                list.Add((service.Class, service.Site));
            }
        foreach (var (dbContext, set, entity, site) in tables)
        {
            var dispatchers = readers.GetValueOrDefault(set, []).Concat(readers.GetValueOrDefault(entity, [])).Distinct().ToList();
            if (dispatchers.Count == 0)
                continue;
            var evidence = new List<SourceEvidence> { HandwrittenOutbox.At(site.Path, site.Start, site.End) };
            evidence.AddRange(dispatchers.Select(d => HandwrittenOutbox.At(d.Site.Path, d.Site.Start, d.Site.End)));
            context.Add(FactKind.Publication, $"outbox:{project.Slug}:hand-written", "Transactional outbox", evidence,
            [
                .. project.Owner, ("pattern", "transactional-outbox"), ("mechanism", "hand-written"),
                ("dbContext", dbContext), ("entity", entity),
                ("dispatcher", TextUtil.JoinSorted(dispatchers.Select(d => d.Class))),
            ]);
        }
    }

    // ---- message contracts ------------------------------------------------------------

    private static void AddContract(ExtractionContext context, SourceText file, Project project, TokenList t, TypeDecl type)
    {
        var fields = new List<string>();
        if (type.Keyword == "record" && type.ParametersOpen >= 0)
        {
            foreach (var (start, end) in SplitParameters(t, type.ParametersOpen))
            {
                var s = start;
                while (t.P(s, '[') && t.Match[s] > s)
                    s = t.Match[s] + 1;
                // "Type Name" or "Type Name = default".
                var nameIndex = end;
                for (var k = s; k <= end; k++)
                    if (t.P(k, '=') && !t.P(k + 1, '>'))
                    {
                        nameIndex = k - 1;
                        break;
                    }
                if (t.Id(nameIndex) && nameIndex > s)
                    fields.Add($"{t[nameIndex].Text}:{t.Text(s, nameIndex - 1)}");
            }
        }
        for (var i = type.BodyOpen + 1; type.BodyOpen >= 0 && i < type.EndIndex; i++)
        {
            // A property: "<modifiers> Type Name { get; …".
            if (!t.P(i, '{') || t.Parent[i] != type.BodyOpen || !(t.Id(i + 1, "get") || t.Id(i + 1, "init") || t.Id(i + 1, "set")) || !t.Id(i - 1))
                continue;
            var first = i - 1;
            while (first - 1 > type.BodyOpen && !(t.P(first - 1, ';') || t.P(first - 1, '}') || t.P(first - 1, '{') || t.P(first - 1, ']')))
                first--;
            var isPublic = type.Keyword == "interface";
            var isStatic = false;
            while (first < i - 1 && t.Id(first) && Modifiers.Contains(t[first].Text))
            {
                isPublic |= t[first].Text == "public";
                isStatic |= t[first].Text == "static";
                first++;
            }
            if (isPublic && !isStatic && first < i - 1)
                fields.Add($"{t[i - 1].Text}:{t.Text(first, i - 2)}");
        }

        context.Add(FactKind.Message, "message:" + type.Name, type.Name,
            [Contract.At(file.Path, t.Line(type.StartIndex), t.EndLine(type.EndIndex))],
            [
                ("clrType", type.Namespace.Length == 0 ? type.Name : $"{type.Namespace}.{type.Name}"),
                ("declaredIn", project.Path),
                ("fields", string.Join(";", fields.Distinct(StringComparer.Ordinal).OrderBy(f => f, StringComparer.Ordinal))),
            ]);
    }

    /// <summary>Parameters split at top-level commas; a comma inside a type argument list does not split.</summary>
    private static List<(int Start, int End)> SplitParameters(TokenList t, int open)
    {
        var result = new List<(int, int)>();
        var close = t.Match[open];
        int depth = 0, start = open + 1;
        for (var i = open + 1; i < close; i++)
        {
            if (t.Match[i] > i)
            {
                i = t.Match[i];
                continue;
            }
            if (t.P(i, '<')) depth++;
            else if (t.P(i, '>')) depth = Math.Max(0, depth - 1);
            else if (t.P(i, ',') && depth == 0)
            {
                result.Add((start, i - 1));
                start = i + 1;
            }
        }
        if (start < close)
            result.Add((start, close - 1));
        return result;
    }
}
