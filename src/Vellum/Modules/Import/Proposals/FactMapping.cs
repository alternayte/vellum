using Vellum.Modules.Import.Extraction;
using Vellum.Modules.Import.Scans;
using Vellum.Shared;

namespace Vellum.Modules.Import.Proposals;

public static class EntityKinds
{
    public const string Element = "element";
    public const string Operation = "operation";
    public const string Channel = "channel";
    public const string Subscription = "subscription";
    public const string Binding = "binding";
    public const string Message = "message";
    public const string Schema = "schema";
    public const string Relationship = "relationship";

    public static readonly string[] All = [Element, Operation, Channel, Subscription, Binding, Message, Schema, Relationship];
}

/// <summary>The names of the values a proposal can hold. They are the names of the model's own fields.</summary>
internal static class Field
{
    public const string Kind = "kind";
    public const string Name = "name";
    public const string Technology = "technology";
    public const string Transport = "transport";
    public const string Content = "content";
    public const string ParentId = "parentId";
    public const string OwnerId = "ownerId";
    public const string ChannelId = "channelId";
    public const string ConsumerId = "consumerId";
    public const string DeadLetterChannelId = "deadLetterChannelId";
    public const string SourceChannelId = "sourceChannelId";
    public const string TargetChannelId = "targetChannelId";
    public const string RoutingKey = "routingKey";
    public const string ProducerId = "producerId";
    public const string ConsumerIds = "consumerIds";
    public const string SchemaId = "schemaId";
    public const string FromId = "fromId";
    public const string ToId = "toId";
    public const string Label = "label";
    /// <summary>Set on an element when it is added. Not compared: a description is the intent of a person.</summary>
    public const string Description = "description";
    /// <summary>Set on an element when it is added, comma-separated. Not compared.</summary>
    public const string Tags = "tags";

    /// <summary>The fields that hold the ID of another entity.</summary>
    public static readonly string[] References =
        [ParentId, OwnerId, ChannelId, ConsumerId, DeadLetterChannelId, SourceChannelId, TargetChannelId, ProducerId, SchemaId, FromId, ToId];
}

/// <summary>A value of a candidate that is another fact. <paramref name="FactKey"/> is null when the source names none.</summary>
internal sealed record Reference(string Field, string? FactKey, bool RequiredToAdd);

/// <summary>
/// What a fact would be in the model, before the model is looked at: the entity kind, the
/// values the source establishes and the facts it points at.
/// </summary>
/// <param name="FactKey">The key of the fact, or <see cref="FactMapping.RepositoryKey"/> for the repository itself.</param>
/// <param name="Fields">Scalar values by model field name. A null value means the source does not establish it.</param>
/// <param name="ConsumerKeys">For a message: the services whose subscriptions name it.</param>
/// <param name="Observed">The fact's attributes that no model field holds. They stay visible on the proposal and the link.</param>
/// <param name="NotAddable">Why the candidate cannot become a new entity, or null. A linked entity is still compared.</param>
/// <param name="MatchRoute">For an operation: the method and route that a hand-made operation is matched on.</param>
internal sealed record Candidate(
    string FactKey, string EntityKind, string Name, int Rank,
    SortedDictionary<string, string?> Fields,
    IReadOnlyList<Reference> References,
    IReadOnlyList<string> ConsumerKeys,
    SortedDictionary<string, string> Observed,
    IReadOnlyDictionary<string, string> Attributes,
    IReadOnlyList<EvidenceRef> Evidence,
    string? NotAddable,
    string? MatchRoute = null,
    string FactStatus = TruthLabels.ObservedInSource);

/// <summary>A fact of the snapshot with what the mapping decided for it.</summary>
internal sealed record MappedFact(ExtractedFact Fact, FactDisposition Disposition, string? Reason);

/// <param name="Refused">Facts that are not kept at all, each with the path of its first evidence. See <see cref="FactMapping.Refusal"/>.</param>
internal sealed record MappingResult(
    IReadOnlyList<Candidate> Candidates, IReadOnlyList<MappedFact> Facts, IReadOnlyList<ExtractedFact> Refused);

