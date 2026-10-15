using Vellum.Shared;
using Microsoft.EntityFrameworkCore;
using Deedbox;

namespace Vellum.Modules.Modelling;

public sealed record MessageDto(
    Guid Id, string Name, string? Description,
    Guid ProducerId, Guid[] ConsumerIds,
    Guid? SchemaId, string[] Tags,
    string FactStatus, ProvenanceDto? Provenance, StaleDto? Stale = null);

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

public static class GetMessage
{
    public static async Task<IResult> Handle(
        Guid projectId, Guid streamId, Guid messageId,
        AppDbContext db, CancellationToken ct)
    {
        var entity = await db.Messages.AsNoTracking()
            .FirstOrDefaultAsync(m => m.Id == messageId && m.ProjectId == projectId && m.Branch == streamId, ct);

        if (entity is null) return Results.NotFound(new ErrorResponse("not_found", "Message not found"));

        return Results.Ok(new MessageDto(
            entity.Id, entity.Name, entity.Description,
            entity.ProducerId, entity.ConsumerIds, entity.SchemaId, entity.Tags,
            entity.FactStatus.ToApiString(),
            ProvenanceMapper.ToDto(entity.ProvenanceSource, entity.ProvenanceRevision,
                entity.ProvenanceConfidence, entity.ProvenanceAcceptedBy, entity.ProvenanceAcceptedAt, entity.ProvenanceAcceptedAtVersion, entity.ProvenanceAcceptedOnBranch, entity.ProvenanceContentNotHeld),
            StaleDto.From(entity.StaleSinceRevision, entity.StaleReason)));
    }
}

public static class ListMessages
{
    public static async Task<IResult> Handle(
        Guid projectId, Guid streamId,
        Guid? producerId, string? cursor, int? limit,
        AppDbContext db, CancellationToken ct)
    {
        var pageSize = Math.Clamp(limit ?? 50, 1, 200);

        var query = db.Messages.AsNoTracking()
            .Where(m => m.ProjectId == projectId && m.Branch == streamId);

        if (producerId.HasValue) query = query.Where(m => m.ProducerId == producerId);

        var decoded = CursorEncoder.Decode(cursor);
        if (decoded is not null)
        {
            var (sortKey, afterId) = decoded.Value;
            query = query.Where(m => string.Compare(m.Name, sortKey) > 0
                || (m.Name == sortKey && m.Id.CompareTo(afterId) > 0));
        }

        var rows = await query
            .OrderBy(m => m.Name).ThenBy(m => m.Id)
            .Take(pageSize + 1)
            .Select(m => new
            {
                m.Id,
                m.Name,
                m.Description,
                m.ProducerId,
                m.ConsumerIds,
                m.SchemaId,
                m.Tags,
                m.FactStatus,
                m.ProvenanceSource,
                m.ProvenanceRevision,
                m.ProvenanceConfidence,
                m.ProvenanceAcceptedBy,
                m.ProvenanceAcceptedAt,
                m.ProvenanceAcceptedAtVersion,
                m.ProvenanceAcceptedOnBranch,
                m.ProvenanceContentNotHeld,
                m.StaleSinceRevision,
                m.StaleReason,
            })
            .ToListAsync(ct);

        string? nextCursor = null;
        if (rows.Count > pageSize)
        {
            rows = rows[..pageSize];
            var last = rows[^1];
            nextCursor = CursorEncoder.Encode(last.Name, last.Id);
        }

        var items = rows.Select(m => new MessageDto(
            m.Id, m.Name, m.Description, m.ProducerId, m.ConsumerIds, m.SchemaId, m.Tags, m.FactStatus.ToApiString(),
            ProvenanceMapper.ToDto(m.ProvenanceSource, m.ProvenanceRevision, m.ProvenanceConfidence,
                m.ProvenanceAcceptedBy, m.ProvenanceAcceptedAt, m.ProvenanceAcceptedAtVersion, m.ProvenanceAcceptedOnBranch, m.ProvenanceContentNotHeld),
            StaleDto.From(m.StaleSinceRevision, m.StaleReason))).ToList();

        return Results.Ok(new Page<MessageDto>(items, nextCursor));
    }
}

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

public static class RemoveMessage
{
    public static Task<CommandResult> HandleAsync(
        IEventStore store, Guid projectId, Guid streamId, Guid messageId, CancellationToken ct) =>
        store.ForProject(projectId).Decide<ModelState>(
            streamId,
            state => ModelDecider.RemoveMessage(state, messageId),
            ct);
}
