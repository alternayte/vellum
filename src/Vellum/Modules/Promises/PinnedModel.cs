using Deedbox;
using Microsoft.EntityFrameworkCore;
using Vellum.Modules.Drafts;
using Vellum.Modules.Modelling;
using Vellum.Modules.Modelling.Model;
using Vellum.Modules.Modelling.Revisions;
using Vellum.Modules.Workspaces;
using Vellum.Shared;

namespace Vellum.Modules.Promises;

/// <summary>
/// A model at one exact place: a named revision, or a branch stream at the version it had
/// when it was read. A check and an export say which one they used, so a result can be
/// repeated on the same input (SDD guarantee 4).
/// </summary>
public sealed record PinnedModel(ModelPin Pin, ModelState State)
{
    /// <summary>
    /// A named revision when <paramref name="revisionId"/> is given, else the branch of
    /// <paramref name="branchId"/> (the main model when null) at its current version.
    /// </summary>
    public static async Task<(PinnedModel? Model, IResult? Refusal)> ResolveAsync(
        IServiceProvider services, Guid projectId, Guid? revisionId, Guid? branchId, CancellationToken ct)
    {
        var modellingDb = services.GetRequiredService<ModellingDbContext>();
        if (revisionId is not null && branchId is not null)
            return (null, Invalid("revisionId", "Give a revision or a branch, not both"));

        if (revisionId is { } id)
        {
            var revision = await modellingDb.Revisions.AsNoTracking()
                .FirstOrDefaultAsync(r => r.Id == id && r.ProjectId == projectId, ct);
            return revision is null
                ? (null, Invalid("revisionId", "No revision with this ID exists in the project"))
                : (new PinnedModel(
                    new ModelPin(revision.StreamId, revision.Version, revision.Id, revision.Name),
                    CreateRevision.ReadState(revision.State)), null);
        }

        var mainStreamId = await services.GetRequiredService<WorkspacesDbContext>().Projects.AsNoTracking()
            .Where(p => p.Id == projectId).Select(p => p.StreamId).FirstAsync(ct);
        var (streamId, refusal) = await ModelBranches.ResolveAsync(
            branchId, projectId, mainStreamId, services.GetRequiredService<DraftsDbContext>(), modellingDb, forWrite: false, ct);
        if (refusal is not null)
            return (null, refusal);

        var (state, version) = await services.GetRequiredService<IEventStore>().Load<ModelState>(StreamId.From(streamId), ct);
        return (new PinnedModel(new ModelPin(streamId, version, null, null), state), null);
    }

    /// <summary>
    /// The model of a branch stream as it was at <paramref name="version"/>, folded from the
    /// stored events. Null when the stream has fewer events than that.
    /// </summary>
    public static async Task<ModelState?> AtVersionAsync(IEventStore store, Guid streamId, long version, CancellationToken ct)
    {
        var state = ModelState.Initial;
        long after = 0;
        while (after < version)
        {
            var page = await store.ReadStream(StreamId.From(streamId), after, (int)Math.Min(500, version - after), ct);
            if (page.Count == 0)
                return null;
            foreach (var envelope in page)
                state = ModelState.Evolve(state, envelope.Event);
            after = page[^1].Version;
        }
        return after == version ? state : null;
    }

    private static IResult Invalid(string field, string message) =>
        Results.BadRequest(new ErrorResponse("validation_error", "Validation failed", Errors: [new FieldError(field, message)]));
}
