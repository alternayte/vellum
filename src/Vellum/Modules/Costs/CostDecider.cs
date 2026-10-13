using System.Collections.Immutable;
using Vellum.Modules.Modelling.Model;
using Vellum.Shared;
using Decision = Vellum.Shared.CommandResult<System.Collections.Generic.IReadOnlyList<object>>;

namespace Vellum.Modules.Costs;

/// <summary>
/// What a sheet may point at, read before the decision: the elements of the branch's model,
/// the things an assumption may be about (elements, operations and channels) and the price
/// entries of the project's workspace.
/// </summary>
public sealed record CostWorld(IReadOnlySet<Guid> Elements, IReadOnlySet<Guid> Subjects, IReadOnlySet<Guid> Prices);

public static class CostRules
{
    public const int MaxResources = 500;
    public const int MaxUsageLines = 20;
    public const int MaxAssumptions = 500;
    public const int MaxRegionChars = 100;
    public const decimal MaxQuantity = 1_000_000_000_000m;
    public const int MaxQuantityDecimals = 6;

    public static ValidationError? Quantity(string field, decimal value) =>
        value is < 0 or > MaxQuantity || value.Scale > MaxQuantityDecimals
            ? new ValidationError(field, $"Must be from 0 to {MaxQuantity:0} with at most {MaxQuantityDecimals} decimal places.")
            : null;

    public static ValidationError? Unit(string field, string? unit) =>
        UnitExpression.Parse(unit) is null
            ? new ValidationError(field, "Must be a unit such as 'instances', 'hours', 'GB', 'GB/month' or 'requests/month'.")
            : null;

    public static List<ValidationError> Check(CostResource resource, CostWorld world, IReadOnlySet<Guid> assumptions)
    {
        var errors = new List<ValidationError>();
        void Add(ValidationError? error)
        {
            if (error is not null)
                errors.Add(error);
        }

        if (resource.Id == Guid.Empty)
            errors.Add(new("id", "An ID is required"));
        if (!world.Elements.Contains(resource.ElementId))
            errors.Add(new("elementId", "The element is not in the model of this branch"));
        if (string.IsNullOrWhiteSpace(resource.Name))
            errors.Add(new("name", "A name is required"));
        Add(TextRule.Line("name", resource.Name));
        if (!CostCategory.All.Contains(resource.Category))
            errors.Add(new("category", $"Must be one of: {string.Join(", ", CostCategory.All)}"));
        if (!CostProvider.All.Contains(resource.Provider))
            errors.Add(new("provider", $"Must be one of: {string.Join(", ", CostProvider.All)}"));
        Add(TextRule.Line("region", resource.Region, MaxRegionChars));
        Add(TextRule.Line("sku", resource.Sku));

        if (resource.Quantity is null != resource.Unit is null)
            errors.Add(new("quantity", "A quantity and its unit are set together, or both are left out"));
        if (resource.Quantity is { } quantity)
            Add(Quantity("quantity", quantity));
        if (resource.Unit is not null)
            Add(Unit("unit", resource.Unit));
        if (resource.Quantity is null && resource.PriceRef is not null)
            errors.Add(new("priceRef", "A price needs a quantity. Set the quantity, or price the resource with usage lines"));
        if (resource.PriceRef is { } price && !world.Prices.Contains(price))
            errors.Add(new("priceRef", "The price entry is not in the price book of this workspace"));

        if (resource.Usage.Count > MaxUsageLines)
            errors.Add(new("usage", $"A resource has at most {MaxUsageLines} usage lines"));
        var lineIds = new HashSet<Guid>();
        for (var i = 0; i < resource.Usage.Count; i++)
        {
            var line = resource.Usage[i];
            if (line.Id == Guid.Empty || !lineIds.Add(line.Id))
                errors.Add(new($"usage[{i}].id", "Must be an ID that no other usage line of the resource has"));
            if (string.IsNullOrWhiteSpace(line.Name))
                errors.Add(new($"usage[{i}].name", "A name is required"));
            Add(TextRule.Line($"usage[{i}].name", line.Name));
            if (!assumptions.Contains(line.AssumptionId))
                errors.Add(new($"usage[{i}].assumptionId", "The assumption is not in this sheet"));
            if (line.PriceRef is { } linePrice && !world.Prices.Contains(linePrice))
                errors.Add(new($"usage[{i}].priceRef", "The price entry is not in the price book of this workspace"));
        }

        if (!FactStatusFormat.TryParseApiString(resource.FactStatus, out var status))
            errors.Add(new("factStatus", "Invalid fact status"));
        else
            Add(FactRules.Validate(status, resource.Provenance));

        return errors;
    }

