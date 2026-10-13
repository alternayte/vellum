using System.Xml;

namespace Vellum.Modules.Import.Extraction;

/// <summary>
/// .NET projects and C# source. Nothing is compiled, restored or loaded: a project file is
/// read as XML, and source is read as tokens (see <see cref="CSharpLexer"/> for strings,
/// comments and the preprocessor, and <see cref="CSharpFile"/> for declarations and scopes).
/// <para>Ownership. A C# file belongs to the project file in the nearest directory at or above
/// it. A file with no project above it, under a project file that does not parse, or in a
/// directory with two project files (nothing says which one compiles it) is noted and not read.</para>
/// <para>Keys. <c>&lt;pid&gt;</c> is <see cref="ExtractionContext.ProjectId"/>: the directory of the
/// project file. A type is named with its namespace and enclosing types
/// (<c>Orders.Contracts.OrderCreated</c>). A type name written at a use site is resolved to
/// one declaration of the scan by the rule of <see cref="TypeIndex"/>; a partial type is one
/// type made of its parts in one project.</para>
/// <para>Conditional compilation. A fact from a token in a preprocessor branch whose condition
/// the scan cannot evaluate has the attribute <c>conditional</c> with the condition; its key is
/// the same as without. Branches that are not compiled give nothing. The condition is that of
/// the site of the fact (the call, the type keyword, the action), not of every token the rule reads.</para>
/// <para>Rules (all version 2), the exact recognition rule of each, and what it does not recognise:</para>
/// <list type="bullet">
/// <item><c>dotnet.project</c> — <c>service:&lt;pid&gt;</c> when the SDK is Web or Worker, or the
/// output type is Exe or WinExe. Not: <c>Directory.Build.props</c>, imports, <c>Condition</c>
/// attributes, old-style project files without an <c>Sdk</c> attribute that are not Exe.</item>
/// <item><c>dotnet.package-reference</c> — <c>dependency:&lt;pid&gt;:nuget:&lt;package&gt;</c> for each
/// <c>PackageReference</c>, and <c>dependency:&lt;pid&gt;:project:&lt;name&gt;</c> for each
/// <c>ProjectReference</c>. Not: central package versions, transitive packages; two references
/// to projects with one file name share a key.</item>
/// <item><c>dotnet.minimal-api-route</c> — <c>x.MapGet/Post/Put/Patch/Delete("literal", handler…)</c>
/// where the receiver is, in the scope of the call, an endpoint builder with a prefix that the
/// text fixes: a name declared as <c>WebApplication</c> (prefix empty); a name whose every
/// assignment is <c>b.Build()</c> with <c>b</c> a <c>WebApplicationBuilder</c> or assigned from
/// <c>WebApplication.CreateBuilder/CreateSlimBuilder/CreateEmptyBuilder(…)</c>, or is
/// <c>WebApplication.Create(…)</c>; or <c>MapGroup("literal")</c> on one of those, through the
/// calls that return their group (<c>WithTags</c>, <c>RequireAuthorization</c>, …), directly or
/// through variables (at most 32 variables deep, 64 groups, 400 characters). Top-level
/// statements are one scope; a method, local function or lambda is its own, and its
/// parameters hide outer names. A receiver that is a builder, or may be one, without such a
/// prefix (an <c>IEndpointRouteBuilder</c> or <c>RouteGroupBuilder</c> parameter such as the
/// <c>this</c> parameter of an extension method, a lambda parameter, a name with no visible
/// declaration, a name with two different assignments, <c>MapGroup(expression)</c>) gives a
/// note. A receiver declared with any other type, or constructed with <c>new</c>, gives nothing.
/// Not: <c>MapMethods</c>, <c>MapFallback</c>, a route that is not one string literal (noted),
/// a prefix set by the caller of an extension method or in another file, fields of another
/// part of a partial class, <c>UsePathBase</c>.</item>
/// <item><c>dotnet.controller-route</c> — verb attributes (<c>[HttpGet]</c> … <c>[HttpOptions]</c>) on
/// public instance methods of a class that is a controller: it or a base class has
/// <c>[ApiController]</c>, or its base class chain ends at <c>ControllerBase</c> or
/// <c>Controller</c>. The chain is followed through base classes declared in the scan (at most
/// 8). The class route is the <c>[Route]</c> set of the nearest class of the chain that has one;
/// actions of base classes are inherited; <c>[controller]</c> is the name of the concrete class
/// and <c>[action]</c> the method. An abstract or static class gives no routes itself. No facts
/// and a note when the route cannot be established: a first base type that is not declared in
/// the scan (it can be a class with a route), a partial class with one part in the scan, two
/// declarations of one name, a template that is not one literal, more than 8 route or verb
/// attributes on one declaration, an inherited action that the derived class declares again, a
/// verb attribute with no template on a class with no route (conventional routing).
/// Attributes on properties, fields, delegates, events and non-public or static methods give nothing.
/// Not: <c>[AcceptVerbs]</c>, <c>[area]</c>, controllers by naming convention only, <c>[NonController]</c>.</item>
/// <item><c>dotnet.masstransit-consumer</c> — <c>class X : IConsumer&lt;T&gt;</c> with T a plain type
/// name, in a project with a MassTransit package or a file with <c>using MassTransit</c>. T a
/// type parameter gives nothing; a constructed generic T is noted. The key is
/// <c>subscription:&lt;pid&gt;:&lt;consumer type&gt;:&lt;message type&gt;</c>; when T resolves to no one
/// type of the scan the last part is <c>unresolved:&lt;name as written&gt;</c>, there is no
/// <c>message</c> attribute, and a note. <c>messageType</c> is always the name as written.
/// Not: consumers registered by convention without the interface, sagas, routing slips.</item>
/// <item><c>dotnet.masstransit-receive-endpoint</c> — <c>x.ReceiveEndpoint("literal", …)</c> with
/// <c>.ConfigureConsumer&lt;X&gt;</c> or <c>.Consumer&lt;X&gt;</c> inside its call gives the queue
/// <c>channel:&lt;broker&gt;:&lt;queue&gt;</c>, and the <c>queue</c> of the subscriptions of X when X
/// resolves to one type and has one queue. The broker is that of the one MassTransit transport
/// package of the project of the endpoint (<c>amqp</c>, <c>kafka</c>, <c>azure-service-bus</c>,
/// <c>amazon-sqs</c>), else <c>masstransit</c>. A name that is not a literal is noted. An
/// endpoint inside another endpoint is noted and not read. Not: endpoint name formatters,
/// <c>ConsumerDefinition</c>, <c>ConfigureEndpoints</c> conventions (the queue is then unknown).</item>
/// <item><c>dotnet.masstransit-publish</c> — <c>r.Publish&lt;T&gt;(…)</c>, <c>r.Publish(new T…)</c>,
/// <c>r.Send&lt;T&gt;(…)</c>, <c>r.Send(new T…)</c> where r (or <c>this.r</c>) is a name whose declaration
/// in the scope of the call (parameter, lambda parameter with a type, local, field, primary
/// constructor parameter of the enclosing class) has the type <c>IPublishEndpoint</c>,
/// <c>IBus</c>, <c>IBusControl</c>, <c>ISendEndpoint</c>, <c>ISendEndpointProvider</c> or
/// <c>ConsumeContext</c>. Key <c>publication:&lt;pid&gt;:&lt;message type&gt;</c>, resolved as for a
/// consumer. T a type parameter gives nothing. A message passed as a variable is noted. Not:
/// any other receiver, silently (<c>IMediator.Send</c>, a bus configurator, a name declared in
/// another file or part), <c>PublishBatch</c>, scheduled messages.</item>
/// <item><c>dotnet.masstransit-entity-name</c> — <c>x.Message&lt;T&gt;(m =&gt; … m.SetEntityName("literal") …)</c>
/// gives <c>channel:&lt;broker&gt;:&lt;name&gt;</c> with <c>entityOf</c> the message key, and is the
/// <c>channel</c> of the publications of T in that project when T has one entity name. Its
/// <c>kind</c> is <c>exchange</c> for amqp, <c>topic</c> for the other known brokers, and absent
/// when the broker is not known. Exchange type and durability are not in the text and are not stated.</item>
/// <item><c>dotnet.masstransit-outbox</c> — kind <c>Pattern</c>, not a publication.
/// <c>outbox:&lt;pid&gt;:masstransit-entity-framework</c> (<c>pattern=transactional-outbox</c>) needs
/// <c>AddEntityFrameworkOutbox&lt;TContext&gt;(…)</c>; <c>UseBusOutbox</c> and the outbox entity
/// mappings are more evidence. <c>outbox:&lt;pid&gt;:masstransit-in-memory</c>
/// (<c>pattern=in-memory-outbox</c>) is <c>AddInMemoryOutbox</c> or <c>UseInMemoryOutbox</c>.</item>
/// <item><c>dotnet.outbox-handwritten</c> — kind <c>Pattern</c>, <c>outbox:&lt;pid&gt;:hand-written</c>.
/// All of, in one project: (1) a <c>DbSet&lt;E&gt;</c> of a DbContext where the simple name of E is
/// <c>Outbox</c>, <c>OutboxMessage</c>, <c>OutboxMessages</c>, <c>OutboxEvent</c>, <c>OutboxEvents</c>,
/// <c>OutboxEntry</c>, <c>OutboxEntries</c> or <c>IntegrationEventLogEntry</c>, or has <c>Outbox</c>
/// followed later by <c>Message</c>, or <c>IntegrationEvent</c> followed later by <c>Log</c> (case-sensitive);
/// (2) a class declaration with the base <c>BackgroundService</c> or <c>IHostedService</c> that
/// reads that set: <c>r.SetName</c> not followed by <c>.Add/AddRange/Remove/RemoveRange/Update/UpdateRange/Attach/AttachRange</c>
/// (or their Async forms) or <c>=</c>, or <c>r.Set&lt;E&gt;()</c>, where the declared type of r is that DbContext
/// (by its type name, <c>GetRequiredService&lt;T&gt;()</c> and the like, or <c>CreateDbContext[Async]()</c> on an
/// <c>IDbContextFactory&lt;T&gt;</c>); and (3) in the same class declaration a call of a publish API on a typed
/// receiver: <c>Publish</c>/<c>Send</c> on the MassTransit types above, <c>Produce</c>/<c>ProduceAsync</c> on
/// <c>IProducer</c>/<c>ITopicProducer</c>, <c>BasicPublish</c>/<c>BasicPublishAsync</c> on <c>IModel</c>/<c>IChannel</c>,
/// <c>SendMessageAsync</c>/<c>SendMessagesAsync</c> on <c>ServiceBusSender</c>. At most 16 sets and 16
/// dispatchers of a project are read. Not: a dispatcher in another project or split over
/// partial parts, a context injected through a property, entities mapped only in
/// <c>OnModelCreating</c>, other libraries (CAP, NServiceBus, Wolverine), in-process mediators.</item>
/// <item><c>dotnet.efcore-dbcontext</c> — <c>dataaccess:&lt;pid&gt;:&lt;context type&gt;</c> for a class with
/// the base <c>DbContext</c>, <c>IdentityDbContext</c>, <c>IdentityUserContext</c> or
/// <c>ApiAuthorizationDbContext</c>, with its <c>DbSet&lt;T&gt; Name</c> members. The store kind
/// comes from the one EF Core provider package of the project, else <c>unknown</c>. Not:
/// entities mapped only in <c>OnModelCreating</c>, Dapper or raw SQL, a context derived from a custom base.</item>
/// <item><c>dotnet.message-contract</c> — <c>message:&lt;type&gt;</c> for each type that a consumer,
/// publish, send or entity name resolved to: positional parameters of a record and public
/// instance properties with an accessor block as <c>Name:Type</c> fields (names, checked one by
/// one; a field named <c>Token</c> stays). Not: inherited members, expression-bodied
/// properties, fields, a type declared outside the scan.</item>
/// </list>
/// <para>Bounds. Every loop charges the <see cref="WorkMeter"/> of the file it reads, and each
/// fact costs <see cref="FactCost"/> operations of the file that gives it. Nesting is bounded
/// and documented where it occurs: types and namespaces (<see cref="CSharpFile.MaxNesting"/>),
/// preprocessor groups (<see cref="CSharpLexer.MaxConditionalDepth"/>), base classes, variable
/// chains. Names longer than <see cref="CSharpFile.MaxNameLength"/> are not kept.</para>
/// </summary>
internal sealed partial class DotNetExtractor : IExtractor
{
    private static readonly ExtractionRule ProjectRule = new("dotnet.project", 2);
    private static readonly ExtractionRule PackageRule = new("dotnet.package-reference", 2);
    private static readonly ExtractionRule MinimalApi = new("dotnet.minimal-api-route", 2);
    private static readonly ExtractionRule ControllerRoute = new("dotnet.controller-route", 2);
    private static readonly ExtractionRule Consumer = new("dotnet.masstransit-consumer", 2);
    private static readonly ExtractionRule ReceiveEndpoint = new("dotnet.masstransit-receive-endpoint", 2);
    private static readonly ExtractionRule Publish = new("dotnet.masstransit-publish", 2);
    private static readonly ExtractionRule EntityName = new("dotnet.masstransit-entity-name", 2);
    private static readonly ExtractionRule Outbox = new("dotnet.masstransit-outbox", 2);
    private static readonly ExtractionRule HandwrittenOutbox = new("dotnet.outbox-handwritten", 2);
    private static readonly ExtractionRule DbContextRule = new("dotnet.efcore-dbcontext", 2);
    private static readonly ExtractionRule Contract = new("dotnet.message-contract", 2);

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

