namespace Vellum.Modules.Import.Extraction;

/// <summary>
/// Reads one Go file in one forward pass over its tokens, with every name bound by lexical
/// scope, as the Go compiler binds it:
/// <list type="bullet">
/// <item>A scope is a <c>{ … }</c> block or one <c>case</c>/<c>default</c> clause. A name is
/// declared by a parameter, receiver, type parameter or named result (in the function body),
/// <c>:=</c>, <c>var</c>, <c>const</c> and <c>type</c>. The innermost declaration in scope is
/// the meaning of a name; when there is none, a package-level variable of this file with an
/// explicit recognised type, then a package constant, then an imported package.</item>
/// <item>A declaration is "recognised" when its type is <c>[*]pkg.Type</c> with a type the
/// rules know, or its value is exactly one call <c>x.F(…)</c> that ends the statement and that
/// the rules know. Every other declaration only hides the outer meaning of its name.</item>
/// <item>Wider than Go, in the direction that loses a fact and never invents one: a variable
/// declared in the header of an <c>if</c>, <c>for</c> or <c>switch</c> statement hides an outer
/// variable to the end of the enclosing block, and is never a recognised value (except
/// <c>for d := range deliveries {</c>).</item>
/// <item>Not read: struct fields, method values, variables of another file, assignments that
/// change what a variable holds (an assignment to a router variable makes its prefix unknown).</item>
/// </list>
/// A route is recorded where it is declared and gets its prefix at the end of the file, so
/// that a <c>Mount</c> below the routes of the mounted router still counts. Nothing recurses:
/// scopes, pending declarations and router chains are explicit stacks, and a router chain is
/// followed for at most <see cref="MaxRouterDepth"/> steps.
/// </summary>
internal sealed class GoSourceReader(ExtractionContext context, SourceText file, GoModule module, GoPackage package, TokenList t)
{
    /// <summary>The largest number of routers inside each other (<c>Route</c>, <c>Group</c>, <c>Mount</c>). A deeper router has no known prefix.</summary>
    public const int MaxRouterDepth = 32;
    /// <summary>The largest length of a route with its prefix. A longer route is noted and gives no fact.</summary>
    public const int MaxRouteLength = 256;
    /// <summary>How many levels of struct types below a decoded message type give message facts.</summary>
    public const int MaxStructDepth = 4;
    /// <summary>The largest number of struct types that one decoded message type gives facts for.</summary>
    public const int MaxStructsPerMessage = 32;
    /// <summary>No recognised call has more arguments. A call with more is not read.</summary>
    private const int MaxArguments = 8;
    /// <summary>The values of one <c>return</c> statement that are looked at.</summary>
    private const int MaxReturnTokens = 16;

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

    private const string HttpPath = "net/http", ChiPath = "github.com/go-chi/chi", GinPath = "github.com/gin-gonic/gin",
        EchoPath = "github.com/labstack/echo", SqlPath = "database/sql", SqlxPath = "github.com/jmoiron/sqlx",
        PgxPath = "github.com/jackc/pgx", PgxPoolPath = "github.com/jackc/pgx/pgxpool",
        SegmentioPath = "github.com/segmentio/kafka-go", ConfluentPath = "github.com/confluentinc/confluent-kafka-go/kafka",
        RabbitPath = "github.com/rabbitmq/amqp091-go", StreadwayPath = "github.com/streadway/amqp",
        NatsPath = "github.com/nats-io/nats.go", JsonPath = "encoding/json", OsPath = "os";

    /// <summary>The import paths the rules know, without version directories.</summary>
    private static readonly HashSet<string> KnownImports = new(StringComparer.Ordinal)
    {
        HttpPath, ChiPath, GinPath, EchoPath, SqlPath, SqlxPath, PgxPath, PgxPoolPath, SegmentioPath, ConfluentPath,
        RabbitPath, StreadwayPath, NatsPath, JsonPath, OsPath,
    };

    private enum Kind
    {
        /// <summary>A declaration the rules do not know. It only hides the outer meaning of its name.</summary>
        Other,
        Constant, Router, AmqpConnection, AmqpChannel, Deliveries, Delivery, NatsConnection, NatsMessage,
        ConfluentConsumer, KafkaReader, StructVariable,
    }

    private sealed class Router(string framework)
    {
        public string Framework { get; } = framework;
        public Router? Parent { get; set; }
        /// <summary>The part of the prefix this router adds to its parent, or null when it is not known.</summary>
        public string? Segment { get; set; } = "";
        /// <summary>A type that is the root of its framework (<c>*gin.Engine</c>, <c>*echo.Echo</c>, <c>*http.ServeMux</c>): its prefix is "".</summary>
        public bool FixedRoot { get; init; }
        /// <summary>Assigned from <c>chi.NewRouter()</c>: its prefix is "" unless it is returned or mounted.</summary>
        public bool Constructed { get; init; }
        /// <summary>A parameter or variable whose mount point this file does not fix.</summary>
        public bool Unknown { get; set; }
        public bool Returned { get; set; }
        public bool Reassigned { get; set; }
        public bool Stripped { get; set; }
        public int Mounts { get; set; }
        public int Depth { get; init; }
        public bool Resolved { get; set; }
        public string? Prefix { get; set; }
    }

    /// <summary>What a consume call subscribes to. The key is set when the call gives its subscription fact.</summary>
    private sealed class Consumed
    {
        public string? Key { get; set; }
        public string? Name { get; set; }
        public bool Set { get; set; }
    }

    private sealed class Binding(string name)
    {
        public string Name { get; } = name;
        public Kind Kind { get; set; }
        /// <summary>The '{' of the body of the function that declares it, or -1 at package level.</summary>
        public int Function { get; set; } = -1;
        /// <summary>A constant: its value. A struct variable: its type name. A delivery: the field that holds the payload.</summary>
        public string? Text { get; set; }
        public Router? Router { get; set; }
        public Consumed? Source { get; set; }
    }

    private readonly record struct Scope(int Open, int Watermark, int Function, bool Clause);

    private sealed record RouteSite(
        Router Router, ExtractionRule Rule, string Verb, string Template, int Line, int EndLine, bool Colon,
        string? Declared = null, string? Host = null, bool Subtree = false, bool Exact = false);

    private readonly WorkMeter _meter = context.Meter(file);
    private readonly Dictionary<string, (string Path, string? Name)> _imports = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<Binding>> _names = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Binding> _fileVariables = new(StringComparer.Ordinal);
    private readonly List<Binding> _live = [];
    private readonly List<Scope> _scopes = [];
    private readonly List<Binding> _pending = [];
    private readonly List<(int At, Binding Binding)> _deferred = [];
    private readonly List<(int Start, int End)> _entries = [];
    private readonly (int Start, int End)[] _args = new (int, int)[MaxArguments];
    private readonly List<RouteSite> _routes = [];
    private readonly List<Router> _chain = [];
    private readonly Dictionary<int, Consumed> _readerConfigs = [];
    private readonly Dictionary<string, string> _messages = new(StringComparer.Ordinal);
    private readonly HashSet<(string Subscription, string Message)> _links = [];
    private readonly Router _defaultMux = new("net/http") { FixedRoot = true };
    private int _argCount;
    private int _pendingBody = -1;
    private bool _pendingIsFunction;
    private int _signatureEnd;
    private int _stripUntil = -1;
    private int _literalFunction = -1;
    private Router? _literalRouter;
    private Consumed? _literalSource;
    private int _consumeAt = -1;
    private Consumed? _consumeSource;
    private string? _http, _chi, _gin, _echo, _sql, _sqlx, _pgx, _pgxpool, _kafka, _segmentio, _confluent, _amqp, _nats, _json, _os;

