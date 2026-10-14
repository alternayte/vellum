using System.Security.Claims;
using Deedbox;
using Microsoft.EntityFrameworkCore;
using Vellum.Modules.Costs.Prices;
using Vellum.Modules.Modelling;
using Vellum.Modules.Settings;
using Vellum.Modules.Workspaces;
using Vellum.Shared;
using static Vellum.Shared.ResultExtensions;

namespace Vellum.Modules.Costs;

public sealed record InputMark(string Name, string Hash);

/// <summary>
/// The hash of each input of an estimate, by ID. Two indexes that differ say which resource,
/// assumption, price or setting changed. The mark of a resource covers whether its element
/// is in the model. <paramref name="Engine"/> is the engine that made the marks; it is null
/// in an index that engine <c>cost-1</c> stored. Marks of two engines do not compare.
/// </summary>
public sealed record EstimateInputsIndex(
    IReadOnlyDictionary<Guid, InputMark> Resources,
    IReadOnlyDictionary<Guid, InputMark> Assumptions,
    IReadOnlyDictionary<Guid, InputMark> Prices,
    string Currency,
    int StaleAfterDays,
    string? Engine = null);

/// <param name="Kind"><c>resource</c>, <c>assumption</c>, <c>price</c>, <c>setting</c> or <c>engine</c>.</param>
/// <param name="Change"><c>added</c>, <c>removed</c> or <c>changed</c>.</param>
public sealed record InputChange(string Kind, Guid? Id, string Name, string Change);

/// <summary>
/// The estimate of a branch as it is now. <paramref name="Hash"/> is SHA-256 of the canonical
/// JSON of <paramref name="Estimate"/>; <paramref name="InputsFingerprint"/> is the same of
/// the inputs, without the date.
/// </summary>
public sealed record EstimateDto(
    Guid BranchId, long ModelVersion, long SheetVersion, string InputsFingerprint, string Hash, Estimate Estimate);

/// <param name="AsOf">Default: today (UTC).</param>
public sealed record SaveEstimateRequest(string? Name, DateOnly? AsOf);

/// <param name="Outdated">True when the inputs of the branch are no longer the inputs the estimate was computed from.</param>
/// <param name="Changes">What changed since the save. Empty when <paramref name="Outdated"/> is false.</param>
/// <param name="Result">The stored estimate. Null in a list.</param>
public sealed record SavedEstimateDto(
    Guid Id, string Name, Guid BranchId, long ModelVersion, DateOnly AsOf, string InputsFingerprint, string Hash,
    string SavedBy, DateTimeOffset SavedAt, bool Outdated, IReadOnlyList<InputChange> Changes, Estimate? Result);

public sealed record ComparisonDto(EstimateDto Left, EstimateDto Right, Comparison Comparison);

/// <summary>Builds the estimate of a branch from what is stored now.</summary>
public sealed class EstimateReader(IEventStore store, AppDbContext db, SettingsResolver settings, WorkspaceAuthorizationService auth)
{
    internal sealed record Built(EstimateDto Dto, EstimateInputsIndex Inputs);

    internal async Task<Built> BuildAsync(Guid projectId, Guid branch, DateOnly asOf, CancellationToken ct)
    {
        var (sheet, sheetVersion) = await CostAccess.LoadSheetAsync(db, branch, ct);
        var (model, modelVersion) = await CostAccess.LoadModelAsync(store, branch, ct);
        var elements = CostAccess.ElementIds(model);

        var workspaceId = await auth.GetProjectWorkspaceIdAsync(projectId, ct);
        var cost = (await settings.GetWorkspaceAsync(workspaceId, ct)).Effective.Cost;

        var refs = sheet.Resources
            .SelectMany(r => r.Usage.Select(u => u.PriceRef).Append(r.PriceRef))
            .OfType<Guid>().Distinct().ToList();
        // Only the price book of the project's workspace is read.
        var entries = refs.Count == 0
            ? []
            : await db.PriceEntries.AsNoTracking().Where(p => p.WorkspaceId == workspaceId && refs.Contains(p.Id)).ToListAsync(ct);
        var prices = entries.ToDictionary(p => p.Id, ToPrice);

        var estimate = Estimator.Run(new EstimateInput(sheet.Resources, sheet.Assumptions, prices, elements, asOf, cost.Currency, cost.StalePriceAfterDays));
        var inputs = new EstimateInputsIndex(
            sheet.Resources.ToDictionary(r => r.Id, r => new InputMark(r.Name, CostJson.Hash(new { Resource = r, ElementInModel = elements.Contains(r.ElementId) }))),
            sheet.Assumptions.ToDictionary(a => a.Id, a => new InputMark(a.Name, CostJson.Hash(a))),
            entries.ToDictionary(p => p.Id, p => new InputMark($"{p.Provider} {p.Sku}".Trim(), CostJson.Hash(prices[p.Id]))),
            cost.Currency, cost.StalePriceAfterDays, Estimator.EngineVersion);

        return new Built(
            new EstimateDto(branch, modelVersion, sheetVersion, CostJson.Hash(inputs), CostJson.Hash(estimate), estimate),
            inputs);
    }