    private static readonly HashSet<string> MemberModifiers = new(StringComparer.Ordinal)
    {
        "public", "private", "protected", "internal", "static", "sealed", "abstract", "partial", "readonly",
        "unsafe", "new", "file", "ref", "virtual", "override", "async", "extern", "required",
    };

    private static readonly HashSet<string> BusTypes = new(StringComparer.Ordinal)
    {
        "IPublishEndpoint", "IBus", "IBusControl", "ISendEndpoint", "ISendEndpointProvider", "ConsumeContext",
    };

    /// <summary>The publish, send and produce calls that a hand-written outbox dispatcher makes, with the declared types of their receivers.</summary>
    private static readonly Dictionary<string, HashSet<string>> PublishApis = new(StringComparer.Ordinal)
    {
        ["Publish"] = BusTypes, ["Send"] = BusTypes,
        ["Produce"] = ["IProducer", "ITopicProducer"], ["ProduceAsync"] = ["IProducer", "ITopicProducer"],
        ["BasicPublish"] = ["IModel", "IChannel"], ["BasicPublishAsync"] = ["IModel", "IChannel"],
        ["SendMessageAsync"] = ["ServiceBusSender"], ["SendMessagesAsync"] = ["ServiceBusSender"],
    };

    private static readonly HashSet<string> SetWrites = new(StringComparer.Ordinal)
    {
        "Add", "AddAsync", "AddRange", "AddRangeAsync", "Remove", "RemoveRange", "Update", "UpdateRange", "Attach", "AttachRange",
    };

