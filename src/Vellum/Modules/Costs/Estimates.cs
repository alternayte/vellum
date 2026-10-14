using System.Security.Claims;
using Deedbox;
using Microsoft.EntityFrameworkCore;
using Vellum.Modules.Costs.Prices;
using Vellum.Modules.Modelling;
using Vellum.Modules.Settings.Document;
using Vellum.Modules.Workspaces.Authorization;
using Vellum.Shared;

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
        var (sheet, sheetVersion) = await CostAccess.LoadSheetAsync(store, branch, ct);
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
        await auth.RequireProjectRoleAsync(projectId, PriceBook.UserId(http.User), WorkspaceRole.Viewer, ct);

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
                return PriceBook.Invalid([error]);

            var built = await reader.BuildAsync(projectId, branch, asOf, ct);
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            // One save of a branch at a time, so the count is the count at the insert.
            await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtextextended({"costs.estimates:" + branch}, 0))", ct);
            if (await db.Estimates.CountAsync(e => e.ProjectId == projectId && e.BranchId == branch, ct) >= MaxSavedPerBranch)
                return Results.Conflict(new ErrorResponse("conflict", $"A branch holds at most {MaxSavedPerBranch} saved estimates. Delete one first."));

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
                return CostAccess.NotFound("Saved estimate");
            var now = (await reader.BuildAsync(projectId, branch, Today(time), ct)).Inputs;
            return Results.Ok(ToDto(row, now, withResult: true));
        }, ct);

    public static Task<IResult> Delete(
        Guid projectId, Guid estimateId, Guid? branchId, HttpContext http, AppDbContext db, CancellationToken ct) =>
        ModelBranches.RunAsync(http, projectId, branchId, WorkspaceRole.Editor, async (branch, _) =>
            await db.Estimates.Where(e => e.Id == estimateId && e.ProjectId == projectId && e.BranchId == branch).ExecuteDeleteAsync(ct) == 1
                ? Results.NoContent()
                : CostAccess.NotFound("Saved estimate"), ct);

    private static SavedEstimateDto ToDto(SavedEstimateEntity row, EstimateInputsIndex now, bool withResult)
    {
        var changes = EstimateReader.Changes(CostJson.Read<EstimateInputsIndex>(row.Inputs), now);
        return new SavedEstimateDto(
            row.Id, row.Name, row.BranchId, row.ModelVersion, row.AsOf, row.InputsFingerprint, row.Hash, row.SavedBy, row.SavedAt,
            changes.Count > 0, changes, withResult ? CostJson.Read<Estimate>(row.Result) : null);
    }
}

/// <param name="WorkspaceId">The workspace that owns the price book.</param>
/// <param name="Pinned">The IDs of the entries this project pinned.</param>
public sealed record ProjectPricesDto(Guid WorkspaceId, IReadOnlyList<PriceEntryDto> Entries, IReadOnlyList<Guid> Pinned);

/// <summary>The price entries a project pinned. A pin keeps an entry in the price book of the workspace.</summary>
public static class Pins
{
    public const int MaxPerProject = 500;

    /// <summary>
    /// The price book of the project's workspace, as a project member picks from it, with
    /// the entries the project pinned. A book holds at most <see cref="PriceRules.MaxPerWorkspace"/> entries.
    /// </summary>
    public static async Task<IResult> Prices(
        Guid projectId, ClaimsPrincipal user, WorkspaceAuthorizationService auth, AppDbContext db, CancellationToken ct)
    {
        await auth.RequireProjectRoleAsync(projectId, PriceBook.UserId(user), WorkspaceRole.Viewer, ct);
        var workspaceId = await auth.GetProjectWorkspaceIdAsync(projectId, ct);

        var entries = await db.PriceEntries.AsNoTracking()
            .Where(p => p.WorkspaceId == workspaceId)
            .OrderBy(p => p.Provider).ThenBy(p => p.Sku).ThenBy(p => p.Region).ThenBy(p => p.Id)
            .ToListAsync(ct);
        var pinned = await db.Pins.AsNoTracking()
            .Where(p => p.ProjectId == projectId).OrderBy(p => p.PriceEntryId).Select(p => p.PriceEntryId).ToListAsync(ct);
        return Results.Ok(new ProjectPricesDto(workspaceId, entries.Select(PriceEntryDto.From).ToList(), pinned));
    }

    public static async Task<IResult> Pin(
        Guid projectId, Guid priceId, ClaimsPrincipal user, WorkspaceAuthorizationService auth, AppDbContext db, TimeProvider time, CancellationToken ct)
    {
        var userId = PriceBook.UserId(user);
        await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Editor, ct);
        var workspaceId = await auth.GetProjectWorkspaceIdAsync(projectId, ct);

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        // The row lock makes this pin and a delete of the entry take turns.
        var entry = await db.PriceEntries
            .FromSql($"SELECT * FROM costs.price_entries WHERE id = {priceId} AND workspace_id = {workspaceId} FOR SHARE")
            .AsNoTracking().ToListAsync(ct);
        if (entry.Count == 0)
            return CostAccess.NotFound("Price entry");
        if (await db.Pins.CountAsync(p => p.ProjectId == projectId, ct) >= MaxPerProject)
            return Results.Conflict(new ErrorResponse("conflict", $"A project pins at most {MaxPerProject} price entries."));

        await db.Database.ExecuteSqlAsync(
            $"INSERT INTO costs.pins (project_id, price_entry_id, pinned_by, pinned_at) VALUES ({projectId}, {priceId}, {userId}, {time.GetUtcNow()}) ON CONFLICT DO NOTHING",
            ct);
        await transaction.CommitAsync(ct);
        return Results.NoContent();
    }

    public static async Task<IResult> Unpin(
        Guid projectId, Guid priceId, ClaimsPrincipal user, WorkspaceAuthorizationService auth, AppDbContext db, CancellationToken ct)
    {
        await auth.RequireProjectRoleAsync(projectId, PriceBook.UserId(user), WorkspaceRole.Editor, ct);
        await db.Pins.Where(p => p.ProjectId == projectId && p.PriceEntryId == priceId).ExecuteDeleteAsync(ct);
        return Results.NoContent();
    }
}
