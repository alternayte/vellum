using Deedbox;
using Vellum.Shared;
using Microsoft.EntityFrameworkCore;

namespace Vellum.Modules.Modelling;

public sealed record ElementDto(
    Guid Id, string Kind, string Name, string? Description,
    string? Technology, Guid? OwnerId, string Status,
    Guid? ParentId, string[] Tags, string? Icon,
    string FactStatus, ProvenanceDto? Provenance, StaleDto? Stale = null);

public sealed record AddElementRequest(
    Guid Id, string Kind, string Name, string? Description,
    string? Technology, Guid? OwnerId, string? Status,
    Guid? ParentId, string[]? Tags);

public static class AddElement
{
    public static async Task<CommandResult<ElementDto>> HandleAsync(
        IEventStore store, Guid projectId, Guid streamId, AddElementRequest request, CancellationToken ct)
    {
        if (!ModelEnum.TryParseDefined(request.Kind, out ElementKind kind))
            return new CommandResult<ElementDto>.Invalid([new ValidationError("kind", "Invalid element kind")]);

        var status = ElementStatus.Current;
        if (request.Status is not null && !ModelEnum.TryParseDefined(request.Status, out status))
            return new CommandResult<ElementDto>.Invalid([new ValidationError("status", "Invalid status")]);

        var addCmd = new AddElementCommand(
            request.Id, kind, request.Name, request.Description,
            request.Technology, request.OwnerId, status,
            request.ParentId, request.Tags ?? []);

        return await store.ForProject(projectId).Decide<ModelState, ElementDto>(
            streamId,
            state => ModelDecider.AddElement(state, addCmd),
            state => ToDto(state.Elements[request.Id]),
            ct);
    }

    internal static ElementDto ToDto(ElementState element) => new(
        element.Id, element.Kind.ToString().ToLowerInvariant(), element.Name,
        element.Description, element.Technology, element.OwnerId,
        element.Status.ToString().ToLowerInvariant(), element.ParentId, element.Tags, element.Icon,
        element.FactStatus.ToApiString(), ProvenanceMapper.ToDto(element.Provenance), StaleDto.From(element.Stale));
}

public static class GetElement
{
    public static async Task<IResult> Handle(
        Guid projectId, Guid streamId, Guid elementId,
        AppDbContext db, CancellationToken ct)
    {
        var entity = await db.Elements.AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == elementId && e.ProjectId == projectId && e.Branch == streamId, ct);

        if (entity is null) return Results.NotFound(new ErrorResponse("not_found", "Element not found"));

        return Results.Ok(new ElementDto(
            entity.Id, EnumText.Of(entity.Kind), entity.Name, entity.Description,
            entity.Technology, entity.OwnerId, EnumText.Of(entity.Status),
            entity.ParentId, entity.Tags, entity.Icon, entity.FactStatus.ToApiString(),
            ProvenanceMapper.ToDto(entity.ProvenanceSource, entity.ProvenanceRevision,
                entity.ProvenanceConfidence, entity.ProvenanceAcceptedBy, entity.ProvenanceAcceptedAt, entity.ProvenanceAcceptedAtVersion, entity.ProvenanceAcceptedOnBranch),
            StaleDto.From(entity.StaleSinceRevision, entity.StaleReason)));
    }
}

public static class ListElements
{
    public static async Task<IResult> Handle(
        Guid projectId, Guid streamId,
        string? kind, string? status, Guid? parentId,
        string? cursor, int? limit,
        AppDbContext db, CancellationToken ct)
    {
        var pageSize = Math.Clamp(limit ?? 50, 1, 200);

        var query = db.Elements.AsNoTracking()
            .Where(e => e.ProjectId == projectId && e.Branch == streamId);

        // A kind or status that names no member matches no element.
        if (kind is not null)
        {
            query = EnumText.TryParse(kind.ToLowerInvariant(), out ElementKind wantedKind)
                ? query.Where(e => e.Kind == wantedKind)
                : query.Where(e => false);
        }
        if (status is not null)
        {
            query = EnumText.TryParse(status.ToLowerInvariant(), out ElementStatus wantedStatus)
                ? query.Where(e => e.Status == wantedStatus)
                : query.Where(e => false);
        }
        if (parentId.HasValue) query = query.Where(e => e.ParentId == parentId);

        var decoded = CursorEncoder.Decode(cursor);
        if (decoded is not null)
        {
            var (sortKey, afterId) = decoded.Value;
            query = query.Where(e => string.Compare(e.Name, sortKey) > 0
                || (e.Name == sortKey && e.Id.CompareTo(afterId) > 0));
        }

        var rows = await query
            .OrderBy(e => e.Name).ThenBy(e => e.Id)
            .Take(pageSize + 1)
            .Select(e => new
            {
                e.Id,
                e.Kind,
                e.Name,
                e.Description,
                e.Technology,
                e.OwnerId,
                e.Status,
                e.ParentId,
                e.Tags,
                e.Icon,
                e.FactStatus,
                e.ProvenanceSource,
                e.ProvenanceRevision,
                e.ProvenanceConfidence,
                e.ProvenanceAcceptedBy,
                e.ProvenanceAcceptedAt,
                e.ProvenanceAcceptedAtVersion,
                e.ProvenanceAcceptedOnBranch,
                e.StaleSinceRevision,
                e.StaleReason,
            })
            .ToListAsync(ct);

        string? nextCursor = null;
        if (rows.Count > pageSize)
        {
            rows = rows[..pageSize];
            var last = rows[^1];
            nextCursor = CursorEncoder.Encode(last.Name, last.Id);
        }

        var items = rows.Select(e => new ElementDto(
            e.Id, EnumText.Of(e.Kind), e.Name, e.Description, e.Technology, e.OwnerId, EnumText.Of(e.Status),
            e.ParentId, e.Tags, e.Icon, e.FactStatus.ToApiString(),
            ProvenanceMapper.ToDto(e.ProvenanceSource, e.ProvenanceRevision, e.ProvenanceConfidence,
                e.ProvenanceAcceptedBy, e.ProvenanceAcceptedAt, e.ProvenanceAcceptedAtVersion, e.ProvenanceAcceptedOnBranch),
            StaleDto.From(e.StaleSinceRevision, e.StaleReason))).ToList();

        return Results.Ok(new Page<ElementDto>(items, nextCursor));
    }
}

