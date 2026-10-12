using Microsoft.EntityFrameworkCore;
using Vellum.Kernel.Aggregates;
using Vellum.Kernel.CommandHandling;
using Vellum.Kernel.Results;
using Vellum.Modules.Modelling.Model;
using Vellum.Modules.Schemas;

namespace Vellum.Modules.Modelling.Messages;

public sealed record UpdateMessageRequest(
    string? Name, string? Description,
    Guid? ProducerId, Guid[]? ConsumerIds,
    Guid? SchemaId, bool SetSchemaId = false,
    bool SetDescription = false, bool SetProducerId = false,
    bool SetConsumerIds = false, string[]? Tags = null,
    string? FactStatus = null, ProvenanceInput? Provenance = null, bool SetProvenance = false);

public sealed record UpdateMessageCommandEnvelope(
    Guid ProjectId, Guid StreamId, Guid MessageId, string UserId, UpdateMessageRequest Request);

public sealed class UpdateMessageHandler : ICommandHandler<UpdateMessageCommandEnvelope, CommandResult<MessageDto>>
{
    private readonly AggregateStore _store;
    private readonly ModelProjection _projection;
    private readonly SchemasDbContext _schemasDb;

    public UpdateMessageHandler(AggregateStore store, ModelProjection projection, SchemasDbContext schemasDb)
    {
        _store = store;
        _projection = projection;
        _schemasDb = schemasDb;
    }

    public async Task<CommandResult<MessageDto>> HandleAsync(UpdateMessageCommandEnvelope cmd, CancellationToken ct = default)
    {
        if (cmd.Request.SetSchemaId && cmd.Request.SchemaId.HasValue &&
            !await _schemasDb.Schemas.AsNoTracking()
                .AnyAsync(s => s.Id == cmd.Request.SchemaId.Value && s.ProjectId == cmd.ProjectId, ct))
        {
            return new CommandResult<MessageDto>.Invalid(
                [new ValidationError("schemaId", "Schema not found in this project")]);
        }

        var (state, version) = await _store.LoadAsync<ModelState, ModelEvent>(cmd.StreamId, ct);

        var updateCmd = new UpdateMessageCommand(
            cmd.MessageId,
            Name: cmd.Request.Name, SetName: cmd.Request.Name is not null,
            Description: cmd.Request.Description, SetDescription: cmd.Request.SetDescription,
            ProducerId: cmd.Request.ProducerId, SetProducerId: cmd.Request.SetProducerId,
            ConsumerIds: cmd.Request.ConsumerIds?.ToElementIds(), SetConsumerIds: cmd.Request.SetConsumerIds,
            SchemaId: cmd.Request.SchemaId, SetSchemaId: cmd.Request.SetSchemaId,
            Tags: cmd.Request.Tags);

        var result = ModelDecider.UpdateMessage(state, updateCmd);
        if (result is not CommandResult<IReadOnlyList<ModelEvent>>.Success success)
        {
            return result switch
            {
                CommandResult<IReadOnlyList<ModelEvent>>.Invalid inv => new CommandResult<MessageDto>.Invalid(inv.Errors),
                CommandResult<IReadOnlyList<ModelEvent>>.NotFound n => new CommandResult<MessageDto>.NotFound(n.Message),
                _ => new CommandResult<MessageDto>.Conflict("Unexpected error")
            };
        }

        var events = new List<ModelEvent>(success.Value);
        var stateAfter = events.Aggregate(state, (s, e) => s.Evolve(e));

        FactStatus? requestedStatus = null;
        if (cmd.Request.FactStatus is not null)
        {
            if (!FactStatusFormat.TryParseApiString(cmd.Request.FactStatus, out var parsedStatus))
                return new CommandResult<MessageDto>.Invalid([new ValidationError("factStatus", "Invalid fact status")]);
            requestedStatus = parsedStatus;
        }

        if (requestedStatus is not null || cmd.Request.SetProvenance)
        {
            var current = stateAfter.Messages[cmd.MessageId];
            var factStatus = requestedStatus ?? current.FactStatus;
            var provenance = cmd.Request.SetProvenance
                ? ProvenanceMapper.ToDomain(cmd.Request.Provenance, Guid.Parse(cmd.UserId))
                : current.Provenance;

            var provenanceResult = ModelDecider.RecordMessageProvenance(stateAfter, cmd.MessageId, factStatus, provenance);
            if (provenanceResult is not CommandResult<IReadOnlyList<ModelEvent>>.Success provenanceSuccess)
            {
                return provenanceResult switch
                {
                    CommandResult<IReadOnlyList<ModelEvent>>.Invalid inv => new CommandResult<MessageDto>.Invalid(inv.Errors),
                    CommandResult<IReadOnlyList<ModelEvent>>.NotFound n => new CommandResult<MessageDto>.NotFound(n.Message),
                    _ => new CommandResult<MessageDto>.Conflict("Unexpected error")
                };
            }

            events.AddRange(provenanceSuccess.Value);
            stateAfter = provenanceSuccess.Value.Aggregate(stateAfter, (s, e) => s.Evolve(e));
        }

        if (events.Count > 0)
        {
            var metadata = new EventMetadata { ActorId = Guid.Parse(cmd.UserId), CorrelationId = Guid.NewGuid() };
            _projection.SetContext(cmd.ProjectId, cmd.StreamId);
            await _store.SaveAsync(cmd.StreamId, "model", version, stateAfter, events, metadata, ct);
            state = stateAfter;
        }

        return new CommandResult<MessageDto>.Success(AddMessageHandler.ToDto(state.Messages[cmd.MessageId]));
    }
}