    /// <summary>Calls whose type argument is the type of what they return.</summary>
    private static readonly HashSet<string> ServiceGetters = new(StringComparer.Ordinal)
    {
        "GetRequiredService", "GetService", "GetRequiredKeyedService", "GetKeyedService",
    };

    private static readonly HashSet<string> ApplicationBuilderFactories = new(StringComparer.Ordinal)
    {
        "CreateBuilder", "CreateSlimBuilder", "CreateEmptyBuilder",
    };

    private static readonly HashSet<string> DbContextBases = new(StringComparer.Ordinal)
    {
        "DbContext", "IdentityDbContext", "IdentityUserContext", "ApiAuthorizationDbContext",
    };

    private static readonly HashSet<string> OutboxEntities = new(StringComparer.Ordinal)
    {
        "Outbox", "OutboxMessage", "OutboxMessages", "OutboxEvent", "OutboxEvents", "OutboxEntry", "OutboxEntries",
        "IntegrationEventLogEntry",
    };

    // Calls that return the route group they were called on.
    private static readonly HashSet<string> GroupPassThrough = new(StringComparer.Ordinal)
    {
        "WithTags", "RequireAuthorization", "AllowAnonymous", "WithOpenApi", "AddEndpointFilter", "WithMetadata",
        "RequireCors", "RequireRateLimiting", "WithGroupName", "DisableAntiforgery", "ProducesProblem",
        "ProducesValidationProblem", "CacheOutput", "ExcludeFromDescription", "DisableRateLimiting",
    };

