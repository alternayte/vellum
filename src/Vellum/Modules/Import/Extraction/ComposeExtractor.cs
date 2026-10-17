namespace Vellum.Modules.Import.Extraction;

/// <summary>
/// Docker Compose files (<c>compose*.y[a]ml</c>, <c>docker-compose*.y[a]ml</c>). Rule
/// <c>compose.service</c>, version 2.
/// <para>
/// <b>One fact for each service of each compose project.</b> The compose project is the
/// directory of the file (the repository root is written <c>.</c>). All compose files of one
/// directory are read as one project, so an override file (<c>docker-compose.override.yml</c>,
/// <c>compose.prod.yaml</c>) adds to the fact of the service and never makes a second one. The
/// merge does not depend on the order of the files:
/// <list type="bullet">
/// <item><c>ports</c>, <c>dependsOn</c> and <c>environment</c> are the union over the files.</item>
/// <item><c>image</c>, <c>build</c>, <c>dockerfile</c>, <c>command</c> and <c>entrypoint</c> are
/// given by each file that has them. When two files disagree, no value wins: the fact gets
/// <c>conflict.&lt;attribute&gt;</c> (see <see cref="ExtractionContext.Add(FactKind, string, string, IReadOnlyList{SourceEvidence}, IEnumerable{ValueTuple{string, string}})"/>).</item>
/// </list>
/// </para>
/// <para>
/// <b>Kind of fact.</b> The last path segment of the image, without tag and digest, is looked
/// up in <see cref="Infrastructure"/>:
/// <list type="bullet">
/// <item>a data store image gives <c>datastore:&lt;project&gt;:&lt;service&gt;</c> (DataStore) with <c>kind</c>;</item>
/// <item>a broker image gives <c>deployment:compose:&lt;project&gt;:&lt;service&gt;</c> (Deployment) with <c>kind</c> and <c>role=broker</c>;</item>
/// <item>anything else gives <c>deployment:compose:&lt;project&gt;:&lt;service&gt;</c> with <c>kind=unknown</c> and <c>role=service</c>.</item>
/// </list>
/// An infrastructure image is a data store or a broker only when the service runs the server
/// of the image. It is a plain service (<c>kind=unknown</c>, <c>role=service</c>) with a note when:
/// <list type="bullet">
/// <item>a word of its <c>command</c> or <c>entrypoint</c> (in any file, also inside a quoted
/// script) names a client or dump tool of <see cref="ClientTools"/>: note
/// <c>infrastructure-image-runs-a-tool</c>;</item>
/// <item>a part of its name, or of a word of its <c>command</c> or <c>entrypoint</c> that is
/// not a flag, is one of <see cref="OneShotWords"/> (parts are split at <c>- _ .</c>; a path
/// gives its last segment), or it has <c>restart: "no"</c> together with a <c>command</c> or
/// <c>entrypoint</c>: note <c>one-shot-service</c>;</item>
/// <item>the files of the project give images of different kinds: note <c>conflicting-images</c>.</item>
/// </list>
/// </para>
/// <para>
/// <b>Attributes.</b> No value that can hold a secret is read:
/// <list type="bullet">
/// <item><c>environment</c>: the NAMES of the variables, from the keys of a mapping or the part
/// before '=' of a list item. A name that is not <c>[A-Za-z_][A-Za-z0-9_]*</c> is left out with
/// an <c>invalid-environment-name</c> note. A value is never read.</item>
/// <item><c>command</c>, <c>entrypoint</c>: the executable and the flag names
/// (<see cref="CommandLine"/>), for the string and the list form. When the service has an
/// entrypoint, its command is an argument list.</item>
/// <item><c>image</c>: as written when it is a safe image reference
/// (<see cref="ImageReference.Safe"/>); otherwise left out with an
/// <c>image-reference-not-safe</c> note. A reference with a variable is kept as written with an
/// <c>image-variable-not-resolved</c> note.</item>
/// <item><c>build</c>, <c>dockerfile</c>: a path of letters, digits and <c>. _ / ~ - $ { }</c>.
/// Anything else (a Git URL) is left out with a <c>build-context-not-a-path</c> note.</item>
/// <item><c>ports</c>: items of digits, letters and <c>. : - / $ { } _</c>; others are left out
/// with an <c>invalid-port</c> note. <c>dependsOn</c>: service names.</item>
/// </list>
/// </para>
/// Not recognised: <c>extends</c>, <c>include</c>, profiles, merge keys, variable substitution
/// (the text is kept as written), networks, volumes, labels, top-level <c>x-*</c> fields, and
/// which compose files a deployment really passes to <c>docker compose -f</c>. A service whose
/// name is not an identifier token is left out with an <c>invalid-service-name</c> note. A
/// server that is started with a script whose name holds a one-shot word (<c>init.sh</c>) is
/// read as a one-shot service. A build context is not linked to a code service.
/// </summary>
internal sealed class ComposeExtractor : IExtractor
{
    internal static readonly ExtractionRule Service = new("compose.service", 2);