/// <summary>
/// The rules that say which extracted fact is which model entity. They are exact: a fact
/// maps when the source states everything the entity needs, and stays evidence when it does
/// not. Nothing is guessed, and no fact is joined to another by a similar name.
/// <list type="table">
/// <item><term>Service</term><description>An element of kind <c>app</c> inside the repository's system.</description></item>
/// <item><term>DataStore</term><description>An element of kind <c>store</c> inside the repository's system.</description></item>
/// <item><term>(the repository)</term><description>An element of kind <c>system</c>. The model puts an app or a store inside a system, and the repository is the one grouping the source establishes.</description></item>
/// <item><term>Operation</term><description>An operation of its service. An operation that only a contract file declares has no owner and stays evidence.</description></item>
/// <item><term>Channel</term><description>A channel of kind topic, queue or exchange.</description></item>
/// <item><term>Subscription</term><description>A subscription of its service to its queue, with the queue's dead-letter exchange.</description></item>
/// <item><term>Subscription with an <c>exchange</c> attribute</term><description>A binding from that exchange to the queue the fact names, with the routing key when the source gives one that is not empty. Both channels must be channel facts of this scan; a binding that names a channel the scan did not resolve stays evidence. Only a statement of the source is a binding: a queue that a framework binds by convention has no binding fact.</description></item>
/// <item><term>Message</term><description>A message. Its producer is the one service that a Publication fact names; its consumers are the services whose subscriptions name the message; its schema is its payload schema. A consumer that only a queue binding connects to the message is not added: that is a chain of two facts, not a statement of the source.</description></item>
/// <item><term>Publication</term><description>Not an entity: it gives a message its producer. The model's relationships join two elements and a channel is not an element, so there is no producer-to-channel edge to write. A pattern fact and a contract-only publication stay evidence.</description></item>
/// <item><term>Schema</term><description>A schema, when the fact holds a JSON Schema body. A contract document as a whole stays evidence.</description></item>
/// <item><term>Code and contract</term><description>One thing has one entity. A message that a contract file declares (it has a <c>contract</c> attribute) is the code message with exactly the same name (ordinal) that a Publication names, when there is exactly one: the contract fact is then folded into that message's proposal, with its evidence, its payload schema and its attributes under <c>contract.*</c>. A channel that a contract file declares is the code channel of the same kind whose name is exactly the contract's <c>address</c>, in the same way. With no such code fact the contract fact maps by itself; with several it stays evidence. Names that differ in case or form (a Go struct <c>orderCreated</c>) are other facts.</description></item>
/// <item><term>Every other kind</term><description>Evidence only: the model has no home for it yet.</description></item>
/// </list>
/// </summary>
internal static class FactMapping
{
    public const string NoModelHome = "no-model-home";
    /// <summary>The longest fact key a snapshot keeps. A fact with a longer key is refused.</summary>
    public const int MaxFactKey = 1000;
    /// <summary>The longest fact name a snapshot keeps.</summary>
    public const int MaxFactName = 1000;
    /// <summary>The longest attribute value a snapshot keeps. A schema body is the largest attribute.</summary>
    public const int MaxAttributeValue = 200_000;
    private const int EvidenceCap = 50;

    /// <summary>
    /// The in-memory key of the candidate that stands for the repository. It starts with a
    /// control character, and <see cref="Refusal"/> refuses every fact whose key holds one,
    /// so no fact can have this key. It is never stored: a row of the repository has the
    /// subject <see cref="Subject.Repository"/> and an empty fact key.
    /// </summary>
    public const string RepositoryKey = "\u0001repository";

    public static Subject SubjectOf(string key) => key == RepositoryKey ? Subject.Repository : Subject.Fact;

    /// <summary>The fact key as a row holds it.</summary>
    public static string StoredKey(string key) => key == RepositoryKey ? "" : key;

