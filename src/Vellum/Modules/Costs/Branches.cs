using Microsoft.EntityFrameworkCore;
using Vellum.Modules.Modelling.Model;
using Vellum.Shared;
using Decision = Vellum.Shared.CommandResult<System.Collections.Generic.IReadOnlyList<object>>;

namespace Vellum.Modules.Costs;

/// <param name="Events">The changes that make the sheet of main hold the merged sheet. Empty when the draft changed nothing.</param>
/// <param name="Conflicts">Why the two sheets do not merge, one sentence part for each cause. The merge is refused while this is not empty.</param>
public sealed record SheetMergePlan(IReadOnlyList<object> Events, IReadOnlyList<string> Conflicts);

/// <param name="Changes">The resources and assumptions that the merge adds to, changes in or removes from the sheet of main.</param>
/// <param name="Conflicts">Why the sheet of the draft does not merge. A merge is refused while this is not empty.</param>
public sealed record SheetMergePreview(int Changes, IReadOnlyList<string> Conflicts);

/// <summary>
/// The merge of the cost sheet of a draft into the sheet of the branch the draft was forked
/// from. It follows the merge of the model: a three-way merge by ID, with the sheet as it was
/// when the draft copied it as the common ancestor.
/// <list type="bullet">
/// <item>An entry that the draft did not change keeps what main holds.</item>
/// <item>An entry that only the draft changed, added or removed gets the draft's value.</item>
/// <item>An entry that both sides changed to the same value is no conflict.</item>
/// <item>An entry that both sides changed in different ways is a conflict.</item>
/// </list>
/// A draft sheet that is not a copy of the sheet of main has an empty ancestor: all it holds
/// counts as added by the draft. The model merge takes a resolution for each conflict. A
/// sheet conflict has none: the merge is refused until the two sheets agree, because a
/// choice made without a person would drop a number of one side.
/// </summary>
public static class SheetMerge
{
    public static SheetMergePlan Plan(
        CostSheetState main, CostSheetState draft, Guid projectId, Guid mainBranch, string userId, DateTimeOffset now)
    {
        var ancestor = draft.Base is { } copied && copied.FromBranchId == mainBranch ? copied : null;
        var events = new List<object>();
        var conflicts = new List<string>();

        Merge(ancestor?.Assumptions ?? [], main.Assumptions, draft.Assumptions, a => a.Id, a => a.Name, "assumption",
            a => new CostChange.AssumptionAdded(a, userId, now), a => new CostChange.AssumptionChanged(a, userId, now),
            id => new CostChange.AssumptionRemoved(id, userId, now), events, conflicts);
        Merge(ancestor?.Resources ?? [], main.Resources, draft.Resources, r => r.Id, r => r.Name, "resource",
            r => new CostChange.ResourceAdded(r, userId, now), r => new CostChange.ResourceChanged(r, userId, now),
            id => new CostChange.ResourceRemoved(id, userId, now), events, conflicts);

        // The rules of a sheet that a decider checks for one change hold for the merged sheet too.
        var merged = events.Aggregate(main, CostSheetState.Evolve);
        if (merged.Resources.Count > CostRules.MaxResources)
            conflicts.Add($"the merged sheet has {merged.Resources.Count} resources and a sheet holds at most {CostRules.MaxResources}");
        if (merged.Assumptions.Count > CostRules.MaxAssumptions)
            conflicts.Add($"the merged sheet has {merged.Assumptions.Count} assumptions and a sheet holds at most {CostRules.MaxAssumptions}");
        conflicts.AddRange(merged.Resources
            .GroupBy(r => (r.ElementId, r.Name)).Where(g => g.Count() > 1)
            .OrderBy(g => g.Key.Name, StringComparer.Ordinal).ThenBy(g => g.Key.ElementId)
            .Select(g => $"the draft and main each have a resource '{g.Key.Name}' for one element"));
        conflicts.AddRange(merged.Assumptions
            .GroupBy(a => a.Name).Where(g => g.Count() > 1)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => $"the draft and main each have an assumption '{g.Key}'"));

