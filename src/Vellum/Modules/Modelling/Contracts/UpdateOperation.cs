using Vellum.Modules.Modelling.Model;
using Vellum.Modules.Trace.Engine;
using Vellum.Shared;

namespace Vellum.Modules.Modelling.Contracts;

/// <summary>
/// A missing member leaves the value as it is. To clear a schema ID, send the set flag with
/// a null ID.
/// </summary>
public sealed record UpdateOperationRequest(
    string? Name, Guid? OwnerId, string? Transport,
    Guid? RequestSchemaId, Guid? ResponseSchemaId,
    int? TimeoutMs, RetryDto? Retry,
    bool SetRequestSchemaId = false, bool SetResponseSchemaId = false,
    string? FactStatus = null, ProvenanceInput? Provenance = null, bool SetProvenance = false);

public sealed record UpdateOperationCommandEnvelope(
    Guid ProjectId, Guid StreamId, Guid OperationId, string UserId, UpdateOperationRequest Request);

public sealed class UpdateOperationHandler(IServiceProvider services)
    : ICommandHandler<UpdateOperationCommandEnvelope, CommandResult<OperationDto>>
{
    public async Task<CommandResult<OperationDto>> HandleAsync(UpdateOperationCommandEnvelope cmd, CancellationToken ct = default)
    {
        var request = cmd.Request;
        OperationId id = cmd.OperationId;

        Transport? transport = null;
        if (request.Transport is not null)
        {
            if (!ModelEnum.TryParseDefined(request.Transport, out Transport parsed))
                return new CommandResult<OperationDto>.Invalid([new ValidationError("transport", "Transport must be http, grpc or command")]);
            transport = parsed;
        }

        if (!ContractRequests.TryReadFactStatus(request.FactStatus, out var requestedStatus, out var statusError))
            return new CommandResult<OperationDto>.Invalid([statusError!]);

        var update = new UpdateOperationCommand(
            id, request.Name, request.OwnerId is { } ownerId ? new ElementId(ownerId) : null, transport,
            request.RequestSchemaId, request.SetRequestSchemaId || request.RequestSchemaId is not null,
            request.ResponseSchemaId, request.SetResponseSchemaId || request.ResponseSchemaId is not null,
            request.TimeoutMs, request.Retry?.ToDomain());

        // The acceptance stamp is taken once, so the decision stays a pure function of the state.
        var supplied = request.SetProvenance
            ? ProvenanceMapper.ToDomain(request.Provenance, Guid.Parse(cmd.UserId))
            : null;

        return await SchemaBoundWrite.DecideAsync(
            services, cmd.ProjectId, cmd.StreamId,
            [("requestSchemaId", request.RequestSchemaId), ("responseSchemaId", request.ResponseSchemaId)],
            state => ContractDecider.UpdateOperation(state, update).Then(state, after =>
                requestedStatus is null && !request.SetProvenance
                    ? new CommandResult<IReadOnlyList<object>>.Success([])
                    : ContractDecider.RecordOperationProvenance(
                        after, id,
                        requestedStatus ?? after.Operations[id].FactStatus,
                        request.SetProvenance ? supplied : after.Operations[id].Provenance)),
            state => OperationDto.From(state.Operations[id]),
            ct);
    }
}