    /// <summary>The in-memory key of a stored row.</summary>
    public static string KeyOf(Subject subject, string factKey) => subject == Subject.Repository ? RepositoryKey : factKey;

    /// <summary>
    /// The external key of a source binding for an in-memory key. The repository itself has
    /// <c>@repository</c>: no extractor makes a key that starts with <c>@</c>.
    /// </summary>
    public static string ExternalKey(string key) => key == RepositoryKey ? "@repository" : key;

    /// <summary>
    /// Why a fact is not kept, or null. Text from a repository reaches <c>jsonb</c> columns,
    /// index keys and model names, so <see cref="TextRule"/> applies to it as to any other
    /// input: no NUL and no other control character in a key, a name, an attribute name or a
    /// path, tab and line breaks only inside an attribute value, and a bounded length.
    /// </summary>
    public static string? Refusal(ExtractedFact fact)
    {
        if (fact.Key.Length == 0 || TextRule.Line("key", fact.Key, MaxFactKey) is not null)
            return "key";
        if (TextRule.Line("name", fact.Name, MaxFactName) is not null)
            return "name";
        foreach (var (attribute, value) in fact.Attributes)
        {
            if (TextRule.Line("attribute", attribute, TextRule.MaxName) is not null
                || TextRule.Lines("attribute", value, MaxAttributeValue) is not null)
                return "attribute";
        }
        if (fact.Evidence.Count == 0)
            return "evidence";
        foreach (var evidence in fact.Evidence)
        {
            if (TextRule.Line("path", evidence.Path, ScanFileEntity.MaxPath) is not null
                || TextRule.Line("rule", evidence.Rule, TextRule.MaxName) is not null)
                return "evidence";
        }
        return null;
    }

