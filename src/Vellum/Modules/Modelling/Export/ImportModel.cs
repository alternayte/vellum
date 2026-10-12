using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Vellum.Kernel.Aggregates;
using Vellum.Kernel.CommandHandling;
using Vellum.Kernel.EventStore;
using Vellum.Kernel.Projections;
using Vellum.Kernel.Results;
using Vellum.Modules.Modelling.Model;
using Vellum.Modules.Schemas;
using Vellum.Modules.Workspaces;
using Vellum.Shared;
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
        ModellingDbContext modellingDb,
        SchemasDbContext schemasDb,
        WorkspacesDbContext workspacesDb,
        AggregateStore store,
        EventCollector collector,
        EventStoreDbContext eventStoreDb,
        IEnumerable<IInlineProjection> projections,
        ModelProjection modelProjection,
        Guid actorId,
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
        };

        collector.Clear();

        var documentSchemaIds = doc.Schemas.Select(s => s.Id).ToHashSet();

        await using var transaction = await eventStoreDb.Database.BeginTransactionAsync(ct);
        try
        {
            // Load current model state
            var (state, version) = await store.LoadAsync<ModelState, ModelEvent>(project.StreamId, ct);
            var events = new List<ModelEvent>();

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
                    if (result is not CommandResult<IReadOnlyList<ModelEvent>>.Success updateSuccess)
                        return Results.BadRequest(new ErrorResponse("invalid_export",
                            $"Element {el.Id}: {DeciderFailure(result)}"));

                    events.AddRange(updateSuccess.Value);
                    state = updateSuccess.Value.Aggregate(state, (st, e) => st.Evolve(e));

                    if (existingElement.FactStatus != factStatus ||
                        !ProvenanceEquivalent(existingElement.Provenance, provenance))
                    {
                        var provenanceResult = ModelDecider.RecordElementProvenance(state, el.Id, factStatus, provenance);
                        if (provenanceResult is not CommandResult<IReadOnlyList<ModelEvent>>.Success provenanceSuccess)
                            return Results.BadRequest(new ErrorResponse("invalid_export",
                                $"Element {el.Id}: {DeciderFailure(provenanceResult)}"));

                        events.AddRange(provenanceSuccess.Value);
                        state = provenanceSuccess.Value.Aggregate(state, (st, e) => st.Evolve(e));
                    }
                }
                else
                {
                    var addCmd = new AddElementCommand(el.Id, kind, el.Name, el.Description,
                        el.Technology, null, status, el.ParentId, el.Tags);
                    var result = ModelDecider.AddElement(state, addCmd);
                    if (result is not CommandResult<IReadOnlyList<ModelEvent>>.Success added)
                        return Results.BadRequest(new ErrorResponse("invalid_export",
                            $"Element {el.Id}: {DeciderFailure(result)}"));

                    events.AddRange(added.Value);
                    state = added.Value.Aggregate(state, (st, e) => st.Evolve(e));

                    if (el.Icon is not null)
                    {
                        events.Add(new ModelEvent.ElementIconChanged(el.Id, el.Icon));
                        state = state.Evolve(events[^1]);
                    }

                    if (factStatus != FactStatus.Declared || provenance is not null)
                    {
                        events.Add(new ModelEvent.ElementProvenanceRecorded(el.Id, factStatus, provenance));
                        state = state.Evolve(events[^1]);
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
                    if (result is not CommandResult<IReadOnlyList<ModelEvent>>.Success updateSuccess)
                        return Results.BadRequest(new ErrorResponse("invalid_export",
                            $"Relationship {rel.Id}: {DeciderFailure(result)}"));

                    events.AddRange(updateSuccess.Value);
                    state = updateSuccess.Value.Aggregate(state, (st, e) => st.Evolve(e));

                    if (existingRelationship.FactStatus != factStatus ||
                        !ProvenanceEquivalent(existingRelationship.Provenance, provenance))
                    {
                        var provenanceResult = ModelDecider.RecordRelationshipProvenance(state, rel.Id, factStatus, provenance);
                        if (provenanceResult is not CommandResult<IReadOnlyList<ModelEvent>>.Success provenanceSuccess)
                            return Results.BadRequest(new ErrorResponse("invalid_export",
                                $"Relationship {rel.Id}: {DeciderFailure(provenanceResult)}"));

                        events.AddRange(provenanceSuccess.Value);
                        state = provenanceSuccess.Value.Aggregate(state, (st, e) => st.Evolve(e));
                    }
                }
                else
                {
                    var addCmd = new AddRelationshipCommand(rel.Id, rel.FromId, rel.ToId, rel.Label, rel.Technology, kind);
                    var result = ModelDecider.AddRelationship(state, addCmd);
                    if (result is not CommandResult<IReadOnlyList<ModelEvent>>.Success added)
                        return Results.BadRequest(new ErrorResponse("invalid_export",
                            $"Relationship {rel.Id}: {DeciderFailure(result)}"));

                    events.AddRange(added.Value);
                    state = added.Value.Aggregate(state, (st, e) => st.Evolve(e));

                    if (rel.LineShape is not null)
                    {
                        events.Add(new ModelEvent.RelationshipLineShapeChanged(rel.Id, rel.LineShape));
                        state = state.Evolve(events[^1]);
                    }

                    if (factStatus != FactStatus.Declared || provenance is not null)
                    {
                        events.Add(new ModelEvent.RelationshipProvenanceRecorded(rel.Id, factStatus, provenance));
                        state = state.Evolve(events[^1]);
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
                    if (result is not CommandResult<IReadOnlyList<ModelEvent>>.Success updateSuccess)
                        return Results.BadRequest(new ErrorResponse("invalid_export",
                            $"Message {msg.Id}: {DeciderFailure(result)}"));

                    events.AddRange(updateSuccess.Value);
                    state = updateSuccess.Value.Aggregate(state, (st, e) => st.Evolve(e));

                    if (existingMessage.FactStatus != factStatus ||
                        !ProvenanceEquivalent(existingMessage.Provenance, provenance))
                    {
                        var provenanceResult = ModelDecider.RecordMessageProvenance(state, msg.Id, factStatus, provenance);
                        if (provenanceResult is not CommandResult<IReadOnlyList<ModelEvent>>.Success provenanceSuccess)
                            return Results.BadRequest(new ErrorResponse("invalid_export",
                                $"Message {msg.Id}: {DeciderFailure(provenanceResult)}"));

                        events.AddRange(provenanceSuccess.Value);
                        state = provenanceSuccess.Value.Aggregate(state, (st, e) => st.Evolve(e));
                    }
                }
                else
                {
                    var addCmd = new AddMessageCommand(msg.Id, msg.Name, msg.Description,
                        msg.ProducerId, msg.ConsumerIds.ToElementIds(), msg.SchemaId, msg.Tags);
                    var result = ModelDecider.AddMessage(state, addCmd);
                    if (result is not CommandResult<IReadOnlyList<ModelEvent>>.Success added)
                        return Results.BadRequest(new ErrorResponse("invalid_export",
                            $"Message {msg.Id}: {DeciderFailure(result)}"));

                    events.AddRange(added.Value);
                    state = added.Value.Aggregate(state, (st, e) => st.Evolve(e));

                    if (factStatus != FactStatus.Declared || provenance is not null)
                    {
                        events.Add(new ModelEvent.MessageProvenanceRecorded(msg.Id, factStatus, provenance));
                        state = state.Evolve(events[^1]);
                    }
                }
            }

            // Save model events (EventStore.AppendAsync adds them to collector)
            if (events.Count > 0)
            {
                var metadata = new EventMetadata { ActorId = actorId, CorrelationId = Guid.NewGuid() };
                modelProjection.SetContext(projectId, project.StreamId);
                await store.SaveAsync(project.StreamId, "model", version, state, events, metadata, ct);
            }

            // Import schemas (each is its own aggregate)
            foreach (var schema in doc.Schemas)
            {
                var (schemaState, schemaVersion) = await store.LoadAsync<SchemaState, SchemaEvent>(schema.Id, ct);
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
                    if (result is not CommandResult<IReadOnlyList<SchemaEvent>>.Success updateSuccess)
                        return Results.BadRequest(new ErrorResponse("invalid_export",
                            $"Schema {schema.Id}: {DeciderFailure(result)}"));

                    if (updateSuccess.Value.Count > 0)
                    {
                        var newState = updateSuccess.Value.Aggregate(schemaState, (st, e) => st.Evolve(e));
                        await store.SaveAsync(schema.Id, "schema", schemaVersion, newState, updateSuccess.Value,
                            new EventMetadata { ActorId = actorId, CorrelationId = Guid.NewGuid() }, ct);
                    }
                }
                else
                {
                    var createCmd = new CreateSchemaCommand(schema.Id, schema.Name, schema.Description, schema.Content, projectId);
                    var result = SchemaDecider.Create(schemaState, createCmd);
                    if (result is not CommandResult<IReadOnlyList<SchemaEvent>>.Success created)
                        return Results.BadRequest(new ErrorResponse("invalid_export",
                            $"Schema {schema.Id}: {DeciderFailure(result)}"));

                    var newState = created.Value.Aggregate(schemaState, (st, e) => st.Evolve(e));
                    await store.SaveAsync(schema.Id, "schema", schemaVersion, newState, created.Value,
                        new EventMetadata { ActorId = actorId, CorrelationId = Guid.NewGuid() }, ct);
                }
            }

            // Fire inline projections (ModelProjection + SchemaProjection) for all collected events
            var collectedEvents = collector.Events;
            foreach (var projection in projections)
                await projection.ProjectAsync(collectedEvents, ct);

            await transaction.CommitAsync(ct);

            if (collectedEvents.Count > 0)
                await eventStoreDb.Database.ExecuteSqlRawAsync("NOTIFY new_events", ct);
        }
        catch
        {
            await transaction.RollbackAsync(ct);
            throw;
        }

        return Results.Ok();
    }

    private static async Task<bool> SchemaExistsAsync(
        Guid projectId, Guid schemaId, HashSet<Guid> documentSchemaIds,
        SchemasDbContext schemasDb, CancellationToken ct) =>
        documentSchemaIds.Contains(schemaId) ||
        await schemasDb.Schemas.AsNoTracking()
            .AnyAsync(s => s.Id == schemaId && s.ProjectId == projectId, ct);

    private static string DeciderFailure(object result) => result switch
    {
        CommandResult<IReadOnlyList<ModelEvent>>.Invalid invalid =>
            string.Join("; ", invalid.Errors.Select(e => $"{e.Field}: {e.Message}")),
        CommandResult<IReadOnlyList<ModelEvent>>.Conflict conflict => conflict.Message,
        CommandResult<IReadOnlyList<ModelEvent>>.NotFound notFound => notFound.Message,
        CommandResult<IReadOnlyList<SchemaEvent>>.Invalid invalid =>
            string.Join("; ", invalid.Errors.Select(e => $"{e.Field}: {e.Message}")),
        CommandResult<IReadOnlyList<SchemaEvent>>.Conflict conflict => conflict.Message,
        CommandResult<IReadOnlyList<SchemaEvent>>.NotFound notFound => notFound.Message,
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
