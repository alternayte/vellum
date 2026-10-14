using Deedbox;
using Microsoft.EntityFrameworkCore;
using Vellum.Modules.Promises;
using Vellum.Modules.Review.Rules;
using Vellum.Shared;
using Outcome = Vellum.Shared.CommandResult<System.Collections.Generic.IReadOnlyList<object>>;

namespace Vellum.Modules.Review;

/// <summary>
/// The decision read model. It runs inline, so a row commits in the same transaction as its
/// event. The decision ID is the stream ID, and each event is folded into the stored state.
/// </summary>
public sealed class DecisionProjection : Projection<AppDbContext>
{
    public DecisionProjection()
    {
        On<DecisionEvent.DecisionProposed>(async (proposed, ctx) =>
        {
            var id = Guid.Parse(ctx.StreamId);
            var row = await ctx.Db.Decisions.FindAsync([id], ctx.CancellationToken);
            if (row is null)
            {
                row = new DecisionEntity { Id = id, ProjectId = proposed.ProjectId };
                ctx.Db.Decisions.Add(row);
            }
            Write(row, DecisionState.Evolve(DecisionState.Initial, proposed), ctx.Version);
        });

        Fold<DecisionEvent.DecisionChanged>();
        Fold<DecisionEvent.DecisionAccepted>();
        Fold<DecisionEvent.DecisionRejected>();
        Fold<DecisionEvent.SettlementRecorded>();
        Fold<DecisionEvent.SettlementRemoved>();

        On<DecisionEvent.DecisionDeleted>((_, ctx) => RemoveAsync(ctx));
        // The decision streams of a deleted project are deleted with it. The tombstone of any
        // stream comes here; an ID that is no decision matches no row.
        On<StreamDeleted>((_, ctx) => RemoveAsync(ctx));
    }

    private void Fold<TEvent>() where TEvent : class =>
        On<TEvent>(async (@event, ctx) =>
        {
            if (await ctx.Db.Decisions.FindAsync([Guid.Parse(ctx.StreamId)], ctx.CancellationToken) is { } row)
                Write(row, DecisionState.Evolve(PromiseJson.Read<DecisionState>(row.Document), @event), ctx.Version);
        });

    private static async Task RemoveAsync(ProjectionContext<AppDbContext> ctx)
    {
        if (Guid.TryParse(ctx.StreamId, out var id) && await ctx.Db.Decisions.FindAsync([id], ctx.CancellationToken) is { } row)
            ctx.Db.Decisions.Remove(row);
    }

    private static void Write(DecisionEntity row, DecisionState state, long version)
    {
        row.Version = version;
        row.Number = state.Number;
        row.Title = state.Content!.Title;
        row.Status = state.Status;
        row.UpdatedAt = state.UpdatedAt!.Value;
        row.Document = PromiseJson.Write(state);
    }

    protected override async Task ResetAsync(WriteContext<AppDbContext> context) =>
        await context.Db.Decisions.ExecuteDeleteAsync(context.CancellationToken);
}
