using Deedbox;
using Microsoft.EntityFrameworkCore;
using Vellum.Modules.Drafts;
using Vellum.Modules.Modelling;
using Vellum.Modules.Workspaces;
using Vellum.Shared;
using static Vellum.Shared.ResultExtensions;

namespace Vellum.Modules.Promises;

/// <summary>A stream folded from its stored events: the state, the version it is the state of, and the events that were kept.</summary>
public sealed record FoldedStream<TState>(TState State, long Version, IReadOnlyList<object> Kept);

/// <summary>
/// Reads a stream as it was at one version, by folding its stored events from the first to
/// that version. An export names the version of every stream it read; this gives exactly
/// that input again, whatever was appended later.
/// </summary>
public static class StreamAt
{
    private const int PageSize = 500;

    /// <param name="version">The version to stop at. Null means the newest event.</param>
    /// <param name="keep">Events for which this is true are returned beside the state, in order.</param>
    /// <returns>Null when the stream has no such version, has no event at all, or was deleted.</returns>
    public static async Task<FoldedStream<TState>?> FoldAsync<TState>(
        IEventStore store, Guid streamId, long? version, CancellationToken ct, Func<object, bool>? keep = null)
        where TState : IState<TState>
    {
        if (version is < 1)
            return null;

        var state = TState.Initial;
        var kept = new List<object>();
        long after = 0;
        while (version is null || after < version)
        {
            var page = await store.ReadStream(
                StreamId.From(streamId), after, version is { } last ? (int)Math.Min(PageSize, last - after) : PageSize, ct);
            if (page.Count == 0)
                break;
            foreach (var envelope in page)
            {
                // The tombstone is all that is left of a deleted stream.
                if (envelope.Event is StreamDeleted)
                    return null;
                state = TState.Evolve(state, envelope.Event);
                if (keep?.Invoke(envelope.Event) == true)
                    kept.Add(envelope.Event);
            }
            after = page[^1].Version;
        }

        return after == 0 || (version is { } wanted && after != wanted) ? null : new FoldedStream<TState>(state, after, kept);
    }
}

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
        var db = services.GetRequiredService<AppDbContext>();
        if (revisionId is not null && branchId is not null)
            return (null, Invalid("revisionId", "Give a revision or a branch, not both"));

        if (revisionId is { } id)
        {
            var revision = await db.Revisions.AsNoTracking()
                .FirstOrDefaultAsync(r => r.Id == id && r.ProjectId == projectId, ct);
            return revision is null
                ? (null, Invalid("revisionId", "No revision with this ID exists in the project"))
                : (new PinnedModel(
                    new ModelPin(revision.StreamId, revision.Version, revision.Id, revision.Name),
                    CreateRevision.ReadState(revision.State)), null);
        }

        var mainStreamId = await services.GetRequiredService<AppDbContext>().Projects.AsNoTracking()
            .Where(p => p.Id == projectId).Select(p => p.StreamId).FirstAsync(ct);
        var (streamId, refusal) = await ModelBranches.ResolveAsync(
            branchId, projectId, mainStreamId, db, forWrite: false, ct);
        if (refusal is not null)
            return (null, refusal);

        var (state, version) = await services.GetRequiredService<IEventStore>().Load<ModelState>(StreamId.From(streamId), ct);
        return (new PinnedModel(new ModelPin(streamId, version, null, null), state), null);
    }

    /// <summary>
    /// The model of a branch stream as it was at <paramref name="version"/>, folded from the
    /// stored events. Null when the stream has fewer events than that.
    /// </summary>
    public static async Task<ModelState?> AtVersionAsync(IEventStore store, Guid streamId, long version, CancellationToken ct) =>
        (await StreamAt.FoldAsync<ModelState>(store, streamId, version, ct))?.State;

}