    private static Price ToPrice(PriceEntryEntity entry) => new(
        entry.Id, entry.Provider, entry.Region, entry.Sku, entry.MeterId, entry.Unit, entry.UnitPrice, entry.Currency, entry.EffectiveDate, entry.Source,
        DateOnly.FromDateTime((entry.FetchedAt ?? entry.AcceptedAt).UtcDateTime));

    internal static IReadOnlyList<InputChange> Changes(EstimateInputsIndex saved, EstimateInputsIndex now)
    {
        var changes = new List<InputChange>();
        // Another engine made the saved marks from other fields. They say which inputs were
        // added or removed, and nothing about a change of an input that is on both sides.
        var sameEngine = saved.Engine == now.Engine;
        if (!sameEngine)
            changes.Add(new InputChange("engine", null, now.Engine ?? "", "changed"));
        Diff("resource", saved.Resources, now.Resources);
        Diff("assumption", saved.Assumptions, now.Assumptions);
        Diff("price", saved.Prices, now.Prices);
        if (saved.Currency != now.Currency)
            changes.Add(new InputChange("setting", null, "cost.currency", "changed"));
        if (saved.StaleAfterDays != now.StaleAfterDays)
            changes.Add(new InputChange("setting", null, "cost.stalePriceAfterDays", "changed"));
        return changes;

        void Diff(string kind, IReadOnlyDictionary<Guid, InputMark> before, IReadOnlyDictionary<Guid, InputMark> after)
        {
            foreach (var id in before.Keys.Union(after.Keys).Order())
            {
                var was = before.GetValueOrDefault(id);
                var isNow = after.GetValueOrDefault(id);
                if (was is null)
                    changes.Add(new InputChange(kind, id, isNow!.Name, "added"));
                else if (isNow is null)
                    changes.Add(new InputChange(kind, id, was.Name, "removed"));
                else if (sameEngine && was.Hash != isNow.Hash)
                    changes.Add(new InputChange(kind, id, isNow.Name, "changed"));
            }
        }
    }
}

public static class Estimates
{
    public const int MaxSavedPerBranch = 100;

