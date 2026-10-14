using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Vellum.Modules.Settings;
using Vellum.Modules.Workspaces;
using Vellum.Shared;
using static Vellum.Shared.ResultExtensions;

namespace Vellum.Modules.Costs.Prices;

/// <param name="Provider">A name such as <c>azure</c>, <c>aws</c>, <c>gcp</c> or <c>hetzner</c>. See <see cref="CostProvider"/>.</param>
/// <param name="UnitPrice">Necessary. It is nullable so that a body without it is refused and not stored as a price of 0.</param>
public sealed record CustomPriceRequest(
    string Provider, string? Region, string Sku, string? Description, string Unit, decimal? UnitPrice, string Currency, DateOnly EffectiveDate);

/// <summary>
/// The audit actions of the price book. The target of an entry names the price entry and
/// holds its number: a price is no secret, and the entry itself is gone after a delete.
/// </summary>
public static class PriceAudit
{
    public const string Added = "price.added";
    public const string Imported = "price.imported";
    public const string Deleted = "price.deleted";

    public static string Target(PriceEntryEntity entry) =>
        $"price:{entry.Id} {entry.Provider} sku:{entry.Sku}{(entry.Region.Length > 0 ? $" region:{entry.Region}" : "")} "
        + $"{UnitExpression.Text(entry.UnitPrice)} {entry.Currency} per {entry.Unit}"
        + (entry.MeterId is null ? "" : $" meter:{entry.MeterId}");
}

/// <summary>Where a fetched price came from: the address, the time, the provider's SKU or meter ID and the provider's effective date.</summary>
public sealed record FetchedFrom(string Url, DateTimeOffset FetchedAt, string MeterId, DateOnly? ProviderEffectiveDate);

/// <summary>A project that uses a price entry: a resource of one of its cost sheets, or one of its saved estimates, names the entry.</summary>
public sealed record PriceUserDto(Guid ProjectId, string Name);

/// <param name="AcceptedBy">The person who entered a custom price, or who picked the fetched row.</param>
/// <param name="UnitReadable">False when Vellum does not read <paramref name="Unit"/>. A line with this price is then unknown.</param>
/// <param name="UsedBy">The projects that use the entry. An entry that a project uses cannot be deleted.</param>
public sealed record PriceEntryDto(
    Guid Id, string Provider, string Region, string Sku, string? Description, string Unit, decimal UnitPrice, string Currency,
    DateOnly EffectiveDate, string Source, string AcceptedBy, DateTimeOffset AcceptedAt, FetchedFrom? Fetched, bool UnitReadable,
    IReadOnlyList<PriceUserDto> UsedBy)
{
    public static PriceEntryDto From(PriceEntryEntity entry, IReadOnlyList<PriceUserDto>? usedBy = null) => new(
        entry.Id, entry.Provider, entry.Region, entry.Sku, entry.Description, entry.Unit, entry.UnitPrice, entry.Currency,
        entry.EffectiveDate, EnumText.Of(entry.Source), entry.AcceptedBy, entry.AcceptedAt,
        entry.Source == PriceSourceKind.Fetched
            ? new FetchedFrom(entry.SourceUrl!, entry.FetchedAt!.Value, entry.MeterId!, entry.ProviderEffectiveDate)
            : null,
        UnitExpression.Parse(entry.Unit) is not null,
        usedBy ?? []);
}

/// <param name="WorkspaceId">The workspace that owns the price book.</param>
public sealed record ProjectPricesDto(Guid WorkspaceId, IReadOnlyList<PriceEntryDto> Entries);

public static class PriceRules
{
    public const int MaxPerWorkspace = 2_000;
    public const decimal MaxUnitPrice = 1_000_000m;
    public const int MaxUnitPriceDecimals = 10;

    public static ValidationError? UnitPrice(string field, decimal value) =>
        value is < 0 or > MaxUnitPrice || value.Scale > MaxUnitPriceDecimals
            ? new ValidationError(field, $"Must be from 0 to {MaxUnitPrice:0} with at most {MaxUnitPriceDecimals} decimal places.")
            : null;