    public static List<ValidationError> Check(CostAssumption assumption, CostWorld world)
    {
        var errors = new List<ValidationError>();
        void Add(ValidationError? error)
        {
            if (error is not null)
                errors.Add(error);
        }

        if (assumption.Id == Guid.Empty)
            errors.Add(new("id", "An ID is required"));
        if (string.IsNullOrWhiteSpace(assumption.Name))
            errors.Add(new("name", "A name is required"));
        Add(TextRule.Line("name", assumption.Name));
        if (!AssumptionKind.All.Contains(assumption.Kind))
            errors.Add(new("kind", $"Must be one of: {string.Join(", ", AssumptionKind.All)}"));
        if (assumption.SubjectId is { } subject && !world.Subjects.Contains(subject))
            errors.Add(new("subjectId", "Must be an element, an operation or a channel of the model of this branch"));
        Add(Quantity("value", assumption.Value));
        Add(Unit("unit", assumption.Unit));
        if (!AssumptionLabel.All.Contains(assumption.Label))
            errors.Add(new("label", $"Must be one of: {string.Join(", ", AssumptionLabel.All)}"));
        Add(TextRule.Line("source", assumption.Source, TextRule.MaxPointer));

        // A measured or an inferred number says where it comes from; a declared one may.
        if (assumption.Label is AssumptionLabel.Measured or AssumptionLabel.Inferred && string.IsNullOrWhiteSpace(assumption.Source))
            errors.Add(new("source", $"A {assumption.Label} number needs a source"));
        if (assumption.EffectiveDate == default)
            errors.Add(new("effectiveDate", "An effective date is required"));

        return errors;
    }
}

/// <summary>The decisions of a cost sheet. A sheet opens with its first change.</summary>
public static class CostDecider
{
    private const string ResourceNotFound = "Resource not found";
    private const string AssumptionNotFound = "Assumption not found";

    public static Decision AddResource(
        CostSheetState state, Guid projectId, Guid branchId, CostResource resource, CostWorld world, string userId, DateTimeOffset now)
    {
        if (state.Resources.Count >= CostRules.MaxResources)
            return new Decision.Conflict($"A cost sheet holds at most {CostRules.MaxResources} resources.");
        if (state.Resources.Any(r => r.Id == resource.Id))
            return new Decision.Conflict("This ID is already in use. Choose another ID.");
        return CheckResource(state, resource, world)
            ?? Opened(state, projectId, branchId, userId, now, new CostEvent.ResourceAdded(resource, userId, now));
    }

    public static Decision ChangeResource(CostSheetState state, CostResource resource, CostWorld world, string userId, DateTimeOffset now)
    {
        if (state.Resources.All(r => r.Id != resource.Id))
            return new Decision.NotFound(ResourceNotFound);
        return CheckResource(state, resource, world) ?? new Decision.Success([new CostEvent.ResourceChanged(resource, userId, now)]);
    }

    public static Decision RemoveResource(CostSheetState state, Guid resourceId, string userId, DateTimeOffset now) =>
        state.Resources.Any(r => r.Id == resourceId)
            ? new Decision.Success([new CostEvent.ResourceRemoved(resourceId, userId, now)])
            : new Decision.NotFound(ResourceNotFound);