    /// <summary>Image repository name (the last path segment, without tag) to kind and role.</summary>
    private static readonly Dictionary<string, (string Kind, bool Broker)> Infrastructure = new(StringComparer.Ordinal)
    {
        ["postgres"] = ("postgres", false), ["postgresql"] = ("postgres", false), ["postgis"] = ("postgres", false),
        ["timescaledb"] = ("postgres", false), ["pgvector"] = ("postgres", false),
        ["mysql"] = ("mysql", false), ["mariadb"] = ("mariadb", false),
        ["redis"] = ("redis", false), ["valkey"] = ("redis", false), ["redis-stack"] = ("redis", false),
        ["mongo"] = ("mongodb", false), ["mongodb"] = ("mongodb", false),
        ["elasticsearch"] = ("elasticsearch", false), ["opensearch"] = ("opensearch", false),
        ["cassandra"] = ("cassandra", false), ["clickhouse-server"] = ("clickhouse", false),
        ["memcached"] = ("memcached", false), ["minio"] = ("minio", false),
        ["mssql-server"] = ("sqlserver", false), ["azure-sql-edge"] = ("sqlserver", false), ["cockroach"] = ("cockroachdb", false),
        ["rabbitmq"] = ("rabbitmq", true), ["kafka"] = ("kafka", true), ["cp-kafka"] = ("kafka", true),
        ["redpanda"] = ("kafka", true), ["nats"] = ("nats", true), ["nats-streaming"] = ("nats", true),
        ["activemq"] = ("activemq", true), ["pulsar"] = ("pulsar", true),
    };

    /// <summary>Programs that talk to a server or copy its data. A service that runs one is not the server.</summary>
    private static readonly HashSet<string> ClientTools = new(StringComparer.Ordinal)
    {
        "psql", "pg_dump", "pg_dumpall", "pg_restore", "pg_isready", "pg_basebackup", "createdb", "dropdb",
        "mysql", "mysqldump", "mysqladmin", "mariadb", "mariadb-dump", "mariadb-admin",
        "redis-cli", "redis-benchmark", "valkey-cli",
        "mongosh", "mongo", "mongodump", "mongorestore", "mongoexport", "mongoimport",
        "kafka-topics", "kafka-topics.sh", "kafka-configs", "kafka-configs.sh", "kafka-acls", "kafka-acls.sh",
        "kafka-console-producer", "kafka-console-producer.sh", "kafka-console-consumer", "kafka-console-consumer.sh",
        "rabbitmqadmin", "rabbitmqctl", "cqlsh", "clickhouse-client", "nats", "mc", "sqlcmd",
    };

