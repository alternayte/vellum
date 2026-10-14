using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Vellum.Modules.Drafts;
using Vellum.Modules.Modelling.Contracts;
using Vellum.Modules.Modelling.Elements;
using Vellum.Modules.Modelling.Messages;
using Vellum.Modules.Modelling.Model;
using Vellum.Modules.Modelling.Relationships;
using Vellum.Modules.Promises;
using Vellum.Modules.Workspaces;
using Vellum.Modules.Workspaces.Authorization;
using Vellum.Shared;

namespace Vellum.Modules.Modelling.Revisions;

/// <summary>
/// The whole model of a named revision, in the shapes the model lists give. A revision has
/// no read-model rows, so it has no paged lists: the stored state is the answer. The limits
/// of a model bound its size.
/// </summary>
public sealed record RevisionModelDto(
    RevisionDto Revision,
    IReadOnlyList<ElementDto> Elements,
    IReadOnlyList<RelationshipDto> Relationships,
    IReadOnlyList<MessageDto> Messages,
    IReadOnlyList<OperationDto> Operations,
    IReadOnlyList<ChannelDto> Channels,
    IReadOnlyList<SubscriptionDto> Subscriptions,
    IReadOnlyList<BindingDto> Bindings);

/// <summary>One side of a comparison: a branch at the version it was read at, or a named revision.</summary>
public sealed record ComparedModel(Guid StreamId, long Version, Guid? RevisionId, string? RevisionName);

/// <summary>One fact that the two models do not share.</summary>
/// <param name="EntityType"><c>element</c>, <c>relationship</c>, <c>message</c>, <c>operation</c>, <c>channel</c>, <c>subscription</c> or <c>binding</c>.</param>
/// <param name="ChangeKind"><c>added</c> (only the right model has it), <c>removed</c> (only the left model has it) or <c>modified</c>.</param>
/// <param name="Left">The fact in the left model. Null when the left model does not have it.</param>
/// <param name="Right">The fact in the right model. Null when the right model does not have it.</param>
public sealed record ModelDifference(string EntityType, Guid EntityId, string ChangeKind, object? Left, object? Right);

/// <summary>
/// What differs between two models, as the changes that turn <paramref name="Left"/> into
/// <paramref name="Right"/>. The values are the facts as the models hold them. A stale mark
/// alone is no difference, as in a merge. The list states differences and gives no verdict.
/// </summary>
public sealed record ModelComparisonDto(ComparedModel Left, ComparedModel Right, IReadOnlyList<ModelDifference> Changes);

public static class PinnedModelRead
{
    public const string RevisionPrefix = "revision:";

    public static async Task<IResult> RevisionModel(
        Guid projectId, Guid revisionId, ClaimsPrincipal user, HttpContext http,
        AppDbContext db, WorkspaceAuthorizationService auth, CancellationToken ct)
    {
        var userId = user.UserId();
        await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Viewer, ct);

        var revision = await db.Revisions.FindAsync([revisionId], ct);
        if (revision is null || revision.ProjectId != projectId)
            return Results.NotFound(new ErrorResponse("not_found", "Revision not found"));