    public static List<ValidationError> Check(CustomPriceRequest request)
    {
        var errors = new List<ValidationError>();
        void Add(ValidationError? error)
        {
            if (error is not null)
                errors.Add(error);
        }

        Add(CostProvider.Check("provider", request.Provider?.Trim()));
        Add(TextRule.Line("region", request.Region, CostRules.MaxRegionChars));
        if (string.IsNullOrWhiteSpace(request.Sku))
            errors.Add(new("sku", "A SKU or a name for the priced thing is required"));
        Add(TextRule.Line("sku", request.Sku));
        Add(TextRule.Line("description", request.Description));
        Add(CostRules.Unit("unit", request.Unit));
        if (request.UnitPrice is { } unitPrice)
            Add(UnitPrice("unitPrice", unitPrice));
        else
            errors.Add(new("unitPrice", "A unit price is required"));
        if (!AzureRetailPrices.IsCurrency(request.Currency))
            errors.Add(new("currency", "Must be a three-letter currency code in upper case, for example USD."));
        if (request.EffectiveDate == default)
            errors.Add(new("effectiveDate", "An effective date is required"));
        return errors;
    }

    /// <summary>A custom entry from a request that <see cref="Check"/> passed.</summary>
    public static PriceEntryEntity Custom(Guid workspaceId, CustomPriceRequest request, string userId, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(),
        WorkspaceId = workspaceId,
        Provider = request.Provider.Trim(),
        Region = request.Region?.Trim() ?? "",
        Sku = request.Sku.Trim(),
        Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim(),
        Unit = request.Unit.Trim(),
        UnitPrice = request.UnitPrice!.Value,
        Currency = request.Currency,
        EffectiveDate = request.EffectiveDate,
        Source = PriceSourceKind.Custom,
        AcceptedBy = userId,
        AcceptedAt = now,
    };
}

/// <summary>
/// Which projects use a price entry. A project uses an entry while a resource of one of its
/// cost sheets (main, a draft or a variant) names it, or one of its saved estimates was made
/// with it. Nobody pins an entry by hand: the use itself protects it.
/// </summary>
internal static class PriceUsage
{
    private sealed class Use
    {
        public Guid PriceId { get; set; }
        public Guid ProjectId { get; set; }
        public string ProjectName { get; set; } = "";
    }

    public static IEnumerable<Guid> Refs(CostResource resource) =>
        resource.Usage.Select(u => u.PriceRef).Append(resource.PriceRef).OfType<Guid>();

    /// <summary>
    /// Locks the entries until the end of the transaction, so a delete of one of them waits.
    /// Returns the IDs that still exist.
    /// </summary>
    public static async Task<List<Guid>> LockAsync(AppDbContext db, IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        if (ids.Count == 0)
            return [];
        var array = ids.ToArray();
        return await db.Database
            .SqlQuery<Guid>($"""SELECT id AS "Value" FROM costs.price_entries WHERE id = ANY({array}) FOR SHARE""")
            .ToListAsync(ct);
    }

    /// <summary>The projects of the workspace that use each entry, by name.</summary>
    public static async Task<Dictionary<Guid, List<PriceUserDto>>> ProjectsAsync(
        AppDbContext db, Guid workspaceId, IReadOnlyCollection<Guid> priceIds, CancellationToken ct)
    {
        if (priceIds.Count == 0)
            return [];
        var ids = priceIds.ToArray();
        // `$.**.priceRef` finds the price of a resource, of each usage line and of the copy
        // that a draft keeps as the base of its merge. The keys of `prices` in the inputs of
        // a saved estimate are the entries that the estimate was made with.
        var rows = await db.Database.SqlQuery<Use>($$"""
            SELECT DISTINCT u.price_id, p.id AS project_id, p.name AS project_name
            FROM (
                SELECT s.project_id, (r.ref #>> '{}')::uuid AS price_id
                FROM costs.sheets s
                CROSS JOIN LATERAL jsonb_path_query(s.document, '$.**.priceRef') AS r(ref)
                WHERE jsonb_typeof(r.ref) = 'string'
                UNION
                SELECT e.project_id, k.key::uuid
                FROM costs.estimates e
                CROSS JOIN LATERAL jsonb_object_keys(
                    CASE WHEN jsonb_typeof(e.inputs -> 'prices') = 'object' THEN e.inputs -> 'prices' END) AS k(key)
            ) u
            JOIN workspaces.projects p ON p.id = u.project_id
            WHERE p.workspace_id = {{workspaceId}} AND u.price_id = ANY({{ids}})
            """).ToListAsync(ct);

        return rows
            .GroupBy(r => r.PriceId)
            .ToDictionary(
                g => g.Key,
                g => g.OrderBy(r => r.ProjectName, StringComparer.Ordinal).ThenBy(r => r.ProjectId)
                    .Select(r => new PriceUserDto(r.ProjectId, r.ProjectName)).ToList());
    }