    /// <summary>Words that say a service runs once and ends.</summary>
    private static readonly HashSet<string> OneShotWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "migrate", "migration", "migrations", "migrator", "dump", "backup", "restore",
        "seed", "seeder", "init", "setup", "bootstrap",
    };

    private sealed record Part(SourceText File, DocEntry Entry);

    public string Id => "compose";
    public IReadOnlyList<ExtractionRule> Rules => [Service];

    public static bool IsComposeFile(SourceText file) =>
        file.IsYaml && (file.Name.StartsWith("docker-compose", StringComparison.OrdinalIgnoreCase)
            || file.Name.StartsWith("compose", StringComparison.OrdinalIgnoreCase));

    public void Run(ExtractionContext context)
    {
        // Project directory, then service name, then the definitions in path order: the files
        // of the context are in path order, so nothing here depends on the order of the input.
        var projects = new SortedDictionary<string, SortedDictionary<string, List<Part>>>(StringComparer.Ordinal);
        foreach (var file in context.Files)
        {
            if (context.Stopped)
                return;
            if (IsComposeFile(file))
                context.Guarded(Id, file.Path, () => Collect(context, file, projects));
        }
        foreach (var (directory, services) in projects)
        {
            foreach (var (name, parts) in services)
            {
                if (context.Stopped)
                    return;
                context.Guarded(Id, parts[0].File.Path, () => AddService(context, directory.Length == 0 ? "." : directory, name, parts));
            }
        }
    }

    private static void Collect(ExtractionContext context, SourceText file, SortedDictionary<string, SortedDictionary<string, List<Part>>> projects)
    {
        if (DocumentSet.Single(context, file, document => document.Map("services") is not null) is not { } root)
            return;
        var meter = context.Meter(file);
        foreach (var service in root.Map("services")!.Entries)
        {
            if (!meter.Charge())
                return;
            if (service.Value.Kind != DocKind.Map)
                continue;
            if (!CredentialGuard.IsIdentifierToken(service.Key))
            {
                context.Note(NoteKind.Undecided, "invalid-service-name", file.Path, service.KeyLine);
                continue;
            }
            if (!projects.TryGetValue(file.Directory, out var services))
                projects[file.Directory] = services = new SortedDictionary<string, List<Part>>(StringComparer.Ordinal);
            if (!services.TryGetValue(service.Key, out var parts))
                services[service.Key] = parts = [];
            // A key that is written twice in one mapping: the first one is the service.
            if (parts.Count == 0 || !ReferenceEquals(parts[^1].File, file))
                parts.Add(new Part(file, service));
        }
    }

    private static void AddService(ExtractionContext context, string project, string name, List<Part> parts)
    {
        var first = parts[0];
        var hasEntrypoint = parts.Exists(p => p.Entry.Value.Get("entrypoint") is not null);
        var kinds = new HashSet<(string Kind, bool Broker)>();
        (int Rank, string Reason, Part Part, int Line)? plain = null;
        var ports = new List<string>();
        var dependsOn = new List<string>();
        var environment = new List<string>();
        var dependsAt = new List<ComposeReference>();
        var variables = new List<ComposeVariable>();
        var builds = new HashSet<(string? Build, string? Dockerfile)>();
        var evidence = new List<SourceEvidence>(parts.Count);
        var single = new List<(Part Part, List<(string, string?)> Attributes)>(parts.Count);
        var hasCommand = false;
        (Part Part, int Line)? restartNo = null;

        // The lowest rank is the reason that is noted, at the first file in path order that gives it.
        void Plain(int rank, string reason, Part part, int line)
        {
            if (plain is null || rank < plain.Value.Rank)
                plain = (rank, reason, part, line);
        }

        if (HasOneShotWord(name.Split('-', '_', '.')))
            Plain(1, "one-shot-service", first, first.Entry.KeyLine);

        foreach (var part in parts)
        {
            var (file, entry) = (part.File, part.Entry);
            var meter = context.Meter(file);
            var service = entry.Value;
            if (!meter.Charge(service.Entries.Count + 1))
                return;
            evidence.Add(Service.At(file.Path, entry.KeyLine, service.EndLine));
            var attributes = new List<(string, string?)>();

            if (service.Entry("image") is { } image && image.Value.AsText() is { } reference)
            {
                if (ImageReference.Checked(context, file, reference, image.KeyLine) is { } safe)
                {
                    attributes.Add(("image", safe));
                    kinds.Add(Infrastructure.TryGetValue(ImageReference.Repository(safe), out var known) ? known : ("unknown", false));
                }
                else
                {
                    kinds.Add(("unknown", false));
                }
            }

            if (service.Entry("build") is { } build)
            {
                var buildPath = PathValue(context, file, build.Value.Kind == DocKind.Map ? build.Value.Entry("context") : build);
                var dockerfile = build.Value.Kind == DocKind.Map ? PathValue(context, file, build.Value.Entry("dockerfile")) : null;
                attributes.Add(("build", buildPath));
                attributes.Add(("dockerfile", dockerfile));
                builds.Add((buildPath, dockerfile));
            }

            foreach (var key in (string[])["entrypoint", "command"])
            {
                if (service.Entry(key) is not { } command)
                    continue;
                var words = Words(command.Value, meter);
                if (words is null)
                    return;
                var asArguments = key == "command" && hasEntrypoint;
                attributes.Add((key, command.Value.AsText() is { } text
                    ? CommandLine.ReduceShell(text, !asArguments, context, file.Path, command.KeyLine)
                    : CommandLine.Reduce(words, !asArguments, context, file.Path, command.KeyLine)));

                // Classification reads every word, also inside a quoted script. None of it is emitted.
                var tokens = words.SelectMany(CommandLine.Tokens).ToList();
                if (tokens.Exists(t => ClientTools.Contains(CommandLine.BaseName(t))))
                    Plain(0, "infrastructure-image-runs-a-tool", part, command.KeyLine);
                else if (tokens.Exists(t => !t.StartsWith('-') && HasOneShotWord(CommandLine.BaseName(t).Split('-', '_', '.'))))
                    Plain(2, "one-shot-service", part, command.KeyLine);
                hasCommand = true;
            }
            if (restartNo is null && service.Entry("restart") is { } restart && restart.Value.AsText() == "no")
                restartNo = (part, restart.KeyLine);

            if (service.Get("ports") is { Kind: DocKind.Seq } portList)
            {
                if (!meter.Charge(portList.Items.Count))
                    return;
                foreach (var item in portList.Items)
                {
                    var port = item.AsText()
                        ?? (item.Kind == DocKind.Map && item.Str("target") is { } target
                            ? item.Str("published") is { } published ? $"{published}:{target}" : target
                            : null);
                    if (port is null)
                        continue;
                    if (IsPort(port))
                        ports.Add(port);
                    else
                        context.Note(NoteKind.Undecided, "invalid-port", file.Path, item.StartLine);
                }
            }

            if (service.Get("depends_on") is { } depends)
            {
                if (!meter.Charge(depends.Entries.Count + depends.Items.Count))
                    return;
                foreach (var (dependency, line) in depends.Kind == DocKind.Map
                    ? depends.Entries.Select(e => (e.Key, e.KeyLine))
                    : depends.Items.Select(i => (i.AsText() ?? "", i.StartLine)))
                    if (CredentialGuard.IsIdentifierToken(dependency))
                    {
                        dependsOn.Add(dependency);
                        dependsAt.Add(new ComposeReference(dependency, file.Path, line));
                    }
            }

            if (service.Get("environment") is { } environmentList)
            {
                if (!meter.Charge(environmentList.Entries.Count + environmentList.Items.Count))
                    return;
                foreach (var (variable, line, value) in environmentList.Kind == DocKind.Map
                    ? environmentList.Entries.Select(e => (e.Key, e.KeyLine, e.Value.AsText()))
                    : environmentList.Items.Where(i => i.AsText() is not null).Select(i => (NameOf(i.Text), i.StartLine, ValueOf(i.Text))))
                {
                    if (CommandLine.IsVariableName(variable))
                    {
                        environment.Add(variable);
                        // The value is read for one thing only: the compose service it names as
                        // its host. The value itself is never kept.
                        variables.Add(new ComposeVariable(variable, file.Path, line, value is null ? null : HostOf(value)));
                    }
                    else
                        context.Note(NoteKind.Undecided, "invalid-environment-name", file.Path, line);
                }
            }
            single.Add((part, attributes));
        }

        if (hasCommand && restartNo is { } once)
            Plain(3, "one-shot-service", once.Part, once.Line);

        (string Kind, bool Broker) kind = ("unknown", false);
        if (kinds.Count == 1)
            kind = kinds.First();
        else if (kinds.Count > 1)
            context.Note(NoteKind.Undecided, "conflicting-images", first.File.Path, first.Entry.KeyLine);
        if (kind.Kind != "unknown" && plain is { } reason)
        {
            context.Note(NoteKind.Undecided, reason.Reason, reason.Part.File.Path, reason.Line);
            kind = ("unknown", false);
        }

        var isStore = kind.Kind != "unknown" && !kind.Broker;
        var factKind = isStore ? FactKind.DataStore : FactKind.Deployment;
        var factKey = isStore ? $"datastore:{project}:{name}" : $"deployment:compose:{project}:{name}";
        var at = evidence[0];
        context.Add(factKind, factKey, name, evidence,
        [
            ("source", "compose"), ("kind", kind.Kind),
            ("role", isStore ? null : kind.Broker ? "broker" : "service"),
            ("ports", context.List(ports.Distinct(StringComparer.Ordinal), at.Path, at.StartLine)),
            ("dependsOn", context.List(dependsOn, at.Path, at.StartLine, sorted: true)),
            ("environment", context.Names(environment, "environment", at.Path, at.StartLine)),
        ]);
        foreach (var (part, attributes) in single)
            context.Add(factKind, factKey, name, [Service.At(part.File.Path, part.Entry.KeyLine, part.Entry.Value.EndLine)], attributes);

        // Two files that build the service differently say no one build.
        var (buildContext, buildFile) = builds.Count == 1 ? builds.First() : (null, null);
        context.ComposeServices.Add(new ComposeService(
            project, name, factKey, isStore, kind.Broker, at.Path, at.StartLine, builds.Count > 0, buildContext, buildFile, dependsAt, variables));
    }

    /// <summary>"NAME=value" gives "value", "NAME" gives null.</summary>
    private static string? ValueOf(string item)
    {
        var equals = item.IndexOf('=');
        return equals < 0 ? null : item[(equals + 1)..];
    }

    /// <summary>
    /// The host that a variable value names, when it is a plain name: the host of a URL
    /// (<c>postgres://u:p@db:5432/x</c>), the <c>Host</c>, <c>Server</c>, <c>Data Source</c> or
    /// <c>Address</c> of a connection string (<c>Host=db;Database=x</c>), or <c>db:5432</c>.
    /// Null for anything else, and for a list of hosts.
    /// </summary>
    internal static string? HostOf(string value)
    {
        value = value.Trim();
        var scheme = value.IndexOf("://", StringComparison.Ordinal);
        if (scheme > 0)
        {
            var authority = value[(scheme + 3)..];
            var end = authority.IndexOfAny(['/', '?', '#']);
            if (end >= 0)
                authority = authority[..end];
            var at = authority.LastIndexOf('@');
            if (at >= 0)
                authority = authority[(at + 1)..];
            if (authority.Contains(','))
                return null;
            var colon = authority.IndexOf(':');
            return Plain(colon >= 0 ? authority[..colon] : authority);
        }
        if (value.Contains('='))
        {
            foreach (var part in value.Split(';'))
            {
                var equals = part.IndexOf('=');
                if (equals < 0)
                    continue;
                var key = part[..equals].Replace(" ", "", StringComparison.Ordinal).Trim().ToLowerInvariant();
                if (key is not ("host" or "server" or "datasource" or "address" or "addr" or "hostname"))
                    continue;
                var host = part[(equals + 1)..].Trim();
                if (host.StartsWith("tcp:", StringComparison.OrdinalIgnoreCase))
                    host = host[4..];
                if (host.Contains(',') && host.IndexOf(',') != host.LastIndexOf(','))
                    return null;
                var cut = host.IndexOfAny([',', ':']);
                return Plain(cut >= 0 ? host[..cut] : host);
            }
            return null;
        }
        var port = value.LastIndexOf(':');
        return port > 0 && port < value.Length - 1 && value.AsSpan(port + 1).IndexOfAnyExceptInRange('0', '9') < 0
            ? Plain(value[..port])
            : null;

        static string? Plain(string host) => CredentialGuard.IsIdentifierToken(host) ? host : null;
    }

    private static bool HasOneShotWord(string[] parts)
    {
        foreach (var part in parts)
            if (OneShotWords.Contains(part))
                return true;
        return false;
    }

    /// <summary>
    /// The words of a command in the string form (as one item) or the list form. The length of
    /// the text is charged; null when the work of the file is spent.
    /// </summary>
    private static List<string>? Words(DocNode command, WorkMeter meter)
    {
        if (command.AsText() is { } text)
            return meter.Charge(text.Length) ? [text] : null;
        var words = new List<string>(command.Items.Count);
        long length = command.Items.Count;
        foreach (var item in command.Items)
        {
            if (item.AsText() is not { } word)
                continue;
            words.Add(word);
            length += word.Length;
        }
        return meter.Charge(length) ? words : null;
    }

    /// <summary>"NAME=value" gives "NAME". The value is not returned.</summary>
    private static string NameOf(string item)
    {
        var equals = item.IndexOf('=');
        return equals < 0 ? item : item[..equals];
    }

    private static string? PathValue(ExtractionContext context, SourceText file, DocEntry? entry)
    {
        if (entry?.Value.AsText() is not { Length: > 0 } path)
            return null;
        if (path.Length <= 300 && path.AsSpan().IndexOfAnyExcept(PathCharacters) < 0)
            return path;
        context.Note(NoteKind.Undecided, "build-context-not-a-path", file.Path, entry.KeyLine);
        return null;
    }

    private static bool IsPort(string port) => port.Length is > 0 and <= 64 && port.AsSpan().IndexOfAnyExcept(PortCharacters) < 0;

    private static readonly System.Buffers.SearchValues<char> PathCharacters =
        System.Buffers.SearchValues.Create("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789._/~-${}");

    private static readonly System.Buffers.SearchValues<char> PortCharacters =
        System.Buffers.SearchValues.Create("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789.:-/${}_");
}

