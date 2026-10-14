using Deedbox;
using Microsoft.EntityFrameworkCore;
using Vellum.Modules.Trace.Scenarios.Entities;
using Vellum.Shared;

namespace Vellum.Modules.Trace.Scenarios;

/// <summary>
/// The scenario list read model: one row per live scenario. It runs inline, so the row
/// commits in the same transaction as the event. The scenario ID is the stream ID.
/// </summary>
public sealed class ScenarioProjection : Projection<AppDbContext>
{
    public ScenarioProjection()
    {
        On<ScenarioEvent.ScenarioSaved>(async (saved, ctx) =>
        {
            var id = Guid.Parse(ctx.StreamId);
            var row = await ctx.Db.Scenarios.FindAsync([id], ctx.CancellationToken);
            if (row is null)
            {
                ctx.Db.Scenarios.Add(new ScenarioEntity { Id = id, ProjectId = saved.ProjectId, Name = saved.Name });
                return;
            }

            row.Name = saved.Name;
        });

        On<ScenarioEvent.ScenarioDeleted>(async (_, ctx) =>
        {
            var row = await ctx.Db.Scenarios.FindAsync([Guid.Parse(ctx.StreamId)], ctx.CancellationToken);
            if (row is not null) ctx.Db.Scenarios.Remove(row);
        });
    }

    protected override Task ResetAsync(WriteContext<AppDbContext> context) =>
        context.Db.Scenarios.ExecuteDeleteAsync(context.CancellationToken);
}
