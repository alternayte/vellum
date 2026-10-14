using Vellum.Modules.Modelling.Model;
using Vellum.Shared;

namespace Vellum.Modules.Modelling.Messages;

public sealed record AddMessageRequest(
    Guid Id, string Name, string? Description,
    Guid ProducerId, Guid[] ConsumerIds,
    Guid? SchemaId, string[]? Tags);

public static class AddMessage
{
    public static async Task<CommandResult<MessageDto>> HandleAsync(
        IServiceProvider services, Guid projectId, Guid streamId, AddMessageRequest request, CancellationToken ct)
    {
        var addCmd = new AddMessageCommand(
            request.Id, request.Name, request.Description,
            request.ProducerId, request.ConsumerIds.ToElementIds(),
            request.SchemaId, request.Tags ?? []);

        return await SchemaBoundWrite.DecideAsync(
            services, projectId, streamId,
            [("schemaId", request.SchemaId)],
            state => ModelDecider.AddMessage(state, addCmd),
            state => ToDto(state.Messages[request.Id]),
            ct);
    }

    internal static MessageDto ToDto(MessageState message) => new(
        message.Id, message.Name, message.Description,
        message.ProducerId, message.ConsumerIds.ToGuids(), message.SchemaId, message.Tags,
        message.FactStatus.ToApiString(), ProvenanceMapper.ToDto(message.Provenance), StaleDto.From(message.Stale));
}
