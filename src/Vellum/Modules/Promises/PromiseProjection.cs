using System.Globalization;
using Deedbox;
using Microsoft.EntityFrameworkCore;

namespace Vellum.Modules.Promises;

/// <summary>
/// The promise read model. It runs inline, so a row commits in the same transaction as its
/// event. The promise ID is the stream ID. Each event is folded into the stored state with
/// <see cref="PromiseState.Evolve"/>, so a row and a loaded stream never disagree.
/// </summary>
public sealed class PromiseProjection : Projection<PromisesDbContext>
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
            Write(row, PromiseState.Evolve(PromiseState.Initial, created));
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
            foreach (var run in await ctx.Db.Runs.Where(r => r.PromiseId == id).ToListAsync(ctx.CancellationToken))
                ctx.Db.Runs.Remove(run);
        });

        On<PromiseEvent.VerificationRunRecorded>(async (recorded, ctx) =>
        {
            var id = Guid.Parse(ctx.StreamId);
            if (await ctx.Db.Promises.FindAsync([id], ctx.CancellationToken) is not { } row)
                return;

            var run = recorded.Run;
            ctx.Db.Runs.Add(new RunEntity
            {
                Id = run.Id, PromiseId = id, ProjectId = row.ProjectId, Version = ctx.Version,
                ObligationId = run.ObligationId, PromiseRevision = run.PromiseRevision,
                Result = run.Result, At = run.At, Document = PromiseJson.Write(run),
            });

            var latest = PromiseJson.Read<Dictionary<string, VerificationRun>>(row.LatestRuns);
            latest[LatestRunKey(run.PromiseRevision, run.ObligationId)] = run;
            row.LatestRuns = PromiseJson.Write(latest);
            Write(row, PromiseState.Evolve(PromiseJson.Read<PromiseState>(row.Document), recorded));
        });
    }

    public static string LatestRunKey(int revision, string obligationId) =>
        string.Create(CultureInfo.InvariantCulture, $"{revision}:{obligationId}");

    private void Fold<TEvent>() where TEvent : class =>
        On<TEvent>(async (@event, ctx) =>
        {
            if (await ctx.Db.Promises.FindAsync([Guid.Parse(ctx.StreamId)], ctx.CancellationToken) is { } row)
                Write(row, PromiseState.Evolve(PromiseJson.Read<PromiseState>(row.Document), @event));
        });

    private static void Write(PromiseEntity row, PromiseState state)
    {
        row.RequirementId = state.RequirementId!;
        row.Number = state.Number;
        row.Title = (state.InForce ?? state.Latest!).Content.Title;
        row.Lifecycle = state.Lifecycle;
        row.Origin = state.Origin!;
        row.UpdatedAt = state.UpdatedAt!.Value;
        row.Document = PromiseJson.Write(state);
    }

    protected override async Task ResetAsync(WriteContext<PromisesDbContext> context)
    {
        await context.Db.Runs.ExecuteDeleteAsync(context.CancellationToken);
        await context.Db.Promises.ExecuteDeleteAsync(context.CancellationToken);
    }
}