        var state = CreateRevision.ReadState(revision.State);
        return Results.Ok(new RevisionModelDto(
            CreateRevision.ToDto(revision),
            [.. state.Elements.Values.OrderBy(e => e.Name, StringComparer.Ordinal).ThenBy(e => e.Id.Value).Select(ToDto)],
            [.. state.Relationships.Values.OrderBy(r => r.Id.Value).Select(ToDto)],
            [.. state.Messages.Values.OrderBy(m => m.Name, StringComparer.Ordinal).ThenBy(m => m.Id.Value).Select(ToDto)],
            [.. state.Operations.Values.OrderBy(o => o.Name, StringComparer.Ordinal).ThenBy(o => o.Id.Value).Select(OperationDto.From)],
            [.. state.Channels.Values.OrderBy(c => c.Name, StringComparer.Ordinal).ThenBy(c => c.Id.Value).Select(ChannelDto.From)],
            [.. state.Subscriptions.Values.OrderBy(s => s.Id.Value).Select(SubscriptionDto.From)],
            [.. state.Bindings.Values.OrderBy(b => b.Id.Value).Select(BindingDto.From)]));
    }

    /// <summary>
    /// Compares two models of the project. Each side is <c>main</c>, the stream ID of an open
    /// draft or variant, or <c>revision:</c> and the ID of a named revision.
    /// </summary>
    public static async Task<IResult> Compare(
        Guid projectId, string? left, string? right, ClaimsPrincipal user, HttpContext http,
        WorkspaceAuthorizationService auth, CancellationToken ct)
    {
        var userId = user.UserId();
        await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Viewer, ct);

        var (a, refusalLeft) = await SideAsync(http.RequestServices, projectId, left, "left", ct);
        if (a is null)
            return refusalLeft!;
        var (b, refusalRight) = await SideAsync(http.RequestServices, projectId, right, "right", ct);
        if (b is null)
            return refusalRight!;

        // The values go out in the shapes the model lists give, so a reader of the comparison
        // and a reader of a list see the same fields and the same words.
        var differences = Differences(a.State, b.State)
            .Select(d => d with { Left = Shape(d.Left), Right = Shape(d.Right) })
            .ToList();
        return Results.Ok(new ModelComparisonDto(Of(a), Of(b), differences));
    }

    private static object? Shape(object? fact) => fact switch
    {
        ElementState e => ToDto(e),
        RelationshipState r => ToDto(r),
        MessageState m => ToDto(m),
        OperationState o => OperationDto.From(o),
        ChannelState c => ChannelDto.From(c),
        SubscriptionState s => SubscriptionDto.From(s),
        BindingState b => BindingDto.From(b),
        _ => null,
    };

    /// <summary>
    /// The facts that the two states do not share, in the order of the kinds and then by ID.
    /// Two facts are the same by the equality that a merge uses.
    /// </summary>
    public static IReadOnlyList<ModelDifference> Differences(ModelState left, ModelState right)
    {
        var differences = new List<ModelDifference>();
        void Of<TKey, T>(string entityType, IReadOnlyDictionary<TKey, T> a, IReadOnlyDictionary<TKey, T> b)
            where TKey : notnull, IArchitectureId
            where T : class
        {
            foreach (var id in a.Keys.Union(b.Keys).OrderBy(k => k.Value))
            {
                var (x, y) = (a.GetValueOrDefault(id), b.GetValueOrDefault(id));
                if (x is null || y is null || !x.Equals(y))
                    differences.Add(new ModelDifference(entityType, id.Value, x is null ? "added" : y is null ? "removed" : "modified", x, y));
            }
        }

        Of("element", left.Elements, right.Elements);
        Of("relationship", left.Relationships, right.Relationships);
        Of("message", left.Messages, right.Messages);
        Of("operation", left.Operations, right.Operations);
        Of("channel", left.Channels, right.Channels);
        Of("subscription", left.Subscriptions, right.Subscriptions);
        Of("binding", left.Bindings, right.Bindings);
        return differences;
    }

    private static ComparedModel Of(PinnedModel model) =>
        new(model.Pin.StreamId, model.Pin.Version, model.Pin.RevisionId, model.Pin.RevisionName);

    private static async Task<(PinnedModel? Model, IResult? Refusal)> SideAsync(
        IServiceProvider services, Guid projectId, string? side, string field, CancellationToken ct)
    {
        Guid? revisionId = null, branchId = null;
        if (side is not null && side.StartsWith(RevisionPrefix, StringComparison.Ordinal))
        {
            if (!Guid.TryParse(side[RevisionPrefix.Length..], out var id))
                return (null, Invalid(field));
            revisionId = id;
        }
        else if (side != "main")
        {
            if (!Guid.TryParse(side, out var id))
                return (null, Invalid(field));
            branchId = id;
        }

        // The refusals of the resolver name its own parameters, so a side is checked here and
        // a refusal names the side.
        if (revisionId is { } revision && !await services.GetRequiredService<AppDbContext>().Revisions
                .AnyAsync(r => r.Id == revision && r.ProjectId == projectId, ct))
            return (null, Invalid(field, "No revision with this ID exists in the project"));
        if (branchId is { } branch && !(await ModelBranches.OpenAsync(projectId,
                services.GetRequiredService<AppDbContext>(), ct)).Any(b => b.StreamId == branch))
            return (null, Invalid(field, "No open draft or variant with this stream ID exists in the project"));

        return await PinnedModel.ResolveAsync(services, projectId, revisionId, branchId, ct);
    }

    private static IResult Invalid(string field, string message = "Must be main, the stream ID of an open draft or variant, or revision: and the ID of a revision") =>
        Results.BadRequest(new ErrorResponse("validation_error", "Validation failed", Errors: [new FieldError(field, message)]));

    private static ElementDto ToDto(ElementState e) => new(
        e.Id, e.Kind.ToString().ToLowerInvariant(), e.Name, e.Description, e.Technology, e.OwnerId,
        e.Status.ToString().ToLowerInvariant(), e.ParentId?.Value, e.Tags, e.Icon,
        e.FactStatus.ToApiString(), ProvenanceMapper.ToDto(e.Provenance), StaleDto.From(e.Stale));

    private static RelationshipDto ToDto(RelationshipState r) => new(
        r.Id, r.FromId, r.ToId, r.Label, r.Technology, r.MessageId?.Value, r.LineShape,
        r.Kind.ToString().ToLowerInvariant(), r.FactStatus.ToApiString(), ProvenanceMapper.ToDto(r.Provenance), StaleDto.From(r.Stale));

    private static MessageDto ToDto(MessageState m) => new(
        m.Id, m.Name, m.Description, m.ProducerId, m.ConsumerIds.ToGuids(), m.SchemaId?.Value, m.Tags,
        m.FactStatus.ToApiString(), ProvenanceMapper.ToDto(m.Provenance), StaleDto.From(m.Stale));
}
