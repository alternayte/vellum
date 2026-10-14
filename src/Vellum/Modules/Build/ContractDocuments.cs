using System.Text.Json;
using System.Text.RegularExpressions;
using Vellum.Modules.Modelling.Export;
using Vellum.Modules.Modelling;
using Vellum.Modules.Promises;
using Vellum.Modules.Review.Decisions;
using Vellum.Modules.Trace.Engine;

namespace Vellum.Modules.Build;

/// <summary>An HTTP operation whose name is a method and a path, such as <c>POST /orders</c>.</summary>
internal sealed record Route(OperationState Operation, string Method, string Path);

/// <summary>
/// The machine-readable contract files of a bundle: <c>architecture.json</c>, one OpenAPI
/// document per service that owns a routed HTTP operation, and one AsyncAPI document for the
/// project's channels. They are generated from the pinned model and hold nothing else.
/// </summary>
internal static partial class ContractDocuments
{
    private static readonly string[] Methods = ["GET", "PUT", "POST", "DELETE", "OPTIONS", "HEAD", "PATCH", "TRACE"];

    [GeneratedRegex(@"\A(GET|PUT|POST|DELETE|OPTIONS|HEAD|PATCH|TRACE)[ \t]+(/[^\s]*)\z", RegexOptions.IgnoreCase)]
    private static partial Regex RouteName();

    /// <summary>
    /// The method in upper case and the path of an operation name such as <c>POST /orders</c>,
    /// or null when the name is not a method and a path. The OpenAPI documents and the k6
    /// scripts read a route in this one way.
    /// </summary>
    public static (string Method, string Path)? RouteOf(string name) =>
        RouteName().Match(name) is { Success: true } match
            ? (match.Groups[1].Value.ToUpperInvariant(), match.Groups[2].Value)
            : null;