    private static readonly HashSet<string> ReadAttributes = new(HttpVerbs.Keys, StringComparer.Ordinal)
    {
        "Route", "ApiController", "NonAction",
    };

    /// <summary>
    /// The operations charged for one fact. Adding a fact checks its key, name and every
    /// attribute value for the shape of a credential, which is far more than one step.
    /// </summary>
    private const int FactCost = 512;
    /// <summary>Route or verb attributes on one class or one action. More is noted and gives no routes.</summary>
    private const int MaxRouteAttributes = 8;
    /// <summary>The longest route prefix with its template. A longer one cannot be a key.</summary>
    private const int MaxRouteLength = 400;
    private const int MaxGroupSegments = 64;
    /// <summary>The longest chain of variables that the route prefix of a builder is followed through.</summary>
    private const int MaxPrefixDepth = 32;
    /// <summary>The longest chain of base classes that a controller is followed through.</summary>
    private const int MaxBaseDepth = 8;
    /// <summary>Evidence of one kind on one fact from a list the input controls (the endpoints of a consumer, the dispatchers of an outbox).</summary>
    private const int MaxEvidenceItems = 16;

    public void Run(ExtractionContext context)
    {
        var projects = new List<Project>();
        var byDirectory = new Dictionary<string, List<Project>>(StringComparer.Ordinal);
        foreach (var file in context.Files)
        {
            if (file.Extension != ".csproj")
                continue;
            var project = new Project(file.Path, file.Directory, file.Name[..^".csproj".Length], context.ProjectId(file));
            context.Guarded(Id, file.Path, () => ReadProject(context, file, project));
            projects.Add(project);
            if (!byDirectory.TryGetValue(file.Directory, out var list))
                byDirectory[file.Directory] = list = [];
            list.Add(project);
        }

        foreach (var file in context.Files)
        {
            if (context.Stopped)
                return;
            if (file.Extension != ".cs")
                continue;
            var owner = OwnerOf(file, byDirectory, out var reason);
            if (owner is null)
            {
                context.Note(NoteKind.Undecided, reason, file.Path);
                continue;
            }
            context.Guarded(Id, file.Path, () => ReadSource(context, file, owner));
        }

        var readable = projects.Where(p => p.IsReadable).ToList();
        var index = new TypeIndex(context, readable);
        context.Guarded(Id, "", () => EmitControllers(context, index));
        context.Guarded(Id, "", () => EmitDataAccess(context, index));
        context.Guarded(Id, "", () => EmitMessaging(context, readable, index));
        foreach (var project in readable)
            context.Guarded(Id, project.Path, () => EmitOutbox(context, project, index));
    }

