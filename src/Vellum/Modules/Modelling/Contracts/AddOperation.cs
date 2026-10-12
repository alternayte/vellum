using Deedbox;
using Vellum.Modules.Modelling.Model;
using Vellum.Modules.Schemas;
using Vellum.Modules.Trace.Engine;
using Vellum.Shared;

namespace Vellum.Modules.Modelling.Contracts;

/// <summary>A missing timeout declares none (0). A missing retry means one attempt.</summary>
public sealed record AddOperationRequest(
    Guid Id, Guid OwnerId, string Name, string Transport,
    Guid? RequestSchemaId, Guid? ResponseSchemaId,
    int? TimeoutMs, RetryDto? Retry);

public sealed record AddOperationCommandEnvelope(Guid ProjectId, Guid StreamId, AddOperationRequest Request);

public sealed class AddOperationHandler(IEventStore store, SchemasDbContext schemasDb)
    : ICommandHandler<AddOperationCommandEnvelope, CommandResult<OperationDto>>
{
    public async Task<CommandResult<OperationDto>> HandleAsync(AddOperationCommandEnvelope cmd, CancellationToken ct = default)
    {
        var request = cmd.Request;
        if (request.Transport is null || !ModelEnum.TryParseDefined(request.Transport, out Transport transport))
            return new CommandResult<OperationDto>.Invalid([new ValidationError("transport", "Transport must be http, grpc or command")]);

        var schemaError =
            await ContractRequests.CheckSchemaAsync(schemasDb, cmd.ProjectId, "requestSchemaId", request.RequestSchemaId, ct)
            ?? await ContractRequests.CheckSchemaAsync(schemasDb, cmd.ProjectId, "responseSchemaId", request.ResponseSchemaId, ct);
        if (schemaError is not null)
            return new CommandResult<OperationDto>.Invalid([schemaError]);

        var add = new AddOperationCommand(
            request.Id, request.OwnerId, request.Name, transport,
            request.RequestSchemaId, request.ResponseSchemaId,
            request.TimeoutMs ?? 0, request.Retry?.ToDomain() ?? RetryPolicy.None);

        return await store.ForProject(cmd.ProjectId).Decide<ModelState, OperationDto>(
            cmd.StreamId,
            state => ContractDecider.AddOperation(state, add),
            state => OperationDto.From(state.Operations[request.Id]),
            ct);
    }
}