    private (string, string?)[] Owner => [("service", module.ServiceKey), ("project", module.Path)];

    private int CurrentFunction => _scopes.Count > 0 ? _scopes[^1].Function : -1;

    public void Read()
    {
        ReadImports();
        if (_imports.Count == 0 || _meter.Exhausted)
            return;
        _http = Local(HttpPath);
        _chi = Local(ChiPath);
        _gin = Local(GinPath);
        _echo = Local(EchoPath);
        _sql = Local(SqlPath);
        _sqlx = Local(SqlxPath);
        _pgx = Local(PgxPath);
        _pgxpool = Local(PgxPoolPath);
        _segmentio = Local(SegmentioPath);
        _confluent = Local(ConfluentPath);
        _kafka = _segmentio ?? _confluent;
        _amqp = Local(RabbitPath) ?? Local(StreadwayPath);
        _nats = Local(NatsPath);
        _json = Local(JsonPath);
        _os = Local(OsPath);

        ReadFileVariables();
        if (!_meter.Charge(t.Count))
            return;
        for (var i = 0; i < t.Count; i++)
        {
            while (_deferred.Count > 0 && _deferred[^1].At <= i)
            {
                Declare(_deferred[^1].Binding);
                _deferred.RemoveAt(_deferred.Count - 1);
            }
            var token = t[i];
            if (token.Kind == TokKind.Punct)
            {
                switch (token.Text[0])
                {
                    case '{':
                        Enter(i);
                        break;
                    case '}':
                        Leave(i);
                        break;
                    case ':' when t.P(i + 1, '='):
                        ShortDeclaration(i);
                        break;
                    case '=':
                        Assignment(i);
                        break;
                }
                continue;
            }
            if (token.Kind != TokKind.Ident)
                continue;
            if (_meter.Exhausted)
                return;
            if (t.P(i - 1, '.'))
            {
                if (t.P(i + 1, '(') && t.Match[i + 1] > i && t.Id(i - 2) && !t.P(i - 3, '.'))
                    Call(i);
                continue;
            }

            var inFunction = CurrentFunction >= 0;
            switch (token.Text)
            {
                case "func":
                    Function(i);
                    continue;
                case "case" or "default":
                    Clause(i);
                    continue;
                case "return":
                    Return(i);
                    continue;
                case "var":
                    if (inFunction && !t.P(i + 1, '('))
                        VariableSpec(i + 1);
                    continue;
                case "const":
                    if (inFunction && !t.P(i + 1, '('))
                        ConstantSpec(i + 1);
                    continue;
                case "type":
                    if (inFunction && t.Id(i + 1))
                        Declare(new Binding(t[i + 1].Text));
                    continue;
            }

            if (i < _stripUntil && Lookup(token.Text) is { Kind: Kind.Router, Router: { Framework: "net/http" } stripped })
                stripped.Stripped = true;
            if (_kafka is not null && token.Text == "Topic" && t.P(i + 1, ':'))
                ReadKafkaTopic(i);

            // The first name of a line in a "var (", "const (" or "type (" group of a function.
            var parent = t.Parent[i];
            if (inFunction && parent > 0 && t.P(parent, '(') && (i == parent + 1 || t.Line(i) > t.EndLine(i - 1) || t.P(i - 1, ';')))
            {
                switch (t.IdText(parent - 1))
                {
                    case "var":
                        VariableSpec(i);
                        break;
                    case "const":
                        ConstantSpec(i);
                        break;
                    case "type":
                        Declare(new Binding(token.Text));
                        break;
                }
            }
        }
        AddRoutes();
    }

    // ---- imports ----------------------------------------------------------------------

    private void ReadImports()
    {
        if (!_meter.Charge(t.Count))
            return;
        for (var i = 0; i < t.Count; i++)
        {
            if (t.Parent[i] >= 0 || !t.Id(i, "import"))
                continue;
            if (t.P(i + 1, '(') && t.Match[i + 1] > i)
            {
                var open = i + 1;
                var close = t.Match[open];
                for (var k = open + 1; k < close; k++)
                {
                    if (!_meter.Charge())
                        return;
                    if (t.StaticString(k) is { } path)
                        Import(path, k - 1 > open && t.Line(k - 1) == t.Line(k) && (t.Id(k - 1) || t.P(k - 1, '.')) ? t[k - 1].Text : null);
                }
                i = close;
            }
            else if (t.StaticString(i + 1) is { } single)
            {
                Import(single, null);
            }
            else if (t.StaticString(i + 2) is { } aliased && (t.Id(i + 1) || t.P(i + 1, '.')))
            {
                Import(aliased, t[i + 1].Text);
            }
        }
    }

    /// <summary>
    /// Keeps an import of a known package under its path without version directories. When
    /// a file imports two versions of one package, the first path in ordinal order is the one used.
    /// </summary>
    private void Import(string path, string? alias)
    {
        if (!_meter.Charge(path.Length))
            return;
        var canonical = path;
        if (path.Contains("/v", StringComparison.Ordinal))
        {
            var parts = path.Split('/');
            canonical = string.Join("/", parts.Where((part, index) =>
                !(index > 0 && part.Length > 1 && part[0] == 'v' && part.AsSpan(1).IndexOfAnyExceptInRange('0', '9') < 0)));
        }
        if (!KnownImports.Contains(canonical))
            return;
        if (_imports.TryGetValue(canonical, out var existing) && string.CompareOrdinal(existing.Path, path) <= 0)
            return;
        var name = alias ?? DefaultName(canonical);
        _imports[canonical] = (path, name is "_" or "." ? null : name);
    }

    /// <summary>The package name an import path gives when it has no alias.</summary>
    private static string DefaultName(string path)
    {
        var name = path[(path.LastIndexOf('/') + 1)..];
        if (name == "amqp091-go")
            return "amqp091";
        if (name.EndsWith(".go", StringComparison.Ordinal))
            name = name[..^3];
        if (name.EndsWith("-go", StringComparison.Ordinal))
            name = name[..^3];
        return name.StartsWith("go-", StringComparison.Ordinal) ? name[3..] : name;
    }

    /// <summary>The local name of an imported package, or null when it is not imported by a usable name.</summary>
    private string? Local(string path) => _imports.TryGetValue(path, out var import) ? import.Name : null;

    // ---- scopes and names -------------------------------------------------------------

    private Binding? Lookup(string name) =>
        _names.TryGetValue(name, out var declared) && declared.Count > 0 ? declared[^1] : _fileVariables.GetValueOrDefault(name);

    /// <summary>True when <paramref name="name"/> is the local name of that package and nothing in scope hides it.</summary>
    private bool IsPackage(string name, string? package) => package is not null && name == package && Lookup(name) is null;

    private void Declare(Binding binding)
    {
        if (binding.Name == "_")
            return;
        binding.Function = CurrentFunction;
        _live.Add(binding);
        if (!_names.TryGetValue(binding.Name, out var declared))
            _names[binding.Name] = declared = [];
        declared.Add(binding);
    }

    private void Truncate(int watermark)
    {
        while (_live.Count > watermark)
        {
            var declared = _names[_live[^1].Name];
            declared.RemoveAt(declared.Count - 1);
            _live.RemoveAt(_live.Count - 1);
        }
    }

    private void Enter(int open)
    {
        var isBody = open == _pendingBody;
        _scopes.Add(new Scope(open, _live.Count, isBody && _pendingIsFunction ? open : CurrentFunction, false));
        if (!isBody)
            return;
        foreach (var binding in _pending)
            Declare(binding);
        _pending.Clear();
        _pendingBody = -1;
    }