    /// <summary>
    /// The routed HTTP operations of each owner. A routed operation is an <c>http</c>
    /// operation named as a method and a path. When two operations of one owner have the same
    /// method and path, the one with the lower ID is routed and the other is not.
    /// </summary>
    public static IReadOnlyDictionary<Guid, IReadOnlyList<Route>> Routes(BuildView view) =>
        view.Operations
            .Where(o => o.Transport == Transport.Http)
            .Select(o => (Operation: o, Route: RouteOf(o.Name)))
            .Where(x => x.Route is not null)
            .Select(x => new Route(x.Operation, x.Route!.Value.Method, x.Route.Value.Path))
            .GroupBy(r => r.Operation.OwnerId.Value)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<Route>)[.. g.GroupBy(r => (r.Method, r.Path))
                    .Select(same => same.OrderBy(r => r.Operation.Id.Value).First())
                    .OrderBy(r => r.Path, StringComparer.Ordinal).ThenBy(r => Array.IndexOf(Methods, r.Method))]);

    public static string OpenApiPath(Guid ownerId) => $"openapi/{ownerId}.yaml";

    public const string AsyncApiPath = "asyncapi.yaml";

    // The keys of the AsyncAPI operations hold IDs with their dashes. A long run of letters and
    // digits reads as a credential to the import extractor, which then leaves the operation out.
    public static string ReceiveKey(Guid subscriptionId) => $"receive.{subscriptionId}";

    public static string SendKey(Guid publisherId, Guid channelId) => $"send.{publisherId}.{channelId}";

    /// <summary>
    /// The existing model export document, plus the contracts of the operations that have no
    /// OpenAPI document, and the promises: the binding ones, and apart from them the ones that
    /// were never approved and the retired ones.
    /// </summary>
    public static byte[] Architecture(BuildView view, IReadOnlyDictionary<Guid, IReadOnlyList<Route>> routes)
    {
        var model = view.Model;
        var routed = routes.Values.SelectMany(r => r).Select(r => r.Operation.Id).ToHashSet();
        var document = new
        {
            version = "1.0",
            project = new ProjectInfo(view.Input.ProjectName),
            elements = model.Elements.Values.OrderBy(e => e.Id.Value).Select(e => new ElementExport(
                e.Id, e.Kind.ToString().ToLowerInvariant(), e.Name, e.Description, e.Technology,
                e.Status.ToString().ToLowerInvariant(), e.ParentId, e.Tags, e.Icon,
                e.FactStatus.ToApiString(), ProvenanceExport.From(e.Provenance))),
            relationships = model.Relationships.Values.OrderBy(r => r.Id.Value).Select(r => new RelationshipExport(
                r.Id, r.FromId, r.ToId, r.Label, r.Technology, r.Kind.ToString().ToLowerInvariant(), r.LineShape,
                r.FactStatus.ToApiString(), ProvenanceExport.From(r.Provenance))),
            messages = model.Messages.Values.OrderBy(m => m.Id.Value).Select(m => new MessageExport(
                m.Id, m.Name, m.Description, m.ProducerId, [.. m.ConsumerIds.Select(c => c.Value)], m.SchemaId, m.Tags,
                m.FactStatus.ToApiString(), ProvenanceExport.From(m.Provenance))),
            schemas = view.Input.Schemas.OrderBy(s => s.Id).Select(s => new SchemaExport(s.Id, s.Name, s.Description, s.Content, s.Version)),
            operations = model.Operations.Values.OrderBy(o => o.Id.Value).Select(o => new OperationExport(
                o.Id, o.OwnerId, o.Name, o.Transport.ToString().ToLowerInvariant(), o.RequestSchemaId, o.ResponseSchemaId,
                o.TimeoutMs, new RetryExport(o.Retry.MaxAttempts, o.Retry.BackoffMs),
                o.FactStatus.ToApiString(), ProvenanceExport.From(o.Provenance))),
            channels = model.Channels.Values.OrderBy(c => c.Id.Value).Select(c => new ChannelExport(
                c.Id, c.Name, c.Kind.ToString().ToLowerInvariant(), c.FactStatus.ToApiString(), ProvenanceExport.From(c.Provenance))),
            subscriptions = model.Subscriptions.Values.OrderBy(s => s.Id.Value).Select(s => new SubscriptionExport(
                s.Id, s.ChannelId, s.ConsumerId, new RetryExport(s.Retry.MaxAttempts, s.Retry.BackoffMs), s.DeadLetterChannelId,
                s.FactStatus.ToApiString(), ProvenanceExport.From(s.Provenance))),
            bindings = model.Bindings.Values.OrderBy(b => b.Id.Value).Select(b => new BindingExport(
                b.Id, b.SourceChannelId, b.TargetChannelId, b.RoutingKey,
                b.FactStatus.ToApiString(), ProvenanceExport.From(b.Provenance))),
            // An operation that is not a routed HTTP operation has no OpenAPI document. Its whole contract is here.
            operationContracts = model.Operations.Values.Where(o => !routed.Contains(o.Id)).OrderBy(o => o.Id.Value).Select(o => new
            {
                id = o.Id.Value,
                ownerId = o.OwnerId.Value,
                name = o.Name,
                transport = o.Transport.ToString().ToLowerInvariant(),
                requestSchemaId = o.RequestSchemaId?.Value,
                responseSchemaId = o.ResponseSchemaId?.Value,
                timeoutMs = o.TimeoutMs,
                retry = new RetryExport(o.Retry.MaxAttempts, o.Retry.BackoffMs),
                truth = o.FactStatus.ToApiString(),
                openApi = (string?)null,
                noOpenApiBecause = NoOpenApiReason(o),
                calledBy = view.AllSteps.Where(s => s.Step is CallStep call && call.Operation == o.Id)
                    .Select(s => new { scenarioId = s.ScenarioId, stepId = s.Step.Id }),
            }),
            staleFacts = StaleFacts(model),
            promises = view.Requirements.Select(PromiseDocuments.Export),
            promisesNotApproved = view.Promises.Where(p => p.State == PromiseLifecycle.Draft).Select(PromiseDocuments.Export),
            promisesRetired = view.Promises.Where(p => p.State == PromiseLifecycle.Retired).Select(PromiseDocuments.Export),
        };
        // A state of the format before the review has no "decisions" member, so its file keeps its bytes.
        var json = System.Text.Json.JsonSerializer.SerializeToNode(document, BuildText.Json)!.AsObject();
        if (view.Reviewed)
            json["decisions"] = System.Text.Json.JsonSerializer.SerializeToNode(DecisionDocuments.InBundle(view).Select(d => DecisionDocuments.Export(view.Decisions, d)), BuildText.Json);
        return BuildText.CanonicalJson(System.Text.Json.JsonSerializer.SerializeToElement(json, BuildText.Json));
    }

    /// <summary>Why an operation has no OpenAPI document. OpenAPI describes HTTP operations that have a method and a path.</summary>
    public static string NoOpenApiReason(OperationState operation) => operation.Transport == Transport.Http
        ? "The name of this http operation is not a method and a path, so it has no place in an OpenAPI document."
        : $"The transport is {operation.Transport.ToString().ToLowerInvariant()}. OpenAPI describes HTTP operations only.";

    /// <summary>Every fact a scan marked stale, by ID. The export format of the model has no place for the mark.</summary>
    private static IEnumerable<object> StaleFacts(ModelState model) =>
        model.Elements.Values.Where(e => e.Stale is not null).Select(e => (Id: e.Id.Value, Kind: "element", e.Stale))
            .Concat(model.Relationships.Values.Where(r => r.Stale is not null).Select(r => (Id: r.Id.Value, Kind: "relationship", r.Stale)))
            .Concat(model.Messages.Values.Where(m => m.Stale is not null).Select(m => (Id: m.Id.Value, Kind: "message", m.Stale)))
            .Concat(model.Operations.Values.Where(o => o.Stale is not null).Select(o => (Id: o.Id.Value, Kind: "operation", o.Stale)))
            .Concat(model.Channels.Values.Where(c => c.Stale is not null).Select(c => (Id: c.Id.Value, Kind: "channel", c.Stale)))
            .Concat(model.Subscriptions.Values.Where(s => s.Stale is not null).Select(s => (Id: s.Id.Value, Kind: "subscription", s.Stale)))
            .Concat(model.Bindings.Values.Where(b => b.Stale is not null).Select(b => (Id: b.Id.Value, Kind: "binding", b.Stale)))
            .OrderBy(f => f.Id)
            .Select(f => (object)new { id = f.Id, kind = f.Kind, sinceRevision = f.Stale!.SinceRevision, reason = f.Stale.Reason });

    /// <summary>OpenAPI 3.1 for the routed HTTP operations of one service.</summary>
    public static byte[] OpenApi(BuildView view, Guid ownerId, IReadOnlyList<Route> routes)
    {
        var used = routes.SelectMany(r => new[] { r.Operation.RequestSchemaId, r.Operation.ResponseSchemaId })
            .OfType<SchemaId>().Select(id => id.Value).Where(view.SchemasById.ContainsKey).Distinct().ToList();
        var schemaKeys = BuildText.Keys(used.Select(id => (id, view.SchemasById[id].Name)));

        var paths = new OrderedDictionary<string, object?>();
        foreach (var byPath in routes.GroupBy(r => r.Path))
        {
            var item = new OrderedDictionary<string, object?>();
            foreach (var route in byPath)
            {
                var operation = route.Operation;
                var entry = BuildText.Map(
                    ("operationId", operation.Id.Value.ToString()),
                    ("summary", operation.Name),
                    ("x-vellum-truth", operation.FactStatus.ToApiString()),
                    ("x-vellum-timeout-ms", operation.TimeoutMs),
                    ("x-vellum-retry", BuildText.Map(("maxAttempts", operation.Retry.MaxAttempts), ("backoffMs", operation.Retry.BackoffMs))));
                if (Reference(operation.RequestSchemaId) is { } request)
                    entry["requestBody"] = BuildText.Map(("required", true), ("content", Content(request)));
                var response = BuildText.Map(("description", "The declared response."));
                if (Reference(operation.ResponseSchemaId) is { } responseSchema)
                    response["content"] = Content(responseSchema);
                // The model declares one response contract and no status code, and the scenario
                // format has no status number either. What the scenarios show is whether a call
                // is answered or fails. So the document carries the class 2XX when a playback
                // shows the operation answering, with the steps that show it, and `default`
                // for every other answer, with the steps whose playback shows a failure. No
                // status number is invented.
                var responses = new OrderedDictionary<string, object?>();
                var answered = ShownBy(view, operation, TraceEventKind.ResponseReceived);
                if (answered.Count > 0)
                {
                    var success = BuildText.Map(("description", "The operation answers. Scenario playbacks show it; they state no status number."));
                    if (Reference(operation.ResponseSchemaId) is { } successSchema)
                        success["content"] = Content(successSchema);
                    success["x-vellum-shown-by"] = answered;
                    responses["2XX"] = success;
                }
                var failing = ShownBy(view, operation, TraceEventKind.RequestFailed, TraceEventKind.RequestTimedOut);
                if (failing.Count > 0)
                    response["x-vellum-failure-shown-by"] = failing;
                responses["default"] = response;
                entry["responses"] = responses;
                item[route.Method.ToLowerInvariant()] = entry;
            }
            paths[byPath.Key] = item;
        }

        var document = BuildText.Map(
            ("openapi", "3.1.0"),
            ("info", BuildText.Map(
                ("title", view.Name(ownerId)),
                ("version", "declared"),
                ("x-vellum-element-id", ownerId.ToString()))),
            ("paths", paths));
        if (schemaKeys.Count > 0)
            document["components"] = BuildText.Map(("schemas", Schemas(view, schemaKeys)));
        return BuildText.Yaml(document);

        string? Reference(SchemaId? id) =>
            id is { } schemaId && schemaKeys.TryGetValue(schemaId.Value, out var key) ? $"#/components/schemas/{key}" : null;
    }

    /// <summary>The call steps whose playback on the pinned model emits one of <paramref name="kinds"/> for the operation, as <c>scenario ID/step ID</c>.</summary>
    private static List<object?> ShownBy(BuildView view, OperationState operation, params TraceEventKind[] kinds) =>
        [.. view.AllSteps.Where(s => s.Step is CallStep call && call.Operation == operation.Id)
            .Where(s => view.Playbacks[s.ScenarioId].Events.Any(e => e.StepId == s.Step.Id && e.Target == operation.Id.Value && kinds.Contains(e.Kind)))
            .Select(s => (object?)$"{s.ScenarioId}/{s.Step.Id}")
            .Distinct()];

    private static OrderedDictionary<string, object?> Content(string reference) =>
        BuildText.Map(("application/json", BuildText.Map(("schema", BuildText.Map(("$ref", reference))))));

    private static OrderedDictionary<string, object?> Schemas(BuildView view, Dictionary<Guid, string> keys)
    {
        var schemas = new OrderedDictionary<string, object?>();
        foreach (var (id, key) in keys.OrderBy(k => k.Value, StringComparer.Ordinal))
            schemas[key] = SchemaBody(view.SchemasById[id]);
        return schemas;
    }

    /// <summary>The schema as stored, or an empty schema that names the stored text when it is not a JSON object.</summary>
    private static object? SchemaBody(BuildSchema schema)
    {
        using var document = BuildText.TryParseJson(schema.Content);
        if (document?.RootElement.ValueKind is JsonValueKind.Object or JsonValueKind.True or JsonValueKind.False)
            return BuildText.Plain(document.RootElement);
        return BuildText.Map(("description", $"Schema {schema.Id} is not a JSON Schema object. See architecture.json for its stored text."));
    }

    /// <summary>
    /// AsyncAPI 3.0 for every channel, message and subscription of the model. The messages of
    /// a channel and the <c>send</c> operations come from the publish steps of the scenarios,
    /// because the model has no publication of its own.
    /// </summary>
    public static byte[] AsyncApi(BuildView view)
    {
        var channelKeys = BuildText.Keys(view.Channels.Select(c => (c.Id.Value, c.Name)));
        var messageKeys = BuildText.Keys(view.Messages.Select(m => (m.Id.Value, m.Name)));
        var schemaIds = view.Messages.Select(m => m.SchemaId).OfType<SchemaId>().Select(id => id.Value)
            .Where(view.SchemasById.ContainsKey).Distinct().ToList();
        var schemaKeys = BuildText.Keys(schemaIds.Select(id => (id, view.SchemasById[id].Name)));
        var publications = view.Publications()
            .Where(p => channelKeys.ContainsKey(p.Channel) && messageKeys.ContainsKey(p.Message)).ToList();

        var channels = new OrderedDictionary<string, object?>();
        foreach (var channel in view.Channels.OrderBy(c => channelKeys[c.Id.Value], StringComparer.Ordinal))
        {
            var entry = BuildText.Map(("address", channel.Name));
            var messages = new OrderedDictionary<string, object?>();
            foreach (var message in publications.Where(p => p.Channel == channel.Id.Value).Select(p => p.Message).Distinct()
                         .OrderBy(id => messageKeys[id], StringComparer.Ordinal))
                messages[messageKeys[message]] = BuildText.Map(("$ref", $"#/components/messages/{messageKeys[message]}"));
            if (messages.Count > 0)
                entry["messages"] = messages;
            // AsyncAPI has no vendor-neutral place for a route between two channels.
            var routes = view.Bindings.Where(b => b.SourceChannelId == channel.Id && channelKeys.ContainsKey(b.TargetChannelId.Value))
                .OrderBy(b => b.Id.Value)
                .Select(b => (object?)BuildText.Map(
                    ("id", b.Id.Value.ToString()),
                    ("to", BuildText.Map(("$ref", $"#/channels/{channelKeys[b.TargetChannelId.Value]}"))),
                    ("routingKey", b.RoutingKey),
                    ("truth", b.FactStatus.ToApiString())))
                .ToList();
            if (routes.Count > 0)
                entry["x-vellum-bindings"] = routes;
            entry["x-vellum-channel-id"] = channel.Id.Value.ToString();
            entry["x-vellum-kind"] = channel.Kind.ToString().ToLowerInvariant();
            entry["x-vellum-truth"] = channel.FactStatus.ToApiString();
            channels[channelKeys[channel.Id.Value]] = entry;
        }

        var operations = new OrderedDictionary<string, object?>();
        foreach (var subscription in view.Model.Subscriptions.Values.Where(s => channelKeys.ContainsKey(s.ChannelId.Value)).OrderBy(s => s.Id.Value))
        {
            var entry = BuildText.Map(
                ("action", "receive"),
                ("channel", BuildText.Map(("$ref", $"#/channels/{channelKeys[subscription.ChannelId.Value]}"))),
                ("summary", ContractDecider.SubscriptionLabel(view.Model, subscription)),
                ("x-vellum-subscription-id", subscription.Id.Value.ToString()),
                ("x-vellum-consumer-id", subscription.ConsumerId.Value.ToString()),
                ("x-vellum-retry", BuildText.Map(("maxAttempts", subscription.Retry.MaxAttempts), ("backoffMs", subscription.Retry.BackoffMs))),
                ("x-vellum-truth", subscription.FactStatus.ToApiString()));
            if (subscription.DeadLetterChannelId is { } deadLetter)
                entry["x-vellum-dead-letter-channel-id"] = deadLetter.Value.ToString();
            operations[ReceiveKey(subscription.Id.Value)] = entry;
        }
        foreach (var send in publications.GroupBy(p => (p.Publisher, p.Channel)).OrderBy(g => g.Key.Publisher).ThenBy(g => g.Key.Channel))
        {
            var channelKey = channelKeys[send.Key.Channel];
            operations[SendKey(send.Key.Publisher, send.Key.Channel)] = BuildText.Map(
                ("action", "send"),
                ("channel", BuildText.Map(("$ref", $"#/channels/{channelKey}"))),
                ("messages", send.Select(p => messageKeys[p.Message]).Distinct().Order(StringComparer.Ordinal)
                    .Select(key => (object?)BuildText.Map(("$ref", $"#/channels/{channelKey}/messages/{key}"))).ToList()),
                ("summary", $"{view.Name(send.Key.Publisher)} publishes to {view.Name(send.Key.Channel)}"),
                ("x-vellum-publisher-id", send.Key.Publisher.ToString()),
                ("x-vellum-derived-from", "scenario publish steps"));
        }

        var messageMap = new OrderedDictionary<string, object?>();
        foreach (var message in view.Messages.OrderBy(m => messageKeys[m.Id.Value], StringComparer.Ordinal))
        {
            var entry = BuildText.Map(("name", message.Name), ("contentType", "application/json"));
            if (message.SchemaId is { } schemaId && schemaKeys.TryGetValue(schemaId.Value, out var schemaKey))
                entry["payload"] = BuildText.Map(("$ref", $"#/components/schemas/{schemaKey}"));
            entry["x-vellum-message-id"] = message.Id.Value.ToString();
            entry["x-vellum-producer-id"] = message.ProducerId.Value.ToString();
            entry["x-vellum-truth"] = message.FactStatus.ToApiString();
            messageMap[messageKeys[message.Id.Value]] = entry;
        }

        var components = new OrderedDictionary<string, object?>();
        if (messageMap.Count > 0) components["messages"] = messageMap;
        if (schemaKeys.Count > 0) components["schemas"] = Schemas(view, schemaKeys);

        var document = BuildText.Map(
            ("asyncapi", "3.0.0"),
            ("info", BuildText.Map(("title", view.Input.ProjectName), ("version", "declared"))),
            ("channels", channels),
            ("operations", operations));
        if (components.Count > 0)
            document["components"] = components;
        return BuildText.Yaml(document);
    }
}

