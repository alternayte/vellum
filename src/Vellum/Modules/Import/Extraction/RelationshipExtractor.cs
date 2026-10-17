using System.Globalization;
using Vellum.Modules.Trace.Engine;

namespace Vellum.Modules.Import.Extraction;

/// <summary>
/// Relationships between the elements of a scan, from the facts of the other extractors. It
/// runs after them and reads their facts; it reads no file of its own. Each relationship is a
/// <see cref="FactKind.Relationship"/> fact with <c>from</c> and <c>to</c> (the keys of two
/// Service or DataStore facts), <c>label</c> and <c>relationshipKind</c>. Its key names its rule
/// and its ends, never a line, so a rescan finds the same relationship.
/// <para>A compose service is an element when it is a DataStore fact, or when it builds one code
/// service: the directory of its Dockerfile, else its build context, is the directory of one
/// Service fact's project file, else exactly one such directory is under the build context. A
/// compose service with no build is the one code service whose directory is named like it. A
/// broker is no element.</para>
/// <list type="bullet">
/// <item><c>relationship.message-flow</c> — <c>relationship:message-flow:&lt;producer&gt;-&gt;&lt;consumer&gt;:&lt;message&gt;</c>,
/// label <c>publishes &lt;message&gt;</c>, behavioural. A Publication fact names the producer and the
/// message. A consumer is the service of a Subscription that names the same message, or of a
/// Subscription on a channel that the publication's channel reaches through binding facts, as
/// the trace engine routes (<see cref="RoutingKeys"/>): a binding with a key passes only a
/// publication whose routing key matches it. A Subscription that names another message which
/// a publication of the scan produces is not a consumer of this one. The evidence is the
/// publish lines, the subscribe lines and the binding lines.</item>
/// <item><c>relationship.data-access</c> — <c>relationship:data-access:&lt;app&gt;-&gt;&lt;store&gt;</c>,
/// label <c>reads and writes</c>, behavioural. A DataAccess fact names the app. The store is a
/// DataStore of the compose project of a compose service of the app, found by the first rule
/// that names exactly one: (1) the variable that the code reads (<c>connectionVariable</c>, or
/// <c>ConnectionStrings__&lt;connectionName&gt;</c>) is set on the compose service with a value whose
/// host is the store; (2) the other variables whose value names a store as host, with the
/// <c>depends_on</c> items that name a store; (3) a store named like the connection name. Rules
/// (2) and (3) take only a store of the kind the fact states, when it states one. The value of a variable is read for its host only and never
/// kept. With no such store there is no relationship, and a scan that has a compose file adds
/// a note <c>data-access-store-not-resolved</c>.</item>
/// <item><c>relationship.compose-depends-on</c> — <c>relationship:depends-on:&lt;from&gt;-&gt;&lt;to&gt;</c>,
/// label <c>depends on</c>, structural, for each <c>depends_on</c> item between two compose
/// services that are elements.</item>
/// <item><c>relationship.http-call</c> — <c>relationship:http-call:&lt;app&gt;-&gt;&lt;app&gt;</c>, label
/// <c>calls</c>, behavioural, <c>truthLabel=inferred</c>. A variable of a compose service of an app,
/// or the base address key of an HTTP client of the app (<c>dotnet.http-client-base-address</c>),
/// whose name is the name of another compose service that is an app followed by an address
/// word (<c>PAYMENTS_URL</c>, <c>Payments__BaseUrl</c>, <c>Services:Payments:Endpoint</c> for the
/// service <c>payments</c>). A name is a hint and not proof: the confidence is
/// <see cref="NameConfidence"/> for a compose variable and <see cref="ClientConfidence"/> for an
/// HTTP client that reads it. It is never applied without a person.</item>
/// </list>
/// </summary>
internal sealed class RelationshipExtractor : IExtractor
{
    internal static readonly ExtractionRule MessageFlow = new("relationship.message-flow", 1);
    internal static readonly ExtractionRule DataAccess = new("relationship.data-access", 1);
    internal static readonly ExtractionRule DependsOn = new("relationship.compose-depends-on", 1);
    internal static readonly ExtractionRule HttpCall = new("relationship.http-call", 1);

    /// <summary>
    /// The first line of each fact a relationship rests on besides its own lines: the element at
    /// each end and, for a message flow, the message. A rescan that could not read one of those
    /// files does not take the relationship for gone. It is never the pointer of the relationship.
    /// </summary>
    internal static readonly ExtractionRule Basis = new("relationship.basis", 1);

