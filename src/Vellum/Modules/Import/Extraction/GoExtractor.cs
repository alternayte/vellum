namespace Vellum.Modules.Import.Extraction;

/// <summary>
/// Go modules and source. Nothing is built or run: <c>go.mod</c> is read by line, and source
/// is read as tokens (see <see cref="GoLexer"/>). A Go file belongs to the nearest
/// <c>go.mod</c> at or above its directory; a file with no module is noted and not read.
/// <c>_test.go</c> files are not read. <c>&lt;pid&gt;</c> is
/// <see cref="ExtractionContext.ProjectId"/> of the <c>go.mod</c>; the name of the service is
/// the last element of the module path.
/// <para>
/// Names are bound by lexical scope (see <see cref="GoSourceReader"/>). A call counts only
/// when its receiver is a plain identifier whose declaration in scope is a recognised
/// constructor or a recognised type of the imported package; a receiver that is a field
/// (<c>s.router.Get</c>), a call (<c>r.With(a).Get</c>) or an unknown variable gives nothing.
/// A package name counts only where no declaration in scope hides it. A name or path argument
/// is one string literal, or one identifier that is a <c>const</c> string literal in scope:
/// a package-level constant that no parameter or local hides, or a constant of the same
/// function. Anything else is noted and gives no fact.
/// </para>
/// <para>Rules, and what each does not recognise:</para>
/// <list type="bullet">
/// <item><c>go.module</c> — <c>service:&lt;pid&gt;</c>, with <c>hasMain</c>. Not: workspaces
/// (<c>go.work</c>), several commands in one module (they give one service).</item>
/// <item><c>go.require</c> — <c>dependency:&lt;pid&gt;:go:&lt;module&gt;</c>. Not: <c>replace</c> and <c>exclude</c>.</item>
/// <item><c>go.net-http-route</c> — <c>operation:&lt;pid&gt;:&lt;METHOD&gt; &lt;path&gt;</c> for
/// <c>http.HandleFunc/Handle</c> (two arguments) on the package, on a variable assigned from
/// <c>http.NewServeMux()</c>, or on a parameter or variable of type <c>*http.ServeMux</c>, with
/// the Go 1.22 method and host patterns. A pattern that ends in a slash matches the subtree:
/// the path has no trailing slash and the fact has <c>subtree=true</c>. A pattern that ends in
/// <c>{$}</c> gives <c>exact=true</c>. Not: a mux that is an argument of
/// <c>http.StripPrefix</c> in the same file (noted), a mux nested under another handler in
/// another file, a handler's own switch on the method.</item>
/// <item><c>go.chi-route</c> — <c>Get/Post/Put/Patch/Delete/Head/Options/Connect/Trace</c>,
/// <c>Method/MethodFunc</c>, <c>Handle/HandleFunc</c> on a variable assigned from
/// <c>chi.NewRouter()</c>/<c>NewMux()</c>, with <c>Route("/prefix", func(r chi.Router) {…})</c>,
/// <c>Group(func(r chi.Router) {…})</c>, <c>x := r.With(…)/Group(…)/Route(…)</c>, and one
/// <c>Mount("/prefix", x)</c> of that variable in scope. Not: a router that is returned,
/// reassigned, mounted twice, a parameter, or a package-level variable (its mount point is
/// set elsewhere; noted), more than <see cref="GoSourceReader.MaxRouterDepth"/> routers inside
/// each other (noted).</item>
/// <item><c>go.gin-route</c> and <c>go.echo-route</c> — <c>GET/POST/…</c>, <c>Any</c>,
/// <c>Handle</c>/<c>Add</c> on a variable assigned from <c>gin.Default()/New()</c> or
/// <c>echo.New()</c>, a <c>*gin.Engine</c> or <c>*echo.Echo</c> parameter or variable, and
/// <c>x := r.Group("/prefix")</c>. Not: a <c>*gin.RouterGroup</c> or <c>*echo.Group</c>
/// parameter and a reassigned group (noted), <c>Static</c> and <c>File</c> routes.</item>
/// <item><c>go.amqp-exchange-declare</c>, <c>go.amqp-queue-declare</c> (with the
/// <c>x-dead-letter-exchange</c> argument), <c>go.amqp-queue-bind</c>, <c>go.amqp-publish</c>,
/// <c>go.amqp-consume</c> — the amqp091-go and streadway methods with their exact argument
/// count, on a receiver that is a <c>*amqp.Channel</c> parameter or variable, or is assigned
/// from <c>conn.Channel()</c> where <c>conn</c> is a <c>*amqp.Connection</c> or is assigned from
/// <c>amqp.Dial/DialTLS/DialConfig/Open</c>. Keys: <c>channel:amqp:&lt;name&gt;</c>,
/// <c>binding:&lt;pid&gt;:amqp:&lt;queue&gt;:&lt;exchange&gt;[:&lt;key&gt;]</c>,
/// <c>publication:&lt;pid&gt;:amqp:&lt;exchange&gt;:&lt;key&gt;</c>,
/// <c>subscription:&lt;pid&gt;:amqp:&lt;queue&gt;</c>. Not: a channel that is a struct field.</item>
/// <item><c>go.kafka-topic</c> — <c>Topic:</c> in a <c>kafka.ReaderConfig</c>, <c>kafka.Writer</c>,
/// <c>kafka.WriterConfig</c> or <c>kafka.Message</c> literal of the imported package;
/// confluent <c>SubscribeTopics([]string{…}, x)</c> on a <c>*kafka.Consumer</c> parameter or
/// variable or a variable assigned from <c>kafka.NewConsumer</c>. Keys:
/// <c>channel:kafka:&lt;topic&gt;</c>, <c>subscription|publication:&lt;pid&gt;:kafka:&lt;topic&gt;</c>.
/// Not: confluent <c>TopicPartition{Topic: &amp;name}</c> (a pointer to a variable; noted).</item>
/// <item><c>go.nats-subject</c> — <c>Subscribe</c>, <c>QueueSubscribe</c>, <c>SubscribeSync</c>,
/// <c>ChanSubscribe</c>, <c>Publish</c>, <c>Request</c> on a <c>*nats.Conn</c> parameter or
/// variable or a variable assigned from <c>nats.Connect</c>. Keys: <c>channel:nats:&lt;subject&gt;</c>,
/// <c>subscription|publication:&lt;pid&gt;:nats:&lt;subject&gt;</c>. Not: JetStream consumers and streams.</item>
/// <item><c>go.sql-open</c> — <c>sql.Open</c>, sqlx <c>Open/Connect/MustConnect/MustOpen</c>,
/// <c>pgx.Connect/ConnectConfig</c>, <c>pgxpool.New/Connect/NewWithConfig</c> on the imported
/// package. The connection string is never read into a fact. Not: tables and queries, ORMs,
/// the database name.</item>
/// <item><c>go.struct-message</c> — <c>message:&lt;import path of the package&gt;.&lt;T&gt;</c> for a
/// struct that one function decodes from a consumed delivery: <c>json.Unmarshal(d.F, &amp;v)</c>
/// (<c>encoding/json</c>, two arguments) where, in that same function, <c>v</c> is declared as
/// <c>var v T</c> or <c>v := T{}</c> with <c>T</c> a struct the package declares once and that
/// nothing in scope hides, and <c>d</c> is one of:
/// an AMQP delivery (<c>F</c> = <c>Body</c>) from <c>d := &lt;-ds</c>, <c>d, ok := &lt;-ds</c>,
/// <c>case d[, ok] := &lt;-ds:</c> or <c>for d := range ds {</c>, where <c>ds</c> is assigned from
/// <c>ch.Consume/ConsumeWithContext</c> on a recognised channel;
/// a NATS message (<c>F</c> = <c>Data</c>) that is the only parameter (<c>*nats.Msg</c>) of the
/// function literal that is the last argument of a recognised <c>Subscribe/QueueSubscribe</c>;
/// a kafka-go message (<c>F</c> = <c>Value</c>) assigned from <c>r.ReadMessage/FetchMessage</c>
/// where <c>r := kafka.NewReader(kafka.ReaderConfig{…})</c>.
/// The fact lists <c>fields</c> as <c>jsonName:GoType</c> (see <see cref="GoPackage"/>). Each
/// struct of the same package that a field type names gives its own message fact, to a depth of
/// <see cref="GoSourceReader.MaxStructDepth"/> and at most <see cref="GoSourceReader.MaxStructsPerMessage"/>
/// structs. The subscription of the consume call gets a <c>message</c> attribute with the key.
/// A decode of a delivery that does not meet the rule is noted and gives no fact. Not: a
/// decode in a helper function, a decoder other than <c>json.Unmarshal</c>, a struct with an
/// embedded field, a type from another package.</item>
/// </list>
/// <para>
/// Work is linear in the size of a file: each pass reads the tokens once, a name is resolved
/// in constant time, and nothing recurses. Every pass charges the <see cref="WorkMeter"/> of the file.
/// </para>
/// </summary>
internal sealed class GoExtractor : IExtractor
{
    internal static readonly ExtractionRule Module = new("go.module", 2);
    internal static readonly ExtractionRule Require = new("go.require", 2);
    internal static readonly ExtractionRule NetHttp = new("go.net-http-route", 2);
    internal static readonly ExtractionRule Chi = new("go.chi-route", 2);
    internal static readonly ExtractionRule Gin = new("go.gin-route", 2);
    internal static readonly ExtractionRule Echo = new("go.echo-route", 2);
    internal static readonly ExtractionRule ExchangeDeclare = new("go.amqp-exchange-declare", 2);
    internal static readonly ExtractionRule QueueDeclare = new("go.amqp-queue-declare", 2);
    internal static readonly ExtractionRule QueueBind = new("go.amqp-queue-bind", 2);
    internal static readonly ExtractionRule AmqpPublish = new("go.amqp-publish", 2);
    internal static readonly ExtractionRule AmqpConsume = new("go.amqp-consume", 2);
    internal static readonly ExtractionRule KafkaTopic = new("go.kafka-topic", 2);
    internal static readonly ExtractionRule NatsSubject = new("go.nats-subject", 2);
    internal static readonly ExtractionRule SqlOpen = new("go.sql-open", 2);
    internal static readonly ExtractionRule StructMessage = new("go.struct-message", 1);

