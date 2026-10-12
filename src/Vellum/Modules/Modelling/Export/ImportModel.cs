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

    public static async Task<IResult> Handle(
        Guid projectId,
        HttpRequest request,
        WorkspacesDbContext workspacesDb,
        CancellationToken ct)
    {
        var project = await workspacesDb.Projects.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == projectId, ct);
        if (project is null) return Results.NotFound();

        var body = await new StreamReader(request.Body).ReadToEndAsync(ct);
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

        // The import appends to the model stream and to one stream per schema. A transaction
        // with several appends can lose a lock cycle against a concurrent append, so the whole
        // import runs again when the database aborts it.
        return await SharedTransaction.RunAsync(request.HttpContext.RequestServices,
            services => ImportAsync(services, projectId, project.StreamId, doc, ct), ct);
    }

    private static async Task<IResult> ImportAsync(
        IServiceProvider services, Guid projectId, Guid modelStreamId, ModelExportDocument doc, CancellationToken ct)
    {
        var modellingDb = services.GetRequiredService<ModellingDbContext>();
        var schemasDb = services.GetRequiredService<SchemasDbContext>();

        var documentSchemaIds = doc.Schemas.Select(s => s.Id).ToHashSet();

        // Every load, append and read-model write below runs in this transaction. A return
        // before the commit rolls all of it back.
        await using var transaction = await modellingDb.Database.BeginTransactionAsync(ct);
        var store = services.GetRequiredService<IEventStore>().UseDbContext(modellingDb);

        // Load current model state
        var (state, version) = await store.Load<ModelState>(StreamId.From(modelStreamId), ct);
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

            if (msg.SchemaId is { } schemaId &&
                !await SchemaExistsAsync(projectId, schemaId, documentSchemaIds, schemasDb, ct))
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
                if (schemaId is { } referenced &&
                    !await SchemaExistsAsync(projectId, referenced, documentSchemaIds, schemasDb, ct))
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

        if (events.Count > 0)
        {
            await store.ForProject(projectId).Append(
                StreamId.From(modelStreamId), ExpectedVersion.Exact(version), events, ct);
        }

        // Import schemas (each is its own aggregate)
        foreach (var schema in doc.Schemas)
        {
            var (schemaState, schemaVersion) = await store.Load<SchemaState>(StreamId.From(schema.Id), ct);
            if (schemaState.Schema is not null)
            {
                if (schemaState.Schema.ProjectId != projectId)
                {
                    return Results.BadRequest(new ErrorResponse("invalid_export",
                        $"Schema {schema.Id} belongs to another project"));
                }

                var updateCmd = new UpdateSchemaCommand(schema.Id,
                    Name: schema.Name, SetName: true,
                    Description: schema.Description, SetDescription: true,
                    Content: schema.Content, SetContent: true);
                var result = SchemaDecider.Update(schemaState, updateCmd);
                if (result is not CommandResult<IReadOnlyList<object>>.Success updateSuccess)
                    return Results.BadRequest(new ErrorResponse("invalid_export",
                        $"Schema {schema.Id}: {DeciderFailure(result)}"));

                if (updateSuccess.Value.Count > 0)
                {
                    await store.Append(
                        StreamId.From(schema.Id), ExpectedVersion.Exact(schemaVersion), updateSuccess.Value, ct);
                }
            }
            else
            {
                var createCmd = new CreateSchemaCommand(schema.Id, schema.Name, schema.Description, schema.Content, projectId);
                var result = SchemaDecider.Create(schemaState, createCmd);
                if (result is not CommandResult<IReadOnlyList<object>>.Success created)
                    return Results.BadRequest(new ErrorResponse("invalid_export",
                        $"Schema {schema.Id}: {DeciderFailure(result)}"));

                await store.Append(
                    StreamId.From(schema.Id), ExpectedVersion.Exact(schemaVersion), created.Value, ct);
            }
        }

        await transaction.CommitAsync(ct);
        return Results.Ok();
    }

    private static async Task<bool> SchemaExistsAsync(
        Guid projectId, Guid schemaId, HashSet<Guid> documentSchemaIds,
        SchemasDbContext schemasDb, CancellationToken ct) =>
        documentSchemaIds.Contains(schemaId) ||
        await schemasDb.Schemas.AsNoTracking()
            .AnyAsync(s => s.Id == schemaId && s.ProjectId == projectId, ct);

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
