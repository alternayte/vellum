using Vellum.Modules.Modelling.Model;
using Vellum.Shared;

namespace Vellum.Modules.Modelling.Messages;

public sealed record UpdateMessageRequest(
    string? Name, string? Description,
    Guid? ProducerId, Guid[]? ConsumerIds,
    Guid? SchemaId, bool SetSchemaId = false,
    bool SetDescription = false, bool SetProducerId = false,
    bool SetConsumerIds = false, string[]? Tags = null,
    string? FactStatus = null, ProvenanceInput? Provenance = null, bool SetProvenance = false);

public static class UpdateMessage
{
    public static async Task<CommandResult<MessageDto>> HandleAsync(
        IServiceProvider services, Guid projectId, Guid streamId, Guid messageId, string userId, UpdateMessageRequest request, TimeProvider time, CancellationToken ct)
    {
        FactStatus? requestedStatus = null;
        if (request.FactStatus is not null)
        {
            if (!FactStatusFormat.TryParseApiString(request.FactStatus, out var parsedStatus))
                return new CommandResult<MessageDto>.Invalid([new ValidationError("factStatus", "Invalid fact status")]);
            requestedStatus = parsedStatus;
        }

        var updateCmd = new UpdateMessageCommand(
            messageId,
            Name: request.Name, SetName: request.Name is not null,
            Description: request.Description, SetDescription: request.SetDescription,
            ProducerId: request.ProducerId, SetProducerId: request.SetProducerId,
            ConsumerIds: request.ConsumerIds?.ToElementIds(), SetConsumerIds: request.SetConsumerIds,
            SchemaId: request.SchemaId, SetSchemaId: request.SetSchemaId,
            Tags: request.Tags);

        // The acceptance stamp is taken once, so the decision stays a pure function of the state.
        var suppliedProvenance = request.SetProvenance
            ? ProvenanceMapper.ToDomain(request.Provenance, Guid.Parse(userId), time.GetUtcNow())
            : null;

        return await SchemaBoundWrite.DecideAsync(
            services, projectId, streamId,
            [("schemaId", request.SetSchemaId ? request.SchemaId : null)],
            Decide,
            state => AddMessage.ToDto(state.Messages[messageId]),
            ct);

        CommandResult<IReadOnlyList<object>> Decide(ModelState state)
        {
            var result = ModelDecider.UpdateMessage(state, updateCmd);
            if (result is not CommandResult<IReadOnlyList<object>>.Success success)
                return result;
            if (requestedStatus is null && !request.SetProvenance)
                return result;

            var events = new List<object>(success.Value);
            var stateAfter = events.Aggregate(state, ModelState.Evolve);
            var current = stateAfter.Messages[messageId];

            var provenanceResult = ModelDecider.RecordMessageProvenance(
                stateAfter, messageId,
                requestedStatus ?? current.FactStatus,
                request.SetProvenance ? suppliedProvenance : current.Provenance);
            if (provenanceResult is not CommandResult<IReadOnlyList<object>>.Success provenanceSuccess)
                return provenanceResult;

            events.AddRange(provenanceSuccess.Value);
            return new CommandResult<IReadOnlyList<object>>.Success(events);
        }
    }
}