/// <param name="Project">The directory of the compose files, <c>.</c> at the repository root.</param>
/// <param name="FactKey">The key of the DataStore or Deployment fact of the service.</param>
/// <param name="HasBuild">True when a file of the project builds the service. <paramref name="Build"/> is null when the files disagree.</param>
internal sealed record ComposeService(
    string Project, string Name, string FactKey, bool IsStore, bool IsBroker, string Path, int Line,
    bool HasBuild, string? Build, string? Dockerfile,
    IReadOnlyList<ComposeReference> DependsOn, IReadOnlyList<ComposeVariable> Environment);

/// <summary>A <c>depends_on</c> item: the service it names, and its line.</summary>
internal sealed record ComposeReference(string Service, string Path, int Line);

/// <summary>A variable of a service: its name, its line, and the host its value names (<see cref="ComposeExtractor.HostOf"/>).</summary>
internal sealed record ComposeVariable(string Name, string Path, int Line, string? Host);

/// <summary>
/// An image reference of a compose file, a Dockerfile or a Kubernetes manifest:
/// <c>[registry[:port]/]path[:tag][@sha256:digest]</c>.
/// </summary>
internal static class ImageReference
{
    private static readonly System.Buffers.SearchValues<char> Characters =
        System.Buffers.SearchValues.Create("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789._/:-${}?+");