    /// <summary>A compose variable whose name names an app.</summary>
    public const double NameConfidence = 0.6;
    /// <summary>An HTTP client whose base address reads a key that names an app.</summary>
    public const double ClientConfidence = 0.8;

    public const string PublishesLabel = "publishes ";
    public const string ReadsAndWrites = "reads and writes";
    public const string DependsOnLabel = "depends on";
    public const string Calls = "calls";

    /// <summary>The most lines one relationship cites.</summary>
    private const int MaxEvidence = 16;
    private const int FactCost = 512;

    /// <summary>The words after a service name that make a name an address of that service.</summary>
    private static readonly string[][] AddressWords =
    [
        ["url"], ["uri"], ["baseurl"], ["baseuri"], ["base", "url"], ["base", "uri"], ["baseaddress"], ["base", "address"],
        ["endpoint"], ["address"], ["host"], ["api", "url"], ["api", "base", "url"], ["service", "url"], ["http", "url"],
    ];

    /// <summary>Words before a service name that group the addresses of a configuration.</summary>
    private static readonly HashSet<string> GroupWords = new(StringComparer.Ordinal) { "services", "service", "clients", "client" };

    public string Id => "relationship";
    public IReadOnlyList<ExtractionRule> Rules => [MessageFlow, DataAccess, DependsOn, HttpCall, Basis];

    /// <summary>True for a relationship that this extractor derived, as opposed to one a model file declares.</summary>
    public static bool IsDerived(ExtractedFact fact) =>
        fact.Kind == FactKind.Relationship && fact.Evidence.Count > 0
        && fact.Evidence.All(e => e.Rule.StartsWith("relationship.", StringComparison.Ordinal));

    private sealed class Edge(string from, string to, string label, string kind)
    {
        public string From { get; } = from;
        public string To { get; } = to;
        public string Label { get; } = label;
        public string Kind { get; } = kind;
        public List<SourceEvidence> Evidence { get; } = [];
        public SortedSet<string> Variables { get; } = new(StringComparer.Ordinal);
        public double Confidence { get; set; }
        public string? Message { get; set; }
    }

    public void Run(ExtractionContext context)
    {
        if (context.Stopped)
            return;
        context.Guarded(Id, "", () => Derive(context));
    }

    private static void Derive(ExtractionContext context)
    {
        var facts = context.CurrentFacts();
        var byKey = new Dictionary<string, ExtractedFact>(StringComparer.Ordinal);
        foreach (var fact in facts)
            byKey.TryAdd(fact.Key, fact);
        var map = new ComposeMap(context.ComposeServices, facts, byKey);
        var edges = new SortedDictionary<string, Edge>(StringComparer.Ordinal);

        MessageFlows(facts, byKey, edges);
        DataAccesses(context, facts, byKey, map, edges);
        DependsOnItems(map, edges);
        HttpCalls(facts, byKey, map, edges);

        foreach (var (key, edge) in edges)
        {
            if (context.Stopped)
                return;
            if (edge.Evidence.Count == 0)
                continue;
            List<SourceEvidence> ends =
            [
                .. Cite(Basis, byKey[edge.From].Evidence.Take(1)), .. Cite(Basis, byKey[edge.To].Evidence.Take(1)),
                .. edge.Message is { } message ? Cite(Basis, byKey[message].Evidence.Take(1)) : [],
            ];
            var evidence = ends.Concat(edge.Evidence.Distinct()
                    .OrderBy(e => e.Path, StringComparer.Ordinal).ThenBy(e => e.StartLine).ThenBy(e => e.EndLine)
                    .Take(MaxEvidence - ends.Count))
                .Distinct().ToList();
            if (!context.Meter(evidence[0].Path).Charge(FactCost))
                continue;
            var name = $"{byKey[edge.From].Name} → {byKey[edge.To].Name}: {edge.Label}";
            context.Add(FactKind.Relationship, key, name, evidence,
            [
                ("from", edge.From), ("to", edge.To), ("label", edge.Label), ("relationshipKind", edge.Kind),
                ("message", edge.Message),
                ("truthLabel", edge.Confidence > 0 ? "inferred" : null),
                ("confidence", edge.Confidence > 0 ? edge.Confidence.ToString("0.0#", CultureInfo.InvariantCulture) : null),
                ("variables", edge.Variables.Count > 0 ? string.Join(",", edge.Variables) : null),
            ]);
        }
    }