    public string Id => "go";
    public IReadOnlyList<ExtractionRule> Rules =>
    [
        Module, Require, NetHttp, Chi, Gin, Echo, ExchangeDeclare, QueueDeclare, QueueBind,
        AmqpPublish, AmqpConsume, KafkaTopic, NatsSubject, SqlOpen, StructMessage,
    ];

    private static readonly (string Module, string Prefix, string Technology, string Role)[] KnownModules =
        new (string Module, string Technology, string Role)[]
        {
            ("github.com/rabbitmq/amqp091-go", "rabbitmq", "messaging"), ("github.com/streadway/amqp", "rabbitmq", "messaging"),
            ("github.com/segmentio/kafka-go", "kafka", "messaging"), ("github.com/confluentinc/confluent-kafka-go", "kafka", "messaging"),
            ("github.com/IBM/sarama", "kafka", "messaging"), ("github.com/nats-io/nats.go", "nats", "messaging"),
            ("github.com/jackc/pgx", "postgres", "data"), ("github.com/lib/pq", "postgres", "data"),
            ("github.com/go-sql-driver/mysql", "mysql", "data"), ("github.com/mattn/go-sqlite3", "sqlite", "data"),
            ("modernc.org/sqlite", "sqlite", "data"), ("github.com/microsoft/go-mssqldb", "sqlserver", "data"),
            ("github.com/redis/go-redis", "redis", "data"), ("github.com/go-redis/redis", "redis", "data"),
            ("go.mongodb.org/mongo-driver", "mongodb", "data"), ("gorm.io/gorm", "gorm", "data"),
            ("github.com/jmoiron/sqlx", "sqlx", "data"),
            ("github.com/go-chi/chi", "chi", "http-router"), ("github.com/gin-gonic/gin", "gin", "http-router"),
            ("github.com/labstack/echo", "echo", "http-router"), ("github.com/gorilla/mux", "gorilla-mux", "http-router"),
        }.Select(k => (k.Module, k.Module + "/", k.Technology, k.Role)).ToArray();