    /// <summary>The project of the nearest directory at or above the file that has a project file.</summary>
    private static Project? OwnerOf(SourceText file, Dictionary<string, List<Project>> byDirectory, out string reason)
    {
        reason = "no-owning-project";
        var directory = file.Directory;
        while (true)
        {
            if (byDirectory.TryGetValue(directory, out var found))
            {
                if (found.Count > 1)
                    reason = "ambiguous-owning-project";
                return found.Count == 1 && found[0].IsReadable ? found[0] : null;
            }
            if (directory.Length == 0)
                return null;
            var slash = directory.LastIndexOf('/');
            directory = slash < 0 ? "" : directory[..slash];
        }
    }

    private static void ReadSource(ExtractionContext context, SourceText file, Project project)
    {
        var meter = context.Meter(file);
        var source = CSharpLexer.Tokenize(file.Content, meter);
        if (meter.Exhausted)
            return;
        foreach (var (reason, line) in source.Notes)
            context.Note(NoteKind.Undecided, reason, file.Path, line);
        var code = new CSharpFile(source.Tokens, meter, ReadAttributes.Contains);
        foreach (var (reason, line) in code.Notes)
            context.Note(NoteKind.Undecided, reason, file.Path, line);
        if (!code.IsComplete)
            return;

        var model = new FileModel(file, project) { Usings = code.Usings, Aliases = code.Aliases };
        project.Files.Add(model);
        project.GlobalUsings.UnionWith(code.GlobalUsings);
        new SourceReader(context, file, project, model, source, code, meter).Read();
    }

    // ---- project files ----------------------------------------------------------------

    private static void ReadProject(ExtractionContext context, SourceText file, Project project)
    {
        // One pass of the XML reader over the text.
        if (!context.Meter(file).Charge(file.Content.Length + 1))
            return;
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
            return;
        }
        catch (InvalidOperationException)
        {
            context.Note(NoteKind.ParseFailure, "xml-syntax", file.Path);
            return;
        }
        project.IsReadable = true;

        var kind = sdk is null ? null
            : sdk.StartsWith("Microsoft.NET.Sdk.Web", StringComparison.OrdinalIgnoreCase) ? "web"
            : sdk.StartsWith("Microsoft.NET.Sdk.Worker", StringComparison.OrdinalIgnoreCase) ? "worker"
            : null;
        if (kind is null && outputType is not null && (outputType.Equals("Exe", StringComparison.OrdinalIgnoreCase) || outputType.Equals("WinExe", StringComparison.OrdinalIgnoreCase)))
            kind = "executable";
        if (kind is not null)
        {
            project.ServiceKey = "service:" + project.Id;
            context.Add(FactKind.Service, project.ServiceKey, project.Name, ProjectRule.At(file.Path, projectLine, projectLine),
                ("language", "dotnet"), ("kind", kind), ("project", file.Path), ("sdk", sdk),
                ("outputType", outputType), ("targetFramework", framework));
        }