    private void Leave(int close)
    {
        var open = t.Match[close];
        if (open < 0)
            return;
        while (_scopes.Count > 0)
        {
            var scope = _scopes[^1];
            _scopes.RemoveAt(_scopes.Count - 1);
            Truncate(scope.Watermark);
            if (!scope.Clause && scope.Open == open)
                break;
        }
    }

    /// <summary>A <c>case</c> or <c>default</c> clause is a block of its own: it ends the clause before it.</summary>
    private void Clause(int keyword)
    {
        if (_scopes.Count == 0 || _scopes[^1].Open != t.Parent[keyword])
            return;
        var top = _scopes[^1];
        if (top.Clause)
        {
            _scopes.RemoveAt(_scopes.Count - 1);
            Truncate(top.Watermark);
        }
        _scopes.Add(new Scope(top.Open, _live.Count, top.Function, true));
    }

    /// <summary>True when the token at <paramref name="last"/> ends its statement.</summary>
    private bool Ends(int last)
    {
        var next = last + 1;
        return next >= t.Count || t.Line(next) > t.EndLine(last) || t.P(next, ';') || t.P(next, '}');
    }

    /// <summary>The value of one token that is a string literal or a string constant in scope.</summary>
    private string? ValueAt(int index)
    {
        if (t.StaticString(index) is { } literal)
            return literal;
        if (t.IdText(index) is not { } name)
            return null;
        return Lookup(name) is { } binding ? (binding.Kind == Kind.Constant ? binding.Text : null) : package.Constant(name);
    }

    private string? ValueOf((int Start, int End) argument) => argument.Start == argument.End ? ValueAt(argument.Start) : null;

    /// <summary>
    /// Splits the arguments of a call into <see cref="_args"/>. False when the call has more
    /// than <see cref="MaxArguments"/> arguments or an empty one.
    /// </summary>
    private bool Arguments(int open)
    {
        _argCount = 0;
        var close = t.Match[open];
        if (close < 0)
            return false;
        var start = open + 1;
        for (var k = open + 1; k <= close; k++)
        {
            if (!_meter.Charge())
                return false;
            if (k < close && t.Match[k] > k)
            {
                k = t.Match[k];
                continue;
            }
            if (k != close && !t.P(k, ','))
                continue;
            if (k > start)
            {
                if (_argCount == MaxArguments)
                    return false;
                _args[_argCount++] = (start, k - 1);
            }
            else if (k != close)
            {
                return false;
            }
            start = k + 1;
        }
        return true;
    }

    private int EndLine(int method) => t.EndLine(t.Match[method + 1]);

    // ---- declarations -----------------------------------------------------------------

    /// <summary>
    /// A function declaration or literal: its receiver, type parameters, parameters and named
    /// results are declared in its body. A <c>func</c> that has no body on the line of its
    /// signature is a type, and declares nothing.
    /// </summary>
    private void Function(int func)
    {
        if (func < _signatureEnd || t.P(func - 1, ']'))
            return;
        var isLiteralArgument = func == _literalFunction;
        var p = func + 1;
        int receiver = -1, typeParameters = -1;
        if (t.Parent[func] < 0 && (func == 0 || t.Line(func) > t.EndLine(func - 1)))
        {
            // "func (r T) Name[P any](…)": only a declaration has a receiver, a name and type parameters.
            if (t.P(p, '(') && t.Match[p] > p && t.Id(t.Match[p] + 1))
            {
                receiver = p;
                p = t.Match[p] + 1;
            }
            if (t.Id(p))
                p++;
            if (t.P(p, '[') && t.Match[p] > p)
            {
                typeParameters = p;
                p = t.Match[p] + 1;
            }
        }
        if (!t.P(p, '(') || t.Match[p] < p)
            return;
        var after = t.Match[p] + 1;
        var body = Body(after);
        if (body < 0)
            return;

        _pending.Clear();
        if (receiver >= 0)
            Parameters(receiver, typed: true);
        if (typeParameters >= 0)
            Parameters(typeParameters, typed: false);
        var first = _pending.Count;
        Parameters(p, typed: true);
        var count = _pending.Count - first;
        if (after < body && t.P(after, '(') && t.Match[after] > after)
            Parameters(after, typed: false);

        if (isLiteralArgument && count == 1)
        {
            var parameter = _pending[first];
            if (_literalRouter is not null && parameter is { Kind: Kind.Router, Router.Framework: "chi" })
            {
                parameter.Router = _literalRouter;
            }
            else if (_literalSource is not null && parameter.Kind == Kind.NatsMessage)
            {
                parameter.Kind = Kind.Delivery;
                parameter.Source = _literalSource;
                parameter.Text = "Data";
            }
        }
        if (isLiteralArgument)
            _literalFunction = -1;
        // A *nats.Msg that is not the parameter of a recognised handler is not tied to a subject.
        foreach (var binding in _pending)
            if (binding.Kind == Kind.NatsMessage)
                binding.Kind = Kind.Other;
        _pendingBody = body;
        _pendingIsFunction = true;
    }

    /// <summary>
    /// The '{' of the body that follows a parameter list on the same line, after an optional
    /// result type; or -1. The tokens it reads are a signature: a <c>func</c> among them is a type.
    /// </summary>
    private int Body(int from)
    {
        var k = from;
        while (k < t.Count && _meter.Charge())
        {
            if (t.Line(k) != t.EndLine(k - 1))
                break;
            if (t.P(k, '{'))
            {
                if ((t.Id(k - 1, "struct") || t.Id(k - 1, "interface")) && t.Match[k] > k)
                {
                    k = t.Match[k] + 1;
                    continue;
                }
                _signatureEnd = Math.Max(_signatureEnd, k);
                return k;
            }
            if (t.P(k, '(') || t.P(k, '['))
            {
                if (t.Match[k] < k)
                    break;
                k = t.Match[k] + 1;
                continue;
            }
            if (!(t.Id(k) || t.P(k, '.') || t.P(k, '*') || t.P(k, '<') || t.P(k, '-')))
                break;
            k++;
        }
        _signatureEnd = Math.Max(_signatureEnd, k);
        return -1;
    }

    /// <summary>
    /// Adds the names of a parameter list to <see cref="_pending"/>. A list names its
    /// parameters when one entry is <c>name Type</c>; then the first identifier of every entry is a name.
    /// </summary>
    private void Parameters(int open, bool typed)
    {
        var close = t.Match[open];
        _entries.Clear();
        var start = open + 1;
        var named = false;
        for (var k = open + 1; k <= close; k++)
        {
            if (!_meter.Charge())
                return;
            if (k < close && t.Match[k] > k)
            {
                k = t.Match[k];
                continue;
            }
            if (k != close && !t.P(k, ','))
                continue;
            if (k > start)
            {
                _entries.Add((start, k - 1));
                named |= t.Id(start) && k - 1 > start && !t.P(start + 1, '.');
            }
            start = k + 1;
        }
        if (!named)
            return;
        var untyped = _pending.Count;
        foreach (var (first, last) in _entries)
        {
            if (!t.Id(first))
                continue;
            _pending.Add(new Binding(t[first].Text));
            if (last == first)
                continue;
            // "a, b *T": the type of this entry is the type of the names before it that have none.
            if (typed)
                for (var k = untyped; k < _pending.Count; k++)
                    Typed(_pending[k], first + 1, last);
            untyped = _pending.Count;
        }
    }

