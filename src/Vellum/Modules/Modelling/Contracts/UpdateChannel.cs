using Deedbox;
using Vellum.Modules.Modelling.Model;
using Vellum.Modules.Trace.Engine;
using Vellum.Shared;

namespace Vellum.Modules.Modelling.Contracts;

public sealed record UpdateChannelRequest(
    string? Name, string? Kind,
    string? FactStatus = null, ProvenanceInput? Provenance = null, bool SetProvenance = false);

public sealed record UpdateChannelCommandEnvelope(
    Guid ProjectId, Guid StreamId, Guid ChannelId, string UserId, UpdateChannelRequest Request);

public sealed class UpdateChannelHandler(IEventStore store)
    : ICommandHandler<UpdateChannelCommandEnvelope, CommandResult<ChannelDto>>
{
    public async Task<CommandResult<ChannelDto>> HandleAsync(UpdateChannelCommandEnvelope cmd, CancellationToken ct = default)
    {
        var request = cmd.Request;
        ChannelId id = cmd.ChannelId;

        ChannelKind? kind = null;
        if (request.Kind is not null)
        {
            if (!ModelEnum.TryParseDefined(request.Kind, out ChannelKind parsed))
                return new CommandResult<ChannelDto>.Invalid([new ValidationError("kind", "Kind must be topic, queue or exchange")]);
            kind = parsed;
        }

        if (!ContractRequests.TryReadFactStatus(request.FactStatus, out var requestedStatus, out var statusError))
            return new CommandResult<ChannelDto>.Invalid([statusError!]);

        var update = new UpdateChannelCommand(id, request.Name, kind);

        // The acceptance stamp is taken once, so the decision stays a pure function of the state.
        var supplied = request.SetProvenance
            ? ProvenanceMapper.ToDomain(request.Provenance, Guid.Parse(cmd.UserId))
            : null;

        return await store.ForProject(cmd.ProjectId).Decide<ModelState, ChannelDto>(
            cmd.StreamId,
            state => ContractDecider.UpdateChannel(state, update).Then(state, after =>
                requestedStatus is null && !request.SetProvenance
                    ? new CommandResult<IReadOnlyList<object>>.Success([])
                    : ContractDecider.RecordChannelProvenance(
                        after, id,
                        requestedStatus ?? after.Channels[id].FactStatus,
                        request.SetProvenance ? supplied : after.Channels[id].Provenance)),
            state => ChannelDto.From(state.Channels[id]),
            ct);
    }
}
