using Deedbox;
using Vellum.Shared;
using Microsoft.EntityFrameworkCore;

namespace Vellum.Modules.Modelling;

public sealed record RelationshipDto(
    Guid Id, Guid FromId, Guid ToId, string? Label,
    string? Technology, Guid? MessageId, string? LineShape,
    string Kind, string FactStatus, ProvenanceDto? Provenance, StaleDto? Stale = null);

public sealed record AddRelationshipRequest(
    Guid Id, Guid FromId, Guid ToId, string? Label, string? Technology,
    string? Kind = null);

public static class AddRelationship
{
    public static async Task<CommandResult<RelationshipDto>> HandleAsync(
        IEventStore store, Guid projectId, Guid streamId, AddRelationshipRequest request, CancellationToken ct)
    {
        var kind = RelationshipKind.Structural;
        if (request.Kind is not null && !ModelEnum.TryParseDefined(request.Kind, out kind))
            return new CommandResult<RelationshipDto>.Invalid([new ValidationError("kind", "Invalid relationship kind")]);

        var addCmd = new AddRelationshipCommand(
            request.Id, request.FromId, request.ToId,
            request.Label, request.Technology, kind);

        return await store.ForProject(projectId).Decide<ModelState, RelationshipDto>(
            streamId,
            state => ModelDecider.AddRelationship(state, addCmd),
            state => ToDto(state.Relationships[request.Id]),
            ct);
    }

    internal static RelationshipDto ToDto(RelationshipState relationship) => new(
        relationship.Id, relationship.FromId, relationship.ToId, relationship.Label,
        relationship.Technology, relationship.MessageId, relationship.LineShape,
        relationship.Kind.ToString().ToLowerInvariant(),
        relationship.FactStatus.ToApiString(), ProvenanceMapper.ToDto(relationship.Provenance), StaleDto.From(relationship.Stale));
}

public static class GetRelationship
{
    public static async Task<IResult> Handle(
        Guid projectId, Guid streamId, Guid relationshipId,
        AppDbContext db, CancellationToken ct)
    {
        var entity = await db.Relationships.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == relationshipId && r.ProjectId == projectId && r.Branch == streamId, ct);

        if (entity is null) return Results.NotFound(new ErrorResponse("not_found", "Relationship not found"));

        return Results.Ok(new RelationshipDto(
            entity.Id, entity.FromId, entity.ToId,
            entity.Label, entity.Technology, entity.MessageId, entity.LineShape,
            EnumText.Of(entity.Kind), entity.FactStatus.ToApiString(),
            ProvenanceMapper.ToDto(entity.ProvenanceSource, entity.ProvenanceRevision,
                entity.ProvenanceConfidence, entity.ProvenanceAcceptedBy, entity.ProvenanceAcceptedAt, entity.ProvenanceAcceptedAtVersion, entity.ProvenanceAcceptedOnBranch, entity.ProvenanceContentNotHeld),
            StaleDto.From(entity.StaleSinceRevision, entity.StaleReason)));
    }
}

public static class ListRelationships
{
    public static async Task<IResult> Handle(
        Guid projectId, Guid streamId,
        Guid? fromId, Guid? toId,
        string? cursor, int? limit,
        AppDbContext db, CancellationToken ct)
    {
        var pageSize = Math.Clamp(limit ?? 50, 1, 200);

        var query = db.Relationships.AsNoTracking()
            .Where(r => r.ProjectId == projectId && r.Branch == streamId);

        if (fromId.HasValue) query = query.Where(r => r.FromId == fromId);
        if (toId.HasValue) query = query.Where(r => r.ToId == toId);

        var decoded = CursorEncoder.Decode(cursor);
        if (decoded is not null)
        {
            var (_, afterId) = decoded.Value;
            query = query.Where(r => r.Id.CompareTo(afterId) > 0);
        }

        var rows = await query
            .OrderBy(r => r.Id)
            .Take(pageSize + 1)
            .Select(r => new
            {
                r.Id,
                r.FromId,
                r.ToId,
                r.Label,
                r.Technology,
                r.MessageId,
                r.LineShape,
                r.Kind,
                r.FactStatus,
                r.ProvenanceSource,
                r.ProvenanceRevision,
                r.ProvenanceConfidence,
                r.ProvenanceAcceptedBy,
                r.ProvenanceAcceptedAt,
                r.ProvenanceAcceptedAtVersion,
                r.ProvenanceAcceptedOnBranch,
                r.ProvenanceContentNotHeld,
                r.StaleSinceRevision,
                r.StaleReason,
            })
            .ToListAsync(ct);

        string? nextCursor = null;
        if (rows.Count > pageSize)
        {
            rows = rows[..pageSize];
            var last = rows[^1];
            nextCursor = CursorEncoder.Encode(last.Id.ToString(), last.Id);
        }

        var items = rows.Select(r => new RelationshipDto(
            r.Id, r.FromId, r.ToId, r.Label, r.Technology, r.MessageId, r.LineShape, EnumText.Of(r.Kind), r.FactStatus.ToApiString(),
            ProvenanceMapper.ToDto(r.ProvenanceSource, r.ProvenanceRevision, r.ProvenanceConfidence,
                r.ProvenanceAcceptedBy, r.ProvenanceAcceptedAt, r.ProvenanceAcceptedAtVersion, r.ProvenanceAcceptedOnBranch, r.ProvenanceContentNotHeld),
            StaleDto.From(r.StaleSinceRevision, r.StaleReason))).ToList();

        return Results.Ok(new Page<RelationshipDto>(items, nextCursor));
    }
}

