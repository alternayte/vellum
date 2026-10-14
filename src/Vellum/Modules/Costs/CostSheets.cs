using System.Collections.Immutable;
using Deedbox;
using Microsoft.EntityFrameworkCore;
using Vellum.Modules.Costs.Prices;
using Vellum.Modules.Modelling;
using Vellum.Modules.Modelling.Model;
using Vellum.Modules.Workspaces;
using Vellum.Modules.Workspaces.Authorization;
using Vellum.Shared;
using Decision = Vellum.Shared.CommandResult<System.Collections.Generic.IReadOnlyList<object>>;

namespace Vellum.Modules.Costs;

public sealed record UsageLineRequest(Guid? Id, string Name, Guid AssumptionId, Guid? PriceRef);

/// <param name="Id">Optional on create: the server makes one when it is left out. Ignored on change.</param>
/// <param name="Quantity">The monthly baseline, with <paramref name="Unit"/>. Leave both out for a resource with usage lines only.</param>
/// <param name="FactStatus">Default <c>declared</c>.</param>
public sealed record ResourceRequest(
    Guid? Id, Guid ElementId, string Name, string Category, string Provider, string? Region, string? Sku,
    decimal? Quantity, string? Unit, Guid? PriceRef, IReadOnlyList<UsageLineRequest>? Usage,
    string? FactStatus, ProvenanceInput? Provenance);

/// <param name="Value">Necessary. It is nullable so that a body without it is refused and not read as 0.</param>
public sealed record AssumptionRequest(
    Guid? Id, string Name, string Kind, Guid? SubjectId, decimal? Value, string Unit,
    string Label, string? Source, DateOnly EffectiveDate);

/// <param name="From">The branch to copy from: <c>main</c>, or the stream ID of a draft or a variant.</param>
public sealed record CopySheetRequest(string From);

/// <param name="ElementName">Null when the element is no longer in the model of the branch.</param>
public sealed record ResourceDto(
    Guid Id, Guid ElementId, string? ElementName, string Name, string Category, string Provider, string Region, string Sku,
    decimal? Quantity, string? Unit, Guid? PriceRef, IReadOnlyList<UsageLine> Usage,
    string FactStatus, ProvenanceDto? Provenance);

/// <param name="BranchId">The stream ID of the model branch the sheet belongs to.</param>
/// <param name="Version">The number of changes that made the sheet. 0 for a branch with no sheet yet.</param>
public sealed record CostSheetDto(
    Guid BranchId, long Version, IReadOnlyList<ResourceDto> Resources, IReadOnlyList<CostAssumption> Assumptions);

/// <summary>What the cost handlers share: where a sheet is stored and what it may point at.</summary>
internal static class CostAccess
{
    /// <summary>The sheet of a branch. A branch with no sheet, or one that was closed since the caller resolved it, has an empty one.</summary>
    public static async Task<(CostSheetState State, long Version)> LoadSheetAsync(AppDbContext db, Guid branchId, CancellationToken ct)
    {
        var row = await db.Sheets.AsNoTracking()
            .Where(s => s.BranchId == branchId)
            .Select(s => new { s.Version, s.Document })
            .FirstOrDefaultAsync(ct);
        return row is null ? (CostSheetState.Initial, 0) : (CostJson.Read<CostSheetState>(row.Document), row.Version);
    }

