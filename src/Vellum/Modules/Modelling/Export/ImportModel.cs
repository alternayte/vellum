using System.Security.Cryptography;
using System.Text.Json;
using Deedbox;
using Microsoft.EntityFrameworkCore;
using Vellum.Modules.Drafts;
using Vellum.Shared;
using Vellum.Modules.Modelling.Model;
using Vellum.Modules.Schemas;
using Vellum.Modules.Trace.Engine;
using Vellum.Modules.Workspaces;

namespace Vellum.Modules.Modelling.Export;

/// <summary>What an import does, or would do, with one entry of the document.</summary>
/// <param name="Kind"><c>element</c>, <c>relationship</c>, <c>message</c>, <c>channel</c>, <c>operation</c>, <c>subscription</c>, <c>binding</c> or <c>schema</c>.</param>
/// <param name="Change"><c>added</c>, <c>changed</c>, <c>unchanged</c> or <c>refused</c>.</param>
/// <param name="Error">Why the entry is refused. Null for every other change.</param>
/// <param name="ImportedAs">
/// For a schema whose ID belongs to another project: the ID the schema has in this project.
/// Every reference to the schema in the document is read as this ID.
/// </param>
public sealed record ImportItem(string Kind, Guid Id, string? Name, string Change, string? Error = null, Guid? ImportedAs = null);

/// <summary>
/// The result of an import or of its preview. <paramref name="Applied"/> is false for a
/// preview (<c>dryRun=true</c>): nothing was written. <paramref name="ModelVersion"/> is the
/// version of the main model that the entries were read against; an import that sends it as
/// <c>expectedVersion</c> is refused when the model has changed since.
/// </summary>
public sealed record ImportResult(
    bool Applied, long ModelVersion, int Added, int Changed, int Unchanged, int Refused, IReadOnlyList<ImportItem> Items);

public static class ImportModel
{
    public const string Added = "added";
    public const string Changed = "changed";
    public const string Unchanged = "unchanged";
    public const string Refused = "refused";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    // Any Editor can post an import, and one import is one transaction that keeps its streams
    // locked. The limits bound the memory, the lock time and the number of schema appends.
    public const int MaxBytes = 16 * 1024 * 1024;
    public const int MaxElements = 5_000;
    public const int MaxRelationships = 10_000;
    public const int MaxMessages = 5_000;
    public const int MaxSchemas = 200;
    public const int MaxOperations = ContractDecider.MaxPerKind;
    public const int MaxChannels = ContractDecider.MaxPerKind;
    public const int MaxSubscriptions = ContractDecider.MaxPerKind;
    public const int MaxBindings = ContractDecider.MaxPerKind;

    public static async Task<IResult> Handle(
        Guid projectId,
        string userId,
        HttpRequest request,
        bool dryRun,
        long? expectedVersion,
        WorkspacesDbContext workspacesDb,
        CancellationToken ct)
    {
        var project = await workspacesDb.Projects.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == projectId, ct);
        if (project is null) return Results.NotFound();

        // The endpoint's body limit is MaxBytes; a larger body never reaches this line.
        string body;
        using (var reader = new StreamReader(request.Body))
            body = await reader.ReadToEndAsync(ct);

        ModelExportDocument? doc;

        var isYaml = (request.ContentType ?? "application/json").Contains("yaml");
        try
        {
            doc = isYaml
                ? ModelYaml.Read(body)
                : JsonSerializer.Deserialize<ModelExportDocument>(body, JsonOpts);
        }
        // The YAML reader ends in the JSON reader, so YAML that is no export document fails there.
        catch (Exception ex) when (ex is JsonException or YamlDotNet.Core.YamlException)
        {
            return Results.BadRequest(new ErrorResponse("invalid_export",
                isYaml ? "Document is not valid YAML" : "Document is not valid JSON"));
        }

        if (doc is null)
            return Results.BadRequest(new ErrorResponse("invalid_export", "Invalid export document"));

        doc = doc with
        {
            Elements = doc.Elements ?? [],
            Relationships = doc.Relationships ?? [],
            Messages = doc.Messages ?? [],
            Schemas = doc.Schemas ?? [],
            // A document written before contracts existed has none.
            Operations = doc.Operations ?? [],
            Channels = doc.Channels ?? [],
            Subscriptions = doc.Subscriptions ?? [],
            // A document written before bindings existed has none.
            Bindings = doc.Bindings ?? [],
        };