    /// <summary>Sets the kind of a binding whose declared type is exactly <c>[*]pkg.Type</c> with a type the rules know.</summary>
    private void Typed(Binding binding, int start, int end)
    {
        if (t.P(start, '*'))
            start++;
        if (end != start + 2 || !t.Id(start) || !t.P(start + 1, '.') || !t.Id(end))
            return;
        var from = t[start].Text;
        var type = t[end].Text;
        if (from == _chi && type is "Router" or "Mux")
            (binding.Kind, binding.Router) = (Kind.Router, new Router("chi") { Unknown = true });
        else if (from == _gin && type == "Engine")
            (binding.Kind, binding.Router) = (Kind.Router, new Router("gin") { FixedRoot = true });
        else if (from == _gin && type is "RouterGroup" or "IRouter" or "IRoutes")
            (binding.Kind, binding.Router) = (Kind.Router, new Router("gin") { Unknown = true });
        else if (from == _echo && type == "Echo")
            (binding.Kind, binding.Router) = (Kind.Router, new Router("echo") { FixedRoot = true });
        else if (from == _echo && type == "Group")
            (binding.Kind, binding.Router) = (Kind.Router, new Router("echo") { Unknown = true });
        else if (from == _http && type == "ServeMux")
            (binding.Kind, binding.Router) = (Kind.Router, new Router("net/http") { FixedRoot = true });
        else if (from == _amqp && type == "Connection")
            binding.Kind = Kind.AmqpConnection;
        else if (from == _amqp && type == "Channel")
            binding.Kind = Kind.AmqpChannel;
        else if (from == _nats && type == "Conn")
            binding.Kind = Kind.NatsConnection;
        else if (from == _nats && type == "Msg")
            binding.Kind = Kind.NatsMessage;
        else if (from == _confluent && type == "Consumer")
            binding.Kind = Kind.ConfluentConsumer;
    }

    /// <summary>
    /// Sets the kind of a binding from the expression that gives its value, when the expression
    /// is one the rules know and ends the statement. Returns the index after the expression
    /// (the binding is in scope from there), or -1 when the expression is not recognised.
    /// </summary>
    private int Initialise(Binding binding, int value, int names)
    {
        // "T{}"
        if (names == 1 && t.Id(value) && t.P(value + 1, '{') && t.Match[value + 1] == value + 2 && Ends(value + 2))
        {
            if (Lookup(t[value].Text) is null)
                (binding.Kind, binding.Text) = (Kind.StructVariable, t[value].Text);
            return value + 3;
        }
        // "<-deliveries"
        if (t.P(value, '<') && t.P(value + 1, '-') && t.Id(value + 2) && Ends(value + 2))
        {
            if (Lookup(t[value + 2].Text) is { Kind: Kind.Deliveries } deliveries)
                (binding.Kind, binding.Source, binding.Text) = (Kind.Delivery, deliveries.Source, "Body");
            return value + 3;
        }
        // "x.F(…)"
        if (!t.Id(value) || !t.P(value + 1, '.') || !t.Id(value + 2) || !t.P(value + 3, '('))
            return -1;
        var open = value + 3;
        var close = t.Match[open];
        if (close < open || !Ends(close))
            return -1;
        var from = t[value].Text;
        var method = t[value + 2].Text;
        var receiver = Lookup(from);
        if (receiver is null)
        {
            if (from == _chi && method is "NewRouter" or "NewMux")
                (binding.Kind, binding.Router) = (Kind.Router, new Router("chi") { Constructed = true });
            else if (from == _gin && method is "Default" or "New")
                (binding.Kind, binding.Router) = (Kind.Router, new Router("gin") { FixedRoot = true });
            else if (from == _echo && method == "New")
                (binding.Kind, binding.Router) = (Kind.Router, new Router("echo") { FixedRoot = true });
            else if (from == _http && method == "NewServeMux")
                (binding.Kind, binding.Router) = (Kind.Router, new Router("net/http") { FixedRoot = true });
            else if (from == _amqp && method is "Dial" or "DialTLS" or "DialConfig" or "DialTLS_ExternalAuth" or "Open")
                binding.Kind = Kind.AmqpConnection;
            else if (from == _nats && method == "Connect")
                binding.Kind = Kind.NatsConnection;
            else if (from == _confluent && method == "NewConsumer")
                binding.Kind = Kind.ConfluentConsumer;
            else if (from == _segmentio && method == "NewReader" && names == 1)
            {
                binding.Kind = Kind.KafkaReader;
                binding.Source = new Consumed();
                // The topic is known only when the one argument is the configuration literal itself.
                if (t.Id(open + 1, _segmentio!) && t.P(open + 2, '.') && t.Id(open + 3, "ReaderConfig") && t.P(open + 4, '{') && t.Match[open + 4] == close - 1)
                    _readerConfigs[open + 4] = binding.Source;
            }
            return close + 1;
        }
        switch (receiver.Kind)
        {
            case Kind.Router:
            {
                var parent = receiver.Router!;
                var derives = parent.Framework switch
                {
                    "gin" or "echo" => method == "Group",
                    "chi" => method is "With" or "Group" or "Route",
                    _ => false,
                };
                if (!derives)
                    break;
                var segment = parent.Framework == "chi" && method != "Route"
                    ? ""
                    : Arguments(open) && _argCount >= 1 ? ValueOf(_args[0]) : null;
                (binding.Kind, binding.Router) = (Kind.Router, Derive(parent, segment, t.Line(value)));
                break;
            }
            case Kind.AmqpConnection when method == "Channel" && close == open + 1:
                binding.Kind = Kind.AmqpChannel;
                break;
            case Kind.AmqpChannel when method is "Consume" or "ConsumeWithContext":
                binding.Kind = Kind.Deliveries;
                binding.Source = _consumeSource = new Consumed();
                _consumeAt = value + 2;
                break;
            case Kind.KafkaReader when method is "ReadMessage" or "FetchMessage":
                (binding.Kind, binding.Source, binding.Text) = (Kind.Delivery, receiver.Source, "Value");
                break;
        }
        return close + 1;
    }

    private Router Derive(Router parent, string? segment, int line)
    {
        var child = new Router(parent.Framework) { Parent = parent, Segment = segment, Depth = parent.Depth + 1 };
        if (child.Depth > MaxRouterDepth)
        {
            child.Unknown = true;
            if (child.Depth == MaxRouterDepth + 1)
                context.Note(NoteKind.Undecided, "router-nesting-too-deep", file.Path, line);
        }
        return child;
    }

    /// <summary>"a, b := value". The first name can be a recognised value; every name hides its outer meaning.</summary>
    private void ShortDeclaration(int colon)
    {
        if (!t.Id(colon - 1))
            return;
        var first = colon - 1;
        var count = 1;
        while (t.P(first - 1, ',') && t.Id(first - 2) && _meter.Charge())
        {
            first -= 2;
            count++;
        }
        if (t.P(first - 1, '.'))
            return;
        var value = colon + 2;
        var lead = new Binding(t[first].Text);
        var from = -1;
        switch (t.IdText(first - 1))
        {
            case "case":
                // "case d := <-deliveries:" and "case d, ok := <-deliveries:".
                if (count <= 2 && t.P(value, '<') && t.P(value + 1, '-') && t.Id(value + 2) && t.P(value + 3, ':')
                    && Lookup(t[value + 2].Text) is { Kind: Kind.Deliveries } received)
                    (lead.Kind, lead.Source, lead.Text) = (Kind.Delivery, received.Source, "Body");
                break;
            case "for":
                // "for d := range deliveries {": the variable is declared in the body.
                if (count == 1 && t.Id(value, "range") && t.Id(value + 1) && t.P(value + 2, '{'))
                {
                    if (Lookup(t[value + 1].Text) is { Kind: Kind.Deliveries } ranged)
                        (lead.Kind, lead.Source, lead.Text) = (Kind.Delivery, ranged.Source, "Body");
                    _pending.Clear();
                    _pending.Add(lead);
                    _pendingBody = value + 2;
                    _pendingIsFunction = false;
                    return;
                }
                break;
            case "if" or "switch":
                break;
            default:
                if (count <= 2)
                    from = Initialise(lead, value, count);
                break;
        }

        // The value of "r := r.Group(…)" is read with the outer r: the new one is in scope after it.
        if (from >= 0)
            _deferred.Add((from, lead));
        else
            Declare(lead);
        for (var k = first + 2; k < colon; k += 2)
            Declare(new Binding(t[k].Text));
    }