    public void Run(ExtractionContext context)
    {
        var modules = new List<GoModule>();
        var byDirectory = new Dictionary<string, GoModule>(StringComparer.Ordinal);
        foreach (var file in context.Files)
        {
            if (file.Name != "go.mod")
                continue;
            context.Guarded(Id, file.Path, () =>
            {
                if (ReadModule(context, file) is not { } module)
                    return;
                modules.Add(module);
                byDirectory[file.Directory] = module;
            });
        }

        // The files of one directory are one package: constants and struct types are resolved inside it.
        var packages = new SortedDictionary<string, List<(SourceText File, GoModule Module)>>(StringComparer.Ordinal);
        foreach (var file in context.Files)
        {
            if (file.Extension != ".go" || file.Name.EndsWith("_test.go", StringComparison.Ordinal))
                continue;
            if (Owner(byDirectory, file.Directory) is not { } owner)
            {
                context.Note(NoteKind.Undecided, "no-owning-module", file.Path);
                continue;
            }
            if (!packages.TryGetValue(file.Directory, out var files))
                packages[file.Directory] = files = [];
            files.Add((file, owner));
        }

        foreach (var files in packages.Values)
        {
            if (context.Stopped)
                break;
            // Two passes, and the tokens of one file at a time: the first pass keeps only the
            // package-level constants and structs, so the memory in use is that of one file.
            var package = new GoPackage();
            foreach (var (file, module) in files)
                context.Guarded(Id, file.Path, () =>
                {
                    if (Tokens(context, file) is { } tokens && package.Declare(file, tokens, context.Meter(file)))
                        module.HasMain = true;
                });
            foreach (var (file, module) in files)
            {
                if (context.Stopped)
                    break;
                context.Guarded(Id, file.Path, () =>
                {
                    if (context.Meter(file).Exhausted)
                        return;
                    if (Tokens(context, file) is { } tokens)
                        new GoSourceReader(context, file, module, package, tokens).Read();
                });
            }
        }

        foreach (var module in modules)
            context.Guarded(Id, module.Path, () => AddModule(context, module));
    }

