namespace Vellum.Modules.Import.Extraction;

/// <summary>
/// Go modules and source. Nothing is built or run: <c>go.mod</c> is read by line, and source
/// is read as tokens (see <see cref="GoLexer"/>). A Go file belongs to the nearest
/// <c>go.mod</c> at or above its directory; a file with no module is noted and not read.
/// <c>_test.go</c> files are not read. <c>&lt;service&gt;</c> is the last element of the module
/// path. A name or path argument must be a string literal or a string constant declared in
/// the same directory; anything else is noted and gives no fact.
/// <para>Rules, and what each does not recognise:</para>
/// <list type="bullet">
/// <item><c>go.module</c> — <c>service:&lt;service&gt;</c>, with <c>hasMain</c>. Not: workspaces (<c>go.work</c>), several commands in one module (they give one service).</item>
/// <item><c>go.require</c> — <c>dependency:&lt;service&gt;:go:&lt;module&gt;</c>. Not: <c>replace</c> and <c>exclude</c>.</item>
/// <item><c>go.net-http-route</c> — <c>http.HandleFunc/Handle</c> and the same on a
/// <c>http.NewServeMux()</c> variable or a <c>*http.ServeMux</c> parameter, with Go 1.22
/// method and host patterns. Not: a mux that is passed to <c>StripPrefix</c> (noted), a
/// handler's own switch on the method.</item>
/// <item><c>go.chi-route</c> — <c>Get/Post/Put/Patch/Delete/Head/Options</c>, <c>Method</c>,
/// <c>Handle</c>, with <c>Route("/prefix", func…)</c>, <c>Group</c> and <c>With</c>, and
/// <c>Mount</c> of a router from the same file. Not: a router that is returned from a
/// function or is a parameter (its mount point is set by a caller; noted).</item>
/// <item><c>go.gin-route</c> and <c>go.echo-route</c> — <c>GET/POST/…</c>, <c>Any</c>,
/// <c>Handle</c>/<c>Add</c>, with <c>Group("/prefix")</c>. Not: a <c>*gin.RouterGroup</c> or
/// <c>*echo.Group</c> parameter (noted), <c>Static</c> and <c>File</c> routes.</item>
/// <item><c>go.amqp-exchange-declare</c>, <c>go.amqp-queue-declare</c> (with the
/// <c>x-dead-letter-exchange</c> argument), <c>go.amqp-queue-bind</c>, <c>go.amqp-publish</c>,
/// <c>go.amqp-consume</c> — the amqp091-go and streadway calls, matched by name and argument
/// count in a file that imports the package. Not: which message type a queue carries.</item>
/// <item><c>go.kafka-topic</c> — <c>Topic:</c> in <c>kafka.ReaderConfig</c>, <c>kafka.Writer</c>,
/// <c>kafka.WriterConfig</c>, <c>kafka.Message</c>; confluent <c>SubscribeTopics([]string{…})</c>.
/// Not: confluent <c>TopicPartition{Topic: &amp;name}</c> (a pointer to a variable; noted).</item>
/// <item><c>go.nats-subject</c> — <c>Subscribe</c>, <c>QueueSubscribe</c>, <c>SubscribeSync</c>,
/// <c>ChanSubscribe</c>, <c>Publish</c>, <c>Request</c>. Not: JetStream consumers and streams.</item>
/// <item><c>go.sql-open</c> — <c>sql.Open</c>, sqlx <c>Open/Connect/MustConnect</c>,
/// <c>pgx.Connect</c>, <c>pgxpool.New/Connect</c>. The connection string is never read into a
/// fact. Not: tables and queries, ORMs, the database name.</item>
/// </list>
/// </summary>
internal sealed class GoExtractor : IExtractor
{
    private static readonly ExtractionRule Module = new("go.module", 1);
    private static readonly ExtractionRule Require = new("go.require", 1);
    private static readonly ExtractionRule NetHttp = new("go.net-http-route", 1);
    private static readonly ExtractionRule Chi = new("go.chi-route", 1);
    private static readonly ExtractionRule Gin = new("go.gin-route", 1);
    private static readonly ExtractionRule Echo = new("go.echo-route", 1);
    private static readonly ExtractionRule ExchangeDeclare = new("go.amqp-exchange-declare", 1);
    private static readonly ExtractionRule QueueDeclare = new("go.amqp-queue-declare", 1);
    private static readonly ExtractionRule QueueBind = new("go.amqp-queue-bind", 1);
    private static readonly ExtractionRule AmqpPublish = new("go.amqp-publish", 1);
    private static readonly ExtractionRule AmqpConsume = new("go.amqp-consume", 1);
    private static readonly ExtractionRule KafkaTopic = new("go.kafka-topic", 1);
    private static readonly ExtractionRule NatsSubject = new("go.nats-subject", 1);
    private static readonly ExtractionRule SqlOpen = new("go.sql-open", 1);

    public string Id => "go";
    public IReadOnlyList<ExtractionRule> Rules =>
    [
        Module, Require, NetHttp, Chi, Gin, Echo, ExchangeDeclare, QueueDeclare, QueueBind,
        AmqpPublish, AmqpConsume, KafkaTopic, NatsSubject, SqlOpen,
    ];