    /// <summary>"var a, b T", "var a = value" or "var a T = value" in a function.</summary>
    private void VariableSpec(int name)
    {
        if (!t.Id(name))
            return;
        var after = name + 1;
        var count = 1;
        while (t.P(after, ',') && t.Id(after + 1) && _meter.Charge())
        {
            after += 2;
            count++;
        }
        var lead = new Binding(t[name].Text);
        if (t.P(after, '='))
        {
            var from = count <= 2 ? Initialise(lead, after + 1, count) : -1;
            if (from >= 0)
                _deferred.Add((from, lead));
            else
                Declare(lead);
            for (var k = name + 2; k < after; k += 2)
                Declare(new Binding(t[k].Text));
            return;
        }

        var qualified = t.P(after, '*') ? after + 1 : after;
        for (var k = name; k < after; k += 2)
        {
            var binding = k == name ? lead : new Binding(t[k].Text);
            if (t.Id(after) && !t.P(after + 1, '.') && (Ends(after) || t.P(after + 1, '=')))
            {
                // "var v T": T is a type of the package unless something in scope hides the name.
                if (Lookup(t[after].Text) is null)
                    (binding.Kind, binding.Text) = (Kind.StructVariable, t[after].Text);
            }
            else if (t.Id(qualified) && t.P(qualified + 1, '.') && t.Id(qualified + 2) && (Ends(qualified + 2) || t.P(qualified + 3, '='))
                && Lookup(t[qualified].Text) is null)
            {
                Typed(binding, after, qualified + 2);
                if (binding.Kind == Kind.NatsMessage)
                    binding.Kind = Kind.Other;
            }
            Declare(binding);
        }
    }

    /// <summary>"const name = "literal"" in a function is a constant of that function; any other constant only hides its name.</summary>
    private void ConstantSpec(int name)
    {
        if (!t.Id(name))
            return;
        if (GoPackage.StringConstant(t, name) is { } value)
        {
            Declare(new Binding(t[name].Text) { Kind = Kind.Constant, Text = value });
            return;
        }
        for (var k = name; t.Id(k) && _meter.Charge(); k += 2)
        {
            Declare(new Binding(t[k].Text));
            if (!t.P(k + 1, ','))
                break;
        }
    }

    /// <summary>
    /// The package-level variables of this file that have a recognised type: <c>var x [*]pkg.Type</c>,
    /// or <c>var x = pkg.New…()</c> for a root router. A chi router at package level can be
    /// mounted from any file, so its prefix is not known.
    /// </summary>
    private void ReadFileVariables()
    {
        if (!_meter.Charge(t.Count))
            return;
        for (var i = 0; i < t.Count; i++)
        {
            if (t.Parent[i] >= 0 || !t.Id(i, "var"))
                continue;
            if (t.P(i + 1, '(') && t.Match[i + 1] > i)
            {
                var open = i + 1;
                var close = t.Match[open];
                for (var k = open + 1; k < close; k++)
                {
                    if (!_meter.Charge())
                        return;
                    if (t.Parent[k] == open && (k == open + 1 || t.Line(k) > t.EndLine(k - 1) || t.P(k - 1, ';')))
                        FileVariable(k);
                }
                i = close;
            }
            else
            {
                FileVariable(i + 1);
            }
        }
    }

    private void FileVariable(int name)
    {
        if (!t.Id(name))
            return;
        var after = name + 1;
        while (t.P(after, ',') && t.Id(after + 1) && _meter.Charge())
            after += 2;
        if (t.P(after, '='))
        {
            var value = after + 1;
            if (after != name + 1 || !t.Id(value) || !t.P(value + 1, '.') || !t.Id(value + 2) || !t.P(value + 3, '(')
                || t.Match[value + 3] < value || !Ends(t.Match[value + 3]))
                return;
            var from = t[value].Text;
            var method = t[value + 2].Text;
            var router =
                from == _chi && method is "NewRouter" or "NewMux" ? new Router("chi") { Unknown = true }
                : from == _gin && method is "Default" or "New" ? new Router("gin") { FixedRoot = true }
                : from == _echo && method == "New" ? new Router("echo") { FixedRoot = true }
                : from == _http && method == "NewServeMux" ? new Router("net/http") { FixedRoot = true }
                : null;
            if (router is not null)
                _fileVariables[t[name].Text] = new Binding(t[name].Text) { Kind = Kind.Router, Router = router };
            return;
        }
        var qualified = t.P(after, '*') ? after + 1 : after;
        if (!t.Id(qualified) || !t.P(qualified + 1, '.') || !t.Id(qualified + 2) || !(Ends(qualified + 2) || t.P(qualified + 3, '=')))
            return;
        for (var k = name; k < after; k += 2)
        {
            var binding = new Binding(t[k].Text);
            Typed(binding, after, qualified + 2);
            if (binding.Kind is not (Kind.Other or Kind.NatsMessage) && binding.Name != "_")
                _fileVariables[binding.Name] = binding;
        }
    }

    /// <summary>"a, r = …": a router variable that is assigned again has no known prefix, unless its type is a root.</summary>
    private void Assignment(int equals)
    {
        if (CurrentFunction < 0 || t.P(equals + 1, '=') || !t.Id(equals - 1))
            return;
        var parent = t.Parent[equals];
        if (parent > 0 && t.P(parent, '(') && (t.Id(parent - 1, "var") || t.Id(parent - 1, "const")))
            return;
        var first = equals - 1;
        while (t.P(first - 1, ',') && t.Id(first - 2) && _meter.Charge())
            first -= 2;
        if (t.Id(first - 1, "var") || t.Id(first - 1, "const"))
            return;
        for (var k = first; k < equals; k += 2)
            if (!t.P(k - 1, '.') && Lookup(t[k].Text) is { Kind: Kind.Router, Router: { } router })
                router.Reassigned = true;
    }

    /// <summary>"return r, nil": a router that leaves the function is mounted by a caller.</summary>
    private void Return(int keyword)
    {
        for (var k = keyword + 1; k < t.Count && k <= keyword + MaxReturnTokens && t.Line(k) == t.Line(keyword) && _meter.Charge(); k++)
        {
            if (t.Parent[k] != t.Parent[keyword])
                break;
            if (t.Id(k) && !t.P(k - 1, '.') && !t.P(k + 1, '.') && !t.P(k + 1, '(')
                && Lookup(t[k].Text) is { Kind: Kind.Router, Router: { } router })
                router.Returned = true;
        }
    }

    // ---- calls ------------------------------------------------------------------------

    /// <summary>"receiver.Method(…)" with a plain identifier as the receiver.</summary>
    private void Call(int method)
    {
        var name = t[method].Text;
        var receiver = t[method - 2].Text;
        var open = method + 1;
        var binding = Lookup(receiver);
        if (binding is null)
        {
            if (receiver == _http)
            {
                // The package functions of net/http register on the default mux.
                if (name is "HandleFunc" or "Handle" && Arguments(open) && _argCount == 2)
                    AddNetHttpRoute(method, _args[0], _defaultMux);
                else if (name == "StripPrefix")
                    _stripUntil = Math.Max(_stripUntil, t.Match[open]);
            }
            else if (receiver == _json && name == "Unmarshal")
                Decode(method);
            else
                ReadSql(method, receiver, name);
            return;
        }
        switch (binding.Kind)
        {
            case Kind.Router:
                ReadRoute(method, name, binding.Router!);
                break;
            case Kind.AmqpChannel:
                ReadAmqp(method, name);
                break;
            case Kind.NatsConnection:
                ReadNats(method, name);
                break;
            case Kind.ConfluentConsumer:
                ReadConfluent(method, name);
                break;
        }
    }

