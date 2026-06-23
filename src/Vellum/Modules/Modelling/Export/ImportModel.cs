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
        ModelExportDocument doc;

        var contentType = request.ContentType ?? "application/json";
        if (contentType.Contains("yaml"))
        {
            doc = YamlDeserializer.Deserialize<ModelExportDocument>(body);
        }
        else
        {
            doc = JsonSerializer.Deserialize<ModelExportDocument>(body, JsonOpts)!;
        }

        if (doc is null)
            return Results.BadRequest("Invalid export document");

        collector.Clear();

        await using var transaction = await eventStoreDb.Database.BeginTransactionAsync(ct);
        try
        {
            // Load current model state
            var (state, version) = await store.LoadAsync<ModelState, ModelEvent>(project.StreamId, ct);
            var events = new List<ModelEvent>();

            // Import elements
            foreach (var el in doc.Elements)
            {
                if (state.Elements.ContainsKey(el.Id))
                {
                    var status = Enum.Parse<ElementStatus>(el.Status, ignoreCase: true);
                    var updateCmd = new UpdateElementCommand(el.Id,
                        Name: el.Name, SetName: true,
                        Description: el.Description, SetDescription: true,
                        Technology: el.Technology, SetTechnology: true,
                        ParentId: el.ParentId, SetParentId: true,
                        Status: status, Tags: el.Tags);
                    var result = ModelDecider.UpdateElement(state, updateCmd);
                    if (result is CommandResult<IReadOnlyList<ModelEvent>>.Success s && s.Value.Count > 0)
                    {
                        events.AddRange(s.Value);
                        state = s.Value.Aggregate(state, (st, e) => st.Evolve(e));
                    }
                }
                else
                {
                    var kind = Enum.Parse<ElementKind>(el.Kind, ignoreCase: true);
                    var status = Enum.Parse<ElementStatus>(el.Status, ignoreCase: true);
                    var addCmd = new AddElementCommand(el.Id, kind, el.Name, el.Description,
                        el.Technology, null, status, el.ParentId, el.Tags);
                    var result = ModelDecider.AddElement(state, addCmd);
                    if (result is CommandResult<IReadOnlyList<ModelEvent>>.Success s)
                    {
                        events.AddRange(s.Value);
                        state = s.Value.Aggregate(state, (st, e) => st.Evolve(e));
                    }
                }
            }

            // Import relationships
            foreach (var rel in doc.Relationships)
            {
                if (state.Relationships.ContainsKey(rel.Id))
                {
                    var updateCmd = new UpdateRelationshipCommand(rel.Id,
                        Label: rel.Label, SetLabel: true,
                        Technology: rel.Technology, SetTechnology: true);
                    var result = ModelDecider.UpdateRelationship(state, updateCmd);
                    if (result is CommandResult<IReadOnlyList<ModelEvent>>.Success s && s.Value.Count > 0)
                    {
                        events.AddRange(s.Value);
                        state = s.Value.Aggregate(state, (st, e) => st.Evolve(e));
                    }
                }
                else
                {
                    var addCmd = new AddRelationshipCommand(rel.Id, rel.FromId, rel.ToId, rel.Label, rel.Technology);
                    var result = ModelDecider.AddRelationship(state, addCmd);
                    if (result is CommandResult<IReadOnlyList<ModelEvent>>.Success s)
                    {
                        events.AddRange(s.Value);
                        state = s.Value.Aggregate(state, (st, e) => st.Evolve(e));
                    }
                }
            }

            // Import messages
            foreach (var msg in doc.Messages)
            {
                if (state.Messages.ContainsKey(msg.Id))
                {
                    var updateCmd = new UpdateMessageCommand(msg.Id,
                        Name: msg.Name, SetName: true,
                        Description: msg.Description, SetDescription: true,
                        ProducerId: msg.ProducerId, SetProducerId: true,
                        ConsumerIds: msg.ConsumerIds, SetConsumerIds: true,
                        SchemaId: msg.SchemaId, SetSchemaId: true,
                        Tags: msg.Tags);
                    var result = ModelDecider.UpdateMessage(state, updateCmd);
                    if (result is CommandResult<IReadOnlyList<ModelEvent>>.Success s && s.Value.Count > 0)
                    {
                        events.AddRange(s.Value);
                        state = s.Value.Aggregate(state, (st, e) => st.Evolve(e));
                    }
                }
                else
                {
                    var addCmd = new AddMessageCommand(msg.Id, msg.Name, msg.Description,
                        msg.ProducerId, msg.ConsumerIds, msg.SchemaId, msg.Tags);
                    var result = ModelDecider.AddMessage(state, addCmd);
                    if (result is CommandResult<IReadOnlyList<ModelEvent>>.Success s)
                    {
                        events.AddRange(s.Value);
                        state = s.Value.Aggregate(state, (st, e) => st.Evolve(e));
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
                    var updateCmd = new UpdateSchemaCommand(schema.Id,
                        Name: schema.Name, SetName: true,
                        Description: schema.Description, SetDescription: true,
                        Content: schema.Content, SetContent: true);
                    var result = SchemaDecider.Update(schemaState, updateCmd);
                    if (result is CommandResult<IReadOnlyList<SchemaEvent>>.Success s && s.Value.Count > 0)
                    {
                        var newState = s.Value.Aggregate(schemaState, (st, e) => st.Evolve(e));
                        await store.SaveAsync(schema.Id, "schema", schemaVersion, newState, s.Value,
                            new EventMetadata { ActorId = actorId, CorrelationId = Guid.NewGuid() }, ct);
                    }
                }
                else
                {
                    var createCmd = new CreateSchemaCommand(schema.Id, schema.Name, schema.Description, schema.Content, projectId);
                    var result = SchemaDecider.Create(schemaState, createCmd);
                    if (result is CommandResult<IReadOnlyList<SchemaEvent>>.Success s)
                    {
                        var newState = s.Value.Aggregate(schemaState, (st, e) => st.Evolve(e));
                        await store.SaveAsync(schema.Id, "schema", schemaVersion, newState, s.Value,
                            new EventMetadata { ActorId = actorId, CorrelationId = Guid.NewGuid() }, ct);
                    }
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
}