    public static async Task<List<PriceEntryDto>> ToDtosAsync(AppDbContext db, Guid workspaceId, IReadOnlyList<PriceEntryEntity> entries, CancellationToken ct)
    {
        var users = await ProjectsAsync(db, workspaceId, entries.Select(e => e.Id).ToList(), ct);
        return entries.Select(e => PriceEntryDto.From(e, users.GetValueOrDefault(e.Id))).ToList();
    }
}

/// <summary>
/// The price book of a workspace. Vellum ships no price: an entry exists because a person
/// entered it, or because a person picked a row that Vellum fetched from a provider. A member
/// reads the book; only an owner changes it, as with the other workspace settings.
/// </summary>
public static class PriceBook
{
    public static async Task<IResult> List(
        Guid workspaceId, string? cursor, int? limit,
        ClaimsPrincipal user, WorkspaceAuthorizationService auth, AppDbContext db, CancellationToken ct)
    {
        await auth.RequireRoleAsync(workspaceId, user.UserId(), WorkspaceRole.Viewer, ct);

        var pageSize = Math.Clamp(limit ?? 50, 1, 200);
        var query = db.PriceEntries.AsNoTracking().Where(p => p.WorkspaceId == workspaceId);
        if (CursorEncoder.Decode(cursor) is { } after)
            query = query.Where(p => p.Id > after.Id);

        var rows = await query.OrderBy(p => p.Id).Take(pageSize + 1).ToListAsync(ct);
        string? next = null;
        if (rows.Count > pageSize)
        {
            rows = rows[..pageSize];
            next = CursorEncoder.Encode("", rows[^1].Id);
        }

        return Results.Ok(new Page<PriceEntryDto>(await PriceUsage.ToDtosAsync(db, workspaceId, rows, ct), next));
    }

    /// <summary>
    /// The price book of the project's workspace, as a project member picks from it. A book
    /// holds at most <see cref="PriceRules.MaxPerWorkspace"/> entries.
    /// </summary>
    public static async Task<IResult> ForProject(
        Guid projectId, ClaimsPrincipal user, WorkspaceAuthorizationService auth, AppDbContext db, CancellationToken ct)
    {
        await auth.RequireProjectRoleAsync(projectId, user.UserId(), WorkspaceRole.Viewer, ct);
        var workspaceId = await auth.GetProjectWorkspaceIdAsync(projectId, ct);

        var entries = await db.PriceEntries.AsNoTracking()
            .Where(p => p.WorkspaceId == workspaceId)
            .OrderBy(p => p.Provider).ThenBy(p => p.Sku).ThenBy(p => p.Region).ThenBy(p => p.Id)
            .ToListAsync(ct);
        return Results.Ok(new ProjectPricesDto(workspaceId, await PriceUsage.ToDtosAsync(db, workspaceId, entries, ct)));
    }

