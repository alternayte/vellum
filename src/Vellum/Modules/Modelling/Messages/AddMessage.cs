using Microsoft.EntityFrameworkCore;
using Vellum.Kernel.Aggregates;
using Vellum.Kernel.CommandHandling;
using Vellum.Kernel.Results;
using Vellum.Modules.Modelling.Model;
using Vellum.Modules.Schemas;

namespace Vellum.Modules.Modelling.Messages;

public sealed record AddMessageRequest(
    Guid Id, string Name, string? Description,
    Guid ProducerId, Guid[] ConsumerIds,
    Guid? SchemaId, string[]? Tags);

public sealed record AddMessageCommandEnvelope(
    Guid ProjectId, Guid StreamId, string UserId, AddMessageRequest Request);

public sealed class AddMessageHandler : ICommandHandler<AddMessageCommandEnvelope, CommandResult<MessageDto>>
{
    private readonly AggregateStore _store;
    private readonly ModelProjection _projection;
    private readonly SchemasDbContext _schemasDb;

    public AddMessageHandler(AggregateStore store, ModelProjection projection, SchemasDbContext schemasDb)
    {
        _store = store;
        _projection = projection;
        _schemasDb = schemasDb;
    }

    public async Task<CommandResult<MessageDto>> HandleAsync(AddMessageCommandEnvelope cmd, CancellationToken ct = default)
    {
        if (cmd.Request.SchemaId.HasValue &&
            !await _schemasDb.Schemas.AsNoTracking()
                .AnyAsync(s => s.Id == cmd.Request.SchemaId.Value && s.ProjectId == cmd.ProjectId, ct))
        {
            return new CommandResult<MessageDto>.Invalid(
                [new ValidationError("schemaId", "Schema not found in this project")]);
        }

        var (state, version) = await _store.LoadAsync<ModelState, ModelEvent>(cmd.StreamId, ct);

        var addCmd = new AddMessageCommand(
            cmd.Request.Id, cmd.Request.Name, cmd.Request.Description,
            cmd.Request.ProducerId, cmd.Request.ConsumerIds.ToElementIds(),
            cmd.Request.SchemaId, cmd.Request.Tags ?? []);

        var result = ModelDecider.AddMessage(state, addCmd);
        if (result is not CommandResult<IReadOnlyList<ModelEvent>>.Success success)
        {
            return result switch
            {
                CommandResult<IReadOnlyList<ModelEvent>>.Invalid inv =>
                    new CommandResult<MessageDto>.Invalid(inv.Errors),
                CommandResult<IReadOnlyList<ModelEvent>>.Conflict c =>
                    new CommandResult<MessageDto>.Conflict(c.Message),
                _ => new CommandResult<MessageDto>.Conflict("Unexpected error")
            };
        }

        var metadata = new EventMetadata
        {
            ActorId = Guid.Parse(cmd.UserId),
            CorrelationId = Guid.NewGuid()
        };

        _projection.SetContext(cmd.ProjectId, cmd.StreamId);
        var newState = success.Value.Aggregate(state, (s, e) => s.Evolve(e));
        await _store.SaveAsync(cmd.StreamId, "model", version, newState, success.Value, metadata, ct);

        return new CommandResult<MessageDto>.Success(ToDto(newState.Messages[cmd.Request.Id]));
    }

    internal static MessageDto ToDto(MessageState message) => new(
        message.Id, message.Name, message.Description,
        message.ProducerId, message.ConsumerIds.ToGuids(), message.SchemaId, message.Tags,
        message.FactStatus.ToApiString(), ProvenanceMapper.ToDto(message.Provenance));
}
