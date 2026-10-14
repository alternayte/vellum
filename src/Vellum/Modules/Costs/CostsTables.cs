using Deedbox;
using Microsoft.EntityFrameworkCore;
using Vellum.Shared;

namespace Vellum.Modules.Costs;

/// <summary>
/// The index of the cost sheets: which stream holds the sheet of which branch of which
/// project. The sheet itself is read from its stream. Project deletion finds the streams here.
/// </summary>
public class SheetEntity
{
    /// <summary>The ID of the sheet stream.</summary>
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    /// <summary>The ID of the model stream of the branch: the main stream, a draft or a variant.</summary>
    public Guid BranchId { get; set; }
    public DateTimeOffset OpenedAt { get; set; }
}

/// <summary>
/// One price of the price book of a workspace. A row is added and never changed: a new price
/// is a new entry, so an estimate that names an entry always reads the same number.
/// </summary>
public class PriceEntryEntity
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public string Provider { get; set; } = null!;
    public string Region { get; set; } = null!;
    /// <summary>Free text for a custom price; the provider's SKU name for a fetched one.</summary>
    public string Sku { get; set; } = null!;
    public string? Description { get; set; }
    public string Unit { get; set; } = null!;
    public decimal UnitPrice { get; set; }
    public string Currency { get; set; } = null!;
    public DateOnly EffectiveDate { get; set; }
    /// <summary><c>custom</c> or <c>fetched</c>.</summary>
    public string Source { get; set; } = null!;
    /// <summary>The person who entered a custom price, or who picked the fetched row.</summary>
    public string AcceptedBy { get; set; } = null!;
    public DateTimeOffset AcceptedAt { get; set; }
    /// <summary>Fetched only: the address the row came from. It holds no credential; the price API takes none.</summary>
    public string? SourceUrl { get; set; }
    public DateTimeOffset? FetchedAt { get; set; }
    /// <summary>Fetched only: the provider's own ID of the meter.</summary>
    public string? MeterId { get; set; }
    /// <summary>Fetched only: the provider's effective date, when the provider gives one.</summary>
    public DateOnly? ProviderEffectiveDate { get; set; }
}

/// <summary>A price entry that a project pinned. A pinned entry cannot be deleted from the price book.</summary>
public class PinEntity
{
    public Guid ProjectId { get; set; }
    public Guid PriceEntryId { get; set; }
    public string PinnedBy { get; set; } = null!;
    public DateTimeOffset PinnedAt { get; set; }
}

/// <summary>An estimate that a person saved. Rows are added and never changed.</summary>
public class SavedEstimateEntity
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public Guid BranchId { get; set; }
    public string Name { get; set; } = null!;
    /// <summary>The version of the model stream of the branch when the estimate was saved.</summary>
    public long ModelVersion { get; set; }
    public DateOnly AsOf { get; set; }
    public string InputsFingerprint { get; set; } = null!;
    /// <summary>SHA-256 of the canonical JSON of <see cref="Result"/>.</summary>
    public string Hash { get; set; } = null!;
    /// <summary>A JSON <see cref="EstimateInputsIndex"/>: the hash of each input, to say later what changed.</summary>
    public string Inputs { get; set; } = null!;
    /// <summary>A JSON <see cref="Estimate"/>.</summary>
    public string Result { get; set; } = null!;
    public string SavedBy { get; set; } = null!;
    public DateTimeOffset SavedAt { get; set; }
}

/// <summary>The tables of the module, in the Postgres schema <c>costs</c>.</summary>
public static class CostsTables
{
    public const string Schema = "costs";

    public static void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SheetEntity>(b =>
        {
            b.ToTable("sheets", Schema);
            b.HasKey(s => s.Id);
            b.HasIndex(s => new { s.ProjectId, s.BranchId }).IsUnique();
        });

        modelBuilder.Entity<PriceEntryEntity>(b =>
        {
            b.ToTable("price_entries", Schema);
            b.HasKey(p => p.Id);
            // No fixed scale: the number is stored with the decimal places it was entered or fetched with.
            b.Property(p => p.UnitPrice).HasColumnType("numeric");
            b.HasIndex(p => new { p.WorkspaceId, p.Provider, p.Sku, p.Id });
        });

        modelBuilder.Entity<PinEntity>(b =>
        {
            b.ToTable("pins", Schema);
            b.HasKey(p => new { p.ProjectId, p.PriceEntryId });
            b.HasIndex(p => p.PriceEntryId);
        });

        modelBuilder.Entity<SavedEstimateEntity>(b =>
        {
            b.ToTable("estimates", Schema);
            b.HasKey(e => e.Id);
            b.Property(e => e.Inputs).HasColumnType("jsonb");
            b.Property(e => e.Result).HasColumnType("jsonb");
            b.HasIndex(e => new { e.ProjectId, e.BranchId, e.SavedAt });
        });
    }
}

/// <summary>Keeps the sheet index. It runs inline, so the row commits with the first event of the sheet.</summary>
public sealed class CostSheetProjection : Projection<AppDbContext>
{
    public CostSheetProjection()
    {
        On<CostEvent.SheetOpened>(async (opened, ctx) =>
        {
            var id = Guid.Parse(ctx.StreamId);
            if (await ctx.Db.Sheets.FindAsync([id], ctx.CancellationToken) is null)
                ctx.Db.Sheets.Add(new SheetEntity { Id = id, ProjectId = opened.ProjectId, BranchId = opened.BranchId, OpenedAt = opened.At });
        });
    }

    protected override async Task ResetAsync(WriteContext<AppDbContext> context) =>
        await context.Db.Sheets.ExecuteDeleteAsync(context.CancellationToken);
}
