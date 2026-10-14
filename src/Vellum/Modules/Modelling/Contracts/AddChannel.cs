using Deedbox;
using Vellum.Modules.Modelling.Model;
using Vellum.Modules.Trace.Engine;
using Vellum.Shared;

namespace Vellum.Modules.Modelling.Contracts;

public sealed record AddChannelRequest(Guid Id, string Name, string Kind);

public static class AddChannel
{
    public static async Task<CommandResult<ChannelDto>> HandleAsync(
        IEventStore store, Guid projectId, Guid streamId, AddChannelRequest request, CancellationToken ct)
    {
        if (request.Kind is null || !ModelEnum.TryParseDefined(request.Kind, out ChannelKind kind))
            return new CommandResult<ChannelDto>.Invalid([new ValidationError("kind", "Kind must be topic, queue or exchange")]);

        var add = new AddChannelCommand(request.Id, request.Name, kind);

        return await store.ForProject(projectId).Decide<ModelState, ChannelDto>(
            streamId,
            state => ContractDecider.AddChannel(state, add),
            state => ChannelDto.From(state.Channels[request.Id]),
            ct);
    }
}