        // A document is untrusted: a list can hold `null`, and an entry can leave a list out.
        var hasEmptyEntry = doc.Elements.Any(e => e is null) || doc.Relationships.Any(r => r is null)
            || doc.Messages.Any(m => m is null) || doc.Schemas.Any(s => s is null)
            || doc.Operations.Any(o => o is null) || doc.Channels.Any(c => c is null)
            || doc.Subscriptions.Any(s => s is null) || doc.Bindings.Any(b => b is null);
        if (hasEmptyEntry)
            return Results.BadRequest(new ErrorResponse("invalid_export", "A list in the document holds an empty entry"));

        doc = doc with
        {
            Elements = [.. doc.Elements.Select(e => e with { Tags = e.Tags ?? [] })],
            Messages = [.. doc.Messages.Select(m => m with { Tags = m.Tags ?? [], ConsumerIds = m.ConsumerIds ?? [] })],
        };

        (string Name, int Count, int Max)[] sizes =
        [
            ("elements", doc.Elements.Count, MaxElements),
            ("relationships", doc.Relationships.Count, MaxRelationships),
            ("messages", doc.Messages.Count, MaxMessages),
            ("schemas", doc.Schemas.Count, MaxSchemas),
            ("operations", doc.Operations.Count, MaxOperations),
            ("channels", doc.Channels.Count, MaxChannels),
            ("subscriptions", doc.Subscriptions.Count, MaxSubscriptions),
            ("bindings", doc.Bindings.Count, MaxBindings),
        ];
        foreach (var (name, count, max) in sizes)
        {
            if (count > max)
            {
                return Results.BadRequest(new ErrorResponse("import_too_large",
                    $"An import document holds at most {max} {name}. This one has {count}."));
            }
        }

        // Each schema is locked, decided and appended once.
        var repeated = doc.Schemas.GroupBy(s => s.Id).FirstOrDefault(g => g.Count() > 1);
        if (repeated is not null)
            return Results.BadRequest(new ErrorResponse("invalid_export", $"Schema {repeated.Key} appears more than once"));

        // A schema stream must not take the ID of the project's model stream.
        if (doc.Schemas.Any(s => s.Id == project.StreamId || SchemaIdIn(projectId, s.Id) == project.StreamId))
            return Results.BadRequest(new ErrorResponse("invalid_export", $"Schema {project.StreamId}: this ID is already in use"));

        // A parent comes before its children, whatever the order of the document. An export
        // orders elements by ID, so a child can stand before its parent there.
        doc = doc with { Elements = ParentsFirst(doc.Elements) };

