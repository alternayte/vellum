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

public sealed record UpdateMessageCommandEnvelope(
    Guid ProjectId, Guid StreamId, Guid MessageId, string UserId, UpdateMessageRequest Request);

public sealed class UpdateMessageHandler(IServiceProvider services)
    : ICommandHandler<UpdateMessageCommandEnvelope, CommandResult<MessageDto>>
{
    public async Task<CommandResult<MessageDto>> HandleAsync(UpdateMessageCommandEnvelope cmd, CancellationToken ct = default)
    {
        FactStatus? requestedStatus = null;
        if (cmd.Request.FactStatus is not null)
        {
            if (!FactStatusFormat.TryParseApiString(cmd.Request.FactStatus, out var parsedStatus))
                return new CommandResult<MessageDto>.Invalid([new ValidationError("factStatus", "Invalid fact status")]);
            requestedStatus = parsedStatus;
        }

        var updateCmd = new UpdateMessageCommand(
            cmd.MessageId,
            Name: cmd.Request.Name, SetName: cmd.Request.Name is not null,
            Description: cmd.Request.Description, SetDescription: cmd.Request.SetDescription,
            ProducerId: cmd.Request.ProducerId, SetProducerId: cmd.Request.SetProducerId,
            ConsumerIds: cmd.Request.ConsumerIds?.ToElementIds(), SetConsumerIds: cmd.Request.SetConsumerIds,
            SchemaId: cmd.Request.SchemaId, SetSchemaId: cmd.Request.SetSchemaId,
            Tags: cmd.Request.Tags);

        // The acceptance stamp is taken once, so the decision stays a pure function of the state.
        var suppliedProvenance = cmd.Request.SetProvenance
            ? ProvenanceMapper.ToDomain(cmd.Request.Provenance, Guid.Parse(cmd.UserId))
            : null;

        return await SchemaBoundWrite.DecideAsync(
            services, cmd.ProjectId, cmd.StreamId,
            [("schemaId", cmd.Request.SetSchemaId ? cmd.Request.SchemaId : null)],
            Decide,
            state => AddMessageHandler.ToDto(state.Messages[cmd.MessageId]),
            ct);

        CommandResult<IReadOnlyList<object>> Decide(ModelState state)
        {
            var result = ModelDecider.UpdateMessage(state, updateCmd);
            if (result is not CommandResult<IReadOnlyList<object>>.Success success)
                return result;
            if (requestedStatus is null && !cmd.Request.SetProvenance)
                return result;

            var events = new List<object>(success.Value);
            var stateAfter = events.Aggregate(state, ModelState.Evolve);
            var current = stateAfter.Messages[cmd.MessageId];

            var provenanceResult = ModelDecider.RecordMessageProvenance(
                stateAfter, cmd.MessageId,
                requestedStatus ?? current.FactStatus,
                cmd.Request.SetProvenance ? suppliedProvenance : current.Provenance);
            if (provenanceResult is not CommandResult<IReadOnlyList<object>>.Success provenanceSuccess)
                return provenanceResult;

            events.AddRange(provenanceSuccess.Value);
            return new CommandResult<IReadOnlyList<object>>.Success(events);
        }
    }
}