    public static async Task<IResult> AddCustom(
        Guid workspaceId, CustomPriceRequest request,
        ClaimsPrincipal user, WorkspaceAuthorizationService auth, AppDbContext db, TimeProvider time, CancellationToken ct)
    {
        var userId = user.UserId();
        await auth.RequireRoleAsync(workspaceId, userId, WorkspaceRole.Owner, ct);
        if (PriceRules.Check(request) is { Count: > 0 } errors)
            return Invalid(errors);

        return await AddAsync(db, PriceRules.Custom(workspaceId, request, userId, time.GetUtcNow()), PriceAudit.Added, ct);
    }

    /// <summary>An entry that a project uses stays. The refusal names the projects.</summary>
    public static async Task<IResult> Delete(
        Guid workspaceId, Guid priceId,
        ClaimsPrincipal user, WorkspaceAuthorizationService auth, AppDbContext db, CancellationToken ct)
    {
        var userId = user.UserId();
        await auth.RequireRoleAsync(workspaceId, userId, WorkspaceRole.Owner, ct);

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        // A sheet write or an estimate save that names the entry holds a share lock on it. This
        // lock waits for those to commit, so the check below sees their use.
        var locked = await db.PriceEntries
            .FromSql($"SELECT * FROM costs.price_entries WHERE id = {priceId} AND workspace_id = {workspaceId} FOR UPDATE")
            .AsNoTracking().ToListAsync(ct);
        if (locked.Count == 0)
            return Results.NotFound(new ErrorResponse("not_found", "Price entry not found"));

        var users = (await PriceUsage.ProjectsAsync(db, workspaceId, [priceId], ct)).GetValueOrDefault(priceId) ?? [];
        if (users.Count > 0)
        {
            return Results.Conflict(new ErrorResponse("price_in_use",
                $"Used by {users.Count} {(users.Count == 1 ? "project" : "projects")}: {string.Join(", ", users.Select(u => u.Name))}",
                "Remove the price from the resources of these projects, and delete their saved estimates that use it. Then delete the price."));
        }

        await db.PriceEntries.Where(p => p.Id == priceId).ExecuteDeleteAsync(ct);
        await AuditLog.AppendAsync(db, workspaceId, userId, PriceAudit.Deleted, PriceAudit.Target(locked[0]), ct);
        await transaction.CommitAsync(ct);
        return Results.NoContent();
    }

    /// <summary>
    /// Adds the entry unless the book is full, and records who added it. The audit entry
    /// commits with the price entry.
    /// </summary>
    /// <param name="action"><see cref="PriceAudit.Added"/> or <see cref="PriceAudit.Imported"/>.</param>
    internal static async Task<IResult> AddAsync(AppDbContext db, PriceEntryEntity entry, string action, CancellationToken ct)
    {
        var refusal = await AddManyAsync(db, entry.WorkspaceId, [entry], action, ct);
        return refusal ?? Results.Created($"/api/workspaces/{entry.WorkspaceId}/prices/{entry.Id}", PriceEntryDto.From(entry));
    }

    /// <summary>
    /// Adds all entries or none: none when the book has no room for all of them. Each entry
    /// gets an audit entry in the same transaction. Null when the entries were added.
    /// </summary>
    internal static async Task<IResult?> AddManyAsync(
        AppDbContext db, Guid workspaceId, IReadOnlyList<PriceEntryEntity> entries, string action, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        // One writer of a workspace's book at a time, so the count is the count at the insert.
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtextextended({"costs.price-book:" + workspaceId}, 0))", ct);
        var count = await db.PriceEntries.CountAsync(p => p.WorkspaceId == workspaceId, ct);
        if (count + entries.Count > PriceRules.MaxPerWorkspace)
        {
            return Results.Conflict(new ErrorResponse("conflict",
                $"A price book holds at most {PriceRules.MaxPerWorkspace} entries. It has {count}, and this adds {entries.Count}. Nothing was added.",
                "Delete entries that no project uses."));
        }

        db.PriceEntries.AddRange(entries);
        await db.SaveChangesAsync(ct);
        foreach (var entry in entries)
            await AuditLog.AppendAsync(db, workspaceId, entry.AcceptedBy, action, PriceAudit.Target(entry), ct);
        await transaction.CommitAsync(ct);
        return null;
    }
}
