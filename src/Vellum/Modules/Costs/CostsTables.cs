using Microsoft.EntityFrameworkCore;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure;
using Vellum.Shared;

namespace Vellum.Modules.Costs;

/// <summary>Where the number of a price entry came from.</summary>
public enum PriceSourceKind
{
    /// <summary>A person entered the number, one at a time or in a CSV file.</summary>
    Custom,
    /// <summary>Vellum read the number from a provider's price API, and a person picked the row.</summary>
    Fetched,
}

/// <summary>
/// The cost sheet of one model branch of one project. A branch has at most one sheet; a
/// branch with no row has an empty sheet at version 0.
/// </summary>
public class SheetEntity
{
    /// <summary>The ID of the model stream of the branch: the main stream, a draft or a variant.</summary>
    public Guid BranchId { get; set; }
    public Guid ProjectId { get; set; }
    /// <summary>The number of changes that made the sheet. Each write adds the number of its changes.</summary>
    public long Version { get; set; }
    /// <summary>A JSON <see cref="CostSheetState"/>.</summary>
    public string Document { get; set; } = null!;
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
    public PriceSourceKind Source { get; set; }
    /// <summary>The person who entered a custom price, or who picked the fetched row.</summary>
    public string AcceptedBy { get; set; } = null!;
    public DateTimeOffset AcceptedAt { get; set; }
    /// <summary>Fetched only: the address the row came from. It holds no credential: a key goes in a header.</summary>
    public string? SourceUrl { get; set; }
    public DateTimeOffset? FetchedAt { get; set; }
    /// <summary>Fetched only: the provider's own ID of the price: the Azure meter ID, the AWS rate code or the Google Cloud SKU ID.</summary>
    public string? MeterId { get; set; }
    /// <summary>Fetched only: the provider's effective date, when the provider gives one.</summary>
    public DateOnly? ProviderEffectiveDate { get; set; }
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

    public static void MapEnums(NpgsqlDbContextOptionsBuilder npgsql)
    {
        npgsql.MapEnum<PriceSourceKind>("price_source_kind", Schema, AppDatabase.EnumLabels);
    }

    public static void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SheetEntity>(b =>
        {
            b.ToTable("sheets", Schema);
            b.HasKey(s => s.BranchId);
            b.Property(s => s.BranchId).ValueGeneratedNever();
            b.Property(s => s.Document).HasColumnType("jsonb");
            b.HasIndex(s => s.ProjectId);
        });

        modelBuilder.Entity<PriceEntryEntity>(b =>
        {
            b.ToTable("price_entries", Schema);
            b.HasKey(p => p.Id);
            // No fixed scale: the number is stored with the decimal places it was entered or fetched with.
            b.Property(p => p.UnitPrice).HasColumnType("numeric");
            b.HasIndex(p => new { p.WorkspaceId, p.Provider, p.Sku, p.Id });
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
