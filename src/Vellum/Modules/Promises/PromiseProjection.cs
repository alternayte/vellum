using Deedbox;
using Microsoft.EntityFrameworkCore;
using Vellum.Shared;

namespace Vellum.Modules.Promises;

/// <summary>
/// The promise read model. It runs inline, so a row commits in the same transaction as its
/// event. The promise ID is the stream ID. Each event is folded into the stored state with
/// <see cref="PromiseState.Evolve"/>, so a row and a loaded stream never disagree.
/// </summary>
public sealed class PromiseProjection : Projection<AppDbContext>
{
    public PromiseProjection()
    {
        On<PromiseEvent.PromiseCreated>(async (created, ctx) =>
        {
            var id = Guid.Parse(ctx.StreamId);
            var row = await ctx.Db.Promises.FindAsync([id], ctx.CancellationToken);
            if (row is null)
            {
                row = new PromiseEntity { Id = id, ProjectId = created.ProjectId };
                ctx.Db.Promises.Add(row);
            }
            Write(row, PromiseState.Evolve(PromiseState.Initial, created), ctx.Version);
        });

        Fold<PromiseEvent.PromiseDraftChanged>();
        Fold<PromiseEvent.PromiseRevised>();
        Fold<PromiseEvent.PromiseApproved>();
        Fold<PromiseEvent.ObligationAdded>();
        Fold<PromiseEvent.ObligationChanged>();
        Fold<PromiseEvent.ObligationRemoved>();

        On<PromiseEvent.PromiseDeleted>(async (_, ctx) =>
        {
            var id = Guid.Parse(ctx.StreamId);
            if (await ctx.Db.Promises.FindAsync([id], ctx.CancellationToken) is { } row)
                ctx.Db.Promises.Remove(row);
            foreach (var run in await ctx.Db.PromiseRuns.Where(r => r.PromiseId == id).ToListAsync(ctx.CancellationToken))
                ctx.Db.PromiseRuns.Remove(run);
        });

        // The promise streams of a deleted project are deleted with it. Their rows go too, and
        // a rebuild does not bring them back. The tombstone of any stream comes here; an ID
        // that is no promise matches no row.
        On<StreamDeleted>(async (_, ctx) =>
        {
            if (!Guid.TryParse(ctx.StreamId, out var id))
                return;
            foreach (var run in await ctx.Db.PromiseRuns.Where(r => r.PromiseId == id).ToListAsync(ctx.CancellationToken))
                ctx.Db.PromiseRuns.Remove(run);
            if (await ctx.Db.Promises.FindAsync([id], ctx.CancellationToken) is { } row)
                ctx.Db.Promises.Remove(row);
        });

        On<PromiseEvent.VerificationRunRecorded>(async (recorded, ctx) =>
        {
            var id = Guid.Parse(ctx.StreamId);
            if (await ctx.Db.Promises.FindAsync([id], ctx.CancellationToken) is not { } row)
                return;

            var run = recorded.Run;
            ctx.Db.PromiseRuns.Add(new RunEntity
            {
                Id = run.Id, PromiseId = id, ProjectId = row.ProjectId, Version = ctx.Version,
                ObligationId = run.ObligationId, PromiseRevision = run.PromiseRevision,
                Result = EnumText.Parse<StoredRunResult>(run.Result), At = run.At, Document = PromiseJson.Write(run),
            });

            // A run against a comparison scan is in the runs table only: it leaves the promise as it was against the followed ref.
            if (PromiseState.CountsAsLatest(run))
            {
                var latest = PromiseJson.Read<Dictionary<string, VerificationRun>>(row.LatestRuns);
                latest[PromiseState.RunKey(run.PromiseRevision, run.ObligationId)] = run;
                row.LatestRuns = PromiseJson.Write(latest);
            }
            Write(row, PromiseState.Evolve(PromiseJson.Read<PromiseState>(row.Document), recorded), ctx.Version);
        });
    }

    private void Fold<TEvent>() where TEvent : class =>
        On<TEvent>(async (@event, ctx) =>
        {
            if (await ctx.Db.Promises.FindAsync([Guid.Parse(ctx.StreamId)], ctx.CancellationToken) is { } row)
                Write(row, PromiseState.Evolve(PromiseJson.Read<PromiseState>(row.Document), @event), ctx.Version);
        });

    private static void Write(PromiseEntity row, PromiseState state, long version)
    {
        row.Version = version;
        row.RequirementId = state.RequirementId!;
        row.Number = state.Number;
        row.Title = (state.InForce ?? state.Latest!).Content.Title;
        row.Lifecycle = EnumText.Parse<StoredPromiseLifecycle>(state.Lifecycle);
        row.Origin = EnumText.Parse<StoredPromiseOrigin>(state.Origin!);
        row.UpdatedAt = state.UpdatedAt!.Value;
        row.Document = PromiseJson.Write(state);
    }

    protected override async Task ResetAsync(WriteContext<AppDbContext> context)
    {
        await context.Db.PromiseRuns.ExecuteDeleteAsync(context.CancellationToken);
        await context.Db.Promises.ExecuteDeleteAsync(context.CancellationToken);
    }
}