    private static Edge EdgeOf(SortedDictionary<string, Edge> edges, string key, string from, string to, string label, string kind)
    {
        if (!edges.TryGetValue(key, out var edge))
            edges[key] = edge = new Edge(from, to, label, kind);
        return edge;
    }

    private static IEnumerable<SourceEvidence> Cite(ExtractionRule rule, IEnumerable<SourceEvidence> evidence) =>
        evidence.Select(e => rule.At(e.Path, e.StartLine, e.EndLine));

    // ---- message flow -----------------------------------------------------------------

    private static void MessageFlows(IReadOnlyList<ExtractedFact> facts, Dictionary<string, ExtractedFact> byKey, SortedDictionary<string, Edge> edges)
    {
        bool IsService(string? key) => key is not null && byKey.TryGetValue(key, out var f) && f.Kind == FactKind.Service;

        var publications = facts.Where(f => f.Kind == FactKind.Publication && !f.Attributes.ContainsKey("pattern")
            && IsService(f.Attributes.GetValueOrDefault("service")) && f.Attributes.ContainsKey("message")).ToList();
        if (publications.Count == 0)
            return;
        var subscriptions = facts.Where(f => f.Kind == FactKind.Subscription && !f.Attributes.ContainsKey("exchange")
            && f.Attributes.ContainsKey("queue") && IsService(f.Attributes.GetValueOrDefault("service"))).ToList();
        var bindings = facts.Where(f => f.Kind == FactKind.Subscription && f.Attributes.ContainsKey("exchange") && f.Attributes.ContainsKey("queue"))
            .ToLookup(f => f.Attributes["exchange"], StringComparer.Ordinal);
        var produced = publications.Select(p => p.Attributes["message"]).ToHashSet(StringComparer.Ordinal);

        foreach (var publication in publications)
        {
            var message = publication.Attributes["message"];
            if (!byKey.TryGetValue(message, out var messageFact) || messageFact.Kind != FactKind.Message)
                continue;
            var producer = publication.Attributes["service"];
            var reached = Reachable(publication.Attributes.GetValueOrDefault("channel"), publication.Attributes.GetValueOrDefault("routingKey"), bindings);

            foreach (var subscription in subscriptions)
            {
                var consumer = subscription.Attributes["service"];
                if (consumer == producer)
                    continue;
                var named = subscription.Attributes.GetValueOrDefault("message");
                var byMessage = named == message;
                var route = reached.GetValueOrDefault(subscription.Attributes["queue"]);
                var byRoute = route is not null && !(named is not null && named != message && produced.Contains(named));
                if (!byMessage && !byRoute)
                    continue;

                var edge = EdgeOf(edges, $"relationship:message-flow:{producer}->{consumer}:{messageFact.Name}",
                    producer, consumer, PublishesLabel + messageFact.Name, "behavioural");
                edge.Message = message;
                edge.Evidence.AddRange(Cite(MessageFlow, publication.Evidence));
                edge.Evidence.AddRange(Cite(MessageFlow, subscription.Evidence));
                if (!byMessage && route is not null)
                    edge.Evidence.AddRange(Cite(MessageFlow, route));
            }
        }
    }

    /// <summary>
    /// The channels a message published to <paramref name="channel"/> reaches, with the channel
    /// itself, each with the evidence of the bindings on the way. A binding with a key passes the
    /// message only when its routing key matches (<see cref="RoutingKeys.Matches"/>); an empty
    /// key is no key.
    /// </summary>
    private static Dictionary<string, List<SourceEvidence>> Reachable(string? channel, string? routingKey, ILookup<string, ExtractedFact> bindings)
    {
        var reached = new Dictionary<string, List<SourceEvidence>>(StringComparer.Ordinal);
        if (channel is null)
            return reached;
        reached[channel] = [];
        var open = new Queue<string>([channel]);
        while (open.TryDequeue(out var current))
        {
            foreach (var binding in bindings[current])
            {
                var key = binding.Attributes.GetValueOrDefault("routingKey") is { Length: > 0 } bound ? bound : null;
                if (!RoutingKeys.Matches(key, routingKey is { Length: > 0 } ? routingKey : null))
                    continue;
                var target = binding.Attributes["queue"];
                if (reached.ContainsKey(target))
                    continue;
                reached[target] = [.. reached[current], .. binding.Evidence];
                open.Enqueue(target);
            }
        }
        return reached;
    }