        var meter = context.Meter(file);
        foreach (var (name, version, line) in packages)
        {
            if (!meter.Charge(FactCost))
                return;
            var known = Known(name);
            if (known?.Technology == "masstransit")
            {
                project.UsesMassTransit = true;
                if (known.Value.Detail is { } transport)
                    project.Transports.Add(transport == "rabbitmq" ? "amqp" : transport);
            }
            if (known is { Technology: "efcore", Detail: { } store })
                project.EfStores.Add(store);
            context.Add(FactKind.Dependency, $"dependency:{project.Id}:nuget:{name}", name, PackageRule.At(file.Path, line, line),
                ("ecosystem", "nuget"), ("package", name), ("version", version), ("project", file.Path),
                ("service", project.ServiceKey), ("technology", known?.Technology), ("role", known?.Role),
                (known?.Role == "data" ? "store" : "broker", known?.Detail));
        }
        foreach (var (reference, line) in references)
        {
            if (!meter.Charge(FactCost))
                return;
            var slash = reference.LastIndexOf('/');
            var name = reference[(slash + 1)..];
            if (name.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                name = name[..^".csproj".Length];
            context.Add(FactKind.Dependency, $"dependency:{project.Id}:project:{name}", name, PackageRule.At(file.Path, line, line),
                ("ecosystem", "project"), ("reference", reference), ("project", file.Path), ("service", project.ServiceKey));
        }
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

    /// <summary>The condition of a fact that two conditional sites establish together.</summary>
    private static string? Both(string? first, string? second) =>
        first is null ? second : second is null || second == first ? first : $"{first} && {second}";

    // ---- EF Core ----------------------------------------------------------------------

    private static void EmitDataAccess(ExtractionContext context, TypeIndex index)
    {
        foreach (var logical in index.All)
        {
            if (logical.Keyword != "class" || !logical.Parts.Any(IsDbContext))
                continue;
            var first = logical.Parts[0];
            var meter = context.Meter(first.File.File);
            if (!meter.Charge(FactCost))
                continue;
            if (!logical.IsConsistent)
            {
                context.Note(NoteKind.Undecided, "ambiguous-type-declaration", first.File.File.Path, first.Line);
                continue;
            }
            var evidence = new List<SourceEvidence>();
            var entities = new List<string>();
            foreach (var part in logical.Parts)
            {
                evidence.Add(DbContextRule.At(part.Header.Path, part.Header.Start, part.Header.End));
                foreach (var set in part.DbSets)
                {
                    if (!meter.Charge())
                        break;
                    entities.Add(set.Entity);
                    if (evidence.Count <= context.Limits.MaxListItems)
                        evidence.Add(DbContextRule.At(set.Site.Path, set.Site.Start, set.Site.End));
                }
            }
            var project = logical.Project;
            context.Add(FactKind.DataAccess, $"dataaccess:{project.Id}:{logical.QualifiedName}", logical.Name, evidence,
            [
                .. project.Owner, ("technology", "efcore"), ("dbContext", logical.Name),
                ("entities", context.Names(entities, "entities", first.Header.Path, first.Line)),
                ("storeKind", project.EfStores.Count == 1 ? project.EfStores.First() : "unknown"),
                ("conditional", first.Conditional),
            ]);
        }
    }

    private static bool IsDbContext(TypeInfo type) => type.Bases.Any(b => DbContextBases.Contains(b.Name));

    // ---- MassTransit ------------------------------------------------------------------

    private static void EmitMessaging(ExtractionContext context, List<Project> projects, TypeIndex index)
    {
        var contracts = new HashSet<LogicalType>();

        // What a written message type is: the key part, and the key of its contract when it resolves.
        (string KeyPart, string? Message) MessageOf(TypeRef reference, WorkMeter meter)
        {
            if (index.Resolve(reference, meter) is { } resolved)
            {
                contracts.Add(resolved);
                return (resolved.QualifiedName, "message:" + resolved.QualifiedName);
            }
            context.Note(NoteKind.Undecided, "unresolved-message-type", reference.File.File.Path, reference.Line);
            return ("unresolved:" + reference.Written, null);
        }

        // Queues: the channel of each endpoint, and the endpoints of each consumer type.
        var endpointsOf = new Dictionary<LogicalType, ConsumerEndpoints>();
        foreach (var project in projects)
            foreach (var endpoint in project.Endpoints)
            {
                var meter = context.Meter(endpoint.Site.Path);
                if (!meter.Charge(FactCost))
                    continue;
                var channel = $"channel:{project.Broker}:{endpoint.Queue}";
                context.Add(FactKind.Channel, channel, endpoint.Queue, ReceiveEndpoint.At(endpoint.Site.Path, endpoint.Site.Start, endpoint.Site.End),
                    ("kind", "queue"), ("transport", project.Broker), ("conditional", endpoint.Conditional));
                foreach (var consumer in endpoint.Consumers)
                {
                    if (!meter.Charge())
                        break;
                    if (index.Resolve(consumer, meter) is not { } resolved)
                    {
                        context.Note(NoteKind.Undecided, "unresolved-consumer-type", consumer.File.File.Path, consumer.Line);
                        continue;
                    }
                    if (!endpointsOf.TryGetValue(resolved, out var known))
                        endpointsOf[resolved] = known = new ConsumerEndpoints(channel);
                    known.HasOneQueue &= known.Channel == channel;
                    // The first endpoints are the evidence. Every endpoint counts for the queue.
                    if (known.Sites.Count < MaxEvidenceItems)
                        known.Sites.Add(endpoint.Site);
                    else
                        context.LimitReached("list-items", endpoint.Site.Path, stop: false, endpoint.Site.Start);
                }
            }

        foreach (var logical in index.All)
        {
            if (!logical.Parts.Exists(p => p.Consumes.Count > 0))
                continue;
            var endpoints = logical.IsConsistent ? endpointsOf.GetValueOrDefault(logical) : null;
            var queue = endpoints is { HasOneQueue: true } ? endpoints.Channel : null;
            foreach (var part in logical.Parts)
            {
                var meter = context.Meter(part.File.File);
                foreach (var consumed in part.Consumes)
                {
                    if (!meter.Charge(FactCost))
                        break;
                    var (keyPart, message) = MessageOf(consumed, meter);
                    var evidence = new List<SourceEvidence> { Consumer.At(part.Header.Path, part.Header.Start, part.Header.End) };
                    if (endpoints is not null)
                        foreach (var site in endpoints.Sites)
                            evidence.Add(ReceiveEndpoint.At(site.Path, site.Start, site.End));
                    context.Add(FactKind.Subscription, $"subscription:{logical.Project.Id}:{part.QualifiedName}:{keyPart}",
                        $"{part.Name} consumes {consumed.Simple}", evidence,
                        [
                            .. logical.Project.Owner, ("transport", "masstransit"), ("message", message), ("messageType", consumed.Written),
                            ("consumer", part.Name), ("clrType", part.QualifiedName), ("queue", queue), ("conditional", part.Conditional),
                        ]);
                }
            }
        }

        foreach (var project in projects)
        {
            // The entity names of each message type of this project. A publication has a channel when its type has exactly one.
            var entitiesOf = new Dictionary<string, (string Channel, bool Single)>(StringComparer.Ordinal);
            var kind = project.Broker switch { "amqp" => "exchange", "masstransit" => null, _ => "topic" };
            foreach (var entity in project.EntityNames)
            {
                var meter = context.Meter(entity.Site.Path);
                if (!meter.Charge(FactCost))
                    continue;
                var (keyPart, message) = MessageOf(entity.Message, meter);
                var channel = $"channel:{project.Broker}:{entity.Entity}";
                context.Add(FactKind.Channel, channel, entity.Entity, EntityName.At(entity.Site.Path, entity.Site.Start, entity.Site.End),
                    ("kind", kind), ("transport", project.Broker), ("entityOf", message), ("messageType", entity.Message.Written),
                    ("conditional", entity.Conditional));
                if (message is not null)
                    entitiesOf[keyPart] = entitiesOf.TryGetValue(keyPart, out var known) ? (known.Channel, known.Single && known.Channel == channel) : (channel, true);
            }
            foreach (var publish in project.Publishes)
            {
                var meter = context.Meter(publish.Site.Path);
                if (!meter.Charge(FactCost))
                    continue;
                var (keyPart, message) = MessageOf(publish.Message, meter);
                context.Add(FactKind.Publication, $"publication:{project.Id}:{keyPart}", publish.Message.Simple,
                    [Publish.At(publish.Site.Path, publish.Site.Start, publish.Site.End)],
                    [
                        .. project.Owner, ("transport", "masstransit"), ("mode", publish.Mode), ("message", message),
                        ("messageType", publish.Message.Written),
                        ("channel", message is not null && entitiesOf.TryGetValue(keyPart, out var entity) && entity.Single ? entity.Channel : null),
                        ("conditional", publish.Conditional),
                    ]);
            }
        }

        // The contract of every type that a use site resolved to, once, from all its parts.
        foreach (var logical in index.All)
        {
            if (!contracts.Contains(logical))
                continue;
            var first = logical.Parts[0];
            var meter = context.Meter(first.File.File);
            if (!meter.Charge(FactCost))
                continue;
            var fields = new List<string>();
            var evidence = new List<SourceEvidence>();
            foreach (var part in logical.Parts)
            {
                if (evidence.Count < MaxEvidenceItems)
                    evidence.Add(Contract.At(part.Whole.Path, part.Whole.Start, part.Whole.End));
                if (fields.Count <= context.Limits.MaxListItems)
                    fields.AddRange(part.Fields);
            }
            context.Add(FactKind.Message, "message:" + logical.QualifiedName, logical.Name, evidence,
            [
                ("clrType", logical.QualifiedName), ("declaredIn", logical.Project.Path),
                ("fields", context.Names(fields, "fields", first.Whole.Path, first.Line, sorted: true, separator: ";")),
                ("conditional", first.Conditional),
            ]);
        }

        foreach (var project in projects)
            EmitMassTransitOutbox(context, project);
    }

    private static void EmitMassTransitOutbox(ExtractionContext context, Project project)
    {
        // The fact needs the registration; the other calls are more evidence.
        var registrations = project.OutboxCalls.Where(c => c.Call == "AddEntityFrameworkOutbox").ToList();
        if (registrations.Count > 0)
        {
            var related = project.OutboxCalls.Where(c => c.Call is not ("AddInMemoryOutbox" or "UseInMemoryOutbox")).ToList();
            var contexts = registrations.Select(r => r.DbContext).OfType<string>().Distinct(StringComparer.Ordinal).Take(2).ToList();
            context.Add(FactKind.Pattern, $"outbox:{project.Id}:masstransit-entity-framework", "Transactional outbox",
                related.Take(MaxEvidenceItems).Select(c => Outbox.At(c.Site.Path, c.Site.Start, c.Site.End)).ToList(),
                [
                    .. project.Owner, ("pattern", "transactional-outbox"), ("mechanism", "masstransit-entity-framework"),
                    ("dbContext", contexts.Count == 1 ? contexts[0] : null),
                    ("busOutbox", related.Any(c => c.Call == "UseBusOutbox") ? "true" : "false"),
                    ("outboxEntities", related.Any(c => c.Call == "AddOutboxMessageEntity") ? "true" : "false"),
                    ("conditional", registrations[0].Conditional),
                ]);
        }
        var inMemory = project.OutboxCalls.Where(c => c.Call is "AddInMemoryOutbox" or "UseInMemoryOutbox").ToList();
        if (inMemory.Count > 0)
            context.Add(FactKind.Pattern, $"outbox:{project.Id}:masstransit-in-memory", "In-memory outbox",
                inMemory.Take(MaxEvidenceItems).Select(c => Outbox.At(c.Site.Path, c.Site.Start, c.Site.End)).ToList(),
                [.. project.Owner, ("pattern", "in-memory-outbox"), ("mechanism", "masstransit-in-memory"), ("conditional", inMemory[0].Conditional)]);
    }

    // ---- hand-written outbox ----------------------------------------------------------

    private static bool IsOutboxEntity(string entity)
    {
        var name = entity[(entity.LastIndexOf('.') + 1)..];
        return OutboxEntities.Contains(name) || Follows(name, "Outbox", "Message") || Follows(name, "IntegrationEvent", "Log");

        static bool Follows(string text, string first, string second)
        {
            var at = text.IndexOf(first, StringComparison.Ordinal);
            return at >= 0 && text.IndexOf(second, at + first.Length, StringComparison.Ordinal) >= 0;
        }
    }

    private static void EmitOutbox(ExtractionContext context, Project project, TypeIndex index)
    {
        // The hosted services of the project that call a publish API.
        var dispatchers = new List<TypeInfo>();
        foreach (var file in project.Files)
            foreach (var type in file.Types)
                if (type.Hosted is { PublishLine: > 0 })
                    dispatchers.Add(type);
        if (dispatchers.Count == 0)
            return;
        if (dispatchers.Count > MaxEvidenceItems)
        {
            context.LimitReached("list-items", dispatchers[MaxEvidenceItems].Header.Path, stop: false, dispatchers[MaxEvidenceItems].Line);
            dispatchers.RemoveRange(MaxEvidenceItems, dispatchers.Count - MaxEvidenceItems);
        }

        var tables = 0;
        foreach (var logical in index.All)
        {
            if (logical.Project != project || logical.Keyword != "class" || !logical.IsConsistent || !logical.Parts.Any(IsDbContext))
                continue;
            foreach (var part in logical.Parts)
                foreach (var set in part.DbSets)
                {
                    if (!context.Meter(set.Site.Path).Charge())
                        return;
                    if (!IsOutboxEntity(set.Entity))
                        continue;
                    if (++tables > MaxEvidenceItems)
                    {
                        context.LimitReached("list-items", set.Site.Path, stop: false, set.Site.Start);
                        return;
                    }
                    var entity = set.Entity[(set.Entity.LastIndexOf('.') + 1)..];
                    var evidence = new List<SourceEvidence>();
                    var readers = new List<string>();
                    foreach (var dispatcher in dispatchers)
                    {
                        var hosted = dispatcher.Hosted!;
                        var readLine = hosted.Reads.TryGetValue((logical.Name, set.Set), out var read) ? read
                            : hosted.SetCalls.TryGetValue((logical.Name, entity), out var call) ? call : 0;
                        if (readLine == 0)
                            continue;
                        readers.Add(dispatcher.Name);
                        evidence.Add(HandwrittenOutbox.At(dispatcher.Header.Path, dispatcher.Header.Start, dispatcher.Header.End));
                        evidence.Add(HandwrittenOutbox.At(dispatcher.Header.Path, readLine, readLine));
                        evidence.Add(HandwrittenOutbox.At(dispatcher.Header.Path, hosted.PublishLine, hosted.PublishLine));
                    }
                    if (readers.Count == 0 || !context.Meter(set.Site.Path).Charge(FactCost))
                        continue;
                    evidence.Add(HandwrittenOutbox.At(set.Site.Path, set.Site.Start, set.Site.End));
                    context.Add(FactKind.Pattern, $"outbox:{project.Id}:hand-written", "Transactional outbox", evidence,
                    [
                        .. project.Owner, ("pattern", "transactional-outbox"), ("mechanism", "hand-written"),
                        ("dbContext", logical.Name), ("entity", set.Entity),
                        ("dispatcher", context.List(readers, set.Site.Path, set.Site.Start, sorted: true)),
                    ]);
                }
        }
    }
}