        if (conflicts.Count > 0)
            return new SheetMergePlan([], conflicts);
        if (events.Count > 0 && main.ProjectId is null)
            events.Insert(0, new CostChange.SheetOpened(projectId, mainBranch, userId, now));
        return new SheetMergePlan(events, []);
    }

    public static SheetMergePreview Preview(SheetMergePlan plan) =>
        new(plan.Events.Count(e => e is not CostChange.SheetOpened), plan.Conflicts);

    /// <summary>The refusal of a merge as one text, or null when the sheets merge.</summary>
    public static string? Refusal(SheetMergePlan plan)
    {
        const int shown = 10;
        if (plan.Conflicts.Count == 0)
            return null;
        var listed = string.Join("; ", plan.Conflicts.Take(shown));
        if (plan.Conflicts.Count > shown)
            listed += $"; and {plan.Conflicts.Count - shown} more";
        return $"In the cost sheets, {listed}. Change the cost sheet of the draft or of main until the two agree, then merge again.";
    }

    private static void Merge<T>(
        IEnumerable<T> ancestor, IEnumerable<T> main, IEnumerable<T> draft, Func<T, Guid> id, Func<T, string> name, string what,
        Func<T, object> added, Func<T, object> changed, Func<Guid, object> removed, List<object> events, List<string> conflicts)
        where T : class
    {
        var (was, ours, theirs) = (ancestor.ToDictionary(id), main.ToDictionary(id), draft.ToDictionary(id));
        foreach (var key in was.Keys.Union(ours.Keys).Union(theirs.Keys).Order())
        {
            var (b, m, d) = (was.GetValueOrDefault(key), ours.GetValueOrDefault(key), theirs.GetValueOrDefault(key));
            // The draft changed nothing, or both sides hold the same value.
            if (Same(d, b) || Same(d, m))
                continue;
            if (!Same(m, b))
            {
                conflicts.Add(
                    d is null ? $"the {what} '{name(m!)}' is removed in the draft and changed on main"
                    : m is null ? $"the {what} '{name(d)}' is changed in the draft and removed on main"
                    : b is null ? $"the {what} '{name(d)}' is added on both sides with different values"
                    : $"the {what} '{name(d)}' is changed on both sides");
                continue;
            }

            events.Add(d is null ? removed(key) : m is null ? added(d) : changed(d));
        }
    }

    /// <summary>Equal by content. A record with a list in it is not equal to its own copy by <c>Equals</c>.</summary>
    private static bool Same<T>(T? left, T? right) where T : class =>
        left is null || right is null ? left is null && right is null : CostJson.Write(left) == CostJson.Write(right);
}

/// <summary>
/// What the cost module does when a model branch is forked, merged or closed. Each method
/// runs in the transaction of its caller, so the sheet changes commit with the change of the
/// branch or not at all.
/// </summary>
public static class CostBranches
{
    // Any constant works; it keeps this lock apart from other advisory locks in the database.
    private const int LockSpace = 0x434F5354;

    /// <summary>
    /// Locks the sheets of the branches until the commit, in ascending branch-ID order, and
    /// returns them. The lock is an advisory lock on the branch, so a branch with no sheet
    /// yet is locked too. A unit that also writes model streams takes it after its stream
    /// locks and before its first append (see <see cref="Vellum.Shared.SharedTransaction"/>).
    /// A writer of one sheet takes no other lock while it holds this one.
    /// </summary>
    public static async Task<Dictionary<Guid, (CostSheetState State, long Version)>> LockSheetsAsync(
        AppDbContext db, IEnumerable<Guid> branches, CancellationToken ct)
    {
        var sheets = new Dictionary<Guid, (CostSheetState State, long Version)>();
        foreach (var branch in branches.Distinct().Order())
        {
            await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({LockSpace}, hashtext({branch.ToString()}))", ct);
            sheets[branch] = await CostAccess.LoadSheetAsync(db, branch, ct);
        }

        return sheets;
    }

    /// <summary>
    /// True when the branch is a draft that was merged or abandoned. Its sheet is gone, and a
    /// writer that waited for the lock of the sheet while the branch closed writes nothing.
    /// </summary>
    public static Task<bool> IsClosedAsync(AppDbContext db, Guid branch, CancellationToken ct) =>
        db.Drafts.AnyAsync(d => d.StreamId == branch && d.Status != "open", ct);

    /// <summary>
    /// Gives a new branch a copy of <paramref name="source"/>, the sheet of
    /// <paramref name="fromBranch"/>. A resource whose element is not in
    /// <paramref name="model"/>, the model of the new branch, is left out. A source with no
    /// sheet gives no sheet. The branch is new and no other writer knows it, so no lock is taken.
    /// </summary>
    public static async Task ForkAsync(
        AppDbContext db, Guid projectId, Guid branch, ModelState model,
        CostSheetState source, Guid fromBranch, long fromVersion, string userId, DateTimeOffset now, CancellationToken ct)
    {
        if (fromVersion == 0)
            return;
        var copy = (Decision.Success)CostDecider.Copy(
            CostSheetState.Initial, projectId, branch, source, fromBranch, fromVersion,
            CostAccess.World(model, new HashSet<Guid>()), userId, now);
        await CostAccess.SaveSheetAsync(db, projectId, branch, CostSheetState.Initial, 0, copy.Value, now, ct);
    }

    /// <summary>
    /// Applies a merge plan to <paramref name="main"/>, the sheet of main that the caller
    /// locked at <paramref name="version"/>.
    /// </summary>
    public static async Task ApplyAsync(
        AppDbContext db, Guid projectId, Guid mainBranch, CostSheetState main, long version, SheetMergePlan plan, DateTimeOffset now, CancellationToken ct)
    {
        if (plan.Events.Count == 0)
            return;
        await CostAccess.SaveSheetAsync(db, projectId, mainBranch, main, version, plan.Events, now, ct);
    }

    /// <summary>
    /// Deletes what the cost module holds for a branch that is closed: the sheet and the
    /// saved estimates. A closed branch resolves for no request, so nothing could read them
    /// again. <paramref name="db"/> is the context whose transaction the caller opened.
    /// </summary>
    public static async Task CloseAsync(DbContext db, Guid projectId, Guid branch, CancellationToken ct)
    {
        await db.Database.ExecuteSqlAsync($"DELETE FROM costs.sheets WHERE project_id = {projectId} AND branch_id = {branch}", ct);
        await db.Database.ExecuteSqlAsync($"DELETE FROM costs.estimates WHERE project_id = {projectId} AND branch_id = {branch}", ct);
    }
}