    // ---- data access ------------------------------------------------------------------

    private static void DataAccesses(
        ExtractionContext context, IReadOnlyList<ExtractedFact> facts, Dictionary<string, ExtractedFact> byKey,
        ComposeMap map, SortedDictionary<string, Edge> edges)
    {
        foreach (var access in facts.Where(f => f.Kind == FactKind.DataAccess))
        {
            if (access.Attributes.GetValueOrDefault("service") is not { } app || !byKey.TryGetValue(app, out var service) || service.Kind != FactKind.Service)
                continue;
            var at = access.Evidence[0];
            var kind = access.Attributes.GetValueOrDefault("storeKind") is { } stated && stated != "unknown" ? stated : null;
            var wanted = WantedVariables(access);
            var connectionName = access.Attributes.GetValueOrDefault("connectionName");

            var named = new Dictionary<string, List<SourceEvidence>>(StringComparer.Ordinal);
            var hinted = new Dictionary<string, List<SourceEvidence>>(StringComparer.Ordinal);
            var byName = new Dictionary<string, List<SourceEvidence>>(StringComparer.Ordinal);
            foreach (var compose in map.ComposeOf(app))
            {
                foreach (var variable in compose.Environment)
                {
                    if (variable.Host is not { } host)
                        continue;
                    var cited = DataAccess.At(variable.Path, variable.Line, variable.Line);
                    // The variable the code reads names its store whatever kind the image is.
                    if (wanted.Contains(Normal(variable.Name)) && map.Store(compose.Project, host, null) is { } exact)
                        Bucket(named, exact.FactKey).Add(cited);
                    else if (map.Store(compose.Project, host, kind) is { } store)
                        Bucket(hinted, store.FactKey).Add(cited);
                }
                foreach (var dependency in compose.DependsOn)
                {
                    if (map.Store(compose.Project, dependency.Service, kind) is { } store)
                        Bucket(hinted, store.FactKey).Add(DataAccess.At(dependency.Path, dependency.Line, dependency.Line));
                }
                if (connectionName is not null)
                {
                    foreach (var store in map.StoresOf(compose.Project))
                    {
                        if (Squash(store.Name) == Squash(connectionName) && map.Store(compose.Project, store.Name, kind) is not null)
                            Bucket(byName, store.FactKey).Add(DataAccess.At(store.Path, store.Line, store.Line));
                    }
                }
            }

            var decided = named.Count == 1 ? named.First()
                : hinted.Count == 1 ? hinted.First()
                : byName.Count == 1 ? byName.First()
                : (KeyValuePair<string, List<SourceEvidence>>?)null;
            if (decided is not { } found)
            {
                // A scan with no compose file has nothing to match a store with: no note.
                if (map.Services.Count > 0)
                    context.Note(NoteKind.Undecided, "data-access-store-not-resolved", at.Path, at.StartLine);
                continue;
            }

            var edge = EdgeOf(edges, $"relationship:data-access:{app}->{found.Key}", app, found.Key, ReadsAndWrites, "behavioural");
            edge.Evidence.AddRange(Cite(DataAccess, access.Evidence.Take(4)));
            edge.Evidence.AddRange(found.Value);
        }
    }

    /// <summary>The variables that set the connection of a data access, in the form <see cref="Normal"/> gives.</summary>
    private static HashSet<string> WantedVariables(ExtractedFact access)
    {
        var wanted = new HashSet<string>(StringComparer.Ordinal);
        if (access.Attributes.GetValueOrDefault("connectionVariable") is { } variable)
            wanted.Add(Normal(variable));
        if (access.Attributes.GetValueOrDefault("connectionName") is { } name)
            wanted.Add(Normal("ConnectionStrings:" + name));
        return wanted;
    }

    /// <summary>A configuration key as a deployment sets it: <c>:</c> and <c>__</c> are one separator, case is not significant.</summary>
    private static string Normal(string key) => key.Replace("__", ":", StringComparison.Ordinal).ToUpperInvariant();

    /// <summary>Letters and digits only, lower case: <c>orders-db</c> and <c>OrdersDb</c> are one name.</summary>
    private static string Squash(string name) => new([.. name.Where(char.IsAsciiLetterOrDigit).Select(char.ToLowerInvariant)]);

