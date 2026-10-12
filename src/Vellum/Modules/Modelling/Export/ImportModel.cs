using System.Text.Json;
using Deedbox;
using Microsoft.EntityFrameworkCore;
using Vellum.Shared;
using Vellum.Modules.Modelling.Model;
using Vellum.Modules.Schemas;
using Vellum.Modules.Trace.Engine;
using Vellum.Modules.Workspaces;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Vellum.Modules.Modelling.Export;

public static class ImportModel
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static readonly IDeserializer YamlDeserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .Build();

    // Any Editor can post an import, and one import is one transaction that keeps its streams
    // locked. The limits bound the memory, the lock time and the number of schema appends.
    public const int MaxBytes = 16 * 1024 * 1024;
    public const int MaxElements = 5_000;
    public const int MaxRelationships = 10_000;
    public const int MaxMessages = 5_000;
    public const int MaxSchemas = 200;
    public const int MaxOperations = 5_000;
    public const int MaxChannels = 5_000;
    public const int MaxSubscriptions = 5_000;

    public static async Task<IResult> Handle(
        Guid projectId,
        HttpRequest request,
        WorkspacesDbContext workspacesDb,
        CancellationToken ct)
    {
        var project = await workspacesDb.Projects.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == projectId, ct);
        if (project is null) return Results.NotFound();

        var body = request.ContentLength > MaxBytes ? null : await ReadBodyAsync(request.Body, ct);
        if (body is null)
        {
            return Results.Json(
                new ErrorResponse("import_too_large", $"An import document holds at most {MaxBytes} bytes."),
                statusCode: StatusCodes.Status413PayloadTooLarge);
        }

        ModelExportDocument? doc;

        var contentType = request.ContentType ?? "application/json";
        try
        {
            doc = contentType.Contains("yaml")
                ? YamlDeserializer.Deserialize<ModelExportDocument>(body)
                : JsonSerializer.Deserialize<ModelExportDocument>(body, JsonOpts);
        }
        catch (JsonException)
        {
            return Results.BadRequest(new ErrorResponse("invalid_export", "Document is not valid JSON"));
        }
        catch (YamlDotNet.Core.YamlException)
        {
            return Results.BadRequest(new ErrorResponse("invalid_export", "Document is not valid YAML"));
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
        if (doc.Schemas.Any(s => s.Id == project.StreamId))
            return Results.BadRequest(new ErrorResponse("invalid_export", $"Schema {project.StreamId}: this ID is already in use"));

        // The whole import is one unit. A unit that fails with DBX038 or a deadlock wrote
        // nothing and runs again.
        return await SharedTransaction.RunAsync(request.HttpContext.RequestServices,
            services => ImportAsync(services, projectId, project.StreamId, doc, ct), ct);
    }

    private static async Task<IResult> ImportAsync(
        IServiceProvider services, Guid projectId, Guid modelStreamId, ModelExportDocument doc, CancellationToken ct)
    {
        var modellingDb = services.GetRequiredService<ModellingDbContext>();
        var schemasDb = services.GetRequiredService<SchemasDbContext>();

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
        var state = ModelState.Initial;
        long version = 0;
        var schemaStreams = new Dictionary<Guid, (SchemaState State, long Version)>();
        foreach (var streamId in doc.Schemas.Select(s => s.Id).Append(modelStreamId).Order())
        {
            if (streamId == modelStreamId)
            {
                var model = await store.Execute<ModelState>(StreamId.From(streamId), _ => [], ct);
                (state, version) = (model.State, model.Version);
            }
            else
            {
                var schema = await store.Execute<SchemaState>(StreamId.From(streamId), _ => [], ct);
                schemaStreams[streamId] = (schema.State, schema.Version);
            }
        }

        // Step 2: decide everything against the locked states. Nothing is appended until the
        // whole document is accepted.
        var knownSchemaIds = await schemasDb.Schemas.AsNoTracking()
            .Where(s => s.ProjectId == projectId)
            .Select(s => s.Id)
            .ToHashSetAsync(ct);
        knownSchemaIds.UnionWith(doc.Schemas.Select(s => s.Id));

        var events = new List<object>();

        // Import elements
        foreach (var el in doc.Elements)
        {
            if (!TryReadFacts(el.FactStatus, el.Provenance, out var factStatus, out var provenance, out var factError))
                return Results.BadRequest(new ErrorResponse("invalid_export", $"Element {el.Id}: {factError}"));

            if (!ModelEnum.TryParseDefined(el.Status, out ElementStatus status))
                return Results.BadRequest(new ErrorResponse("invalid_export",
                    $"Element {el.Id}: invalid status '{el.Status}'"));

            if (!ModelEnum.TryParseDefined(el.Kind, out ElementKind kind))
                return Results.BadRequest(new ErrorResponse("invalid_export",
                    $"Element {el.Id}: invalid kind '{el.Kind}'"));

            if (state.Elements.TryGetValue(el.Id, out var existingElement))
            {
                if (existingElement.Kind != kind)
                    return Results.BadRequest(new ErrorResponse("invalid_export",
                        $"Element {el.Id}: kind change from '{existingElement.Kind}' to '{kind}' is not supported"));

                var updateCmd = new UpdateElementCommand(el.Id,
                    Name: el.Name, SetName: true,
                    Description: el.Description, SetDescription: true,
                    Technology: el.Technology, SetTechnology: true,
                    ParentId: el.ParentId, SetParentId: true,
                    Status: status, Tags: el.Tags,
                    Icon: el.Icon, SetIcon: true);
                var result = ModelDecider.UpdateElement(state, updateCmd);
                if (result is not CommandResult<IReadOnlyList<object>>.Success updateSuccess)
                    return Results.BadRequest(new ErrorResponse("invalid_export",
                        $"Element {el.Id}: {DeciderFailure(result)}"));

                events.AddRange(updateSuccess.Value);
                state = updateSuccess.Value.Aggregate(state, ModelState.Evolve);

                if (existingElement.FactStatus != factStatus ||
                    !ProvenanceEquivalent(existingElement.Provenance, provenance))
                {
                    var provenanceResult = ModelDecider.RecordElementProvenance(state, el.Id, factStatus, provenance);
                    if (provenanceResult is not CommandResult<IReadOnlyList<object>>.Success provenanceSuccess)
                        return Results.BadRequest(new ErrorResponse("invalid_export",
                            $"Element {el.Id}: {DeciderFailure(provenanceResult)}"));

                    events.AddRange(provenanceSuccess.Value);
                    state = provenanceSuccess.Value.Aggregate(state, ModelState.Evolve);
                }
            }
            else
            {
                var addCmd = new AddElementCommand(el.Id, kind, el.Name, el.Description,
                    el.Technology, null, status, el.ParentId, el.Tags);
                var result = ModelDecider.AddElement(state, addCmd);
                if (result is not CommandResult<IReadOnlyList<object>>.Success added)
                    return Results.BadRequest(new ErrorResponse("invalid_export",
                        $"Element {el.Id}: {DeciderFailure(result)}"));

                events.AddRange(added.Value);
                state = added.Value.Aggregate(state, ModelState.Evolve);

                if (el.Icon is not null)
                {
                    events.Add(new ModelEvent.ElementIconChanged(el.Id, el.Icon));
                    state = ModelState.Evolve(state, events[^1]);
                }

                if (factStatus != FactStatus.Declared || provenance is not null)
                {
                    events.Add(new ModelEvent.ElementProvenanceRecorded(el.Id, factStatus, provenance));
                    state = ModelState.Evolve(state, events[^1]);
                }
            }
        }

        // Import relationships
        foreach (var rel in doc.Relationships)
        {
            if (!TryReadFacts(rel.FactStatus, rel.Provenance, out var factStatus, out var provenance, out var factError))
                return Results.BadRequest(new ErrorResponse("invalid_export", $"Relationship {rel.Id}: {factError}"));

            var kind = RelationshipKind.Structural;
            if (!string.IsNullOrEmpty(rel.Kind) && !ModelEnum.TryParseDefined(rel.Kind, out kind))
            {
                return Results.BadRequest(new ErrorResponse("invalid_export",
                    $"Relationship {rel.Id}: invalid kind '{rel.Kind}'"));
            }

            if (state.Relationships.TryGetValue(rel.Id, out var existingRelationship))
            {
                var updateCmd = new UpdateRelationshipCommand(rel.Id,
                    Label: rel.Label, SetLabel: true,
                    Technology: rel.Technology, SetTechnology: true,
                    LineShape: rel.LineShape, SetLineShape: true,
                    Kind: kind, SetKind: true);
                var result = ModelDecider.UpdateRelationship(state, updateCmd);
                if (result is not CommandResult<IReadOnlyList<object>>.Success updateSuccess)
                    return Results.BadRequest(new ErrorResponse("invalid_export",
                        $"Relationship {rel.Id}: {DeciderFailure(result)}"));

                events.AddRange(updateSuccess.Value);
                state = updateSuccess.Value.Aggregate(state, ModelState.Evolve);

                if (existingRelationship.FactStatus != factStatus ||
                    !ProvenanceEquivalent(existingRelationship.Provenance, provenance))
                {
                    var provenanceResult = ModelDecider.RecordRelationshipProvenance(state, rel.Id, factStatus, provenance);
                    if (provenanceResult is not CommandResult<IReadOnlyList<object>>.Success provenanceSuccess)
                        return Results.BadRequest(new ErrorResponse("invalid_export",
                            $"Relationship {rel.Id}: {DeciderFailure(provenanceResult)}"));

                    events.AddRange(provenanceSuccess.Value);
                    state = provenanceSuccess.Value.Aggregate(state, ModelState.Evolve);
                }
            }
            else
            {
                var addCmd = new AddRelationshipCommand(rel.Id, rel.FromId, rel.ToId, rel.Label, rel.Technology, kind);
                var result = ModelDecider.AddRelationship(state, addCmd);
                if (result is not CommandResult<IReadOnlyList<object>>.Success added)
                    return Results.BadRequest(new ErrorResponse("invalid_export",
                        $"Relationship {rel.Id}: {DeciderFailure(result)}"));

                events.AddRange(added.Value);
                state = added.Value.Aggregate(state, ModelState.Evolve);

                if (rel.LineShape is not null)
                {
                    events.Add(new ModelEvent.RelationshipLineShapeChanged(rel.Id, rel.LineShape));
                    state = ModelState.Evolve(state, events[^1]);
                }

                if (factStatus != FactStatus.Declared || provenance is not null)
                {
                    events.Add(new ModelEvent.RelationshipProvenanceRecorded(rel.Id, factStatus, provenance));
                    state = ModelState.Evolve(state, events[^1]);
                }
            }
        }

        // Import messages
        foreach (var msg in doc.Messages)
        {
            if (!TryReadFacts(msg.FactStatus, msg.Provenance, out var factStatus, out var provenance, out var factError))
                return Results.BadRequest(new ErrorResponse("invalid_export", $"Message {msg.Id}: {factError}"));

            if (msg.SchemaId is { } schemaId && !knownSchemaIds.Contains(schemaId))
            {
                return Results.BadRequest(new ErrorResponse("invalid_export",
                    $"Message {msg.Id}: schema {schemaId} not found in this project or the document"));
            }

            if (state.Messages.TryGetValue(msg.Id, out var existingMessage))
            {
                var updateCmd = new UpdateMessageCommand(msg.Id,
                    Name: msg.Name, SetName: true,
                    Description: msg.Description, SetDescription: true,
                    ProducerId: msg.ProducerId, SetProducerId: true,
                    ConsumerIds: msg.ConsumerIds.ToElementIds(), SetConsumerIds: true,
                    SchemaId: msg.SchemaId, SetSchemaId: true,
                    Tags: msg.Tags);
                var result = ModelDecider.UpdateMessage(state, updateCmd);
                if (result is not CommandResult<IReadOnlyList<object>>.Success updateSuccess)
                    return Results.BadRequest(new ErrorResponse("invalid_export",
                        $"Message {msg.Id}: {DeciderFailure(result)}"));

                events.AddRange(updateSuccess.Value);
                state = updateSuccess.Value.Aggregate(state, ModelState.Evolve);

                if (existingMessage.FactStatus != factStatus ||
                    !ProvenanceEquivalent(existingMessage.Provenance, provenance))
                {
                    var provenanceResult = ModelDecider.RecordMessageProvenance(state, msg.Id, factStatus, provenance);
                    if (provenanceResult is not CommandResult<IReadOnlyList<object>>.Success provenanceSuccess)
                        return Results.BadRequest(new ErrorResponse("invalid_export",
                            $"Message {msg.Id}: {DeciderFailure(provenanceResult)}"));

                    events.AddRange(provenanceSuccess.Value);
                    state = provenanceSuccess.Value.Aggregate(state, ModelState.Evolve);
                }
            }
            else
            {
                var addCmd = new AddMessageCommand(msg.Id, msg.Name, msg.Description,
                    msg.ProducerId, msg.ConsumerIds.ToElementIds(), msg.SchemaId, msg.Tags);
                var result = ModelDecider.AddMessage(state, addCmd);
                if (result is not CommandResult<IReadOnlyList<object>>.Success added)
                    return Results.BadRequest(new ErrorResponse("invalid_export",
                        $"Message {msg.Id}: {DeciderFailure(result)}"));

                events.AddRange(added.Value);
                state = added.Value.Aggregate(state, ModelState.Evolve);

                if (factStatus != FactStatus.Declared || provenance is not null)
                {
                    events.Add(new ModelEvent.MessageProvenanceRecorded(msg.Id, factStatus, provenance));
                    state = ModelState.Evolve(state, events[^1]);
                }
            }
        }

        // Import contracts. Channels come first: a subscription names its channels, and both
        // operations and subscriptions name elements that are already in the state.
        foreach (var channel in doc.Channels)
        {
            var label = $"Channel {channel.Id}";
            if (!TryReadFacts(channel.FactStatus, channel.Provenance, out var factStatus, out var provenance, out var factError))
                return Results.BadRequest(new ErrorResponse("invalid_export", $"{label}: {factError}"));

            if (channel.Kind is null || !ModelEnum.TryParseDefined(channel.Kind, out ChannelKind kind))
                return Results.BadRequest(new ErrorResponse("invalid_export", $"{label}: invalid kind '{channel.Kind}'"));

            ChannelId id = channel.Id;
            var decision = state.Channels.ContainsKey(id)
                ? ContractDecider.UpdateChannel(state, new UpdateChannelCommand(id, channel.Name, kind))
                : ContractDecider.AddChannel(state, new AddChannelCommand(id, channel.Name, kind));
            if (Apply(label, decision.Then(state, after =>
                    ContractDecider.RecordChannelProvenance(after, id, factStatus, provenance))) is { } failure)
                return failure;
        }

        foreach (var operation in doc.Operations)
        {
            var label = $"Operation {operation.Id}";
            if (!TryReadFacts(operation.FactStatus, operation.Provenance, out var factStatus, out var provenance, out var factError))
                return Results.BadRequest(new ErrorResponse("invalid_export", $"{label}: {factError}"));

            if (operation.Transport is null || !ModelEnum.TryParseDefined(operation.Transport, out Transport transport))
                return Results.BadRequest(new ErrorResponse("invalid_export", $"{label}: invalid transport '{operation.Transport}'"));

            foreach (var schemaId in new[] { operation.RequestSchemaId, operation.ResponseSchemaId })
            {
                if (schemaId is { } referenced && !knownSchemaIds.Contains(referenced))
                {
                    return Results.BadRequest(new ErrorResponse("invalid_export",
                        $"{label}: schema {referenced} not found in this project or the document"));
                }
            }

            OperationId id = operation.Id;
            var retry = operation.Retry is { } r ? new RetryPolicy(r.MaxAttempts, r.BackoffMs) : RetryPolicy.None;
            var decision = state.Operations.ContainsKey(id)
                ? ContractDecider.UpdateOperation(state, new UpdateOperationCommand(
                    id, operation.Name, new ElementId(operation.OwnerId), transport,
                    operation.RequestSchemaId, true, operation.ResponseSchemaId, true,
                    operation.TimeoutMs, retry))
                : ContractDecider.AddOperation(state, new AddOperationCommand(
                    id, operation.OwnerId, operation.Name, transport,
                    operation.RequestSchemaId, operation.ResponseSchemaId, operation.TimeoutMs, retry));
            if (Apply(label, decision.Then(state, after =>
                    ContractDecider.RecordOperationProvenance(after, id, factStatus, provenance))) is { } failure)
                return failure;
        }

        foreach (var subscription in doc.Subscriptions)
        {
            var label = $"Subscription {subscription.Id}";
            if (!TryReadFacts(subscription.FactStatus, subscription.Provenance, out var factStatus, out var provenance, out var factError))
                return Results.BadRequest(new ErrorResponse("invalid_export", $"{label}: {factError}"));

            SubscriptionId id = subscription.Id;
            var retry = subscription.Retry is { } r ? new RetryPolicy(r.MaxAttempts, r.BackoffMs) : RetryPolicy.None;
            ChannelId? deadLetter = subscription.DeadLetterChannelId is { } d ? new ChannelId(d) : null;
            var decision = state.Subscriptions.ContainsKey(id)
                ? ContractDecider.UpdateSubscription(state, new UpdateSubscriptionCommand(
                    id, new ChannelId(subscription.ChannelId), new ElementId(subscription.ConsumerId), retry,
                    deadLetter, SetDeadLetterChannelId: true))
                : ContractDecider.AddSubscription(state, new AddSubscriptionCommand(
                    id, subscription.ChannelId, subscription.ConsumerId, retry, deadLetter));
            if (Apply(label, decision.Then(state, after =>
                    ContractDecider.RecordSubscriptionProvenance(after, id, factStatus, provenance))) is { } failure)
                return failure;
        }

        // Adds a decision's events to the import, or reports why the document is refused.
        IResult? Apply(string label, CommandResult<IReadOnlyList<object>> decision)
        {
            if (decision is not CommandResult<IReadOnlyList<object>>.Success success)
                return Results.BadRequest(new ErrorResponse("invalid_export", $"{label}: {DeciderFailure(decision)}"));

            events.AddRange(success.Value);
            state = success.Value.Aggregate(state, ModelState.Evolve);
            return null;
        }

        // Schemas: each is its own stream.
        var schemaAppends = new List<(Guid Id, long Version, IReadOnlyList<object> Events)>();
        foreach (var schema in doc.Schemas.OrderBy(s => s.Id))
        {
            var (schemaState, schemaVersion) = schemaStreams[schema.Id];
            CommandResult<IReadOnlyList<object>> decision;
            if (schemaState.Schema is not null)
            {
                if (schemaState.Schema.ProjectId != projectId)
                {
                    return Results.BadRequest(new ErrorResponse("invalid_export",
                        $"Schema {schema.Id} belongs to another project"));
                }

                decision = SchemaDecider.Update(schemaState, new UpdateSchemaCommand(schema.Id,
                    Name: schema.Name, SetName: true,
                    Description: schema.Description, SetDescription: true,
                    Content: schema.Content, SetContent: true));
            }
            else
            {
                decision = SchemaDecider.Create(schemaState,
                    new CreateSchemaCommand(schema.Id, schema.Name, schema.Description, schema.Content, projectId));
            }

            if (decision is not CommandResult<IReadOnlyList<object>>.Success decided)
            {
                return Results.BadRequest(new ErrorResponse("invalid_export",
                    $"Schema {schema.Id}: {DeciderFailure(decision)}"));
            }

            if (decided.Value.Count > 0)
                schemaAppends.Add((schema.Id, schemaVersion, decided.Value));
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
        return Results.Ok();
    }

    /// <summary>The body as text, or null when it holds more than <see cref="MaxBytes"/> bytes.</summary>
    private static async Task<string?> ReadBodyAsync(Stream body, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await body.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > MaxBytes)
                return null;
            buffer.Write(chunk, 0, read);
        }

        buffer.Position = 0;
        using var reader = new StreamReader(buffer);
        return await reader.ReadToEndAsync(ct);
    }

    private static string DeciderFailure(CommandResult<IReadOnlyList<object>> result) => result switch
    {
        CommandResult<IReadOnlyList<object>>.Invalid invalid =>
            string.Join("; ", invalid.Errors.Select(e => $"{e.Field}: {e.Message}")),
        CommandResult<IReadOnlyList<object>>.Conflict conflict => conflict.Message,
        CommandResult<IReadOnlyList<object>>.NotFound notFound => notFound.Message,
        _ => "invalid value",
    };

    private static bool TryReadFacts(
        string? factStatusText, ProvenanceExport? provenanceExport,
        out FactStatus factStatus, out Provenance? provenance, out string? error)
    {
        factStatus = FactStatus.Declared;
        provenance = provenanceExport?.ToDomain();
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

        return true;
    }

    private static bool ProvenanceEquivalent(Provenance? a, Provenance? b) =>
        a?.Source == b?.Source && a?.Revision == b?.Revision && a?.Confidence == b?.Confidence;
}