        // The whole import is one unit. A unit that fails with DBX038 or a deadlock wrote
        // nothing and runs again.
        return await SharedTransaction.RunAsync(request.HttpContext.RequestServices,
            services => ImportAsync(services, projectId, userId, project.StreamId, doc, dryRun, expectedVersion, ct), ct);
    }

    /// <summary>
    /// The ID that a schema of another project gets in <paramref name="projectId"/>. A schema
    /// stream belongs to one project, so a document that comes from another project of the
    /// same instance cannot keep its schema IDs. The ID is a function of the project and of
    /// the ID in the document: the same document gives the same schemas each time, and a
    /// second import changes those schemas and adds none.
    /// </summary>
    public static Guid SchemaIdIn(Guid projectId, Guid documentSchemaId)
    {
        Span<byte> input = stackalloc byte[32];
        projectId.TryWriteBytes(input, bigEndian: true, out _);
        documentSchemaId.TryWriteBytes(input[16..], bigEndian: true, out _);
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(input, hash);
        // The layout of a name-based UUID (version 5, RFC 4122 variant), so the value reads as a UUID.
        hash[6] = (byte)((hash[6] & 0x0F) | 0x50);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        return new Guid(hash[..16], bigEndian: true);
    }

    /// <summary>The elements with each parent before its children. Entries of one depth keep the order of the document.</summary>
    private static IReadOnlyList<ElementExport> ParentsFirst(IReadOnlyList<ElementExport> elements)
    {
        var parents = new Dictionary<Guid, Guid?>();
        foreach (var element in elements)
            parents.TryAdd(element.Id, element.ParentId);

        // The kinds allow three levels. A longer chain or a cycle is refused by the decider; the bound ends the walk.
        int Depth(ElementExport element)
        {
            var depth = 0;
            for (var parent = element.ParentId; parent is { } id && depth < 8 && parents.TryGetValue(id, out var next); parent = next)
                depth++;
            return depth;
        }

        return [.. elements.Select((element, index) => (element, index)).OrderBy(e => Depth(e.element)).ThenBy(e => e.index).Select(e => e.element)];
    }

    private static async Task<IResult> ImportAsync(
        IServiceProvider services, Guid projectId, string userId, Guid modelStreamId, ModelExportDocument doc,
        bool dryRun, long? expectedVersion, CancellationToken ct)
    {
        var modellingDb = services.GetRequiredService<ModellingDbContext>();

        // Every lock, append and read-model write below runs in this transaction. A return
        // before the commit rolls all of it back, so a reader sees the whole import or none of it.
        await using var transaction = await modellingDb.Database.BeginTransactionAsync(ct);
        var store = services.GetRequiredService<IEventStore>().UseDbContext(modellingDb);

        // Step 1: lock every stream the import can write, in stream-ID order, before the first
        // append. An append takes its stream lock and then the store-wide position counter,
        // and the counter stays held until the commit. A transaction that asks for another
        // stream lock while it holds the counter can wait for a writer that waits for the
        // counter. Here no stream lock is asked for after the first append, so that cycle
        // cannot form, and two imports (or an import and a merge) ask in the same order.
        // Execute with no events locks the stream and returns its state.
        // A schema has two streams it can be written to: the one of its ID, and the one of
        // the ID it gets in this project when its own ID belongs to another project. Which of
        // the two is written is known only when both are read, so both are locked.
        var state = ModelState.Initial;
        long version = 0;
        var schemaStreams = new Dictionary<Guid, (SchemaState State, long Version, Guid? Owner)>();
        var lockIds = doc.Schemas.SelectMany(s => new[] { s.Id, SchemaIdIn(projectId, s.Id) }).Append(modelStreamId).Distinct().Order();
        foreach (var streamId in lockIds)
        {
            if (streamId == modelStreamId)
            {
                var model = await store.Execute<ModelState>(StreamId.From(streamId), _ => [], ct);
                (state, version) = (model.State, model.Version);
            }
            else
            {
                var schema = await store.Execute<SchemaState>(StreamId.From(streamId), _ => [], ct);
                schemaStreams[streamId] = (schema.State, schema.Version, await OwnerAsync(services, store, streamId, schema.State, schema.Version, ct));
            }
        }

        // A preview must not tell whether an ID is in use in a project the caller cannot read:
        // otherwise it answers that question for any ID and writes nothing. It decides against
        // the streams as the caller may see them, so such a stream reads as an unused ID. The
        // import itself decides against the streams as they are, and never writes into one of
        // them (see `schemaTargets`).
        if (dryRun)
        {
            var foreignOwners = schemaStreams.Values.Select(s => s.Owner).OfType<Guid>().Where(o => o != projectId).ToHashSet();
            var readable = await ReadableProjectsAsync(services, userId, foreignOwners, ct);
            foreach (var (id, stream) in schemaStreams.ToList())
            {
                if (stream.Owner is { } owner && owner != projectId && !readable.Contains(owner))
                    schemaStreams[id] = (SchemaState.Initial, 0, null);
            }
        }

        if (expectedVersion is { } expected && expected != version)
        {
            return Results.Conflict(new ErrorResponse("conflict",
                "The model has changed since the preview. Preview the import again."));
        }

        // Step 2: decide everything against the locked states. Nothing is appended until the
        // whole document is accepted. An entry that is refused is recorded and the next one
        // is read, so the answer names every refused entry and not only the first.
        var items = new List<ImportItem>();
        void Refuse(string kind, Guid id, string? name, string? reason) =>
            items.Add(new ImportItem(kind, id, name, Refused, reason));

        // Where each schema of the document is written. A schema keeps its ID unless the ID
        // is in use for anything but a schema of this project (a schema of another project,
        // deleted or not, or a model, draft or variant stream); then it takes the ID that this
        // project gives it, and so does every reference to it. A schema that was imported that
        // way before is found under that ID again, whatever happened to the other stream since.
        bool Writable(Guid streamId) => schemaStreams[streamId] is var s
            && (s.Owner is null || (s.Owner == projectId && (s.State.Schema is not null || s.State.Deleted)));
        var schemaTargets = new Dictionary<Guid, Guid>();
        foreach (var schema in doc.Schemas)
        {
            var mapped = SchemaIdIn(projectId, schema.Id);
            schemaTargets[schema.Id] =
                (schemaStreams[mapped].Owner == projectId && Writable(mapped)) || !Writable(schema.Id)
                    ? mapped
                    : schema.Id;
        }
        Guid? Target(Guid? schemaId) =>
            schemaId is { } id && schemaTargets.TryGetValue(id, out var target) ? target : schemaId;

        // A schema the document names is one the document carries, whose stream is locked
        // above, or one the project already has. The rows of the second kind are held until
        // the commit, so none of them can be deleted under the import (see SchemaReferences).
        var knownSchemaIds = schemaTargets.Values.ToHashSet();
        var named = doc.Messages.Select(m => m.SchemaId)
            .Concat(doc.Operations.SelectMany(o => new[] { o.RequestSchemaId, o.ResponseSchemaId }))
            .OfType<Guid>()
            .Where(id => !schemaTargets.ContainsKey(id))
            .ToHashSet();
        var missing = await SchemaReferences.HoldAsync(modellingDb, projectId, named, ct);
        knownSchemaIds.UnionWith(named.Except(missing));

        var events = new List<object>();

        // One entry: all of its decisions count, or none of them does. `decide` returns the
        // reason of a refusal, or null.
        void Entry(string kind, Guid id, string? name, Func<ModelState, object?> find, Func<string?> decide)
        {
            var (stateBefore, eventsBefore, before) = (state, events.Count, find(state));
            if (decide() is { } reason)
            {
                state = stateBefore;
                events.RemoveRange(eventsBefore, events.Count - eventsBefore);
                Refuse(kind, id, name, reason);
                return;
            }

            // The states compare by value and leave the stale mark out, so the mark is compared here.
            var after = find(state);
            var same = before is not null && before.Equals(after) && Equals(StaleOf(before), StaleOf(after));
            items.Add(new ImportItem(kind, id, name, before is null ? Added : same ? Unchanged : Changed));
        }

        // Adds a decision's events to the import, or gives the reason of the refusal.
        string? Apply(CommandResult<IReadOnlyList<object>> decision)
        {
            if (decision is not CommandResult<IReadOnlyList<object>>.Success success)
                return DeciderFailure(decision);

            events.AddRange(success.Value);
            state = success.Value.Aggregate(state, ModelState.Evolve);
            return null;
        }

        // The label of a fact the document carries. An import is not a scan and not a person's
        // statement about one fact, so it clears no mark: a fact that has a mark keeps it, and
        // a fact that has none takes the mark of the document.
        string? Relabel(Guid id, FactStatus factStatus, Provenance? provenance, StaleMark? stale) =>
            Apply(new CommandResult<IReadOnlyList<object>>.Success(StaleDecider.Relabel(state, id, factStatus, provenance, stale)));

        string? Record(object recorded) =>
            Apply(new CommandResult<IReadOnlyList<object>>.Success([recorded]));

        foreach (var el in doc.Elements)
        {
            Entry("element", el.Id, el.Name, s => s.Elements.GetValueOrDefault(el.Id), () =>
            {
                if (!TryReadFacts(el.FactStatus, el.Provenance, el.Stale, out var factStatus, out var provenance, out var stale, out var factError))
                    return factError;
                if (!ModelEnum.TryParseDefined(el.Status, out ElementStatus status))
                    return $"invalid status '{el.Status}'";
                if (!ModelEnum.TryParseDefined(el.Kind, out ElementKind kind))
                    return $"invalid kind '{el.Kind}'";

                if (state.Elements.TryGetValue(el.Id, out var existingElement))
                {
                    if (existingElement.Kind != kind)
                        return $"kind change from '{existingElement.Kind}' to '{kind}' is not supported";

                    return Apply(ModelDecider.UpdateElement(state, new UpdateElementCommand(el.Id,
                            Name: el.Name, SetName: true,
                            Description: el.Description, SetDescription: true,
                            Technology: el.Technology, SetTechnology: true,
                            ParentId: el.ParentId, SetParentId: true,
                            Status: status, Tags: el.Tags,
                            Icon: el.Icon, SetIcon: true)))
                        ?? Relabel(el.Id, factStatus, provenance, stale);
                }

                return Apply(ModelDecider.AddElement(state, new AddElementCommand(el.Id, kind, el.Name, el.Description,
                        el.Technology, null, status, el.ParentId, el.Tags)))
                    // Through the decider, so the icon is held to the same rule as on every other path.
                    ?? (el.Icon is null ? null : Apply(ModelDecider.UpdateElement(
                        state, new UpdateElementCommand(el.Id, Icon: el.Icon, SetIcon: true))))
                    ?? (factStatus != FactStatus.Declared || provenance is not null || stale is not null
                        ? Record(new ModelEvent.ElementProvenanceRecorded(el.Id, factStatus, provenance, stale))
                        : null);
            });
        }

        foreach (var rel in doc.Relationships)
        {
            Entry("relationship", rel.Id, rel.Label, s => s.Relationships.GetValueOrDefault(rel.Id), () =>
            {
                if (!TryReadFacts(rel.FactStatus, rel.Provenance, rel.Stale, out var factStatus, out var provenance, out var stale, out var factError))
                    return factError;

                var kind = RelationshipKind.Structural;
                if (!string.IsNullOrEmpty(rel.Kind) && !ModelEnum.TryParseDefined(rel.Kind, out kind))
                    return $"invalid kind '{rel.Kind}'";

                if (state.Relationships.ContainsKey(rel.Id))
                {
                    return Apply(ModelDecider.UpdateRelationship(state, new UpdateRelationshipCommand(rel.Id,
                            Label: rel.Label, SetLabel: true,
                            Technology: rel.Technology, SetTechnology: true,
                            LineShape: rel.LineShape, SetLineShape: true,
                            Kind: kind, SetKind: true)))
                        ?? Relabel(rel.Id, factStatus, provenance, stale);
                }

                return Apply(ModelDecider.AddRelationship(state,
                        new AddRelationshipCommand(rel.Id, rel.FromId, rel.ToId, rel.Label, rel.Technology, kind)))
                    ?? (rel.LineShape is null ? null : Apply(ModelDecider.UpdateRelationship(
                        state, new UpdateRelationshipCommand(rel.Id, LineShape: rel.LineShape, SetLineShape: true))))
                    ?? (factStatus != FactStatus.Declared || provenance is not null || stale is not null
                        ? Record(new ModelEvent.RelationshipProvenanceRecorded(rel.Id, factStatus, provenance, stale))
                        : null);
            });
        }

        foreach (var msg in doc.Messages)
        {
            Entry("message", msg.Id, msg.Name, s => s.Messages.GetValueOrDefault(msg.Id), () =>
            {
                if (!TryReadFacts(msg.FactStatus, msg.Provenance, msg.Stale, out var factStatus, out var provenance, out var stale, out var factError))
                    return factError;

                var schemaId = Target(msg.SchemaId);
                if (schemaId is { } referenced && !knownSchemaIds.Contains(referenced))
                    return $"schema {msg.SchemaId} not found in this project or the document";

                if (state.Messages.ContainsKey(msg.Id))
                {
                    return Apply(ModelDecider.UpdateMessage(state, new UpdateMessageCommand(msg.Id,
                            Name: msg.Name, SetName: true,
                            Description: msg.Description, SetDescription: true,
                            ProducerId: msg.ProducerId, SetProducerId: true,
                            ConsumerIds: msg.ConsumerIds.ToElementIds(), SetConsumerIds: true,
                            SchemaId: schemaId, SetSchemaId: true,
                            Tags: msg.Tags)))
                        ?? Relabel(msg.Id, factStatus, provenance, stale);
                }

                return Apply(ModelDecider.AddMessage(state, new AddMessageCommand(msg.Id, msg.Name, msg.Description,
                        msg.ProducerId, msg.ConsumerIds.ToElementIds(), schemaId, msg.Tags)))
                    ?? (factStatus != FactStatus.Declared || provenance is not null || stale is not null
                        ? Record(new ModelEvent.MessageProvenanceRecorded(msg.Id, factStatus, provenance, stale))
                        : null);
            });
        }

        // Contracts. Channels come first: a subscription and a binding name their channels,
        // and both operations and subscriptions name elements that are already in the state.
        foreach (var channel in doc.Channels)
        {
            ChannelId id = channel.Id;
            Entry("channel", channel.Id, channel.Name, s => s.Channels.GetValueOrDefault(id), () =>
            {
                if (!TryReadFacts(channel.FactStatus, channel.Provenance, channel.Stale, out var factStatus, out var provenance, out var stale, out var factError))
                    return factError;
                if (channel.Kind is null || !ModelEnum.TryParseDefined(channel.Kind, out ChannelKind kind))
                    return $"invalid kind '{channel.Kind}'";

                return Apply(state.Channels.ContainsKey(id)
                        ? ContractDecider.UpdateChannel(state, new UpdateChannelCommand(id, channel.Name, kind))
                        : ContractDecider.AddChannel(state, new AddChannelCommand(id, channel.Name, kind)))
                    ?? Relabel(id.Value, factStatus, provenance, stale);
            });
        }

        foreach (var operation in doc.Operations)
        {
            OperationId id = operation.Id;
            Entry("operation", operation.Id, operation.Name, s => s.Operations.GetValueOrDefault(id), () =>
            {
                if (!TryReadFacts(operation.FactStatus, operation.Provenance, operation.Stale, out var factStatus, out var provenance, out var stale, out var factError))
                    return factError;
                if (operation.Transport is null || !ModelEnum.TryParseDefined(operation.Transport, out Transport transport))
                    return $"invalid transport '{operation.Transport}'";

                foreach (var schemaId in new[] { operation.RequestSchemaId, operation.ResponseSchemaId })
                {
                    if (Target(schemaId) is { } referenced && !knownSchemaIds.Contains(referenced))
                        return $"schema {schemaId} not found in this project or the document";
                }

                var (requestSchemaId, responseSchemaId) = (Target(operation.RequestSchemaId), Target(operation.ResponseSchemaId));
                var retry = operation.Retry is { } r ? new RetryPolicy(r.MaxAttempts, r.BackoffMs) : RetryPolicy.None;
                return Apply(state.Operations.ContainsKey(id)
                        ? ContractDecider.UpdateOperation(state, new UpdateOperationCommand(
                            id, operation.Name, new ElementId(operation.OwnerId), transport,
                            requestSchemaId, true, responseSchemaId, true,
                            operation.TimeoutMs, retry))
                        : ContractDecider.AddOperation(state, new AddOperationCommand(
                            id, operation.OwnerId, operation.Name, transport,
                            requestSchemaId, responseSchemaId, operation.TimeoutMs, retry)))
                    ?? Relabel(id.Value, factStatus, provenance, stale);
            });
        }

        foreach (var subscription in doc.Subscriptions)
        {
            SubscriptionId id = subscription.Id;
            Entry("subscription", subscription.Id, null, s => s.Subscriptions.GetValueOrDefault(id), () =>
            {
                if (!TryReadFacts(subscription.FactStatus, subscription.Provenance, subscription.Stale, out var factStatus, out var provenance, out var stale, out var factError))
                    return factError;

                var retry = subscription.Retry is { } r ? new RetryPolicy(r.MaxAttempts, r.BackoffMs) : RetryPolicy.None;
                ChannelId? deadLetter = subscription.DeadLetterChannelId is { } d ? new ChannelId(d) : null;
                return Apply(state.Subscriptions.ContainsKey(id)
                        ? ContractDecider.UpdateSubscription(state, new UpdateSubscriptionCommand(
                            id, new ChannelId(subscription.ChannelId), new ElementId(subscription.ConsumerId), retry,
                            deadLetter, SetDeadLetterChannelId: true))
                        : ContractDecider.AddSubscription(state, new AddSubscriptionCommand(
                            id, subscription.ChannelId, subscription.ConsumerId, retry, deadLetter)))
                    ?? Relabel(id.Value, factStatus, provenance, stale);
            });
        }

        foreach (var binding in doc.Bindings ?? [])
        {
            BindingId id = binding.Id;
            Entry("binding", binding.Id, binding.RoutingKey, s => s.Bindings.GetValueOrDefault(id), () =>
            {
                if (!TryReadFacts(binding.FactStatus, binding.Provenance, binding.Stale, out var factStatus, out var provenance, out var stale, out var factError))
                    return factError;

                return Apply(state.Bindings.ContainsKey(id)
                        ? ContractDecider.UpdateBinding(state, new UpdateBindingCommand(
                            id, new ChannelId(binding.SourceChannelId), new ChannelId(binding.TargetChannelId),
                            binding.RoutingKey, SetRoutingKey: true))
                        : ContractDecider.AddBinding(state, new AddBindingCommand(
                            id, binding.SourceChannelId, binding.TargetChannelId, binding.RoutingKey)))
                    ?? Relabel(id.Value, factStatus, provenance, stale);
            });
        }

        // Schemas: each is its own stream.
        var schemaAppends = new List<(Guid Id, long Version, IReadOnlyList<object> Events)>();
        var written = new HashSet<Guid>();
        foreach (var schema in doc.Schemas.OrderBy(s => s.Id))
        {
            var target = schemaTargets[schema.Id];
            Guid? importedAs = target == schema.Id ? null : target;
            var (schemaState, schemaVersion, _) = schemaStreams[target];

            // Two entries of the document that end in one stream: one names the ID that the other gets in this project.
            if (!written.Add(target))
            {
                Refuse("schema", schema.Id, schema.Name, $"the document holds this schema two times, once as {target}");
                continue;
            }

            // The ID that this project gives the schema is in use too. Only a stream made to take
            // that ID gets here; the import never writes into it.
            if (!Writable(target))
            {
                Refuse("schema", schema.Id, schema.Name, "this ID belongs to another project");
                continue;
            }

            CommandResult<IReadOnlyList<object>> decision;
            if (schemaState.Schema is not null)
            {
                decision = SchemaDecider.Update(schemaState, new UpdateSchemaCommand(target,
                    Name: schema.Name, SetName: true,
                    Description: schema.Description, SetDescription: true,
                    Content: schema.Content, SetContent: true));
            }
            else
            {
                decision = SchemaDecider.Create(schemaState,
                    new CreateSchemaCommand(target, schema.Name, schema.Description, schema.Content, projectId));
            }

            if (decision is not CommandResult<IReadOnlyList<object>>.Success decided)
            {
                Refuse("schema", schema.Id, schema.Name, DeciderFailure(decision));
                continue;
            }

            items.Add(new ImportItem("schema", schema.Id, schema.Name,
                schemaState.Schema is null ? Added : decided.Value.Count == 0 ? Unchanged : Changed, ImportedAs: importedAs));
            if (decided.Value.Count > 0)
                schemaAppends.Add((target, schemaVersion, decided.Value));
        }

        var result = new ImportResult(
            Applied: false, version,
            items.Count(i => i.Change == Added), items.Count(i => i.Change == Changed),
            items.Count(i => i.Change == Unchanged), items.Count(i => i.Change == Refused), items);

        // A preview writes nothing. The transaction ends without a commit and gives the locks back.
        if (dryRun)
            return Results.Ok(result);

        // One refused entry refuses the document. The title names the first one; `errors`
        // names each, with the kind and the ID of the entry as the field.
        if (items.FirstOrDefault(i => i.Change == Refused) is { } first)
        {
            return Results.BadRequest(new ErrorResponse("invalid_export", Describe(first),
                Errors: [.. items.Where(i => i.Change == Refused).Select(i => new FieldError($"{i.Kind}:{i.Id}", i.Error!))]));
        }

        // Step 3: append. Deedbox has no append to several streams, so one transaction makes one
        // append per changed stream, and the counter is held from the first to the commit.
        // The model append goes first: its read-model work, the large part, runs before the
        // counter is taken. The appends that follow hold the counter; each writes one schema
        // row, takes no new stream lock and waits for no other writer. MaxSchemas bounds them.
        if (events.Count > 0)
        {
            await store.ForProject(projectId).Append(
                StreamId.From(modelStreamId), ExpectedVersion.Exact(version), events, ct);
        }

        foreach (var (schemaId, schemaVersion, schemaEvents) in schemaAppends)
            await store.Append(StreamId.From(schemaId), ExpectedVersion.Exact(schemaVersion), schemaEvents, ct);

        await transaction.CommitAsync(ct);
        return Results.Ok(result with { Applied = true });
    }

    /// <summary>
    /// The project that a stream belongs to: the project of its schema, also of a deleted one,
    /// or the project of the model, draft or variant that has the stream, also before its
    /// first event. <see cref="Guid.Empty"/> for any other stream that holds events. Null for
    /// a stream that nothing uses.
    /// </summary>
    private static async Task<Guid?> OwnerAsync(
        IServiceProvider services, IEventStore store, Guid streamId, SchemaState state, long version, CancellationToken ct)
    {
        if (state.Schema is { } schema)
            return schema.ProjectId;

        var branchOwner =
            await services.GetRequiredService<WorkspacesDbContext>().Projects.AsNoTracking()
                .Where(p => p.StreamId == streamId).Select(p => (Guid?)p.Id).FirstOrDefaultAsync(ct)
            ?? await services.GetRequiredService<DraftsDbContext>().Drafts.AsNoTracking()
                .Where(d => d.StreamId == streamId).Select(d => (Guid?)d.ProjectId).FirstOrDefaultAsync(ct)
            ?? await services.GetRequiredService<ModellingDbContext>().Variants.AsNoTracking()
                .Where(v => v.StreamId == streamId).Select(v => (Guid?)v.ProjectId).FirstOrDefaultAsync(ct);
        if (branchOwner is not null)
            return branchOwner;
        if (version == 0)
            return null;

        var first = await store.ReadStream(StreamId.From(streamId), 0, 1, ct);
        return first.Count > 0 && first[0].Event is SchemaEvent.SchemaCreated created ? created.ProjectId : Guid.Empty;
    }

    /// <summary>The projects of <paramref name="projectIds"/> in a workspace that <paramref name="userId"/> is a member of.</summary>
    private static async Task<HashSet<Guid>> ReadableProjectsAsync(
        IServiceProvider services, string userId, IReadOnlyCollection<Guid> projectIds, CancellationToken ct)
    {
        if (projectIds.Count == 0)
            return [];

        var db = services.GetRequiredService<WorkspacesDbContext>();
        return [.. await db.Projects.AsNoTracking()
            .Where(p => projectIds.Contains(p.Id) && db.Memberships.Any(m => m.WorkspaceId == p.WorkspaceId && m.UserId == userId))
            .Select(p => p.Id)
            .ToListAsync(ct)];
    }

    /// <summary>"Element {id}: {reason}", the text an import has always given for a refused entry.</summary>
    private static string Describe(ImportItem item) =>
        $"{char.ToUpperInvariant(item.Kind[0])}{item.Kind[1..]} {item.Id}: {item.Error}";

    private static StaleMark? StaleOf(object? fact) => fact switch
    {
        ElementState e => e.Stale,
        RelationshipState r => r.Stale,
        MessageState m => m.Stale,
        OperationState o => o.Stale,
        ChannelState c => c.Stale,
        SubscriptionState s => s.Stale,
        BindingState b => b.Stale,
        _ => null,
    };

    private static string DeciderFailure(CommandResult<IReadOnlyList<object>> result) => result switch
    {
        CommandResult<IReadOnlyList<object>>.Invalid invalid =>
            string.Join("; ", invalid.Errors.Select(e => $"{e.Field}: {e.Message}")),
        CommandResult<IReadOnlyList<object>>.Conflict conflict => conflict.Message,
        CommandResult<IReadOnlyList<object>>.NotFound notFound => notFound.Message,
        _ => "invalid value",
    };

    private static bool TryReadFacts(
        string? factStatusText, ProvenanceExport? provenanceExport, StaleExport? staleExport,
        out FactStatus factStatus, out Provenance? provenance, out StaleMark? stale, out string? error)
    {
        factStatus = FactStatus.Declared;
        provenance = provenanceExport?.ToDomain();
        stale = null;
        error = null;

        if (!string.IsNullOrEmpty(factStatusText) && !FactStatusFormat.TryParseApiString(factStatusText, out factStatus))
        {
            error = $"invalid fact status '{factStatusText}'";
            return false;
        }

        var validationError = FactRules.Validate(factStatus, provenance);
        if (validationError is not null)
        {
            error = validationError.Message;
            return false;
        }

        if (staleExport is null)
            return true;

        // The mark is text from the document: both members are required, bounded and plain.
        if (string.IsNullOrWhiteSpace(staleExport.SinceRevision))
            error = "stale.sinceRevision: A stale mark requires the revision it dates from";
        else if (string.IsNullOrWhiteSpace(staleExport.Reason))
            error = "stale.reason: A stale mark requires a reason";
        else if ((TextRule.Line("stale.sinceRevision", staleExport.SinceRevision)
                ?? TextRule.Line("stale.reason", staleExport.Reason, TextRule.MaxIdentifier)) is { } textError)
            error = $"{textError.Field}: {textError.Message}";
        else
            stale = new StaleMark(staleExport.SinceRevision, staleExport.Reason);

        return error is null;
    }
}
