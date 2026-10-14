using Deedbox;
using Vellum.Modules.Modelling.Model;
using Vellum.Shared;

namespace Vellum.Modules.Modelling.Contracts;

/// <summary>
/// A binding without a routing key passes every message that reaches its source. An empty
/// key reads as none.
/// </summary>
public sealed record AddBindingRequest(Guid Id, Guid SourceChannelId, Guid TargetChannelId, string? RoutingKey);

public static class AddBinding
{
    public static Task<CommandResult<BindingDto>> HandleAsync(
        IEventStore store, Guid projectId, Guid streamId, AddBindingRequest request, CancellationToken ct)
    {
        var add = new AddBindingCommand(request.Id, request.SourceChannelId, request.TargetChannelId, request.RoutingKey);

        return store.ForProject(projectId).Decide<ModelState, BindingDto>(
            streamId,
            state => ContractDecider.AddBinding(state, add),
            state => BindingDto.From(state.Bindings[request.Id]),
            ct);
    }
}