    public static Decision AddAssumption(
        CostSheetState state, Guid projectId, Guid branchId, CostAssumption assumption, CostWorld world, string userId, DateTimeOffset now)
    {
        if (state.Assumptions.Count >= CostRules.MaxAssumptions)
            return new Decision.Conflict($"A cost sheet holds at most {CostRules.MaxAssumptions} assumptions.");
        if (state.Assumptions.Any(a => a.Id == assumption.Id))
            return new Decision.Conflict("This ID is already in use. Choose another ID.");
        return CheckAssumption(state, assumption, world)
            ?? Opened(state, projectId, branchId, userId, now, new CostEvent.AssumptionAdded(assumption, userId, now));
    }

    public static Decision ChangeAssumption(CostSheetState state, CostAssumption assumption, CostWorld world, string userId, DateTimeOffset now)
    {
        if (state.Assumptions.All(a => a.Id != assumption.Id))
            return new Decision.NotFound(AssumptionNotFound);
        return CheckAssumption(state, assumption, world) ?? new Decision.Success([new CostEvent.AssumptionChanged(assumption, userId, now)]);
    }

    /// <summary>A usage line that names the removed assumption stays, and the estimate lists it as unknown.</summary>
    public static Decision RemoveAssumption(CostSheetState state, Guid assumptionId, string userId, DateTimeOffset now) =>
        state.Assumptions.Any(a => a.Id == assumptionId)
            ? new Decision.Success([new CostEvent.AssumptionRemoved(assumptionId, userId, now)])
            : new Decision.NotFound(AssumptionNotFound);

    /// <summary>
    /// Replaces the sheet with a copy of <paramref name="source"/>. A resource whose element
    /// is not in the model of this branch is left out, and so is the subject of an assumption
    /// that this model does not have.
    /// </summary>
    public static Decision Copy(
        CostSheetState state, Guid projectId, Guid branchId, CostSheetState source, Guid fromBranchId, long fromVersion,
        CostWorld world, string userId, DateTimeOffset now)
    {
        var resources = source.Resources.Where(r => world.Elements.Contains(r.ElementId)).ToImmutableList();
        var assumptions = source.Assumptions
            .Select(a => a.SubjectId is { } subject && !world.Subjects.Contains(subject) ? a with { SubjectId = null } : a)
            .ToImmutableList();
        return Opened(state, projectId, branchId, userId, now,
            new CostEvent.SheetCopied(fromBranchId, fromVersion, resources, assumptions, userId, now));
    }

    private static Decision? CheckResource(CostSheetState state, CostResource resource, CostWorld world)
    {
        if (CostRules.Check(resource, world, state.Assumptions.Select(a => a.Id).ToHashSet()) is { Count: > 0 } errors)
            return new Decision.Invalid(errors);

        // Two variants are compared by element and name, so the pair names one resource.
        return state.Resources.Any(r => r.Id != resource.Id && r.ElementId == resource.ElementId && r.Name == resource.Name)
            ? new Decision.Conflict("The element already has a resource with this name.")
            : null;
    }

    private static Decision? CheckAssumption(CostSheetState state, CostAssumption assumption, CostWorld world)
    {
        if (CostRules.Check(assumption, world) is { Count: > 0 } errors)
            return new Decision.Invalid(errors);
        return state.Assumptions.Any(a => a.Id != assumption.Id && a.Name == assumption.Name)
            ? new Decision.Conflict("The sheet already has an assumption with this name.")
            : null;
    }

    private static Decision Opened(CostSheetState state, Guid projectId, Guid branchId, string userId, DateTimeOffset now, object change) =>
        state.ProjectId is null
            ? new Decision.Success([new CostEvent.SheetOpened(projectId, branchId, userId, now), change])
            : new Decision.Success([change]);
}
