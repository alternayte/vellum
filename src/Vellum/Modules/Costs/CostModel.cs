using System.Collections.Immutable;
using Deedbox;
using Vellum.Modules.Modelling.Model;

namespace Vellum.Modules.Costs;

public static class CostCategory
{
    public const string Compute = "compute";
    public const string Database = "database";
    public const string Queue = "queue";
    public const string Storage = "storage";
    public const string Egress = "egress";
    public const string Other = "other";

    /// <summary>The order of the subtotals in an estimate.</summary>
    public static readonly string[] All = [Compute, Database, Queue, Storage, Egress, Other];
}

public static class CostProvider
{
    public const string Azure = "azure";
    public const string Aws = "aws";
    public const string Gcp = "gcp";
    public const string Custom = "custom";

    public static readonly string[] All = [Azure, Aws, Gcp, Custom];
}

/// <summary>
/// How the number of a workload assumption is known. An assumption is never
/// <c>observed-in-source</c> or <c>unknown</c>: it is a number that a person states.
/// </summary>
public static class AssumptionLabel
{
    public const string Declared = "declared";
    public const string Measured = "measured";
    public const string Inferred = "inferred";

    public static readonly string[] All = [Declared, Measured, Inferred];
}

public static class AssumptionKind
{
    public const string Requests = "requests";
    public const string Storage = "storage";
    public const string Egress = "egress";
    public const string Messages = "messages";
    public const string Other = "other";

    public static readonly string[] All = [Requests, Storage, Egress, Messages, Other];

    /// <summary>
    /// True for a number that counts what passes in a period: requests, egress and messages.
    /// A number of the kind <c>storage</c> or <c>other</c> is an amount that is held.
    /// </summary>
    public static bool IsFlow(string kind) => kind is Requests or Egress or Messages;
}

/// <summary>A usage-based price line of a resource: one unit price times one named assumption.</summary>
public sealed record UsageLine(Guid Id, string Name, Guid AssumptionId, Guid? PriceRef);

/// <summary>
/// A deployment resource of one model element. <paramref name="Quantity"/> and
/// <paramref name="Unit"/> are the monthly baseline; both are null for a resource that has
/// usage lines only. <paramref name="FactStatus"/> is the API word of a
/// <see cref="Modelling.Model.FactStatus"/>.
/// </summary>
public sealed record CostResource(
    Guid Id, Guid ElementId, string Name, string Category, string Provider, string Region, string Sku,
    decimal? Quantity, string? Unit, Guid? PriceRef, ImmutableList<UsageLine> Usage,
    string FactStatus, Provenance? Provenance);

/// <summary>
/// A named number that an estimate uses. <paramref name="SubjectId"/> is the element, the
/// operation or the channel the number is about, or null for a number about the whole system.
/// </summary>
public sealed record CostAssumption(
    Guid Id, string Name, string Kind, Guid? SubjectId, decimal Value, string Unit,
    string Label, string? Source, DateOnly EffectiveDate);

/// <summary>The events of a cost sheet stream. Plain records; the stream registers every nested type.</summary>
public static class CostEvent
{
    /// <summary>The first event of a sheet. It names the project and the model branch the sheet belongs to.</summary>
    public sealed record SheetOpened(Guid ProjectId, Guid BranchId, string By, DateTimeOffset At);

    public sealed record ResourceAdded(CostResource Resource, string By, DateTimeOffset At);

    public sealed record ResourceChanged(CostResource Resource, string By, DateTimeOffset At);

    public sealed record ResourceRemoved(Guid ResourceId, string By, DateTimeOffset At);

    public sealed record AssumptionAdded(CostAssumption Assumption, string By, DateTimeOffset At);

    public sealed record AssumptionChanged(CostAssumption Assumption, string By, DateTimeOffset At);

    public sealed record AssumptionRemoved(Guid AssumptionId, string By, DateTimeOffset At);

    /// <summary>
    /// The sheet now holds a copy of the sheet of another branch, as that sheet was at
    /// <paramref name="FromVersion"/>, and nothing else.
    /// </summary>
    public sealed record SheetCopied(
        Guid FromBranchId, long FromVersion,
        ImmutableList<CostResource> Resources, ImmutableList<CostAssumption> Assumptions, string By, DateTimeOffset At);
}

/// <summary>What a sheet held right after its last copy from another branch.</summary>
public sealed record SheetBase(Guid FromBranchId, ImmutableList<CostResource> Resources, ImmutableList<CostAssumption> Assumptions);

/// <summary>
/// The resources and the workload assumptions of one model branch of one project.
/// <paramref name="Base"/> is the common ancestor for the merge of a draft's sheet into the
/// sheet it was copied from; null for a sheet that was never a copy.
/// </summary>
public sealed record CostSheetState(
    Guid? ProjectId,
    Guid? BranchId,
    ImmutableList<CostResource> Resources,
    ImmutableList<CostAssumption> Assumptions,
    DateTimeOffset? UpdatedAt,
    SheetBase? Base)
    : IState<CostSheetState>
{
    public static CostSheetState Initial => new(null, null, [], [], null, null);

    public static CostSheetState Evolve(CostSheetState state, object @event) => @event switch
    {
        CostEvent.SheetOpened e => state with { ProjectId = e.ProjectId, BranchId = e.BranchId, UpdatedAt = e.At },
        CostEvent.ResourceAdded e => state with { Resources = state.Resources.Add(e.Resource), UpdatedAt = e.At },
        CostEvent.ResourceChanged e => state with
        {
            Resources = state.Resources.FindIndex(r => r.Id == e.Resource.Id) is >= 0 and var index
                ? state.Resources.SetItem(index, e.Resource)
                : state.Resources,
            UpdatedAt = e.At,
        },
        CostEvent.ResourceRemoved e => state with { Resources = state.Resources.RemoveAll(r => r.Id == e.ResourceId), UpdatedAt = e.At },
        CostEvent.AssumptionAdded e => state with { Assumptions = state.Assumptions.Add(e.Assumption), UpdatedAt = e.At },
        CostEvent.AssumptionChanged e => state with
        {
            Assumptions = state.Assumptions.FindIndex(a => a.Id == e.Assumption.Id) is >= 0 and var index
                ? state.Assumptions.SetItem(index, e.Assumption)
                : state.Assumptions,
            UpdatedAt = e.At,
        },
        CostEvent.AssumptionRemoved e => state with { Assumptions = state.Assumptions.RemoveAll(a => a.Id == e.AssumptionId), UpdatedAt = e.At },
        CostEvent.SheetCopied e => state with
        {
            Resources = e.Resources,
            Assumptions = e.Assumptions,
            UpdatedAt = e.At,
            Base = new SheetBase(e.FromBranchId, e.Resources, e.Assumptions),
        },
        _ => state,
    };
}