    /// <param name="repository">For example <c>github.com/acme/orders</c>.</param>
    public static MappingResult Map(string repository, IReadOnlyList<ExtractedFact> input)
    {
        var refused = new List<ExtractedFact>();
        var facts = new List<ExtractedFact>(input.Count);
        foreach (var fact in input)
        {
            if (Refusal(fact) is null)
                facts.Add(fact);
            else
                refused.Add(fact);
        }

        var byKey = new Dictionary<string, ExtractedFact>(StringComparer.Ordinal);
        foreach (var fact in facts)
            byKey.TryAdd(fact.Key, fact);

        var candidates = new List<Candidate>();
        var mapped = new List<MappedFact>();
        var inSystem = new List<ExtractedFact>();

        // A publication is read before the messages: it decides whether a message has a producer.
        var producers = facts
            .Where(f => f.Kind == FactKind.Publication && !f.Attributes.ContainsKey("pattern")
                && f.Attributes.ContainsKey("service") && f.Attributes.ContainsKey("message"))
            .ToLookup(f => f.Attributes["message"], StringComparer.Ordinal);
        var consumers = facts
            .Where(f => IsConsumerSubscription(f) && f.Attributes.ContainsKey("message"))
            .ToLookup(f => f.Attributes["message"], StringComparer.Ordinal);

        // A contract file and the code can declare the same message or the same channel.
        // The extractors keep them as two facts. Here they become one entity, by an exact rule.
        var messageOf = facts
            .Where(f => f.Kind == FactKind.Message && !IsContractDeclared(f) && producers[f.Key].Any())
            .ToLookup(f => f.Name, StringComparer.Ordinal);
        var channelOf = facts
            .Where(f => f.Kind == FactKind.Channel && !IsContractDeclared(f) && ChannelKind(f.Attributes.GetValueOrDefault("kind")) is not null)
            .ToLookup(f => (f.Attributes["kind"], f.Name));
        var contractsOf = new Dictionary<string, List<ExtractedFact>>(StringComparer.Ordinal);
        var severalOwners = new HashSet<string>(StringComparer.Ordinal);
        foreach (var fact in facts.Where(IsContractDeclared))
        {
            var owners = fact.Kind switch
            {
                FactKind.Message => messageOf[fact.Name].ToList(),
                FactKind.Channel when fact.Attributes.TryGetValue("address", out var address) && fact.Attributes.TryGetValue("kind", out var kind)
                    => channelOf[(kind, address)].ToList(),
                _ => [],
            };
            if (owners.Count == 1)
            {
                if (!contractsOf.TryGetValue(owners[0].Key, out var contracts))
                    contractsOf[owners[0].Key] = contracts = [];
                contracts.Add(fact);
            }
            else if (owners.Count > 1)
                severalOwners.Add(fact.Key);
        }
        var foldedContracts = contractsOf.Values.SelectMany(c => c).Select(f => f.Key).ToHashSet(StringComparer.Ordinal);

        foreach (var fact in facts)
        {
            if (foldedContracts.Contains(fact.Key) && fact.Kind is FactKind.Message or FactKind.Channel)
            {
                mapped.Add(new MappedFact(fact, FactDisposition.Folded, fact.Kind == FactKind.Message ? "contract-of-message" : "contract-of-channel"));
                continue;
            }
            if (severalOwners.Contains(fact.Key) && fact.Kind is FactKind.Message or FactKind.Channel)
            {
                Evidence(fact, "several-code-declarations");
                continue;
            }

            switch (fact.Kind)
            {
                case FactKind.Service:
                    inSystem.Add(fact);
                    Add(fact, EntityKinds.Element, fact.Name, 1,
                        Fields((Field.Kind, "app"), (Field.Name, fact.Name), (Field.Technology, fact.Attributes.GetValueOrDefault("language"))),
                        [new Reference(Field.ParentId, RepositoryKey, RequiredToAdd: true)], consumed: ["language"]);
                    break;

                case FactKind.DataStore:
                    inSystem.Add(fact);
                    Add(fact, EntityKinds.Element, fact.Name, 1,
                        Fields((Field.Kind, "store"), (Field.Name, fact.Name), (Field.Technology, fact.Attributes.GetValueOrDefault("kind"))),
                        [new Reference(Field.ParentId, RepositoryKey, RequiredToAdd: true)], consumed: ["kind"]);
                    break;

                case FactKind.Operation:
                    if (!fact.Attributes.TryGetValue("service", out var owner))
                        Evidence(fact, "no-owning-service");
                    else if (Transport(fact.Attributes.GetValueOrDefault("transport")) is not { } transport)
                        Evidence(fact, "unsupported-transport");
                    else
                        Add(fact, EntityKinds.Operation, fact.Name, 4,
                            Fields((Field.Name, fact.Name), (Field.Transport, transport)),
                            [new Reference(Field.OwnerId, owner, RequiredToAdd: true)], consumed: ["service", "transport"],
                            matchRoute: Route(fact));
                    break;

                case FactKind.Channel:
                    if (ChannelKind(fact.Attributes.GetValueOrDefault("kind")) is not { } channelKind)
                        Evidence(fact, "unsupported-channel-kind");
                    else
                    {
                        var declared = contractsOf.GetValueOrDefault(fact.Key) ?? [];
                        Add(fact, EntityKinds.Channel, fact.Attributes.GetValueOrDefault("address") ?? fact.Name, 2,
                            Fields((Field.Name, fact.Attributes.GetValueOrDefault("address") ?? fact.Name), (Field.Kind, channelKind)),
                            [], consumed: ["address", "kind"],
                            evidence: fact.Evidence.Concat(declared.SelectMany(c => c.Evidence)),
                            attributes: WithContracts(fact, declared, "address", "kind"));
                    }
                    break;

                case FactKind.Subscription:
                    if (fact.Attributes.TryGetValue("exchange", out var exchange))
                    {
                        if (!fact.Attributes.TryGetValue("queue", out var bound) || !IsChannel(exchange) || !IsChannel(bound))
                            Evidence(fact, "binding-channel-not-resolved");
                        else
                            Add(fact, EntityKinds.Binding, fact.Name, 5,
                                // An empty key is what a fanout binding passes; it states no key.
                                Fields((Field.RoutingKey, fact.Attributes.GetValueOrDefault("routingKey") is { Length: > 0 } key ? key : null)),
                            [
                                new Reference(Field.SourceChannelId, exchange, RequiredToAdd: true),
                                new Reference(Field.TargetChannelId, bound, RequiredToAdd: true),
                            ], consumed: ["exchange", "queue", "routingKey"]);
                    }
                    else if (!IsConsumerSubscription(fact))
                        Evidence(fact, "no-channel-or-consumer");
                    else
                    {
                        var queue = fact.Attributes["queue"];
                        var deadLetter = byKey.TryGetValue(queue, out var queueFact)
                            ? queueFact.Attributes.GetValueOrDefault("deadLetterExchange")
                            : null;
                        Add(fact, EntityKinds.Subscription, fact.Name, 5, Fields(),
                        [
                            new Reference(Field.ChannelId, queue, RequiredToAdd: true),
                            new Reference(Field.ConsumerId, fact.Attributes["service"], RequiredToAdd: true),
                            new Reference(Field.DeadLetterChannelId, deadLetter, RequiredToAdd: false),
                        ], consumed: ["queue", "service"]);
                    }
                    break;

                case FactKind.Publication:
                    if (fact.Attributes.ContainsKey("pattern"))
                        Evidence(fact, "pattern-has-no-model-home");
                    else if (fact.Attributes.ContainsKey("service") && fact.Attributes.ContainsKey("message"))
                        mapped.Add(new MappedFact(fact, FactDisposition.Folded, "message-producer"));
                    else
                        Evidence(fact, "no-producing-service");
                    break;

                case FactKind.Message:
                {
                    var publications = producers[fact.Key].ToList();
                    var services = publications.Select(p => p.Attributes["service"]).Distinct(StringComparer.Ordinal).ToList();
                    var subscriptions = consumers[fact.Key].ToList();
                    // The payload schema is the message's own, else the one schema that its
                    // contract declarations name. Two contracts with two schemas name none.
                    var declared = contractsOf.GetValueOrDefault(fact.Key) ?? [];
                    var schema = fact.Attributes.GetValueOrDefault("payloadSchema");
                    if (schema is null)
                    {
                        var named = declared.Select(c => c.Attributes.GetValueOrDefault("payloadSchema"))
                            .Where(s => s is not null).Distinct(StringComparer.Ordinal).ToList();
                        schema = named.Count == 1 ? named[0] : null;
                    }
                    if (schema is not null && !(byKey.TryGetValue(schema, out var schemaFact) && HasBody(schemaFact)))
                        schema = null;

                    // The model gives a message one producer. With none or several the source
                    // does not say which, so the message cannot be added.
                    var notAddable = services.Count switch
                    {
                        1 => null,
                        0 => "no-producer-established",
                        _ => "several-producers",
                    };
                    var evidence = fact.Evidence
                        .Concat(declared.SelectMany(c => c.Evidence))
                        .Concat(services.Count == 1 ? publications.SelectMany(p => p.Evidence) : [])
                        .Concat(subscriptions.SelectMany(s => s.Evidence));
                    Add(fact, EntityKinds.Message, fact.Name, 6, Fields((Field.Name, fact.Name)),
                    [
                        new Reference(Field.ProducerId, services.Count == 1 ? services[0] : null, RequiredToAdd: true),
                        new Reference(Field.SchemaId, schema, RequiredToAdd: false),
                    ], consumed: ["payloadSchema"], evidence: evidence, notAddable: notAddable,
                        attributes: WithContracts(fact, declared, "payloadSchema", "name"),
                        consumerKeys: subscriptions.Select(s => s.Attributes["service"]).Distinct(StringComparer.Ordinal)
                            .OrderBy(k => k, StringComparer.Ordinal).ToList());
                    break;
                }

                case FactKind.Schema:
                    if (HasBody(fact))
                        Add(fact, EntityKinds.Schema, fact.Name, 3,
                            Fields((Field.Name, fact.Name), (Field.Content, fact.Attributes["schema"])), [], consumed: ["schema"]);
                    else
                        Evidence(fact, "contract-document-has-no-model-home");
                    break;

                case FactKind.Element:
                {
                    var kind = fact.Attributes.GetValueOrDefault("elementKind");
                    var rank = kind switch { "actor" or "system" => 1, "app" or "store" => 2, "component" => 3, _ => 0 };
                    if (rank == 0)
                    {
                        Evidence(fact, "element-kind-has-no-model-home");
                        break;
                    }
                    var parent = fact.Attributes.GetValueOrDefault("parent");
                    var needsParent = rank > 1;
                    Add(fact, EntityKinds.Element, fact.Name, rank,
                        Fields((Field.Kind, kind), (Field.Name, fact.Name), (Field.Technology, fact.Attributes.GetValueOrDefault("technology")),
                            (Field.Description, fact.Attributes.GetValueOrDefault("description")), (Field.Tags, fact.Attributes.GetValueOrDefault("tags"))),
                        needsParent ? [new Reference(Field.ParentId, parent, RequiredToAdd: true)] : [],
                        consumed: ["elementKind", "parent", "technology", "description", "tags"],
                        notAddable: needsParent && parent is null ? "no-parent-declared" : null);
                    break;
                }

                case FactKind.Relationship:
                {
                    if (!fact.Attributes.TryGetValue("from", out var from) || !fact.Attributes.TryGetValue("to", out var to))
                    {
                        Evidence(fact, "relationship-ends-not-declared");
                        break;
                    }
                    var label = fact.Attributes.GetValueOrDefault("label");
                    var shown = $"{byKey.GetValueOrDefault(from)?.Name ?? from} → {byKey.GetValueOrDefault(to)?.Name ?? to}" + (label is null ? "" : $": {label}");
                    Add(fact, EntityKinds.Relationship, shown, 7,
                        Fields((Field.Label, label), (Field.Technology, fact.Attributes.GetValueOrDefault("technology")),
                            (Field.Kind, fact.Attributes.GetValueOrDefault("relationshipKind") == "behavioural" ? "behavioural" : "structural")),
                        [new Reference(Field.FromId, from, RequiredToAdd: true), new Reference(Field.ToId, to, RequiredToAdd: true)],
                        consumed: ["from", "to", "label", "technology", "relationshipKind"]);
                    break;
                }

                case FactKind.Decision:
                    // An ADR becomes a proposed decision of the project's review, not a model entity.
                    Evidence(fact, "proposed-as-decision");
                    break;

                default:
                    Evidence(fact, NoModelHome);
                    break;
            }
        }

        if (inSystem.Count > 0)
        {
            // The repository is not an extracted fact. What shows that it holds these parts
            // is the first evidence of each of them.
            var name = repository[(repository.LastIndexOf('/') + 1)..];
            candidates.Add(new Candidate(
                RepositoryKey, EntityKinds.Element, name, 0,
                Fields((Field.Kind, "system"), (Field.Name, name)), [], [],
                new SortedDictionary<string, string>(StringComparer.Ordinal),
                new Dictionary<string, string>(),
                [.. inSystem.Select(f => Ref(f.Evidence[0])).Take(EvidenceCap)], NotAddable: null));
        }

        return new MappingResult(
            [.. candidates.OrderBy(c => c.Rank).ThenBy(c => c.FactKey, StringComparer.Ordinal)], mapped, refused);

        void Add(
            ExtractedFact fact, string entityKind, string name, int rank,
            SortedDictionary<string, string?> fields, IReadOnlyList<Reference> references, string[] consumed,
            IEnumerable<SourceEvidence>? evidence = null, string? notAddable = null, IReadOnlyList<string>? consumerKeys = null,
            string? matchRoute = null, IReadOnlyDictionary<string, string>? attributes = null)
        {
            attributes ??= fact.Attributes;
            var observed = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var (attribute, value) in attributes)
            {
                if (!consumed.Contains(attribute))
                    observed[attribute] = value;
            }

            var cited = (evidence ?? fact.Evidence).ToList();
            candidates.Add(new Candidate(
                fact.Key, entityKind, name, rank, fields, references, consumerKeys ?? [], observed, attributes,
                [.. cited.Select(Ref).Distinct().Take(EvidenceCap)], notAddable, matchRoute,
                TruthLabels.Of(cited.Select(e => e.Rule))));
            mapped.Add(new MappedFact(fact, FactDisposition.Mapped, null));
        }