public sealed record UpdateRelationshipRequest(
    string? Label, string? Technology, string? LineShape,
    bool SetLabel = false, bool SetTechnology = false, bool SetLineShape = false,
    string? Kind = null, bool SetKind = false,
    string? FactStatus = null, ProvenanceInput? Provenance = null, bool SetProvenance = false);

public static class UpdateRelationship
{
    public static async Task<CommandResult<RelationshipDto>> HandleAsync(
        IEventStore store, Guid projectId, Guid streamId, Guid relationshipId, string userId, UpdateRelationshipRequest request, TimeProvider time, CancellationToken ct)
    {
        RelationshipKind? kind = null;
        if (request.Kind is not null)
        {
            if (!ModelEnum.TryParseDefined(request.Kind, out RelationshipKind parsed))
                return new CommandResult<RelationshipDto>.Invalid([new ValidationError("kind", "Invalid relationship kind")]);
            kind = parsed;
        }

        FactStatus? requestedStatus = null;
        if (request.FactStatus is not null)
        {
            if (!FactStatusFormat.TryParseApiString(request.FactStatus, out var parsedStatus))
                return new CommandResult<RelationshipDto>.Invalid([new ValidationError("factStatus", "Invalid fact status")]);
            requestedStatus = parsedStatus;
        }

        var updateCmd = new UpdateRelationshipCommand(
            relationshipId,
            Label: request.Label, SetLabel: request.SetLabel || request.Label is not null,
            Technology: request.Technology, SetTechnology: request.SetTechnology || request.Technology is not null,
            LineShape: request.LineShape, SetLineShape: request.SetLineShape || request.LineShape is not null,
            Kind: kind, SetKind: request.SetKind || request.Kind is not null);

        // The acceptance stamp is taken once, so the decision stays a pure function of the state.
        var suppliedProvenance = request.SetProvenance
            ? ProvenanceMapper.ToDomain(request.Provenance, Guid.Parse(userId), time.GetUtcNow())
            : null;

        return await store.ForProject(projectId).Decide<ModelState, RelationshipDto>(
            streamId,
            Decide,
            state => AddRelationship.ToDto(state.Relationships[relationshipId]),
            ct);

        CommandResult<IReadOnlyList<object>> Decide(ModelState state)
        {
            var result = ModelDecider.UpdateRelationship(state, updateCmd);
            if (result is not CommandResult<IReadOnlyList<object>>.Success success)
                return result;
            if (requestedStatus is null && !request.SetProvenance)
                return result;

            var events = new List<object>(success.Value);
            var stateAfter = events.Aggregate(state, ModelState.Evolve);
            var current = stateAfter.Relationships[relationshipId];

            var provenanceResult = ModelDecider.RecordRelationshipProvenance(
                stateAfter, relationshipId,
                requestedStatus ?? current.FactStatus,
                request.SetProvenance ? suppliedProvenance : current.Provenance);
            if (provenanceResult is not CommandResult<IReadOnlyList<object>>.Success provenanceSuccess)
                return provenanceResult;

            events.AddRange(provenanceSuccess.Value);
            return new CommandResult<IReadOnlyList<object>>.Success(events);
        }
    }
}

public static class RemoveRelationship
{
    public static Task<CommandResult> HandleAsync(
        IEventStore store, Guid projectId, Guid streamId, Guid relationshipId, CancellationToken ct) =>
        store.ForProject(projectId).Decide<ModelState>(
            streamId,
            state => ModelDecider.RemoveRelationship(state, relationshipId),
            ct);
}