    /// <summary>
    /// The reference as written, or null when it can hold user information: an '@' that does
    /// not start a <c>sha256</c> or <c>sha512</c> digest (<c>user:password@registry/image</c>,
    /// <c>https://user:password@registry/image</c>), or a character that an image reference and
    /// a <c>${variable}</c> do not have. At most 300 characters.
    /// </summary>
    public static string? Safe(string image)
    {
        if (image.Length is 0 or > 300)
            return null;
        var name = image.AsSpan();
        var at = name.IndexOf('@');
        if (at >= 0)
        {
            var digest = name[(at + 1)..];
            if (!(digest.StartsWith("sha256:", StringComparison.Ordinal) || digest.StartsWith("sha512:", StringComparison.Ordinal)))
                return null;
            digest = digest[7..];
            if (digest.Length is < 32 or > 128 || digest.IndexOfAnyExcept("0123456789abcdef") >= 0)
                return null;
            name = name[..at];
        }
        return name.Length > 0 && name.IndexOfAnyExcept(Characters) < 0 ? image : null;
    }

    /// <summary>
    /// <see cref="Safe"/>, with the notes: <c>image-reference-not-safe</c> when the reference is
    /// left out, <c>image-variable-not-resolved</c> when it is kept with a variable in it.
    /// </summary>
    public static string? Checked(ExtractionContext context, SourceText file, string image, int line)
    {
        if (Safe(image) is not { } safe)
        {
            context.Note(NoteKind.Redacted, "image-reference-not-safe", file.Path, line);
            return null;
        }
        if (safe.Contains('$'))
            context.Note(NoteKind.Undecided, "image-variable-not-resolved", file.Path, line);
        return safe;
    }

    /// <summary>"docker.io/bitnami/postgresql:16@sha256:…" gives "postgresql".</summary>
    public static string Repository(string image)
    {
        var at = image.IndexOf('@');
        if (at >= 0)
            image = image[..at];
        var slash = image.LastIndexOf('/');
        var name = slash < 0 ? image : image[(slash + 1)..];
        var colon = name.IndexOf(':');
        return (colon < 0 ? name : name[..colon]).ToLowerInvariant();
    }
}