    /// <summary>
    /// Writes <paramref name="state"/>, which was at <paramref name="version"/>, after
    /// <paramref name="changes"/>. The caller holds the lock of the sheet and the transaction.
    /// </summary>
    public static async Task SaveSheetAsync(
        AppDbContext db, Guid projectId, Guid branchId, CostSheetState state, long version, IReadOnlyList<object> changes,
        DateTimeOffset now, CancellationToken ct)
    {
        var after = changes.Aggregate(state, CostSheetState.Evolve);
        var document = CostJson.Write(after);
        var next = version + changes.Count;
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO costs.sheets (branch_id, project_id, version, document, opened_at)
            VALUES ({branchId}, {projectId}, {next}, {document}::jsonb, {now})
            ON CONFLICT (branch_id) DO UPDATE SET version = excluded.version, document = excluded.document
            """, ct);
    }

    /// <summary>The answer to a write that reached the sheet of a branch after the branch was closed.</summary>
    public static IResult BranchClosed() =>
        Results.BadRequest(new ErrorResponse("invalid_branch", "Branch not found or not open for this project"));

    public static async Task<(ModelState Model, long Version)> LoadModelAsync(IEventStore store, Guid branchId, CancellationToken ct)
    {
        var (model, version) = await store.Load<ModelState>(StreamId.From(branchId), ct);
        return (model, version);
    }

    /// <param name="priceRefs">The price entries the change names. Only these are looked up.</param>
    public static async Task<CostWorld> WorldAsync(
        IServiceProvider services, Guid projectId, ModelState model, IEnumerable<Guid> priceRefs, CancellationToken ct)
    {
        var refs = priceRefs.Distinct().ToList();
        var prices = new HashSet<Guid>();
        if (refs.Count > 0)
        {
            // A price entry of another workspace does not exist for this project.
            var workspaceId = await services.GetRequiredService<WorkspaceAuthorizationService>().GetProjectWorkspaceIdAsync(projectId, ct);
            prices = (await services.GetRequiredService<AppDbContext>().PriceEntries.AsNoTracking()
                .Where(p => p.WorkspaceId == workspaceId && refs.Contains(p.Id))
                .Select(p => p.Id)
                .ToListAsync(ct)).ToHashSet();
        }

        return World(model, prices);
    }

    public static CostWorld World(ModelState model, IReadOnlySet<Guid> prices)
    {
        var elements = ElementIds(model);
        var subjects = elements
            .Concat(model.Operations.Keys.Select(id => id.Value))
            .Concat(model.Channels.Keys.Select(id => id.Value))
            .ToHashSet();
        return new CostWorld(elements, subjects, prices);
    }

    public static HashSet<Guid> ElementIds(ModelState model) => model.Elements.Keys.Select(id => id.Value).ToHashSet();

    public static CostSheetDto ToDto(Guid branchId, CostSheetState state, long version, ModelState model) => new(
        branchId, version,
        state.Resources
            .OrderBy(r => r.ElementId).ThenBy(r => r.Name, StringComparer.Ordinal).ThenBy(r => r.Id)
            .Select(r => new ResourceDto(
                r.Id, r.ElementId, model.Elements.TryGetValue(new ElementId(r.ElementId), out var element) ? element.Name : null,
                r.Name, r.Category, r.Provider, r.Region, r.Sku, r.Quantity, r.Unit, r.PriceRef, r.Usage,
                r.FactStatus, ProvenanceMapper.ToDto(r.Provenance)))
            .ToList(),
        state.Assumptions.OrderBy(a => a.Name, StringComparer.Ordinal).ThenBy(a => a.Id).ToList());

}

public static class CostSheets
{
    public static Task<IResult> Get(Guid projectId, Guid? branchId, HttpContext http, IEventStore store, AppDbContext db, CancellationToken ct) =>
        ModelBranches.RunAsync(http, projectId, branchId, WorkspaceRole.Viewer, async (branch, _) =>
        {
            var (state, version) = await CostAccess.LoadSheetAsync(db, branch, ct);
            var (model, _) = await CostAccess.LoadModelAsync(store, branch, ct);
            return Results.Ok(CostAccess.ToDto(branch, state, version, model));
        }, ct);

    public static Task<IResult> AddResource(
        Guid projectId, Guid? branchId, ResourceRequest request, HttpContext http, IEventStore store, TimeProvider time, CancellationToken ct) =>
        WriteAsync(http, store, time, projectId, branchId, PriceRefs(request), ct, (state, branch, world, userId) =>
            CostDecider.AddResource(state, projectId, branch, ToResource(request, request.Id ?? Guid.NewGuid(), userId, time), world, userId, time.GetUtcNow()),
            created: true);

    public static Task<IResult> ChangeResource(
        Guid projectId, Guid resourceId, Guid? branchId, ResourceRequest request, HttpContext http, IEventStore store, TimeProvider time, CancellationToken ct) =>
        WriteAsync(http, store, time, projectId, branchId, PriceRefs(request), ct, (state, _, world, userId) =>
            CostDecider.ChangeResource(state, ToResource(request, resourceId, userId, time), world, userId, time.GetUtcNow()));

    public static Task<IResult> RemoveResource(
        Guid projectId, Guid resourceId, Guid? branchId, HttpContext http, IEventStore store, TimeProvider time, CancellationToken ct) =>
        WriteAsync(http, store, time, projectId, branchId, [], ct, (state, _, _, userId) =>
            CostDecider.RemoveResource(state, resourceId, userId, time.GetUtcNow()));

    public static Task<IResult> AddAssumption(
        Guid projectId, Guid? branchId, AssumptionRequest request, HttpContext http, IEventStore store, TimeProvider time, CancellationToken ct) =>
        WriteAsync(http, store, time, projectId, branchId, [], ct, (state, branch, world, userId) => request.Value is not { } value
            ? NoValue
            : CostDecider.AddAssumption(state, projectId, branch, ToAssumption(request, request.Id ?? Guid.NewGuid(), value), world, userId, time.GetUtcNow()),
            created: true);

    public static Task<IResult> ChangeAssumption(
        Guid projectId, Guid assumptionId, Guid? branchId, AssumptionRequest request, HttpContext http, IEventStore store, TimeProvider time, CancellationToken ct) =>
        WriteAsync(http, store, time, projectId, branchId, [], ct, (state, _, world, userId) => request.Value is not { } value
            ? NoValue
            : CostDecider.ChangeAssumption(state, ToAssumption(request, assumptionId, value), world, userId, time.GetUtcNow()));

    public static Task<IResult> RemoveAssumption(
        Guid projectId, Guid assumptionId, Guid? branchId, HttpContext http, IEventStore store, TimeProvider time, CancellationToken ct) =>
        WriteAsync(http, store, time, projectId, branchId, [], ct, (state, _, _, userId) =>
            CostDecider.RemoveAssumption(state, assumptionId, userId, time.GetUtcNow()));

    /// <summary>
    /// Replaces the sheet of the branch with a copy of the sheet of another branch of the
    /// project. A new draft starts with no sheet; this is how it gets one. A new variant
    /// starts with a copy already (see <see cref="CostBranches.ForkAsync"/>).
    /// </summary>
    public static Task<IResult> Copy(
        Guid projectId, Guid? branchId, CopySheetRequest request, HttpContext http, IEventStore store, TimeProvider time, CancellationToken ct) =>
        ModelBranches.RunAsync(http, projectId, branchId, WorkspaceRole.Editor, async (branch, userId) =>
        {
            var (from, refusal) = await ResolveSideAsync(http.RequestServices, projectId, request.From, "from", ct);
            if (refusal is not null)
                return refusal;
            if (from == branch)
                return Results.BadRequest(new ErrorResponse("validation_error", "Validation failed", Errors: [new FieldError("from", "Must be another branch")]));

            var db = http.RequestServices.GetRequiredService<AppDbContext>();
            var (source, sourceVersion) = await CostAccess.LoadSheetAsync(db, from, ct);
            var (model, _) = await CostAccess.LoadModelAsync(store, branch, ct);
            var world = await CostAccess.WorldAsync(http.RequestServices, projectId, model, [], ct);
            return await DecideAsync(db, projectId, branch, model, time, created: false, ct, state =>
                CostDecider.Copy(state, projectId, branch, source, from, sourceVersion, world, userId, time.GetUtcNow()));
        }, ct);

    /// <summary>
    /// <c>main</c> or the stream ID of an open draft or variant of the project, as the model
    /// stream it names.
    /// </summary>
    internal static async Task<(Guid Branch, IResult? Refusal)> ResolveSideAsync(
        IServiceProvider services, Guid projectId, string? side, string field, CancellationToken ct)
    {
        Guid? branchId = null;
        if (!string.Equals(side, "main", StringComparison.Ordinal))
        {
            if (!Guid.TryParse(side, out var parsed))
            {
                return (default, Results.BadRequest(new ErrorResponse("validation_error", "Validation failed",
                    Errors: [new FieldError(field, "Must be 'main' or the ID of a branch")])));
            }
            branchId = parsed;
        }

        var mainStreamId = await services.GetRequiredService<AppDbContext>().Projects.AsNoTracking()
            .Where(p => p.Id == projectId).Select(p => p.StreamId).FirstAsync(ct);
        return await ModelBranches.ResolveAsync(
            branchId, projectId, mainStreamId,
            services.GetRequiredService<AppDbContext>(),
            forWrite: false, ct);
    }

    private static IEnumerable<Guid> PriceRefs(ResourceRequest request) =>
        (request.Usage ?? []).Select(u => u.PriceRef).Append(request.PriceRef).OfType<Guid>();

    private static CostResource ToResource(ResourceRequest request, Guid id, string userId, TimeProvider time) => new(
        id, request.ElementId, request.Name?.Trim()!, request.Category, request.Provider?.Trim()!, request.Region?.Trim() ?? "", request.Sku?.Trim() ?? "",
        request.Quantity, request.Unit?.Trim(), request.PriceRef,
        (request.Usage ?? []).Select(u => new UsageLine(u.Id ?? Guid.NewGuid(), u.Name?.Trim()!, u.AssumptionId, u.PriceRef)).ToImmutableList(),
        request.FactStatus ?? FactStatus.Declared.ToApiString(),
        // The acceptance stamp is taken once, so the decision stays a pure function of the state.
        request.Provenance is { } provenance
            ? new Provenance(provenance.Source, provenance.Revision, provenance.Confidence, Guid.Parse(userId), time.GetUtcNow())
            : null);

    /// <summary>A body without the number. The number is not read as 0.</summary>
    private static readonly Decision NoValue = new Decision.Invalid([new ValidationError("value", "A value is required")]);

    private static CostAssumption ToAssumption(AssumptionRequest request, Guid id, decimal value) => new(
        id, request.Name?.Trim()!, request.Kind, request.SubjectId, value, request.Unit?.Trim()!,
        request.Label, string.IsNullOrWhiteSpace(request.Source) ? null : request.Source.Trim(), request.EffectiveDate);

    private static Task<IResult> WriteAsync(
        HttpContext http, IEventStore store, TimeProvider time, Guid projectId, Guid? branchId, IEnumerable<Guid> priceRefs, CancellationToken ct,
        Func<CostSheetState, Guid, CostWorld, string, Decision> decide, bool created = false) =>
        ModelBranches.RunAsync(http, projectId, branchId, WorkspaceRole.Editor, async (branch, userId) =>
        {
            var (model, _) = await CostAccess.LoadModelAsync(store, branch, ct);
            var world = await CostAccess.WorldAsync(http.RequestServices, projectId, model, priceRefs, ct);
            return await DecideAsync(
                http.RequestServices.GetRequiredService<AppDbContext>(), projectId, branch, model, time, created, ct,
                state => decide(state, branch, world, userId));
        }, ct);

    /// <summary>
    /// Decides on the sheet under its lock and stores the result. Every write answers with
    /// the whole sheet, so a client shows what is stored.
    /// </summary>
    private static async Task<IResult> DecideAsync(
        AppDbContext db, Guid projectId, Guid branch, ModelState model, TimeProvider time, bool created, CancellationToken ct,
        Func<CostSheetState, Decision> decide)
    {
        // A return before the commit rolls everything back.
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var (state, version) = (await CostBranches.LockSheetsAsync(db, [branch], ct))[branch];
        // The branch was merged or abandoned between the check of the caller and this lock.
        if (await CostBranches.IsClosedAsync(db, branch, ct))
            return CostAccess.BranchClosed();

        var decision = decide(state);
        if (decision is not Decision.Success { Value: var changes })
            return decision.ToHttpResult();
        // The entries a resource now names are locked until the commit, so a delete of one of
        // them waits and then sees that this sheet uses it. An entry that a delete removed
        // after the check of the caller is refused here.
        var named = changes
            .SelectMany(change => change switch
            {
                CostChange.ResourceAdded e => [e.Resource],
                CostChange.ResourceChanged e => [e.Resource],
                _ => Array.Empty<CostResource>(),
            })
            .SelectMany(PriceUsage.Refs).ToHashSet();
        if ((await PriceUsage.LockAsync(db, named, ct)).Count < named.Count)
            return ResultExtensions.Invalid("priceRef", "The price entry was deleted from the price book. Pick another entry.");
        if (changes.Count > 0)
            await CostAccess.SaveSheetAsync(db, projectId, branch, state, version, changes, time.GetUtcNow(), ct);
        await transaction.CommitAsync(ct);

        var sheet = CostAccess.ToDto(branch, changes.Aggregate(state, CostSheetState.Evolve), version + changes.Count, model);
        return created ? Results.Json(sheet, statusCode: StatusCodes.Status201Created) : Results.Ok(sheet);
    }
}
