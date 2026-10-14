using Deedbox;
using Vellum.Modules.Modelling.Model;
using Vellum.Modules.Trace.Engine;
using Vellum.Shared;

namespace Vellum.Modules.Modelling.Contracts;

public sealed record UpdateChannelRequest(
    string? Name, string? Kind,
    string? FactStatus = null, ProvenanceInput? Provenance = null, bool SetProvenance = false);

public static class UpdateChannel
{
    public static async Task<CommandResult<ChannelDto>> HandleAsync(
        IEventStore store, Guid projectId, Guid streamId, Guid channelId, string userId, UpdateChannelRequest request, TimeProvider time, CancellationToken ct)
    {
        ChannelId id = channelId;

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
            ? ProvenanceMapper.ToDomain(request.Provenance, Guid.Parse(userId), time.GetUtcNow())
            : null;

        return await store.ForProject(projectId).Decide<ModelState, ChannelDto>(
            streamId,
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