    // ---- HTTP routers -----------------------------------------------------------------

    private void ReadRoute(int method, string name, Router router)
    {
        var open = method + 1;
        switch (router.Framework)
        {
            case "net/http":
                if (name is "HandleFunc" or "Handle" && Arguments(open) && _argCount == 2)
                    AddNetHttpRoute(method, _args[0], router);
                break;
            case "chi":
                if (ChiVerbs.TryGetValue(name, out var chiVerb))
                {
                    if (Arguments(open) && _argCount == 2)
                        AddRoute(GoExtractor.Chi, router, method, chiVerb, _args[0], colon: false);
                }
                else if (name is "Handle" or "HandleFunc")
                {
                    if (Arguments(open) && _argCount == 2)
                        AddRoute(GoExtractor.Chi, router, method, "ANY", _args[0], colon: false);
                }
                else if (name is "Method" or "MethodFunc")
                {
                    if (Arguments(open) && _argCount == 3)
                        AddRoute(GoExtractor.Chi, router, method, ValueOf(_args[0])?.ToUpperInvariant(), _args[1], colon: false);
                }
                else if (name == "Route")
                {
                    if (Arguments(open) && _argCount == 2)
                        Literal(_args[1].Start, Derive(router, ValueOf(_args[0]), t.Line(method)), null);
                }
                else if (name == "Group")
                {
                    if (Arguments(open) && _argCount == 1)
                        Literal(_args[0].Start, Derive(router, "", t.Line(method)), null);
                }
                else if (name == "Mount" && Arguments(open) && _argCount == 2 && _args[1].Start == _args[1].End
                    && t.IdText(_args[1].Start) is { } mountedName
                    && Lookup(mountedName) is { Kind: Kind.Router, Router: { Constructed: true } mounted })
                {
                    mounted.Mounts++;
                    mounted.Parent = router;
                    mounted.Segment = ValueOf(_args[0]);
                }
                break;
            case "gin" or "echo":
            {
                var rule = router.Framework == "gin" ? GoExtractor.Gin : GoExtractor.Echo;
                if (UpperVerbs.Contains(name))
                {
                    if (Arguments(open) && _argCount >= 2)
                        AddRoute(rule, router, method, name, _args[0], colon: true);
                }
                else if (name == "Any")
                {
                    if (Arguments(open) && _argCount >= 2)
                        AddRoute(rule, router, method, "ANY", _args[0], colon: true);
                }
                else if (name is "Handle" or "Add" && Arguments(open) && _argCount >= 3)
                {
                    AddRoute(rule, router, method, ValueOf(_args[0])?.ToUpperInvariant(), _args[1], colon: true);
                }
                break;
            }
        }
    }

    /// <summary>A function literal that is an argument of a recognised call: its one parameter gets a meaning from the call.</summary>
    private void Literal(int func, Router? router, Consumed? source)
    {
        if (!t.Id(func, "func"))
            return;
        _literalFunction = func;
        _literalRouter = router;
        _literalSource = source;
    }

