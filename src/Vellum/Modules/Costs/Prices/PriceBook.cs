using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Vellum.Modules.Settings.Audit;
using Vellum.Modules.Workspaces.Authorization;
using Vellum.Shared;

namespace Vellum.Modules.Costs.Prices;

public static class PriceSourceKind
{
    /// <summary>A person entered the number.</summary>
    public const string Custom = "custom";
    /// <summary>Vellum read the number from a provider's price API, and a person picked the row.</summary>
    public const string Fetched = "fetched";
}

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

/// <summary>Where a fetched price came from: the address, the time, the provider's meter ID and the provider's effective date.</summary>
public sealed record FetchedFrom(string Url, DateTimeOffset FetchedAt, string MeterId, DateOnly? ProviderEffectiveDate);

/// <param name="AcceptedBy">The person who entered a custom price, or who picked the fetched row.</param>
/// <param name="UnitReadable">False when Vellum does not read <paramref name="Unit"/>. A line with this price is then unknown.</param>
public sealed record PriceEntryDto(
    Guid Id, string Provider, string Region, string Sku, string? Description, string Unit, decimal UnitPrice, string Currency,
    DateOnly EffectiveDate, string Source, string AcceptedBy, DateTimeOffset AcceptedAt, FetchedFrom? Fetched, bool UnitReadable)
{
    public static PriceEntryDto From(PriceEntryEntity entry) => new(
        entry.Id, entry.Provider, entry.Region, entry.Sku, entry.Description, entry.Unit, entry.UnitPrice, entry.Currency,
        entry.EffectiveDate, entry.Source, entry.AcceptedBy, entry.AcceptedAt,
        entry.Source == PriceSourceKind.Fetched
            ? new FetchedFrom(entry.SourceUrl!, entry.FetchedAt!.Value, entry.MeterId!, entry.ProviderEffectiveDate)
            : null,
        UnitExpression.Parse(entry.Unit) is not null);
}

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

        if (!CostProvider.All.Contains(request.Provider))
            errors.Add(new("provider", $"Must be one of: {string.Join(", ", CostProvider.All)}"));
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
        await auth.RequireRoleAsync(workspaceId, UserId(user), WorkspaceRole.Viewer, ct);

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

        return Results.Ok(new Page<PriceEntryDto>(rows.Select(PriceEntryDto.From).ToList(), next));
    }

    public static async Task<IResult> AddCustom(
        Guid workspaceId, CustomPriceRequest request,
        ClaimsPrincipal user, WorkspaceAuthorizationService auth, AppDbContext db, TimeProvider time, CancellationToken ct)
    {
        var userId = UserId(user);
        await auth.RequireRoleAsync(workspaceId, userId, WorkspaceRole.Owner, ct);
        if (PriceRules.Check(request) is { Count: > 0 } errors)
            return Invalid(errors);

        return await AddAsync(db, new PriceEntryEntity
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            Provider = request.Provider,
            Region = request.Region?.Trim() ?? "",
            Sku = request.Sku.Trim(),
            Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim(),
            Unit = request.Unit.Trim(),
            UnitPrice = request.UnitPrice!.Value,
            Currency = request.Currency,
            EffectiveDate = request.EffectiveDate,
            Source = PriceSourceKind.Custom,
            AcceptedBy = userId,
            AcceptedAt = time.GetUtcNow(),
        }, PriceAudit.Added, ct);
    }

    /// <summary>A pinned entry stays: a project said that it depends on it.</summary>
    public static async Task<IResult> Delete(
        Guid workspaceId, Guid priceId,
        ClaimsPrincipal user, WorkspaceAuthorizationService auth, AppDbContext db, CancellationToken ct)
    {
        var userId = UserId(user);
        await auth.RequireRoleAsync(workspaceId, userId, WorkspaceRole.Owner, ct);

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        // The row lock makes a pin and this delete take turns.
        var locked = await db.PriceEntries
            .FromSql($"SELECT * FROM costs.price_entries WHERE id = {priceId} AND workspace_id = {workspaceId} FOR UPDATE")
            .AsNoTracking().ToListAsync(ct);
        if (locked.Count == 0)
            return Results.NotFound(new ErrorResponse("not_found", "Price entry not found"));

        var pins = await db.Pins.CountAsync(p => p.PriceEntryId == priceId, ct);
        if (pins > 0)
        {
            return Results.Conflict(new ErrorResponse("conflict",
                $"{pins} {(pins == 1 ? "project pins" : "projects pin")} this price entry. Remove the pin in each project first."));
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
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        // One writer of a workspace's book at a time, so the count is the count at the insert.
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtextextended({"costs.price-book:" + entry.WorkspaceId}, 0))", ct);
        if (await db.PriceEntries.CountAsync(p => p.WorkspaceId == entry.WorkspaceId, ct) >= PriceRules.MaxPerWorkspace)
            return Results.Conflict(new ErrorResponse("conflict", $"A price book holds at most {PriceRules.MaxPerWorkspace} entries. Delete entries that no project uses."));

        db.PriceEntries.Add(entry);
        await db.SaveChangesAsync(ct);
        await AuditLog.AppendAsync(db, entry.WorkspaceId, entry.AcceptedBy, action, PriceAudit.Target(entry), ct);
        await transaction.CommitAsync(ct);
        return Results.Created($"/api/workspaces/{entry.WorkspaceId}/prices/{entry.Id}", PriceEntryDto.From(entry));
    }

    internal static string UserId(ClaimsPrincipal user) => user.FindFirstValue(ClaimTypes.NameIdentifier)!;

    internal static IResult Invalid(IReadOnlyList<ValidationError> errors) =>
        Results.BadRequest(new ErrorResponse("validation_error", "Validation failed",
            Errors: errors.Select(e => new FieldError(e.Field, e.Message)).ToList()));
}
