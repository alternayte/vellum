using Vellum.Modules.Modelling.Model;
using Vellum.Shared;

namespace Vellum.Modules.Modelling.Messages;

public sealed record AddMessageRequest(
    Guid Id, string Name, string? Description,
    Guid ProducerId, Guid[] ConsumerIds,
    Guid? SchemaId, string[]? Tags);

public sealed record AddMessageCommandEnvelope(Guid ProjectId, Guid StreamId, AddMessageRequest Request);

public sealed class AddMessageHandler(IServiceProvider services)
    : ICommandHandler<AddMessageCommandEnvelope, CommandResult<MessageDto>>
{
    public async Task<CommandResult<MessageDto>> HandleAsync(AddMessageCommandEnvelope cmd, CancellationToken ct = default)
    {
        var addCmd = new AddMessageCommand(
            cmd.Request.Id, cmd.Request.Name, cmd.Request.Description,
            cmd.Request.ProducerId, cmd.Request.ConsumerIds.ToElementIds(),
            cmd.Request.SchemaId, cmd.Request.Tags ?? []);

        return await SchemaBoundWrite.DecideAsync(
            services, cmd.ProjectId, cmd.StreamId,
            [("schemaId", cmd.Request.SchemaId)],
            state => ModelDecider.AddMessage(state, addCmd),
            state => ToDto(state.Messages[cmd.Request.Id]),
            ct);
    }

    internal static MessageDto ToDto(MessageState message) => new(
        message.Id, message.Name, message.Description,
        message.ProducerId, message.ConsumerIds.ToGuids(), message.SchemaId, message.Tags,
        message.FactStatus.ToApiString(), ProvenanceMapper.ToDto(message.Provenance));
}