    private static DateOnly Today(TimeProvider time) => DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);

    /// <summary>The estimate of the branch, computed now. Nothing is stored.</summary>
    public static Task<IResult> Live(
        Guid projectId, Guid? branchId, DateOnly? asOf, HttpContext http, EstimateReader reader, TimeProvider time, CancellationToken ct) =>
        ModelBranches.RunAsync(http, projectId, branchId, WorkspaceRole.Viewer, async (branch, _) =>
            Results.Ok((await reader.BuildAsync(projectId, branch, asOf ?? Today(time), ct)).Dto), ct);

    /// <summary>
    /// Compares the estimates of two branches of the project at one date. <c>left</c> and
    /// <c>right</c> are <c>main</c> or the stream ID of an open draft or variant.
    /// </summary>
    public static async Task<IResult> Compare(
        Guid projectId, string? left, string? right, DateOnly? asOf,
        HttpContext http, WorkspaceAuthorizationService auth, EstimateReader reader, TimeProvider time, CancellationToken ct)
    {
        await auth.RequireProjectRoleAsync(projectId, http.User.UserId(), WorkspaceRole.Viewer, ct);

        var (leftBranch, leftRefusal) = await CostSheets.ResolveSideAsync(http.RequestServices, projectId, left, "left", ct);
        if (leftRefusal is not null)
            return leftRefusal;
        var (rightBranch, rightRefusal) = await CostSheets.ResolveSideAsync(http.RequestServices, projectId, right, "right", ct);
        if (rightRefusal is not null)
            return rightRefusal;

        var date = asOf ?? Today(time);
        var leftEstimate = (await reader.BuildAsync(projectId, leftBranch, date, ct)).Dto;
        var rightEstimate = (await reader.BuildAsync(projectId, rightBranch, date, ct)).Dto;
        return Results.Ok(new ComparisonDto(leftEstimate, rightEstimate, Comparer.Run(leftEstimate.Estimate, rightEstimate.Estimate)));
    }

    /// <summary>Stores the estimate of the branch as it is now. A stored estimate never changes.</summary>
    public static Task<IResult> Save(
        Guid projectId, Guid? branchId, SaveEstimateRequest request,
        HttpContext http, EstimateReader reader, AppDbContext db, TimeProvider time, CancellationToken ct) =>
        ModelBranches.RunAsync(http, projectId, branchId, WorkspaceRole.Editor, async (branch, userId) =>
        {
            var asOf = request.AsOf ?? Today(time);
            var name = string.IsNullOrWhiteSpace(request.Name) ? $"Estimate {Estimator.Day(asOf)}" : request.Name.Trim();
            if (TextRule.Line("name", name) is { } error)
                return Invalid([error]);

            var built = await reader.BuildAsync(projectId, branch, asOf, ct);
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            // One save of a branch at a time, so the count is the count at the insert.
            await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtextextended({"costs.estimates:" + branch}, 0))", ct);
            if (await db.Estimates.CountAsync(e => e.ProjectId == projectId && e.BranchId == branch, ct) >= MaxSavedPerBranch)
                return Results.Conflict(new ErrorResponse("conflict", $"A branch holds at most {MaxSavedPerBranch} saved estimates. Delete one first."));
            // The entries the estimate was made with are locked until the commit, so a delete of
            // one of them waits and then sees that this estimate uses it.
            var used = built.Inputs.Prices.Keys.ToList();
            if ((await PriceUsage.LockAsync(db, used, ct)).Count < used.Count)
                return Results.Conflict(new ErrorResponse("conflict", "A price entry of the estimate was deleted while it was saved. Save again."));

            var row = new SavedEstimateEntity
            {
                Id = Guid.NewGuid(),
                ProjectId = projectId,
                BranchId = branch,
                Name = name,
                ModelVersion = built.Dto.ModelVersion,
                AsOf = asOf,
                InputsFingerprint = built.Dto.InputsFingerprint,
                Hash = built.Dto.Hash,
                Inputs = CostJson.Write(built.Inputs),
                Result = CostJson.Write(built.Dto.Estimate),
                SavedBy = userId,
                SavedAt = time.GetUtcNow(),
            };
            db.Estimates.Add(row);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return Results.Created($"/api/projects/{projectId}/costs/estimates/{row.Id}", ToDto(row, built.Inputs, withResult: true));
        }, ct);

    /// <summary>The saved estimates of the branch, newest first, each with what changed since it was saved.</summary>
    public static Task<IResult> List(
        Guid projectId, Guid? branchId, HttpContext http, EstimateReader reader, AppDbContext db, TimeProvider time, CancellationToken ct) =>
        ModelBranches.RunAsync(http, projectId, branchId, WorkspaceRole.Viewer, async (branch, _) =>
        {
            var rows = await db.Estimates.AsNoTracking()
                .Where(e => e.ProjectId == projectId && e.BranchId == branch)
                .OrderByDescending(e => e.SavedAt).ThenBy(e => e.Id)
                .ToListAsync(ct);
            if (rows.Count == 0)
                return Results.Ok(Array.Empty<SavedEstimateDto>());

            var now = (await reader.BuildAsync(projectId, branch, Today(time), ct)).Inputs;
            return Results.Ok(rows.Select(row => ToDto(row, now, withResult: false)).ToList());
        }, ct);

    public static Task<IResult> Get(
        Guid projectId, Guid estimateId, Guid? branchId, HttpContext http, EstimateReader reader, AppDbContext db, TimeProvider time, CancellationToken ct) =>
        ModelBranches.RunAsync(http, projectId, branchId, WorkspaceRole.Viewer, async (branch, _) =>
        {
            var row = await db.Estimates.AsNoTracking()
                .FirstOrDefaultAsync(e => e.Id == estimateId && e.ProjectId == projectId && e.BranchId == branch, ct);
            if (row is null)
                return NotFound("Saved estimate");
            var now = (await reader.BuildAsync(projectId, branch, Today(time), ct)).Inputs;
            return Results.Ok(ToDto(row, now, withResult: true));
        }, ct);

    public static Task<IResult> Delete(
        Guid projectId, Guid estimateId, Guid? branchId, HttpContext http, AppDbContext db, CancellationToken ct) =>
        ModelBranches.RunAsync(http, projectId, branchId, WorkspaceRole.Editor, async (branch, _) =>
            await db.Estimates.Where(e => e.Id == estimateId && e.ProjectId == projectId && e.BranchId == branch).ExecuteDeleteAsync(ct) == 1
                ? Results.NoContent()
                : NotFound("Saved estimate"), ct);

    private static SavedEstimateDto ToDto(SavedEstimateEntity row, EstimateInputsIndex now, bool withResult)
    {
        var changes = EstimateReader.Changes(CostJson.Read<EstimateInputsIndex>(row.Inputs), now);
        return new SavedEstimateDto(
            row.Id, row.Name, row.BranchId, row.ModelVersion, row.AsOf, row.InputsFingerprint, row.Hash, row.SavedBy, row.SavedAt,
            changes.Count > 0, changes, withResult ? CostJson.Read<Estimate>(row.Result) : null);
    }
}