    private static TokenList? Tokens(ExtractionContext context, SourceText file)
    {
        var meter = context.Meter(file);
        if (!meter.Charge(file.Content.Length))
            return null;
        var tokens = GoLexer.Tokenize(file.Content);
        // The bracket matching of the token list is one more pass over the tokens.
        return meter.Charge(tokens.Count) ? tokens : null;
    }

    /// <summary>The module of the nearest <c>go.mod</c> at or above a directory.</summary>
    private static GoModule? Owner(Dictionary<string, GoModule> byDirectory, string directory)
    {
        while (true)
        {
            if (byDirectory.TryGetValue(directory, out var module))
                return module;
            if (directory.Length == 0)
                return null;
            var slash = directory.LastIndexOf('/');
            directory = slash < 0 ? "" : directory[..slash];
        }
    }

    // ---- go.mod -----------------------------------------------------------------------

    private static GoModule? ReadModule(ExtractionContext context, SourceText file)
    {
        if (!context.Meter(file).Charge(file.Content.Length))
            return null;
        string? modulePath = null, goVersion = null;
        var moduleLine = 0;
        var requires = new List<GoRequirement>();
        var inRequire = false;
        var line = 0;
        // One more requirement than a file can give facts, so that the bound is reported by the context.
        var maxRequires = context.Limits.MaxFactsPerFile + 1;
        foreach (var rawLine in file.Content.AsSpan().EnumerateLines())
        {
            line++;
            var comment = rawLine.IndexOf("//", StringComparison.Ordinal);
            var indirect = comment >= 0 && rawLine[comment..].Contains("indirect", StringComparison.Ordinal);
            var text = (comment < 0 ? rawLine : rawLine[..comment]).Trim();
            if (text.Length == 0)
                continue;
            var first = Word(ref text);
            if (inRequire)
            {
                if (first.SequenceEqual(")"))
                    inRequire = false;
                else if (Word(ref text) is { Length: > 0 } version && requires.Count < maxRequires)
                    requires.Add(new GoRequirement(first.Trim('"').ToString(), version.ToString(), indirect, line));
            }
            else if (first.SequenceEqual("module") && modulePath is null)
            {
                if (Word(ref text) is { Length: > 0 } path)
                {
                    modulePath = path.Trim('"').ToString();
                    moduleLine = line;
                }
            }
            else if (first.SequenceEqual("go"))
            {
                if (Word(ref text) is { Length: > 0 } version)
                    goVersion = version.ToString();
            }
            else if (first.SequenceEqual("require"))
            {
                var second = Word(ref text);
                if (second.SequenceEqual("("))
                    inRequire = true;
                else if (second.Length > 0 && Word(ref text) is { Length: > 0 } version && requires.Count < maxRequires)
                    requires.Add(new GoRequirement(second.Trim('"').ToString(), version.ToString(), indirect, line));
            }
        }
        var name = GoMod.LastElement(modulePath ?? "");
        if (string.IsNullOrEmpty(modulePath) || name.Length == 0)
        {
            context.Note(NoteKind.ParseFailure, "no-module-path", file.Path);
            return null;
        }
        return new GoModule(file.Path, file.Directory, modulePath, name, context.ProjectId(file), moduleLine, goVersion, requires);
    }