    // ---- compose depends_on -----------------------------------------------------------

    private static void DependsOnItems(ComposeMap map, SortedDictionary<string, Edge> edges)
    {
        foreach (var compose in map.Services)
        {
            if (map.ElementOf(compose) is not { } from)
                continue;
            foreach (var dependency in compose.DependsOn)
            {
                if (map.Find(compose.Project, dependency.Service) is not { } target || map.ElementOf(target) is not { } to || to == from)
                    continue;
                EdgeOf(edges, $"relationship:depends-on:{from}->{to}", from, to, DependsOnLabel, "structural")
                    .Evidence.Add(DependsOn.At(dependency.Path, dependency.Line, dependency.Line));
            }
        }
    }

    // ---- inferred HTTP calls ----------------------------------------------------------

    private static void HttpCalls(
        IReadOnlyList<ExtractedFact> facts, Dictionary<string, ExtractedFact> byKey, ComposeMap map, SortedDictionary<string, Edge> edges)
    {
        void Add(string from, string to, string variable, double confidence, IEnumerable<SourceEvidence> evidence)
        {
            var edge = EdgeOf(edges, $"relationship:http-call:{from}->{to}", from, to, Calls, "behavioural");
            edge.Confidence = Math.Max(edge.Confidence, confidence);
            edge.Variables.Add(variable);
            edge.Evidence.AddRange(evidence);
        }

        foreach (var compose in map.Services)
        {
            if (map.ElementOf(compose) is not { } from || !map.IsApp(from))
                continue;
            foreach (var variable in compose.Environment)
            {
                if (AppNamedBy(map, compose.Project, variable.Name) is { } to && to != from)
                    Add(from, to, variable.Name, NameConfidence, [HttpCall.At(variable.Path, variable.Line, variable.Line)]);
            }
        }

        foreach (var client in facts.Where(f => f.Kind == FactKind.Dependency && f.Attributes.GetValueOrDefault("role") == "http-client"))
        {
            if (client.Attributes.GetValueOrDefault("service") is not { } from || !byKey.TryGetValue(from, out var app) || app.Kind != FactKind.Service
                || client.Attributes.GetValueOrDefault("baseAddressKey") is not { } key)
                continue;
            var targets = map.Projects.Select(project => AppNamedBy(map, project, key)).Where(t => t is not null && t != from).Distinct().ToList();
            if (targets.Count != 1)
                continue;
            var evidence = Cite(HttpCall, client.Evidence).ToList();
            // The compose variable that sets the key, when a compose service of the app sets it.
            foreach (var compose in map.ComposeOf(from))
            {
                foreach (var variable in compose.Environment.Where(v => Normal(v.Name) == Normal(key)))
                    evidence.Add(HttpCall.At(variable.Path, variable.Line, variable.Line));
            }
            Add(from, targets[0]!, key, ClientConfidence, evidence);
        }
    }

    /// <summary>
    /// The app element of the compose service of <paramref name="project"/> that a variable or key
    /// names: the longest service name whose words begin the name (after a group word such as
    /// <c>Services</c>) and are followed only by an address word. Null when none or two fit.
    /// </summary>
    private static string? AppNamedBy(ComposeMap map, string project, string name)
    {
        var words = Words(name);
        if (words.Length > 0 && GroupWords.Contains(words[0]))
            words = words[1..];
        string? found = null;
        var best = 0;
        var tie = false;
        foreach (var service in map.ServicesOf(project))
        {
            var own = Words(service.Name);
            if (own.Length == 0 || own.Length >= words.Length || !words.AsSpan(0, own.Length).SequenceEqual(own))
                continue;
            var rest = words[own.Length..];
            if (!AddressWords.Any(address => address.AsSpan().SequenceEqual(rest)))
                continue;
            if (own.Length > best)
            {
                (best, tie) = (own.Length, false);
                found = map.ElementOf(service) is { } element && map.IsApp(element) ? element : null;
            }
            else if (own.Length == best)
                tie = true;
        }
        return tie ? null : found;
    }

    private static string[] Words(string name) =>
        [.. name.Split(['_', ':', '.', '-'], StringSplitOptions.RemoveEmptyEntries).Select(w => w.ToLowerInvariant())];

