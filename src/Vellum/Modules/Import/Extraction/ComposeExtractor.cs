namespace Vellum.Modules.Import.Extraction;

/// <summary>
/// Docker Compose files (<c>compose*.y[a]ml</c>, <c>docker-compose*.y[a]ml</c>). One fact for
/// each service:
/// <list type="bullet">
/// <item>a recognised data store image gives <c>datastore:&lt;service&gt;</c> (DataStore) with <c>kind</c>;</item>
/// <item>a recognised broker image gives <c>deployment:compose:&lt;service&gt;</c> (Deployment) with <c>kind</c> and <c>role=broker</c>;</item>
/// <item>anything else gives <c>deployment:compose:&lt;service&gt;</c> with <c>kind=unknown</c> and <c>role=service</c>.</item>
/// </list>
/// Attributes: image or build context, ports, <c>depends_on</c>, and the NAMES of environment
/// variables. An environment value is never read into a fact.
/// Not recognised: <c>extends</c>, <c>include</c>, profiles, merge keys, variable
/// substitution (the text is kept as written), networks and volumes. A build context is
/// not linked to a code service; the path is recorded and nothing is inferred from it.
/// </summary>
internal sealed class ComposeExtractor : IExtractor
{
    private static readonly ExtractionRule Service = new("compose.service", 1);

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

    public string Id => "compose";
    public IReadOnlyList<ExtractionRule> Rules => [Service];

    public static bool IsComposeFile(SourceText file) =>
        file.IsYaml && (file.Name.StartsWith("docker-compose", StringComparison.OrdinalIgnoreCase)
            || file.Name.StartsWith("compose", StringComparison.OrdinalIgnoreCase));

    public void Run(ExtractionContext context)
    {
        foreach (var file in context.Files)
        {
            if (context.Stopped)
                return;
            if (!IsComposeFile(file))
                continue;
            var services = context.Documents(file).FirstOrDefault() is { Kind: DocKind.Map } root ? root.Map("services") : null;
            foreach (var service in services?.Entries ?? [])
                if (service.Value.Kind == DocKind.Map)
                    AddService(context, file, service);
        }
    }

    private static void AddService(ExtractionContext context, SourceText file, DocEntry entry)
    {
        var service = entry.Value;
        var image = service.Str("image");
        var build = service.Get("build");
        var attributes = new List<(string, string?)>
        {
            ("source", "compose"), ("image", image),
            ("build", build?.AsText() ?? (build?.Kind == DocKind.Map ? build.Str("context") : null)),
            ("dockerfile", build?.Kind == DocKind.Map ? build.Str("dockerfile") : null),
            ("ports", TextUtil.Join(Ports(service.Get("ports")))),
            ("dependsOn", TextUtil.JoinSorted(Names(service.Get("depends_on")))),
            ("environment", TextUtil.JoinSorted(Names(service.Get("environment")))),
        };

        var evidence = Service.At(file.Path, entry.KeyLine, file.TrimEnd(entry.KeyLine, service.EndLine));
        if (image is not null && Infrastructure.TryGetValue(Repository(image), out var known))
        {
            attributes.Add(("kind", known.Kind));
            if (!known.Broker)
            {
                context.Add(FactKind.DataStore, "datastore:" + entry.Key, entry.Key, [evidence], attributes);
                return;
            }
            attributes.Add(("role", "broker"));
        }
        else
        {
            attributes.Add(("kind", "unknown"));
            attributes.Add(("role", "service"));
        }
        context.Add(FactKind.Deployment, "deployment:compose:" + entry.Key, entry.Key, [evidence], attributes);
    }

    /// <summary>"docker.io/bitnami/postgresql:16@sha256:…" gives "postgresql".</summary>
    internal static string Repository(string image)
    {
        var at = image.IndexOf('@');
        if (at >= 0)
            image = image[..at];
        var slash = image.LastIndexOf('/');
        var name = slash < 0 ? image : image[(slash + 1)..];
        var colon = name.IndexOf(':');
        return (colon < 0 ? name : name[..colon]).ToLowerInvariant();
    }

    /// <summary>The names of a list of "NAME=value" or "NAME" items, or the keys of a mapping. Values are not returned.</summary>
    private static IEnumerable<string> Names(DocNode? node)
    {
        if (node is null)
            yield break;
        if (node.Kind == DocKind.Map)
        {
            foreach (var entry in node.Entries)
                yield return entry.Key;
        }
        else if (node.Kind == DocKind.Seq)
        {
            foreach (var item in node.Items)
            {
                if (item.AsText() is not { } text)
                    continue;
                var equals = text.IndexOf('=');
                yield return equals < 0 ? text : text[..equals];
            }
        }
    }

    private static IEnumerable<string> Ports(DocNode? node)
    {
        if (node is not { Kind: DocKind.Seq })
            yield break;
        foreach (var item in node.Items)
        {
            if (item.AsText() is { } text)
                yield return text;
            else if (item.Kind == DocKind.Map && item.Str("target") is { } target)
                yield return item.Str("published") is { } published ? $"{published}:{target}" : target;
        }
    }
}