public sealed record UpdateElementRequest(
    string? Name, string? Description, string? Technology,
    Guid? OwnerId, Guid? ParentId, string? Status, string[]? Tags,
    string? Icon,
    bool SetDescription = false, bool SetTechnology = false,
    bool SetOwnerId = false, bool SetParentId = false, bool SetIcon = false,
    string? FactStatus = null, ProvenanceInput? Provenance = null, bool SetProvenance = false);

public static class UpdateElement
{
    public static async Task<CommandResult<ElementDto>> HandleAsync(
        IEventStore store, Guid projectId, Guid streamId, Guid elementId, string userId, UpdateElementRequest request, TimeProvider time, CancellationToken ct)
    {
        ElementStatus? status = null;
        if (request.Status is not null)
        {
            if (!ModelEnum.TryParseDefined(request.Status, out ElementStatus parsed))
                return new CommandResult<ElementDto>.Invalid([new ValidationError("status", "Invalid status")]);
            status = parsed;
        }

        FactStatus? requestedStatus = null;
        if (request.FactStatus is not null)
        {
            if (!FactStatusFormat.TryParseApiString(request.FactStatus, out var parsedStatus))
                return new CommandResult<ElementDto>.Invalid([new ValidationError("factStatus", "Invalid fact status")]);
            requestedStatus = parsedStatus;
        }

        var updateCmd = new UpdateElementCommand(
            elementId,
            Name: request.Name, SetName: request.Name is not null,
            Description: request.Description, SetDescription: request.SetDescription || request.Description is not null,
            Technology: request.Technology, SetTechnology: request.SetTechnology || request.Technology is not null,
            OwnerId: request.OwnerId, SetOwnerId: request.SetOwnerId,
            ParentId: request.ParentId, SetParentId: request.SetParentId,
            Status: status,
            Tags: request.Tags,
            Icon: request.Icon, SetIcon: request.SetIcon || request.Icon is not null);

        // The acceptance stamp is taken once, so the decision stays a pure function of the state.
        var suppliedProvenance = request.SetProvenance
            ? ProvenanceMapper.ToDomain(request.Provenance, Guid.Parse(userId), time.GetUtcNow())
            : null;

        return await store.ForProject(projectId).Decide<ModelState, ElementDto>(
            streamId,
            Decide,
            state => AddElement.ToDto(state.Elements[elementId]),
            ct);

        CommandResult<IReadOnlyList<object>> Decide(ModelState state)
        {
            var result = ModelDecider.UpdateElement(state, updateCmd);
            if (result is not CommandResult<IReadOnlyList<object>>.Success success)
                return result;
            if (requestedStatus is null && !request.SetProvenance)
                return result;

            var events = new List<object>(success.Value);
            var stateAfter = events.Aggregate(state, ModelState.Evolve);
            var current = stateAfter.Elements[elementId];

            var provenanceResult = ModelDecider.RecordElementProvenance(
                stateAfter, elementId,
                requestedStatus ?? current.FactStatus,
                request.SetProvenance ? suppliedProvenance : current.Provenance);
            if (provenanceResult is not CommandResult<IReadOnlyList<object>>.Success provenanceSuccess)
                return provenanceResult;

            events.AddRange(provenanceSuccess.Value);
            return new CommandResult<IReadOnlyList<object>>.Success(events);
        }
    }
}

public static class RemoveElement
{
    public static Task<CommandResult> HandleAsync(
        IEventStore store, Guid projectId, Guid streamId, Guid elementId, CancellationToken ct) =>
        store.ForProject(projectId).Decide<ModelState>(
            streamId,
            state => ModelDecider.RemoveElement(state, elementId),
            ct);
}