    private static List<SourceEvidence> Bucket(Dictionary<string, List<SourceEvidence>> buckets, string key)
    {
        if (!buckets.TryGetValue(key, out var bucket))
            buckets[key] = bucket = [];
        return bucket;
    }

    /// <summary>Which element each compose service is.</summary>
    private sealed class ComposeMap
    {
        private readonly Dictionary<string, ExtractedFact> _byKey;
        private readonly List<(string Key, string Directory)> _apps = [];
        private readonly Dictionary<ComposeService, string?> _elements = [];

        public ComposeMap(IReadOnlyList<ComposeService> services, IReadOnlyList<ExtractedFact> facts, Dictionary<string, ExtractedFact> byKey)
        {
            _byKey = byKey;
            Services = services;
            foreach (var fact in facts.Where(f => f.Kind == FactKind.Service))
            {
                if (fact.Attributes.GetValueOrDefault("project") is { } project)
                    _apps.Add((fact.Key, project.LastIndexOf('/') is var slash and >= 0 ? project[..slash] : ""));
            }
            foreach (var service in services)
                _elements[service] = Resolve(service);
        }

        public IReadOnlyList<ComposeService> Services { get; }
        public IEnumerable<string> Projects => Services.Select(s => s.Project).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);

        public IEnumerable<ComposeService> ServicesOf(string project) => Services.Where(s => s.Project == project);

        public ComposeService? Find(string project, string name) => Services.FirstOrDefault(s => s.Project == project && s.Name == name);

        public IEnumerable<ComposeService> StoresOf(string project) => ServicesOf(project).Where(s => s.IsStore && _byKey.ContainsKey(s.FactKey));

        /// <summary>The store named <paramref name="name"/> in the project, of <paramref name="kind"/> when one is given.</summary>
        public ComposeService? Store(string project, string name, string? kind) =>
            Find(project, name) is { IsStore: true } store && _byKey.TryGetValue(store.FactKey, out var fact)
            && (kind is null || fact.Attributes.GetValueOrDefault("kind") == kind)
                ? store
                : null;

        public string? ElementOf(ComposeService service) => _elements.GetValueOrDefault(service);

        public bool IsApp(string key) => _byKey.TryGetValue(key, out var fact) && fact.Kind == FactKind.Service;

        /// <summary>The compose services that build the app.</summary>
        public IEnumerable<ComposeService> ComposeOf(string app) => Services.Where(s => ElementOf(s) == app);

        private string? Resolve(ComposeService service)
        {
            if (service.IsStore)
                return _byKey.ContainsKey(service.FactKey) ? service.FactKey : null;
            if (service.IsBroker)
                return null;
            var project = service.Project == "." ? "" : service.Project;
            if (!service.HasBuild)
                return One(_apps.Where(a => (a.Directory.LastIndexOf('/') is var slash and >= 0 ? a.Directory[(slash + 1)..] : a.Directory) == service.Name));
            if (service.Build is null || Join(project, service.Build) is not { } context)
                return null;
            if (service.Dockerfile is { } dockerfile && Join(context, dockerfile) is { } path
                && One(_apps.Where(a => a.Directory == (path.LastIndexOf('/') is var slash and >= 0 ? path[..slash] : ""))) is { } byDockerfile)
                return byDockerfile;
            return One(_apps.Where(a => a.Directory == context))
                ?? One(_apps.Where(a => context.Length == 0 || a.Directory.StartsWith(context + "/", StringComparison.Ordinal)));
        }

        private static string? One(IEnumerable<(string Key, string Directory)> apps)
        {
            var found = apps.Select(a => a.Key).Distinct(StringComparer.Ordinal).Take(2).ToList();
            return found.Count == 1 ? found[0] : null;
        }

        /// <summary>A relative path joined to a directory, or null when it leaves the repository or holds a variable.</summary>
        private static string? Join(string directory, string relative)
        {
            if (relative.Contains('$') || relative.StartsWith('/') || relative.StartsWith('~'))
                return null;
            var parts = directory.Length == 0 ? new List<string>() : [.. directory.Split('/')];
            foreach (var segment in relative.Split('/'))
            {
                if (segment is "" or ".")
                    continue;
                if (segment == "..")
                {
                    if (parts.Count == 0)
                        return null;
                    parts.RemoveAt(parts.Count - 1);
                    continue;
                }
                parts.Add(segment);
            }
            return string.Join('/', parts);
        }
    }
}