    private void AddNetHttpRoute(int method, (int Start, int End) patternArgument, Router router)
    {
        var line = t.Line(method);
        if (ValueOf(patternArgument) is not { } pattern)
        {
            context.Note(NoteKind.Undecided, "dynamic-route", file.Path, line);
            return;
        }
        if (pattern.Length > MaxRouteLength)
        {
            context.Note(NoteKind.Undecided, "route-too-long", file.Path, line);
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
        var path = rest[slash..];
        // "/{$}" matches only the path before it; a trailing slash matches every path below it.
        var exact = path.EndsWith("/{$}", StringComparison.Ordinal);
        _routes.Add(new RouteSite(router, GoExtractor.NetHttp, verb, path, line, EndLine(method), Colon: false,
            Declared: pattern, Host: slash > 0 ? rest[..slash] : null, Subtree: !exact && path.EndsWith('/'), Exact: exact));
    }

    private void AddRoute(ExtractionRule rule, Router router, int method, string? verb, (int Start, int End) pathArgument, bool colon)
    {
        var line = t.Line(method);
        if (ValueOf(pathArgument) is not { } template || verb is null || !(UpperVerbs.Contains(verb) || verb == "ANY"))
        {
            context.Note(NoteKind.Undecided, "dynamic-route", file.Path, line);
            return;
        }
        if (template.Length > MaxRouteLength)
        {
            context.Note(NoteKind.Undecided, "route-too-long", file.Path, line);
            return;
        }
        _routes.Add(new RouteSite(router, rule, verb, template, line, EndLine(method), colon));
    }

    /// <summary>
    /// The prefix of a router, or null when the file does not fix it. The chain of parents is
    /// followed with a loop, for at most <see cref="MaxRouterDepth"/> routers that are not yet
    /// resolved; a longer chain (or a cycle of mounts) has no known prefix.
    /// </summary>
    private string? Prefix(Router router)
    {
        _chain.Clear();
        var current = router;
        while (!current.Resolved)
        {
            if (!_meter.Charge())
                return null;
            _chain.Add(current);
            if (_chain.Count > MaxRouterDepth)
            {
                foreach (var link in _chain)
                    (link.Resolved, link.Prefix) = (true, null);
                return null;
            }
            if (current.Parent is null)
                break;
            current = current.Parent;
        }
        for (var k = _chain.Count - 1; k >= 0; k--)
        {
            var link = _chain[k];
            link.Prefix = OwnPrefix(link);
            link.Resolved = true;
        }
        return router.Prefix;
    }

    private static string? OwnPrefix(Router router)
    {
        if (router.Unknown || router.Stripped)
            return null;
        if (router.FixedRoot)
            return "";
        if (router.Reassigned || router.Mounts > 1 || (router.Constructed && router.Returned))
            return null;
        if (router.Parent is null)
            return "";
        if (router.Parent.Prefix is not { } parent || router.Segment is not { } segment)
            return null;
        return parent.Length + segment.Length > MaxRouteLength ? null : RoutePath.Join(parent, segment);
    }

    private void AddRoutes()
    {
        foreach (var site in _routes)
        {
            if (context.Stopped || !_meter.Charge())
                return;
            if (Prefix(site.Router) is not { } prefix)
            {
                context.Note(NoteKind.Undecided, "unresolved-route-prefix", file.Path, site.Line);
                continue;
            }
            var framework = site.Router.Framework;
            var raw = framework == "net/http" ? site.Template : RoutePath.Join(prefix, site.Template);
            if (raw.Length > MaxRouteLength)
            {
                context.Note(NoteKind.Undecided, "route-too-long", file.Path, site.Line);
                continue;
            }
            var path = RoutePath.Normalize(site.Colon ? RoutePath.FromColonParameters(raw) : raw);
            context.Add(FactKind.Operation, $"operation:{module.ProjectId}:{site.Verb} {path}", $"{site.Verb} {path}",
                [site.Rule.At(file.Path, site.Line, site.EndLine)],
                [
                    .. Owner, ("transport", "http"), ("method", site.Verb), .. RoutePath.Attributes(path, site.Declared ?? raw),
                    ("host", site.Host), ("subtree", site.Subtree ? "true" : null), ("exact", site.Exact ? "true" : null),
                    ("framework", framework),
                ]);
        }
    }

    // ---- AMQP -------------------------------------------------------------------------

    private void ReadAmqp(int method, string name)
    {
        var expected = name switch
        {
            "ExchangeDeclare" or "ExchangeDeclarePassive" or "Consume" => 7,
            "QueueDeclare" or "QueueDeclarePassive" or "PublishWithContext" => 6,
            "QueueBind" or "Publish" => 5,
            "ConsumeWithContext" => 8,
            _ => 0,
        };
        if (expected == 0 || !Arguments(method + 1) || _argCount != expected)
            return;
        var line = t.Line(method);
        var end = EndLine(method);
        switch (name)
        {
            case "ExchangeDeclare" or "ExchangeDeclarePassive":
                if (Named(_args[0], line) is { } exchange)
                    context.Add(FactKind.Channel, "channel:amqp:" + exchange, exchange, GoExtractor.ExchangeDeclare.At(file.Path, line, end),
                        ("kind", "exchange"), ("transport", "amqp"), ("amqp.exchange.type", ValueOf(_args[1])),
                        ("durable", Flag(_args[2])));
                break;
            case "QueueDeclare" or "QueueDeclarePassive":
                if (Named(_args[0], line) is { } queue)
                {
                    var attributes = new List<(string, string?)>
                    {
                        ("kind", "queue"), ("transport", "amqp"), ("durable", Flag(_args[1])),
                    };
                    for (var i = _args[5].Start; i + 2 <= _args[5].End && _meter.Charge(); i++)
                    {
                        var option = t.StaticString(i);
                        if (!t.P(i + 1, ':') || option is not ("x-dead-letter-exchange" or "x-dead-letter-routing-key"))
                            continue;
                        var value = t.P(i + 3, ',') || t.P(i + 3, '}') ? ValueAt(i + 2) : null;
                        if (value is null)
                            context.Note(NoteKind.Undecided, "dynamic-channel-name", file.Path, t.Line(i));
                        else if (option == "x-dead-letter-exchange")
                            attributes.Add(("deadLetterExchange", "channel:amqp:" + value));
                        else
                            attributes.Add(("deadLetterRoutingKey", value));
                    }
                    context.Add(FactKind.Channel, "channel:amqp:" + queue, queue, [GoExtractor.QueueDeclare.At(file.Path, line, end)], attributes);
                }
                break;
            case "QueueBind":
                if (ValueOf(_args[1]) is not { } key)
                    context.Note(NoteKind.Undecided, "dynamic-channel-name", file.Path, line);
                else if (Named(_args[0], line) is { } bound && Named(_args[2], line) is { } source)
                    context.Add(FactKind.Subscription, $"binding:{module.ProjectId}:amqp:{bound}:{source}{(key.Length == 0 ? "" : ":" + key)}",
                        $"{bound} bound to {source}", GoExtractor.QueueBind.At(file.Path, line, end),
                        [.. Owner, ("transport", "amqp"), ("queue", "channel:amqp:" + bound), ("exchange", "channel:amqp:" + source), ("routingKey", key)]);
                break;
            case "PublishWithContext":
                AddAmqpPublish(_args[1], _args[2], line, end);
                break;
            case "Publish":
                AddAmqpPublish(_args[0], _args[1], line, end);
                break;
            default:
            {
                var consumed = method == _consumeAt ? _consumeSource : null;
                _consumeAt = -1;
                if (Named(_args[name == "Consume" ? 0 : 1], line) is not { } consumedQueue)
                    break;
                var subscription = $"subscription:{module.ProjectId}:amqp:{consumedQueue}";
                context.Add(FactKind.Subscription, subscription, $"consume {consumedQueue}",
                    [GoExtractor.AmqpConsume.At(file.Path, line, end)],
                    [.. Owner, ("transport", "amqp"), ("queue", "channel:amqp:" + consumedQueue)]);
                if (consumed is not null)
                    (consumed.Key, consumed.Name, consumed.Set) = (subscription, $"consume {consumedQueue}", true);
                break;
            }
        }
    }

    private void AddAmqpPublish((int, int) exchangeArgument, (int, int) keyArgument, int line, int end)
    {
        if (ValueOf(exchangeArgument) is not { } exchange || ValueOf(keyArgument) is not { } key)
        {
            context.Note(NoteKind.Undecided, "dynamic-channel-name", file.Path, line);
            return;
        }
        // The default exchange ("") routes to the queue named by the routing key.
        var channel = exchange.Length == 0 ? key : exchange;
        if (channel.Length == 0)
            return;
        context.Add(FactKind.Publication, $"publication:{module.ProjectId}:amqp:{exchange}:{key}", $"publish to {channel}",
            [GoExtractor.AmqpPublish.At(file.Path, line, end)],
            [.. Owner, ("transport", "amqp"), ("channel", "channel:amqp:" + channel), ("exchange", exchange.Length == 0 ? "(default)" : exchange), ("routingKey", key)]);
    }

    /// <summary>A name that is known and not empty, or null with a note.</summary>
    private string? Named((int Start, int End) argument, int line)
    {
        if (ValueOf(argument) is { Length: > 0 } value)
            return value;
        context.Note(NoteKind.Undecided, "dynamic-channel-name", file.Path, line);
        return null;
    }

    private string? Flag((int Start, int End) argument) =>
        argument.Start == argument.End && t.IdText(argument.Start) is "true" or "false" ? t[argument.Start].Text : null;

    // ---- Kafka ------------------------------------------------------------------------

    private void ReadKafkaTopic(int topic)
    {
        var open = t.Parent[topic];
        if (open < 3 || !t.P(open, '{') || !t.Id(open - 1) || !t.P(open - 2, '.') || t.P(open - 4, '.') || !t.Id(open - 3) || !IsPackage(t[open - 3].Text, _kafka))
            return;
        var type = t[open - 1].Text;
        var subscribes = type == "ReaderConfig";
        if (!subscribes && type is not ("Writer" or "WriterConfig" or "Message" or "TopicPartition"))
            return;
        var line = t.Line(topic);
        var single = t.P(topic + 3, ',') || t.P(topic + 3, '}');
        var name = single ? ValueAt(topic + 2) : null;
        _readerConfigs.TryGetValue(open, out var reader);
        if (string.IsNullOrEmpty(name))
        {
            if (reader is not null)
                (reader.Key, reader.Set) = (null, true);
            context.Note(NoteKind.Undecided, "dynamic-channel-name", file.Path, line);
            return;
        }
        var (key, display) = AddTopic(name, subscribes, "kafka", "topic", GoExtractor.KafkaTopic, line, line);
        if (reader is not null)
            // A literal with two topics does not say which one the reader reads.
            (reader.Key, reader.Name, reader.Set) = reader.Set ? (null, null, true) : (key, display, true);
    }

    private void ReadConfluent(int method, string name)
    {
        if (name != "SubscribeTopics" || !Arguments(method + 1) || _argCount != 2)
            return;
        var line = t.Line(method);
        var close = _args[0].End;
        var open = t.P(close, '}') ? t.Match[close] : -1;
        if (open < _args[0].Start)
        {
            context.Note(NoteKind.Undecided, "dynamic-channel-name", file.Path, line);
            return;
        }
        var end = EndLine(method);
        var start = open + 1;
        for (var k = open + 1; k <= close && _meter.Charge(); k++)
        {
            if (k < close && t.Match[k] > k)
            {
                k = t.Match[k];
                continue;
            }
            if (k != close && !t.P(k, ','))
                continue;
            if (k > start)
            {
                if (ValueOf((start, k - 1)) is { Length: > 0 } topic)
                    AddTopic(topic, true, "kafka", "topic", GoExtractor.KafkaTopic, line, end);
                else
                    context.Note(NoteKind.Undecided, "dynamic-channel-name", file.Path, line);
            }
            start = k + 1;
        }
    }

    /// <summary>The channel and its subscription or publication. Returns the key and the name of the latter.</summary>
    private (string Key, string Name) AddTopic(string name, bool subscribes, string broker, string kind, ExtractionRule rule, int line, int end)
    {
        var evidence = rule.At(file.Path, line, end);
        var channel = $"channel:{broker}:{name}";
        context.Add(FactKind.Channel, channel, name, evidence, ("kind", kind), ("transport", broker));
        var key = $"{(subscribes ? "subscription" : "publication")}:{module.ProjectId}:{broker}:{name}";
        var display = subscribes ? $"consume {name}" : $"publish to {name}";
        context.Add(subscribes ? FactKind.Subscription : FactKind.Publication, key, display,
            [evidence], [.. Owner, ("transport", broker), ("channel", channel)]);
        return (key, display);
    }

    // ---- NATS -------------------------------------------------------------------------

    private void ReadNats(int method, string name)
    {
        var expected = name switch
        {
            "Subscribe" or "ChanSubscribe" or "Publish" => 2,
            "QueueSubscribe" or "Request" => 3,
            "SubscribeSync" => 1,
            _ => 0,
        };
        if (expected == 0 || !Arguments(method + 1) || _argCount != expected)
            return;
        var subscribes = name is not ("Publish" or "Request");
        var line = t.Line(method);
        if (ValueOf(_args[0]) is not { Length: > 0 } subject)
        {
            context.Note(NoteKind.Undecided, "dynamic-channel-name", file.Path, line);
            return;
        }
        var (key, display) = AddTopic(subject, subscribes, "nats", "subject", GoExtractor.NatsSubject, line, EndLine(method));
        // "nc.Subscribe(subject, func(m *nats.Msg) { … })": the messages of that subject reach the literal.
        if (name is "Subscribe" or "QueueSubscribe")
            Literal(_args[expected - 1].Start, null, new Consumed { Key = key, Name = display, Set = true });
    }

    // ---- database access --------------------------------------------------------------

    private void ReadSql(int method, string receiver, string name)
    {
        var line = t.Line(method);
        if (receiver == _sql && name == "Open" || receiver == _sqlx && name is "Open" or "Connect" or "MustConnect" or "MustOpen")
        {
            if (!Arguments(method + 1) || _argCount != 2)
                return;
            if (ValueOf(_args[0]) is not { Length: > 0 } driver)
            {
                context.Note(NoteKind.Undecided, "dynamic-driver-name", file.Path, line);
                return;
            }
            context.Add(FactKind.DataAccess, $"dataaccess:{module.ProjectId}:sql:{driver}", $"database/sql ({driver})",
                [GoExtractor.SqlOpen.At(file.Path, line, EndLine(method))],
                [.. Owner, ("technology", "database/sql"), ("driver", driver), ("storeKind", SqlDrivers.GetValueOrDefault(driver, "unknown")),
                    ("connectionVariable", EnvironmentRead(_args[1]))]);
        }
        else if (receiver == _pgx && name is "Connect" or "ConnectConfig" || receiver == _pgxpool && name is "New" or "Connect" or "NewWithConfig")
        {
            var variable = Arguments(method + 1) && _argCount == 2 ? EnvironmentRead(_args[1]) : null;
            context.Add(FactKind.DataAccess, $"dataaccess:{module.ProjectId}:pgx", "pgx",
                [GoExtractor.SqlOpen.At(file.Path, line, EndLine(method))],
                [.. Owner, ("technology", "pgx"), ("storeKind", "postgres"), ("connectionVariable", variable)]);
        }
    }

    /// <summary>
    /// The name of the variable an argument reads when it is exactly <c>os.Getenv("NAME")</c>
    /// on the imported package, else null. The value is not read.
    /// </summary>
    private string? EnvironmentRead((int Start, int End) argument)
    {
        var (start, end) = argument;
        if (end != start + 5 || !t.Id(start) || !IsPackage(t[start].Text, _os) || !t.P(start + 1, '.')
            || !t.Id(start + 2, "Getenv") || !t.P(start + 3, '(') || !t.P(start + 5, ')'))
            return null;
        return t.StaticString(start + 4) is { } name && CommandLine.IsVariableName(name) ? name : null;
    }

    // ---- message contracts ------------------------------------------------------------

    /// <summary>"json.Unmarshal(d.Body, &amp;v)" where d is a consumed delivery of this function.</summary>
    private void Decode(int method)
    {
        if (!Arguments(method + 1) || _argCount != 2)
            return;
        var (payload, payloadEnd) = _args[0];
        if (payloadEnd != payload + 2 || !t.Id(payload) || !t.P(payload + 1, '.') || !t.Id(payload + 2)
            || Lookup(t[payload].Text) is not { Kind: Kind.Delivery } delivery || delivery.Text != t[payload + 2].Text)
            return;
        var line = t.Line(method);
        var end = EndLine(method);
        var (target, targetEnd) = _args[1];
        var function = CurrentFunction;
        if (delivery.Function != function || targetEnd != target + 1 || !t.P(target, '&') || !t.Id(target + 1)
            || Lookup(t[target + 1].Text) is not { Kind: Kind.StructVariable, Text: { } typeName } variable || variable.Function != function
            || package.Struct(typeName) is not { Unsupported: false } root)
        {
            context.Note(NoteKind.Undecided, "undecided-message-type", file.Path, line);
            return;
        }

        // One decode of a type from one subscription is the evidence; a repeat of it adds nothing.
        var key = AddMessage(root);
        if (!_links.Add((delivery.Source?.Key ?? "", key)))
            return;
        var decoded = GoExtractor.StructMessage.At(file.Path, line, end);
        context.Add(FactKind.Message, key, root.Name, [decoded], []);
        if (delivery.Source is { Key: { } subscription, Name: { } name })
            context.Add(FactKind.Subscription, subscription, name, [decoded], [("message", key)]);
    }

    /// <summary>
    /// The message fact of a decoded struct, and one for each struct of the package that its
    /// field types name, breadth first, to <see cref="MaxStructDepth"/> levels.
    /// </summary>
    private string AddMessage(GoStruct root)
    {
        var importPath = module.ImportPath(file.Directory);
        if (_messages.TryGetValue(root.Name, out var known))
            return known;

        var queue = new List<(GoStruct Struct, int Depth)> { (root, 0) };
        var queued = new HashSet<string>(StringComparer.Ordinal) { root.Name };
        for (var next = 0; next < queue.Count; next++)
        {
            var (current, depth) = queue[next];
            if (!_meter.Charge(current.Fields.Count + 1))
                break;
            foreach (var field in current.Fields)
            {
                foreach (var reference in field.References)
                {
                    if (package.Struct(reference) is not { } nested || !queued.Add(reference))
                        continue;
                    if (nested.Unsupported)
                        context.Note(NoteKind.Undecided, "undecided-message-type", nested.Path, nested.Line);
                    else if (depth == MaxStructDepth || queue.Count == MaxStructsPerMessage)
                        context.Note(NoteKind.Undecided, "message-nesting-too-deep", current.Path, current.Line);
                    else
                        queue.Add((nested, depth + 1));
                }
            }
            var messageKey = $"message:{importPath}.{current.Name}";
            var declared = GoExtractor.StructMessage.At(current.Path, current.Line, current.EndLine);
            context.Add(FactKind.Message, messageKey, current.Name, [declared],
                [
                    ("language", "go"), ("goType", $"{importPath}.{current.Name}"), ("declaredIn", module.Path),
                    ("fields", context.Names(current.Fields.Select(f => $"{f.JsonName}:{f.Type}"), "fields", current.Path, current.Line, sorted: true, separator: ";")),
                ]);
        }
        return _messages[root.Name] = $"message:{importPath}.{root.Name}";
    }
}