    private static readonly (string Module, string Technology, string Role)[] KnownModules =
    [
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
    ];

    private static readonly Dictionary<string, string> SqlDrivers = new(StringComparer.Ordinal)
    {
        ["postgres"] = "postgres", ["pgx"] = "postgres", ["pq"] = "postgres", ["cloudsqlpostgres"] = "postgres",
        ["mysql"] = "mysql", ["sqlite3"] = "sqlite", ["sqlite"] = "sqlite", ["sqlserver"] = "sqlserver", ["mssql"] = "sqlserver",
    };

    private static readonly Dictionary<string, string> ChiVerbs = new(StringComparer.Ordinal)
    {
        ["Get"] = "GET", ["Post"] = "POST", ["Put"] = "PUT", ["Patch"] = "PATCH", ["Delete"] = "DELETE",
        ["Head"] = "HEAD", ["Options"] = "OPTIONS", ["Connect"] = "CONNECT", ["Trace"] = "TRACE",
    };

    private static readonly HashSet<string> UpperVerbs = new(StringComparer.Ordinal)
    {
        "GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS", "CONNECT", "TRACE",
    };

    private const int MaxBindings = 2000;
    private const int MaxCallTokens = 64;

    private sealed class GoModule(string path, string directory, string modulePath)
    {
        public string Path { get; } = path;
        public string Directory { get; } = directory;
        public string ModulePath { get; } = modulePath;
        public string Slug { get; } = ServiceName(modulePath);
        public string ServiceKey => "service:" + Slug;
        public bool HasMain { get; set; }
        public int ModuleLine { get; init; }
        public string? GoVersion { get; init; }
    }

    public void Run(ExtractionContext context)
    {
        var modules = new List<GoModule>();
        foreach (var file in context.Files)
            if (file.Name == "go.mod" && ReadModule(context, file) is { } module)
                modules.Add(module);

        var sources = new List<(SourceText File, GoModule Module, TokenList Tokens)>();
        foreach (var file in context.Files)
        {
            if (file.Extension != ".go" || file.Name.EndsWith("_test.go", StringComparison.Ordinal))
                continue;
            var owner = modules.Where(m => file.IsUnder(m.Directory))
                .OrderByDescending(m => m.Directory.Length).ThenBy(m => m.Path, StringComparer.Ordinal).FirstOrDefault();
            if (owner is null)
            {
                context.Note(NoteKind.Undecided, "no-owning-module", file.Path);
                continue;
            }
            var tokens = GoLexer.Tokenize(file.Content);
            sources.Add((file, owner, tokens));
            if (tokens.Id(0, "package") && tokens.Id(1, "main"))
                for (var i = 2; i + 2 < tokens.Count && !owner.HasMain; i++)
                    owner.HasMain = tokens.Id(i, "func") && tokens.Id(i + 1, "main") && tokens.P(i + 2, '(') && tokens.Parent[i] < 0;
        }

        foreach (var module in modules)
            context.Add(FactKind.Service, module.ServiceKey, module.Slug, Module.At(module.Path, module.ModuleLine, module.ModuleLine),
                ("language", "go"), ("module", module.ModulePath), ("project", module.Path),
                ("goVersion", module.GoVersion), ("hasMain", module.HasMain ? "true" : "false"));

        // String constants are resolved inside one package: the files of one directory.
        foreach (var package in sources.GroupBy(s => s.File.Directory, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var constants = Constants(package.Select(s => s.Tokens));
            foreach (var (file, module, tokens) in package)
            {
                if (context.Stopped)
                    return;
                new SourceReader(context, file, module, tokens, constants).Read();
            }
        }
    }

    // ---- go.mod -----------------------------------------------------------------------

    private static GoModule? ReadModule(ExtractionContext context, SourceText file)
    {
        string? modulePath = null, goVersion = null;
        var moduleLine = 0;
        var requires = new List<(string Module, string Version, bool Indirect, int Line)>();
        var inRequire = false;
        var line = 0;
        foreach (var raw in file.Content.Split('\n'))
        {
            line++;
            var comment = raw.IndexOf("//", StringComparison.Ordinal);
            var indirect = comment >= 0 && raw.AsSpan(comment).Contains("indirect", StringComparison.Ordinal);
            var words = (comment < 0 ? raw : raw[..comment]).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0)
                continue;
            if (inRequire)
            {
                if (words[0] == ")")
                    inRequire = false;
                else if (words.Length >= 2)
                    requires.Add((words[0].Trim('"'), words[1], indirect, line));
            }
            else if (words[0] == "module" && words.Length >= 2 && modulePath is null)
            {
                modulePath = words[1].Trim('"');
                moduleLine = line;
            }
            else if (words[0] == "go" && words.Length >= 2)
            {
                goVersion = words[1];
            }
            else if (words[0] == "require")
            {
                if (words.Length >= 2 && words[1] == "(")
                    inRequire = true;
                else if (words.Length >= 3)
                    requires.Add((words[1].Trim('"'), words[2], indirect, line));
            }
        }
        if (string.IsNullOrEmpty(modulePath) || ServiceName(modulePath).Length == 0)
        {
            context.Note(NoteKind.ParseFailure, "no-module-path", file.Path);
            return null;
        }

        var module = new GoModule(file.Path, file.Directory, modulePath) { ModuleLine = moduleLine, GoVersion = goVersion };
        foreach (var (name, version, indirect, at) in requires)
        {
            var known = KnownModules.FirstOrDefault(k => name == k.Module || name.StartsWith(k.Module + "/", StringComparison.Ordinal));
            context.Add(FactKind.Dependency, $"dependency:{module.Slug}:go:{name}", name, Require.At(file.Path, at, at),
                ("ecosystem", "go"), ("package", name), ("version", version), ("indirect", indirect ? "true" : null),
                ("project", file.Path), ("service", module.ServiceKey), ("technology", known.Technology), ("role", known.Role));
        }
        return module;
    }

