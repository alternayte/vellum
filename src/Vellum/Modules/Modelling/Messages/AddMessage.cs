using Deedbox;
using Microsoft.EntityFrameworkCore;
using Vellum.Modules.Modelling.Model;
using Vellum.Modules.Schemas;
using Vellum.Shared;

namespace Vellum.Modules.Modelling.Messages;

public sealed record AddMessageRequest(
    Guid Id, string Name, string? Description,
    Guid ProducerId, Guid[] ConsumerIds,
    Guid? SchemaId, string[]? Tags);

public sealed record AddMessageCommandEnvelope(Guid ProjectId, Guid StreamId, AddMessageRequest Request);

public sealed class AddMessageHandler : ICommandHandler<AddMessageCommandEnvelope, CommandResult<MessageDto>>
{
    private readonly IEventStore _store;
    private readonly SchemasDbContext _schemasDb;

    public AddMessageHandler(IEventStore store, SchemasDbContext schemasDb)
    {
        _store = store;
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

        var addCmd = new AddMessageCommand(
            cmd.Request.Id, cmd.Request.Name, cmd.Request.Description,
            cmd.Request.ProducerId, cmd.Request.ConsumerIds.ToElementIds(),
            cmd.Request.SchemaId, cmd.Request.Tags ?? []);

        return await _store.ForProject(cmd.ProjectId).Decide<ModelState, MessageDto>(
            cmd.StreamId,
            state => ModelDecider.AddMessage(state, addCmd),
            state => ToDto(state.Messages[cmd.Request.Id]),
            ct);
    }

    internal static MessageDto ToDto(MessageState message) => new(
        message.Id, message.Name, message.Description,
        message.ProducerId, message.ConsumerIds.ToGuids(), message.SchemaId, message.Tags,
        message.FactStatus.ToApiString(), ProvenanceMapper.ToDto(message.Provenance));
}