/// <summary>The promise and scenario files of a bundle.</summary>
internal static class PromiseDocuments
{
    /// <summary>
    /// A binding promise is at <c>promises/</c>. A promise that was never approved and a
    /// retired promise are in directories of their own, so no reader takes one for a requirement.
    /// </summary>
    public static string Path(PromiseDto promise) => promise.State switch
    {
        PromiseLifecycle.Approved => $"promises/{promise.RequirementId}.json",
        PromiseLifecycle.Retired => $"promises-retired/{promise.RequirementId}.json",
        _ => $"promises-not-approved/{promise.RequirementId}.json",
    };

    public static string ScenarioPath(Scenario scenario) => $"scenarios/{scenario.Id.Value}.json";

    /// <summary>A promise as an implementer needs it: every revision, with the evidence status of each.</summary>
    /// <param name="promise">
    /// <c>binding</c> says whether the promise is a requirement. <c>requirement</c> is the
    /// approved revision in force, and null for a promise that does not bind. <c>revisions</c>
    /// is the whole history, each with its status.
    /// </param>
    public static object Export(PromiseDto promise) => new
    {
        promise.Id, promise.RequirementId, promise.Origin, promise.State,
        Binding = promise.State == PromiseLifecycle.Approved,
        promise.InForceRevision, promise.LatestRevision, promise.NeedsReview, promise.MissingLinks,
        Requirement = promise.State == PromiseLifecycle.Approved ? promise.Current : null,
        promise.Revisions,
    };

    public static byte[] Promise(PromiseDto promise) => BuildText.CanonicalJson(Export(promise));

    /// <summary>The scenario document and what its playback on the pinned model gives.</summary>
    public static byte[] Scenario(Scenario scenario, TraceResult playback) => BuildText.CanonicalJson(new
    {
        scenario,
        declaredPlayback = new
        {
            engineVersion = playback.EngineVersion,
            hash = playback.Hash,
            issues = playback.Issues,
            outcomes = playback.Outcomes.OrderBy(o => o.Key, StringComparer.Ordinal).ToDictionary(o => o.Key, o => o.Value),
            assertions = playback.Assertions,
            eventCount = playback.Events.Count,
        },
    });
}