    /// <summary>"github.com/acme/orders/inventory-worker" and "…/inventory-worker/v2" both give "inventory-worker".</summary>
    private static string ServiceName(string modulePath)
    {
        var parts = modulePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var last = parts.Length - 1;
        if (last > 0 && parts[last].Length > 1 && parts[last][0] == 'v' && parts[last].AsSpan(1).IndexOfAnyExceptInRange('0', '9') < 0)
            last--;
        return last < 0 ? "" : TextUtil.Slug(parts[last]);
    }

    /// <summary>String constants of a package. A name with two different values is left out.</summary>
    private static Dictionary<string, string> Constants(IEnumerable<TokenList> files)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var ambiguous = new HashSet<string>(StringComparer.Ordinal);

        void Add(string name, string value)
        {
            if (values.TryGetValue(name, out var existing) && existing != value)
                ambiguous.Add(name);
            values[name] = value;
        }

        foreach (var t in files)
        {
            for (var i = 0; i < t.Count; i++)
            {
                if (!t.Id(i, "const"))
                    continue;
                if (t.P(i + 1, '(') && t.Match[i + 1] > i)
                {
                    for (var k = i + 2; k < t.Match[i + 1]; k++)
                        if (t.Id(k) && t.Line(k - 1) < t.Line(k) || t.Id(k) && k == i + 2)
                            TryConstant(t, k, Add);
                }
                else
                {
                    TryConstant(t, i + 1, Add);
                }
            }
        }
        foreach (var name in ambiguous)
            values.Remove(name);
        return values;
    }

    /// <summary>"name = "literal"" or "name string = "literal"", where the literal is the whole value.</summary>
    private static void TryConstant(TokenList t, int name, Action<string, string> add)
    {
        if (!t.Id(name))
            return;
        var equals = t.P(name + 1, '=') ? name + 1 : t.Id(name + 1) && t.P(name + 2, '=') ? name + 2 : -1;
        if (equals < 0 || t.StaticString(equals + 1) is not { } value)
            return;
        // "a" + suffix is not a literal value.
        if (equals + 2 < t.Count && t.Line(equals + 2) == t.Line(equals + 1) && !t.P(equals + 2, ')') && !t.P(equals + 2, ';'))
            return;
        add(t[name].Text, value);
    }

    // ---- one source file --------------------------------------------------------------

    private sealed class Binding(string name, int from, int to, string framework)
    {
        public string Name { get; } = name;
        public int From { get; } = from;
        public int To { get; } = to;
        public string Framework { get; } = framework;
        /// <summary>The route prefix, or null when the text of this file does not fix it.</summary>
        public string? Prefix { get; init; }
    }

    private sealed class SourceReader(ExtractionContext context, SourceText file, GoModule module, TokenList t, Dictionary<string, string> constants)
    {
        private readonly Dictionary<string, string> _imports = new(StringComparer.Ordinal);   // path -> local name
        private readonly List<Binding> _bindings = [];
        private readonly HashSet<int> _boundFunctions = [];
        private readonly HashSet<string> _returned = new(StringComparer.Ordinal);
        private readonly HashSet<string> _stripped = new(StringComparer.Ordinal);
        private readonly Dictionary<string, (int Site, string Receiver, int PathArg)> _mounts = new(StringComparer.Ordinal);
        private string? _http, _chi, _gin, _echo, _sql, _sqlx, _pgx, _pgxpool, _kafka;
        private bool _amqp, _nats, _confluent;

        private (string, string?)[] Owner => [("service", module.ServiceKey), ("project", module.Path)];

        public void Read()
        {
            ReadImports();
            _http = Local("net/http");
            _chi = Local("github.com/go-chi/chi");
            _gin = Local("github.com/gin-gonic/gin");
            _echo = Local("github.com/labstack/echo");
            _sql = Local("database/sql");
            _sqlx = Local("github.com/jmoiron/sqlx");
            _pgx = Local("github.com/jackc/pgx");
            _pgxpool = _imports.OrderBy(i => i.Key, StringComparer.Ordinal)
                .FirstOrDefault(i => i.Key.StartsWith("github.com/jackc/pgx", StringComparison.Ordinal) && i.Key.EndsWith("/pgxpool", StringComparison.Ordinal)).Value;
            _kafka = Local("github.com/segmentio/kafka-go") ?? Local("github.com/confluentinc/confluent-kafka-go");
            _confluent = Local("github.com/confluentinc/confluent-kafka-go") is not null;
            _amqp = Imports("github.com/rabbitmq/amqp091-go") || Imports("github.com/streadway/amqp");
            _nats = Imports("github.com/nats-io/nats.go");

            var routers = _http is not null || _chi is not null || _gin is not null || _echo is not null;
            if (routers)
                Prescan();

            for (var i = 0; i < t.Count; i++)
            {
                if (routers)
                {
                    if (t.Id(i, "func"))
                        BindParameters(i);
                    else if (t.Id(i) && !t.P(i - 1, '.'))
                        BindAssignment(i);
                }
                if (_kafka is not null && t.Id(i, "Topic") && t.P(i + 1, ':'))
                    ReadKafkaTopic(i);
                if (!t.Id(i) || !t.P(i + 1, '(') || t.Match[i + 1] < 0 || !t.P(i - 1, '.') || !t.Id(i - 2))
                    continue;
                var args = t.Args(i + 1);
                var name = t[i].Text;
                if (routers)
                    ReadRoute(i, name, args);
                if (_amqp)
                    ReadAmqp(i, name, args);
                if (_nats)
                    ReadNats(i, name, args);
                if (_confluent)
                    ReadConfluent(i, name, args);
                ReadSql(i, name, args);
            }
        }

        // ---- imports and values ----

        private void ReadImports()
        {
            for (var i = 0; i < t.Count; i++)
            {
                if (!t.Id(i, "import") || t.Parent[i] >= 0)
                    continue;
                if (t.P(i + 1, '(') && t.Match[i + 1] > i)
                {
                    for (var k = i + 2; k < t.Match[i + 1]; k++)
                        if (t.StaticString(k) is { } path)
                            _imports[path] = t.Line(k - 1) == t.Line(k) && (t.Id(k - 1) || t.P(k - 1, '.')) ? t[k - 1].Text : DefaultName(path);
                }
                else if (t.StaticString(i + 1) is { } single)
                {
                    _imports[single] = DefaultName(single);
                }
                else if (t.StaticString(i + 2) is { } aliased)
                {
                    _imports[aliased] = t[i + 1].Text;
                }
            }
        }

        /// <summary>The package name an import path gives when it has no alias.</summary>
        private static string DefaultName(string path)
        {
            var parts = path.Split('/');
            var last = parts.Length - 1;
            if (last > 0 && parts[last].Length > 1 && parts[last][0] == 'v' && parts[last].AsSpan(1).IndexOfAnyExceptInRange('0', '9') < 0)
                last--;
            var name = parts[last];
            if (name == "amqp091-go")
                return "amqp091";
            if (name.EndsWith(".go", StringComparison.Ordinal))
                name = name[..^3];
            if (name.EndsWith("-go", StringComparison.Ordinal))
                name = name[..^3];
            return name.StartsWith("go-", StringComparison.Ordinal) ? name[3..] : name;
        }

        private bool Imports(string path) =>
            _imports.Keys.Any(p => p == path || p.StartsWith(path + "/", StringComparison.Ordinal));

        /// <summary>The local name of an imported package, or null when it is not imported by a usable name.</summary>
        private string? Local(string path)
        {
            foreach (var (imported, name) in _imports.OrderBy(i => i.Key, StringComparer.Ordinal))
            {
                if (imported != path && !imported.StartsWith(path + "/", StringComparison.Ordinal))
                    continue;
                // Only the package itself or its version directory, not a sub-package.
                var rest = imported[path.Length..].Trim('/');
                if (rest.Length > 0 && !(rest[0] == 'v' && rest.AsSpan(1).IndexOfAnyExceptInRange('0', '9') < 0)
                    && rest != "kafka" && !rest.EndsWith("/kafka", StringComparison.Ordinal))
                    continue;
                return name is "_" or "." ? null : name;
            }
            return null;
        }

        /// <summary>The value of an argument that is one string literal or one string constant of the package.</summary>
        private string? Value((int Start, int End) arg)
        {
            if (arg.Start != arg.End)
                return null;
            return t.StaticString(arg.Start) ?? (t.IdText(arg.Start) is { } name && constants.TryGetValue(name, out var value) ? value : null);
        }

        private int EndLine(int method) => t.EndLine(t.Match[method + 1]);

        // ---- HTTP routers ----

        private void Prescan()
        {
            for (var i = 0; i < t.Count; i++)
            {
                if (t.Id(i, "return") && t.Id(i + 1) && !t.P(i + 2, '.') && !t.P(i + 2, '('))
                    _returned.Add(t[i + 1].Text);
                if (!t.Id(i) || !t.P(i + 1, '(') || t.Match[i + 1] < 0)
                    continue;
                if (t.Id(i, "StripPrefix"))
                {
                    for (var k = i + 2; k < t.Match[i + 1] && k < i + MaxCallTokens; k++)
                        if (t.Id(k))
                            _stripped.Add(t[k].Text);
                }
                else if (t.Id(i, "Mount") && t.P(i - 1, '.') && t.Id(i - 2))
                {
                    var args = t.Args(i + 1);
                    if (args.Count == 2 && args[1].Start == args[1].End && t.IdText(args[1].Start) is { } mounted)
                        _mounts[mounted] = (i, t[i - 2].Text, args[0].Start);
                }
            }
        }

        /// <summary>
        /// A file with more routers than any real file has is not followed further: every
        /// route looks its router up in this list.
        /// </summary>
        private void Bind(Binding binding)
        {
            if (_bindings.Count < MaxBindings)
                _bindings.Add(binding);
            else
                context.Note(NoteKind.Undecided, "too-many-routers", file.Path);
        }

        private Binding? Lookup(string name, int index)
        {
            Binding? best = null;
            foreach (var binding in _bindings)
                if (binding.Name == name && binding.From <= index && index <= binding.To && (best is null || binding.From >= best.From))
                    best = binding;
            return best;
        }

        /// <summary>"name := chi.NewRouter()", "name := parent.Group("/prefix")" and the like.</summary>
        private void BindAssignment(int name)
        {
            var value = t.P(name + 1, ':') && t.P(name + 2, '=') ? name + 3 : t.P(name + 1, '=') && !t.P(name + 2, '=') ? name + 2 : -1;
            if (value < 0 || !t.Id(value) || !t.P(value + 1, '.') || !t.Id(value + 2) || !t.P(value + 3, '(') || t.Match[value + 3] < 0)
                return;
            var receiver = t[value].Text;
            var method = t[value + 2].Text;
            var identifier = t[name].Text;
            var to = t.Count;

            if (receiver == _chi && method is "NewRouter" or "NewMux")
            {
                string? prefix = "";
                if (_returned.Contains(identifier))
                    prefix = null;
                else if (_mounts.TryGetValue(identifier, out var mount))
                    prefix = Lookup(mount.Receiver, mount.Site) is { Prefix: { } parent } && Value((mount.PathArg, mount.PathArg)) is { } segment
                        ? RoutePath.Join(parent, segment) : null;
                Bind(new Binding(identifier, name, to, "chi") { Prefix = prefix });
            }
            else if (receiver == _gin && method is "Default" or "New")
                Bind(new Binding(identifier, name, to, "gin") { Prefix = "" });
            else if (receiver == _echo && method == "New")
                Bind(new Binding(identifier, name, to, "echo") { Prefix = "" });
            else if (receiver == _http && method == "NewServeMux")
                Bind(new Binding(identifier, name, to, "net/http") { Prefix = _stripped.Contains(identifier) ? null : "" });
            else if (Lookup(receiver, value) is { } parent)
            {
                var args = t.Args(value + 3);
                if (parent.Framework is "gin" or "echo" && method == "Group")
                    Bind(new Binding(identifier, name, to, parent.Framework)
                    {
                        Prefix = parent.Prefix is not null && args.Count >= 1 && Value(args[0]) is { } segment ? RoutePath.Join(parent.Prefix, segment) : null,
                    });
                else if (parent.Framework == "chi" && method is "With" or "Group")
                    Bind(new Binding(identifier, name, to, "chi") { Prefix = parent.Prefix });
                else if (parent.Framework == "chi" && method == "Route")
                    Bind(new Binding(identifier, name, to, "chi")
                    {
                        Prefix = parent.Prefix is not null && args.Count >= 1 && Value(args[0]) is { } segment ? RoutePath.Join(parent.Prefix, segment) : null,
                    });
            }
        }

        /// <summary>
        /// A parameter of a router type is a router whose prefix the caller sets. Only the
        /// roots (<c>*gin.Engine</c>, <c>*echo.Echo</c>, <c>*http.ServeMux</c>) have a known one.
        /// </summary>
        private void BindParameters(int func)
        {
            if (_boundFunctions.Contains(func))
                return;
            var open = func + 1;
            // "func (r *T) Name(params)": the receiver list, then the name, then the parameters.
            if (t.P(open, '(') && t.Match[open] > open && t.Id(t.Match[open] + 1) && t.P(t.Match[open] + 2, '('))
                open = t.Match[open] + 2;
            else if (t.Id(open) && t.P(open + 1, '('))
                open++;
            if (!t.P(open, '(') || t.Match[open] < 0)
                return;
            var body = BodyAfter(t.Match[open]);
            if (body < 0)
                return;
            foreach (var (start, end) in t.Args(open))
            {
                if (!t.Id(start) || end - start < 3)
                    continue;
                var type = t.P(start + 1, '*') ? start + 2 : start + 1;
                if (!t.Id(type) || !t.P(type + 1, '.') || !t.Id(type + 2) || type + 2 != end)
                    continue;
                var package = t[type].Text;
                var (framework, prefix) = (t[type + 2].Text) switch
                {
                    "Router" or "Mux" when package == _chi => ("chi", (string?)null),
                    "Engine" when package == _gin => ("gin", ""),
                    "RouterGroup" or "IRouter" or "IRoutes" when package == _gin => ("gin", null),
                    "Echo" when package == _echo => ("echo", ""),
                    "Group" when package == _echo => ("echo", null),
                    "ServeMux" when package == _http => ("net/http", ""),
                    _ => ("", null),
                };
                if (framework.Length > 0)
                    Bind(new Binding(t[start].Text, body, t.Match[body], framework) { Prefix = prefix });
            }
        }

        /// <summary>The '{' of the function body that follows a parameter list, or -1.</summary>
        private int BodyAfter(int parametersClose)
        {
            for (var k = parametersClose + 1; k < t.Count && k < parametersClose + 40; k++)
            {
                if (t.P(k, '{'))
                    return t.Match[k] > k ? k : -1;
                if (t.P(k, '(') && t.Match[k] > k)
                    k = t.Match[k];
                else if (t.P(k, ')') || t.P(k, '}') || t.P(k, ';') || t.P(k, '='))
                    return -1;
            }
            return -1;
        }

        private void ReadRoute(int method, string name, List<(int Start, int End)> args)
        {
            var receiver = t[method - 2].Text;
            var binding = Lookup(receiver, method);
            if (binding is null)
            {
                // The package functions of net/http register on the default mux.
                if (receiver == _http && name is "HandleFunc" or "Handle" && args.Count == 2 && !t.P(method - 3, '.'))
                    AddNetHttpRoute(method, args[0], "");
                return;
            }
            if (t.P(method - 3, '.'))
                return;

            switch (binding.Framework)
            {
                case "net/http" when name is "HandleFunc" or "Handle" && args.Count == 2:
                    AddNetHttpRoute(method, args[0], binding.Prefix);
                    break;
                case "chi":
                    if (ChiVerbs.TryGetValue(name, out var chiVerb) && args.Count == 2)
                        AddRoute(Chi, "chi", method, chiVerb, args[0], binding.Prefix, colon: false);
                    else if (name is "Handle" or "HandleFunc" && args.Count == 2)
                        AddRoute(Chi, "chi", method, "ANY", args[0], binding.Prefix, colon: false);
                    else if (name is "Method" or "MethodFunc" && args.Count == 3)
                        AddRoute(Chi, "chi", method, Value(args[0])?.ToUpperInvariant(), args[1], binding.Prefix, colon: false);
                    else if (name == "Route" && args.Count == 2)
                        BindLiteral(args[1].Start, "chi", binding.Prefix is not null && Value(args[0]) is { } segment ? RoutePath.Join(binding.Prefix, segment) : null);
                    else if (name == "Group" && args.Count == 1)
                        BindLiteral(args[0].Start, "chi", binding.Prefix);
                    break;
                case "gin" or "echo":
                {
                    var rule = binding.Framework == "gin" ? Gin : Echo;
                    if (UpperVerbs.Contains(name) && args.Count >= 2)
                        AddRoute(rule, binding.Framework, method, name, args[0], binding.Prefix, colon: true);
                    else if (name == "Any" && args.Count >= 2)
                        AddRoute(rule, binding.Framework, method, "ANY", args[0], binding.Prefix, colon: true);
                    else if (name is "Handle" or "Add" && args.Count >= 3)
                        AddRoute(rule, binding.Framework, method, Value(args[0])?.ToUpperInvariant(), args[1], binding.Prefix, colon: true);
                    break;
                }
            }
        }

        /// <summary>"func(r chi.Router) { … }" as an argument: inside the body, r has the given prefix.</summary>
        private void BindLiteral(int func, string framework, string? prefix)
        {
            if (!t.Id(func, "func") || !t.P(func + 1, '(') || t.Match[func + 1] < 0 || !t.Id(func + 2))
                return;
            var body = BodyAfter(t.Match[func + 1]);
            if (body < 0)
                return;
            _boundFunctions.Add(func);
            Bind(new Binding(t[func + 2].Text, body, t.Match[body], framework) { Prefix = prefix });
        }

        private void AddNetHttpRoute(int method, (int Start, int End) patternArg, string? prefix)
        {
            var line = t.Line(method);
            if (Value(patternArg) is not { } pattern)
            {
                context.Note(NoteKind.Undecided, "dynamic-route", file.Path, line);
                return;
            }
            if (prefix is null)
            {
                context.Note(NoteKind.Undecided, "unresolved-route-prefix", file.Path, line);
                return;
            }
            // Go 1.22: "[METHOD ][HOST]/path".
            var verb = "ANY";
            var rest = pattern.Trim();
            var space = rest.IndexOf(' ');
            if (space > 0)
            {
                verb = rest[..space].ToUpperInvariant();
                rest = rest[(space + 1)..].TrimStart();
            }
            var slash = rest.IndexOf('/');
            if (slash < 0 || !UpperVerbs.Contains(verb) && verb != "ANY")
            {
                context.Note(NoteKind.Undecided, "dynamic-route", file.Path, line);
                return;
            }
            var path = RoutePath.Normalize(rest[slash..], keepTrailingSlash: true);
            context.Add(FactKind.Operation, $"operation:{module.Slug}:{verb} {path}", $"{verb} {path}",
                [NetHttp.At(file.Path, line, EndLine(method))],
                [
                    .. Owner, ("transport", "http"), ("method", verb), ("path", path), ("routeTemplate", pattern),
                    ("host", slash > 0 ? rest[..slash] : null), ("framework", "net/http"),
                ]);
        }

        private void AddRoute(ExtractionRule rule, string framework, int method, string? verb, (int Start, int End) pathArg, string? prefix, bool colon)
        {
            var line = t.Line(method);
            if (Value(pathArg) is not { } template || verb is null || !(UpperVerbs.Contains(verb) || verb == "ANY"))
            {
                context.Note(NoteKind.Undecided, "dynamic-route", file.Path, line);
                return;
            }
            if (prefix is null)
            {
                context.Note(NoteKind.Undecided, "unresolved-route-prefix", file.Path, line);
                return;
            }
            var raw = RoutePath.Join(prefix, template);
            var path = RoutePath.Normalize(colon ? RoutePath.FromColonParameters(raw) : raw);
            context.Add(FactKind.Operation, $"operation:{module.Slug}:{verb} {path}", $"{verb} {path}",
                [rule.At(file.Path, line, EndLine(method))],
                [.. Owner, ("transport", "http"), ("method", verb), ("path", path), ("routeTemplate", raw), ("framework", framework)]);
        }

        // ---- AMQP ----

        private void ReadAmqp(int method, string name, List<(int Start, int End)> args)
        {
            var line = t.Line(method);
            var end = EndLine(method);
            switch (name)
            {
                case "ExchangeDeclare" or "ExchangeDeclarePassive" when args.Count == 7:
                    if (Named(args[0], line) is { } exchange)
                        context.Add(FactKind.Channel, "channel:" + exchange, exchange, ExchangeDeclare.At(file.Path, line, end),
                            ("kind", "exchange"), ("transport", "amqp"), ("amqp.exchange.type", Value(args[1])),
                            ("durable", Flag(args[2])));
                    break;
                case "QueueDeclare" or "QueueDeclarePassive" when args.Count == 6:
                    if (Named(args[0], line) is { } queue)
                    {
                        var attributes = new List<(string, string?)>
                        {
                            ("kind", "queue"), ("transport", "amqp"), ("durable", Flag(args[1])),
                        };
                        for (var i = args[5].Start; i + 2 <= args[5].End; i++)
                        {
                            var option = t.StaticString(i);
                            if (!t.P(i + 1, ':') || option is not ("x-dead-letter-exchange" or "x-dead-letter-routing-key"))
                                continue;
                            var value = t.StaticString(i + 2) ?? (t.IdText(i + 2) is { } constant && constants.TryGetValue(constant, out var text) ? text : null);
                            if (value is null)
                                context.Note(NoteKind.Undecided, "dynamic-channel-name", file.Path, t.Line(i));
                            else if (option == "x-dead-letter-exchange")
                                attributes.Add(("deadLetterExchange", "channel:" + value));
                            else
                                attributes.Add(("deadLetterRoutingKey", value));
                        }
                        context.Add(FactKind.Channel, "channel:" + queue, queue, [QueueDeclare.At(file.Path, line, end)], attributes);
                    }
                    break;
                case "QueueBind" when args.Count == 5:
                    if (Named(args[0], line) is { } bound && Value(args[1]) is { } key && Named(args[2], line) is { } source)
                        context.Add(FactKind.Subscription, $"binding:{bound}:{source}{(key.Length == 0 ? "" : ":" + key)}", $"{bound} bound to {source}",
                            QueueBind.At(file.Path, line, end),
                            [.. Owner, ("transport", "amqp"), ("queue", "channel:" + bound), ("exchange", "channel:" + source), ("routingKey", key)]);
                    else if (Value(args[1]) is null)
                        context.Note(NoteKind.Undecided, "dynamic-channel-name", file.Path, line);
                    break;
                case "PublishWithContext" when args.Count == 6:
                    AddAmqpPublish(args[1], args[2], line, end);
                    break;
                case "Publish" when args.Count == 5:
                    AddAmqpPublish(args[0], args[1], line, end);
                    break;
                case "Consume" when args.Count == 7:
                    AddAmqpConsume(args[0], line, end);
                    break;
                case "ConsumeWithContext" when args.Count == 8:
                    AddAmqpConsume(args[1], line, end);
                    break;
            }
        }

        private void AddAmqpPublish((int, int) exchangeArg, (int, int) keyArg, int line, int end)
        {
            if (Value(exchangeArg) is not { } exchange || Value(keyArg) is not { } key)
            {
                context.Note(NoteKind.Undecided, "dynamic-channel-name", file.Path, line);
                return;
            }
            // The default exchange ("") routes to the queue named by the routing key.
            var channel = exchange.Length == 0 ? key : exchange;
            if (channel.Length == 0)
                return;
            context.Add(FactKind.Publication, $"publication:{module.Slug}:amqp:{exchange}:{key}", $"publish to {channel}",
                [AmqpPublish.At(file.Path, line, end)],
                [.. Owner, ("transport", "amqp"), ("channel", "channel:" + channel), ("exchange", exchange.Length == 0 ? "(default)" : exchange), ("routingKey", key)]);
        }

        private void AddAmqpConsume((int, int) queueArg, int line, int end)
        {
            if (Named(queueArg, line) is { } queue)
                context.Add(FactKind.Subscription, $"subscription:{module.Slug}:amqp:{queue}", $"consume {queue}",
                    [AmqpConsume.At(file.Path, line, end)],
                    [.. Owner, ("transport", "amqp"), ("queue", "channel:" + queue)]);
        }

        /// <summary>A name that is known and not empty, or null with a note.</summary>
        private string? Named((int Start, int End) arg, int line)
        {
            if (Value(arg) is { Length: > 0 } value)
                return value;
            context.Note(NoteKind.Undecided, "dynamic-channel-name", file.Path, line);
            return null;
        }

        private string? Flag((int Start, int End) arg) =>
            arg.Start == arg.End && t.IdText(arg.Start) is "true" or "false" ? t[arg.Start].Text : null;

        // ---- Kafka ----

        private void ReadKafkaTopic(int topic)
        {
            var open = t.Parent[topic];
            if (open < 3 || !t.P(open, '{') || !t.Id(open - 1) || !t.P(open - 2, '.') || !t.Id(open - 3, _kafka!))
                return;
            var type = t[open - 1].Text;
            var subscribes = type == "ReaderConfig";
            if (!subscribes && type is not ("Writer" or "WriterConfig" or "Message" or "TopicPartition"))
                return;
            var line = t.Line(topic);
            var single = t.P(topic + 3, ',') || t.P(topic + 3, '}');
            var name = single ? Value((topic + 2, topic + 2)) : null;
            if (string.IsNullOrEmpty(name))
            {
                context.Note(NoteKind.Undecided, "dynamic-channel-name", file.Path, line);
                return;
            }
            AddTopic(name, subscribes, "kafka", "topic", KafkaTopic, line, line);
        }

        private void ReadConfluent(int method, string name, List<(int Start, int End)> args)
        {
            if (name != "SubscribeTopics" || args.Count != 2)
                return;
            var line = t.Line(method);
            var open = args[0].End >= 0 && t.P(args[0].End, '}') ? t.Match[args[0].End] : -1;
            if (open < 0)
            {
                context.Note(NoteKind.Undecided, "dynamic-channel-name", file.Path, line);
                return;
            }
            foreach (var item in t.Args(open))
            {
                if (Value(item) is { Length: > 0 } topic)
                    AddTopic(topic, true, "kafka", "topic", KafkaTopic, line, EndLine(method));
                else
                    context.Note(NoteKind.Undecided, "dynamic-channel-name", file.Path, line);
            }
        }

        private void AddTopic(string name, bool subscribes, string transport, string kind, ExtractionRule rule, int line, int end)
        {
            var evidence = rule.At(file.Path, line, end);
            context.Add(FactKind.Channel, "channel:" + name, name, evidence, ("kind", kind), ("transport", transport));
            if (subscribes)
                context.Add(FactKind.Subscription, $"subscription:{module.Slug}:{transport}:{name}", $"consume {name}",
                    [evidence], [.. Owner, ("transport", transport), ("channel", "channel:" + name)]);
            else
                context.Add(FactKind.Publication, $"publication:{module.Slug}:{transport}:{name}", $"publish to {name}",
                    [evidence], [.. Owner, ("transport", transport), ("channel", "channel:" + name)]);
        }

        // ---- NATS ----

        private void ReadNats(int method, string name, List<(int Start, int End)> args)
        {
            var subscribes = name switch
            {
                "Subscribe" or "ChanSubscribe" when args.Count == 2 => true,
                "QueueSubscribe" when args.Count == 3 => true,
                "SubscribeSync" when args.Count == 1 => true,
                "Publish" when args.Count == 2 => false,
                "Request" when args.Count == 3 => false,
                _ => (bool?)null,
            };
            if (subscribes is null)
                return;
            var line = t.Line(method);
            if (Value(args[0]) is not { Length: > 0 } subject)
            {
                context.Note(NoteKind.Undecided, "dynamic-channel-name", file.Path, line);
                return;
            }
            AddTopic(subject, subscribes.Value, "nats", "subject", NatsSubject, line, EndLine(method));
        }

        // ---- database access ----

        private void ReadSql(int method, string name, List<(int Start, int End)> args)
        {
            var receiver = t[method - 2].Text;
            if (t.P(method - 3, '.'))
                return;
            var line = t.Line(method);
            if ((receiver == _sql && name == "Open" || receiver == _sqlx && name is "Open" or "Connect" or "MustConnect" or "MustOpen") && args.Count == 2)
            {
                if (Value(args[0]) is not { Length: > 0 } driver)
                {
                    context.Note(NoteKind.Undecided, "dynamic-driver-name", file.Path, line);
                    return;
                }
                context.Add(FactKind.DataAccess, $"dataaccess:{module.Slug}:sql:{driver}", $"database/sql ({driver})",
                    [SqlOpen.At(file.Path, line, EndLine(method))],
                    [.. Owner, ("technology", "database/sql"), ("driver", driver), ("storeKind", SqlDrivers.GetValueOrDefault(driver, "unknown"))]);
            }
            else if (receiver == _pgx && name is "Connect" or "ConnectConfig" || receiver == _pgxpool && name is "New" or "Connect" or "NewWithConfig")
            {
                context.Add(FactKind.DataAccess, $"dataaccess:{module.Slug}:pgx", "pgx",
                    [SqlOpen.At(file.Path, line, EndLine(method))],
                    [.. Owner, ("technology", "pgx"), ("storeKind", "postgres")]);
            }
        }
    }
}