        void Evidence(ExtractedFact fact, string reason) =>
            mapped.Add(new MappedFact(fact, FactDisposition.EvidenceOnly, reason));

        // A channel fact of this scan that maps to a channel of the model.
        bool IsChannel(string key) =>
            byKey.TryGetValue(key, out var channel) && channel.Kind == FactKind.Channel
            && ChannelKind(channel.Attributes.GetValueOrDefault("kind")) is not null;
    }

    /// <summary>True for a message or a channel that a contract file declares (AsyncAPI), as opposed to one the code declares.</summary>
    private static bool IsContractDeclared(ExtractedFact fact) =>
        fact.Kind is FactKind.Message or FactKind.Channel && fact.Attributes.ContainsKey("contract");

    /// <summary>
    /// The attributes of a code fact with those of the contract declarations of the same
    /// thing next to them, each under <c>contract.&lt;name&gt;</c>. Two declarations that
    /// disagree on one attribute show the first, in key order; the others stay readable as
    /// evidence. No value of the code is replaced by a value of a contract.
    /// </summary>
    private static IReadOnlyDictionary<string, string> WithContracts(
        ExtractedFact fact, List<ExtractedFact> declared, params string[] structural)
    {
        if (declared.Count == 0)
            return fact.Attributes;
        var attributes = new Dictionary<string, string>(fact.Attributes, StringComparer.Ordinal);
        foreach (var contract in declared.OrderBy(c => c.Key, StringComparer.Ordinal))
        {
            foreach (var (attribute, value) in contract.Attributes)
            {
                if (!structural.Contains(attribute))
                    attributes.TryAdd("contract." + attribute, value);
            }
        }
        return attributes;
    }

    public static EvidenceRef Ref(SourceEvidence evidence) =>
        new(evidence.Path, evidence.StartLine, evidence.EndLine, evidence.Rule, evidence.RuleVersion);

    /// <summary>A service that reads a queue. A binding of a queue to an exchange names an exchange and is not one.</summary>
    private static bool IsConsumerSubscription(ExtractedFact fact) =>
        fact.Kind == FactKind.Subscription && !fact.Attributes.ContainsKey("exchange")
        && fact.Attributes.ContainsKey("queue") && fact.Attributes.ContainsKey("service");

    /// <summary>
    /// The method and route of an operation fact, from its attributes: the route template
    /// when the extractor gives one, else the path. A template that already starts with the
    /// method is taken as it is. Null when the fact has neither, and the name is then used.
    /// </summary>
    private static string? Route(ExtractedFact fact)
    {
        var route = fact.Attributes.GetValueOrDefault("routeTemplate") ?? fact.Attributes.GetValueOrDefault("path");
        if (route is null)
            return null;
        return route.Contains(' ') || !fact.Attributes.TryGetValue("method", out var method) ? route : $"{method} {route}";
    }

    private static bool HasBody(ExtractedFact fact) =>
        fact.Kind == FactKind.Schema && fact.Attributes.ContainsKey("schema");

    private static string? Transport(string? value) => value switch
    {
        "http" => "http",
        "grpc" => "grpc",
        "command" => "command",
        _ => null,
    };

    private static string? ChannelKind(string? value) => value switch
    {
        "topic" => "topic",
        "queue" => "queue",
        "exchange" => "exchange",
        _ => null,
    };

    private static SortedDictionary<string, string?> Fields(params (string Name, string? Value)[] values)
    {
        var fields = new SortedDictionary<string, string?>(StringComparer.Ordinal);
        foreach (var (name, value) in values)
            fields[name] = value;
        return fields;
    }
}