    /// <summary>Takes the first word of a trimmed line and leaves the rest, trimmed at its start.</summary>
    private static ReadOnlySpan<char> Word(ref ReadOnlySpan<char> text)
    {
        var end = 0;
        while (end < text.Length && !char.IsWhiteSpace(text[end]))
            end++;
        var word = text[..end];
        text = text[end..].TrimStart();
        return word;
    }

    /// <summary>The service first, so that a <c>go.mod</c> with more requirements than a file can give facts still gives its service.</summary>
    private static void AddModule(ExtractionContext context, GoModule module)
    {
        context.Add(FactKind.Service, module.ServiceKey, module.Name, Module.At(module.Path, module.ModuleLine, module.ModuleLine),
            ("language", "go"), ("module", module.ModulePath), ("project", module.Path),
            ("goVersion", module.GoVersion), ("hasMain", module.HasMain ? "true" : "false"));
        var meter = context.Meter(module.Path);
        foreach (var requirement in module.Requirements)
        {
            if (!meter.Charge(KnownModules.Length))
                return;
            var name = requirement.Module;
            string? technology = null, role = null;
            foreach (var known in KnownModules)
            {
                if (name != known.Module && !name.StartsWith(known.Prefix, StringComparison.Ordinal))
                    continue;
                (technology, role) = (known.Technology, known.Role);
                break;
            }
            context.Add(FactKind.Dependency, $"dependency:{module.ProjectId}:go:{name}", name, Require.At(module.Path, requirement.Line, requirement.Line),
                ("ecosystem", "go"), ("package", name), ("version", requirement.Version), ("indirect", requirement.Indirect ? "true" : null),
                ("project", module.Path), ("service", module.ServiceKey), ("technology", technology), ("role", role));
        }
    }
}

internal readonly record struct GoRequirement(string Module, string Version, bool Indirect, int Line);

/// <summary>One <c>go.mod</c>.</summary>
/// <param name="Name">The display name: the last element of the module path.</param>
/// <param name="ProjectId">The identity in every key of the module (<see cref="ExtractionContext.ProjectId"/>).</param>
internal sealed record GoModule(
    string Path, string Directory, string ModulePath, string Name, string ProjectId,
    int ModuleLine, string? GoVersion, IReadOnlyList<GoRequirement> Requirements)
{
    public string ServiceKey { get; } = "service:" + ProjectId;
    public bool HasMain { get; set; }

    /// <summary>The import path of the package in <paramref name="directory"/>, which is at or below the module.</summary>
    public string ImportPath(string directory) =>
        directory.Length > Directory.Length ? ModulePath.TrimEnd('/') + "/" + directory[Directory.Length..].TrimStart('/') : ModulePath;
}
