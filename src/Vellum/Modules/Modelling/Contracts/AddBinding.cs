using Deedbox;
using Vellum.Modules.Modelling.Model;
using Vellum.Shared;

namespace Vellum.Modules.Modelling.Contracts;

/// <summary>
/// A binding without a routing key passes every message that reaches its source. An empty
/// key reads as none.
/// </summary>
public sealed record AddBindingRequest(Guid Id, Guid SourceChannelId, Guid TargetChannelId, string? RoutingKey);

public sealed record AddBindingCommandEnvelope(Guid ProjectId, Guid StreamId, AddBindingRequest Request);

public sealed class AddBindingHandler(IEventStore store)
    : ICommandHandler<AddBindingCommandEnvelope, CommandResult<BindingDto>>
{
    public Task<CommandResult<BindingDto>> HandleAsync(AddBindingCommandEnvelope cmd, CancellationToken ct = default)
    {
        var request = cmd.Request;
        var add = new AddBindingCommand(request.Id, request.SourceChannelId, request.TargetChannelId, request.RoutingKey);

        return store.ForProject(cmd.ProjectId).Decide<ModelState, BindingDto>(
            cmd.StreamId,
            state => ContractDecider.AddBinding(state, add),
            state => BindingDto.From(state.Bindings[request.Id]),
            ct);
    }
}
