using System.Text.RegularExpressions;
using Vellum.Modules.Trace.Engine;
using Vellum.Shared;

namespace Vellum.Modules.Promises;

/// <summary>The things of a project that a promise or an obligation may name.</summary>
/// <param name="ScenarioFaults">The faults each loaded scenario declares itself, by scenario ID. A scenario that was not loaded is absent.</param>
/// <param name="GitHubHost">The host of the repository pages of the workspace's GitHub, or null when it is not configured or was not read.</param>
/// <param name="Repositories"><c>host/owner/name</c> of every repository the project has an accepted import link or a tracked ref for.</param>
public sealed record KnownThings(
    IReadOnlySet<Guid> Scenarios,
    IReadOnlySet<Guid> Operations,
    IReadOnlySet<Guid> Channels,
    IReadOnlySet<Guid> Subscriptions,
    IReadOnlySet<Guid> Messages,
    IReadOnlySet<Guid> Elements,
    IReadOnlyDictionary<Guid, IReadOnlyList<Fault>>? ScenarioFaults = null,
    string? GitHubHost = null,
    IReadOnlySet<string>? Repositories = null)
{
    public bool Has(string kind, Guid id) => kind switch
    {
        "scenario" => Scenarios.Contains(id),
        "operation" => Operations.Contains(id),
        "channel" => Channels.Contains(id),
        "subscription" => Subscriptions.Contains(id),
        "message" => Messages.Contains(id),
        "element" => Elements.Contains(id),
        _ => false,
    };

    public bool HasContract(Guid id) =>
        Operations.Contains(id) || Channels.Contains(id) || Subscriptions.Contains(id) || Messages.Contains(id);

    /// <summary>What a fault or an assertion may target: an element or a contract.</summary>
    public bool HasTarget(Guid id) => Elements.Contains(id) || HasContract(id);
}

/// <summary>
/// What a promise and an obligation must satisfy to be stored: text inside the shared text
/// rule, bounded lists, and links that name things of the project.
/// </summary>
public static partial class PromiseRules
{
    public const int MaxPerProject = 2_000;
    public const int MaxRevisions = 100;
    public const int MaxLinksPerKind = 50;
    public const int MaxForbiddenEvents = 20;
    public const int MaxObligations = 50;
    public const int MaxPlanItems = 50;
    public const int MaxFaults = 50;
    public const int MaxAssertions = 100;

    /// <summary>
    /// The most characters the content and the obligations of ONE revision hold as JSON. The
    /// write that would pass it is refused with 413. A revision that retires holds no content,
    /// so a promise at this limit can still be revised and retired.
    /// </summary>
    public const int MaxRevisionChars = 256 * 1024;

    /// <summary>The most characters one obligation recipe holds as JSON.</summary>
    public const int MaxRecipeChars = 32 * 1024;

    public const int MaxWorkflowChars = 255;

    public const string GitHubActions = "github-actions";

    [GeneratedRegex(@"\A[A-Z]{2,6}\z")]
    private static partial Regex Prefix();

    [GeneratedRegex(@"\A[a-z0-9_.-]{1,100}/[a-z0-9_.-]{1,100}\z")]
    private static partial Regex Repository();

    [GeneratedRegex(@"\A[a-z][a-z0-9-]{0,49}\z")]
    private static partial Regex Kind();

    public static bool IsPrefix(string? value) => value is not null && Prefix().IsMatch(value);

    public static bool IsRepository(string? value) => value is not null && Repository().IsMatch(value);

    /// <summary><c>ORD-014</c>: the number has at least three digits.</summary>
    public static string RequirementId(string prefix, int number) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{prefix}-{number:000}");

    /// <summary>Lists in one order with no repeats, so the same request stores the same content.</summary>
    public static PromiseContent Normalize(PromiseContent content) => content with
    {
        ForbiddenEvents = content.ForbiddenEvents ?? [],
        RequiresPattern = content.RequiresPattern is { } pattern
            ? pattern with { Repository = (pattern.Repository ?? "").Trim().ToLowerInvariant() }
            : null,
        Links = new PromiseLinks(
            Sorted(content.Links?.ScenarioIds), Sorted(content.Links?.OperationIds), Sorted(content.Links?.ChannelIds),
            Sorted(content.Links?.SubscriptionIds), Sorted(content.Links?.MessageIds), Sorted(content.Links?.ElementIds)),
    };

    private static IReadOnlyList<Guid> Sorted(IReadOnlyList<Guid>? ids) => [.. (ids ?? []).Distinct().Order()];

    /// <summary>Every rule the content breaks. The content is already normalized.</summary>
    public static IReadOnlyList<ValidationError> Check(PromiseContent content, KnownThings known)
    {
        var errors = new List<ValidationError>();
        void Add(ValidationError? error)
        {
            if (error is not null) errors.Add(error);
        }

        if (string.IsNullOrWhiteSpace(content.Title)) errors.Add(new("title", "Title is required"));
        if (string.IsNullOrWhiteSpace(content.Claim)) errors.Add(new("claim", "Claim is required"));
        Add(TextRule.Line("title", content.Title));
        Add(TextRule.Lines("claim", content.Claim));
        Add(TextRule.Lines("scope", content.Scope));
        Add(TextRule.Lines("preconditions", content.Preconditions));
        Add(TextRule.Lines("expectedOutcome", content.ExpectedOutcome));
        Add(TextRule.Lines("forbiddenOutcome", content.ForbiddenOutcome));
        Add(TextRule.Lines("evidenceCriteria", content.EvidenceCriteria));

        if (content.ForbiddenEvents.Count > MaxForbiddenEvents)
            errors.Add(new("forbiddenEvents", $"At most {MaxForbiddenEvents} forbidden events are allowed"));
        for (var i = 0; i < content.ForbiddenEvents.Count; i++)
        {
            var forbidden = content.ForbiddenEvents[i];
            if (forbidden is null || !Enum.IsDefined(forbidden.Kind))
                errors.Add(new($"forbiddenEvents[{i}].kind", "The event kind is not known"));
            else if (forbidden.Target is { } target && !known.HasTarget(target))
                errors.Add(new($"forbiddenEvents[{i}].target", $"No element or contract with ID {target} exists in the project"));
        }

        if (content.RequiresPattern is { } pattern)
            errors.AddRange(CheckPattern("requiresPattern", pattern.Repository, pattern.Pattern, pattern.Service, known));

        foreach (var (kind, ids) in new (string, IReadOnlyList<Guid>)[]
                 {
                     ("scenario", content.Links.ScenarioIds), ("operation", content.Links.OperationIds),
                     ("channel", content.Links.ChannelIds), ("subscription", content.Links.SubscriptionIds),
                     ("message", content.Links.MessageIds), ("element", content.Links.ElementIds),
                 })
        {
            var field = $"links.{kind}Ids";
            if (ids.Count > MaxLinksPerKind)
                errors.Add(new(field, $"At most {MaxLinksPerKind} {kind} links are allowed"));
            foreach (var id in ids.Where(id => !known.Has(kind, id)))
                errors.Add(new(field, $"No {kind} with ID {id} exists in the project"));
        }

        return errors;
    }

    /// <summary>What approval needs beyond a valid draft (SDD §8: preconditions, outcomes and what counts as evidence).</summary>
    public static IReadOnlyList<ValidationError> CheckComplete(PromiseContent content)
    {
        var errors = new List<ValidationError>();
        void Need(string field, string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                errors.Add(new(field, "This field is required before approval"));
        }

        Need("preconditions", content.Preconditions);
        Need("expectedOutcome", content.ExpectedOutcome);
        Need("forbiddenOutcome", content.ForbiddenOutcome);
        Need("evidenceCriteria", content.EvidenceCriteria);
        return errors;
    }

    private static IEnumerable<ValidationError> CheckPattern(string field, string? repository, string? pattern, Guid? service, KnownThings known)
    {
        if (!IsRepository(repository))
            yield return new($"{field}.repository", "A repository is owner/name in lower case");
        if (pattern is null || !SourcePattern.Known.Contains(pattern))
            yield return new($"{field}.pattern", $"The pattern is one of: {string.Join(", ", SourcePattern.Known)}");
        if (service is { } element && !known.Elements.Contains(element))
            yield return new($"{field}.service", $"No element with ID {element} exists in the project");
    }

    /// <summary>The fields of an obligation that a caller gives.</summary>
    public sealed record ObligationInput(string Kind, string Title, ObligationRecipe Recipe);

    public static IReadOnlyList<ValidationError> Check(ObligationInput input, KnownThings known)
    {
        var errors = new List<ValidationError>();
        void Add(ValidationError? error)
        {
            if (error is not null) errors.Add(error);
        }

        if (input.Kind is null || !Kind().IsMatch(input.Kind))
            errors.Add(new("kind", "A kind is lower-case letters, digits and hyphens, at most 50 characters"));
        if (string.IsNullOrWhiteSpace(input.Title)) errors.Add(new("title", "Title is required"));
        Add(TextRule.Line("title", input.Title));
        if (input.Recipe is null)
        {
            errors.Add(new("recipe", "A recipe is required"));
            return errors;
        }
        if (string.IsNullOrWhiteSpace(input.Recipe.Text)) errors.Add(new("recipe.text", "The recipe needs text"));
        Add(TextRule.Lines("recipe.text", input.Recipe.Text));
        if (errors.Count > 0)
            return errors;

        var recipe = input.Recipe;
        var parts = (recipe.ScenarioCheck is null ? 0 : 1) + (recipe.SourcePattern is null ? 0 : 1) + (recipe.Plan is null ? 0 : 1);
        switch (input.Kind)
        {
            case ObligationKind.ScenarioCheck when recipe.ScenarioCheck is { } check && parts == 1:
                if (!known.Scenarios.Contains(check.ScenarioId))
                    errors.Add(new("recipe.scenarioCheck.scenarioId", $"No scenario with ID {check.ScenarioId} exists in the project"));
                if ((check.Faults?.Count ?? 0) > MaxFaults)
                    errors.Add(new("recipe.scenarioCheck.faults", $"At most {MaxFaults} faults are allowed"));
                if (check.Expect is not { Count: > 0 })
                    errors.Add(new("recipe.scenarioCheck.expect", "A scenario check needs at least one assertion"));
                else if (check.Expect.Count > MaxAssertions)
                    errors.Add(new("recipe.scenarioCheck.expect", $"At most {MaxAssertions} assertions are allowed"));
                foreach (var (fault, i) in (check.Faults ?? []).Select((f, i) => (f, i)))
                {
                    if (fault is null || !Enum.IsDefined(fault.Kind) || fault.FailFirst is < 1)
                        errors.Add(new($"recipe.scenarioCheck.faults[{i}]", "A fault needs a known kind, and failFirst of at least 1 when given"));
                    else if (!known.HasTarget(fault.Target))
                        errors.Add(new($"recipe.scenarioCheck.faults[{i}].target", $"No element or contract with ID {fault.Target} exists in the project"));
                }
                if ((check.Faults ?? []).Where(f => f is not null).GroupBy(f => f.Target).Any(g => g.Count() > 1))
                    errors.Add(new("recipe.scenarioCheck.faults", "Each target has at most one fault"));
                // An obligation adds failures to the scenario. It cannot weaken or replace one the scenario declares.
                if (known.ScenarioFaults is { } declared && declared.TryGetValue(check.ScenarioId, out var own))
                {
                    foreach (var (fault, i) in (check.Faults ?? []).Select((f, i) => (f, i)).Where(x => x.f is not null))
                    {
                        if (own.Any(f => f is not null && f.Target == fault.Target))
                        {
                            errors.Add(new($"recipe.scenarioCheck.faults[{i}].target",
                                $"The scenario already declares a fault on {fault.Target}. An obligation adds faults to the scenario's own; it cannot weaken or replace one. Remove this fault, or change the scenario."));
                        }
                    }
                }
                foreach (var (assertion, i) in (check.Expect ?? []).Select((a, i) => (a, i)))
                {
                    var at = $"recipe.scenarioCheck.expect[{i}]";
                    if (assertion is null || string.IsNullOrWhiteSpace(assertion.Id))
                    {
                        errors.Add(new($"{at}.id", "An assertion needs an ID"));
                        continue;
                    }
                    Add(TextRule.Line($"{at}.id", assertion.Id, TextRule.MaxIdentifier));
                    switch (assertion)
                    {
                        case OutcomeAssertion outcome:
                            if (string.IsNullOrWhiteSpace(outcome.Step)) errors.Add(new($"{at}.step", "A step ID is required"));
                            Add(TextRule.Line($"{at}.step", outcome.Step, TextRule.MaxIdentifier));
                            if (!Enum.IsDefined(outcome.Expected)) errors.Add(new($"{at}.expected", "The outcome is not known"));
                            break;
                        case EventCountAssertion count:
                            if (!Enum.IsDefined(count.Kind)) errors.Add(new($"{at}.kind", "The event kind is not known"));
                            if (count.Expected < 0) errors.Add(new($"{at}.expected", "A count is zero or more"));
                            if (count.Target is { } target && !known.HasTarget(target))
                                errors.Add(new($"{at}.target", $"No element or contract with ID {target} exists in the project"));
                            break;
                    }
                }
                if ((check.Expect ?? []).Where(a => a?.Id is not null).GroupBy(a => a.Id, StringComparer.Ordinal).Any(g => g.Count() > 1))
                    errors.Add(new("recipe.scenarioCheck.expect", "Assertion IDs must differ"));
                break;

            case ObligationKind.SourcePatternCheck when recipe.SourcePattern is { } source && parts == 1:
                errors.AddRange(CheckPattern("recipe.sourcePattern", source.Repository, source.Pattern, source.Service, known));
                break;

            case var kind when ObligationKind.IsPlan(kind) && recipe.Plan is { } plan && parts == 1:
                Add(PlanList("recipe.plan.steps", plan.Steps, required: true));
                Add(PlanList("recipe.plan.inputs", plan.Inputs, required: false));
                Add(PlanList("recipe.plan.expectedObservations", plan.ExpectedObservations, required: true));
                Add(PlanList("recipe.plan.forbiddenObservations", plan.ForbiddenObservations, required: false));
                if ((plan.ContractIds?.Count ?? 0) > MaxLinksPerKind)
                    errors.Add(new("recipe.plan.contractIds", $"At most {MaxLinksPerKind} contracts are allowed"));
                foreach (var id in (plan.ContractIds ?? []).Where(id => !known.HasContract(id)))
                    errors.Add(new("recipe.plan.contractIds", $"No operation, channel, subscription or message with ID {id} exists in the project"));
                if (plan.EvidenceSource is { } evidenceSource)
                    errors.AddRange(CheckEvidenceSource(evidenceSource, known));
                break;

            case ObligationKind.ScenarioCheck:
                errors.Add(new("recipe", "A scenario-check needs recipe.scenarioCheck and no other typed part"));
                break;
            case ObligationKind.SourcePatternCheck:
                errors.Add(new("recipe", "A source-pattern-check needs recipe.sourcePattern and no other typed part"));
                break;
            case var kind when ObligationKind.IsPlan(kind):
                errors.Add(new("recipe", $"A {kind} needs recipe.plan and no other typed part"));
                break;

            // A kind Vellum does not know is stored as text only. It is unsupported and never passes.
            default:
                if (parts > 0)
                    errors.Add(new("recipe", $"Vellum does not know the kind '{input.Kind}', so its recipe is text only"));
                break;
        }

        if (errors.Count == 0 && PromiseJson.Write(recipe).Length > MaxRecipeChars)
            errors.Add(new("recipe", $"A recipe holds at most {MaxRecipeChars} characters as JSON"));
        return errors;
    }

    /// <summary>
    /// The evidence source of a test plan: a workflow of a repository that the project already
    /// follows on the workspace's GitHub. A run of any other repository is never evidence here.
    /// </summary>
    private static IEnumerable<ValidationError> CheckEvidenceSource(EvidenceSource source, KnownThings known)
    {
        const string field = "recipe.plan.evidenceSource";
        if (source.System != GitHubActions)
            yield return new($"{field}.system", $"The system is {GitHubActions}");
        if (string.IsNullOrWhiteSpace(source.Workflow))
            yield return new($"{field}.workflow", "A workflow is required: the file name of the workflow, such as ci.yml, or its name");
        else if (TextRule.Line($"{field}.workflow", source.Workflow, MaxWorkflowChars) is { } workflowError)
            yield return workflowError;
        if (source.Job is not null && string.IsNullOrWhiteSpace(source.Job))
            yield return new($"{field}.job", "A job is a job name, or null for the whole run");
        else if (TextRule.Line($"{field}.job", source.Job) is { } jobError)
            yield return jobError;

        if (!IsRepository(source.Repository))
            yield return new($"{field}.repository", "A repository is owner/name in lower case");
        else if (known.GitHubHost is not { } host)
            yield return new($"{field}.repository", "The workspace has no usable GitHub address, so no repository can be an evidence source. Set github.apiBaseUrl in the workspace settings.");
        else if (known.Repositories?.Contains($"{host}/{source.Repository}") != true)
        {
            yield return new($"{field}.repository",
                $"The project follows no repository {source.Repository} on {host}. An evidence source is a repository with an accepted import or a tracked ref.");
        }
    }

    private static ValidationError? PlanList(string field, IReadOnlyList<string>? items, bool required)
    {
        if (items is null || items.Count == 0)
            return required ? new ValidationError(field, "At least one item is required") : null;
        if (items.Count > MaxPlanItems)
            return new ValidationError(field, $"At most {MaxPlanItems} items are allowed");
        for (var i = 0; i < items.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(items[i]))
                return new ValidationError($"{field}[{i}]", "An item must not be blank");
            if (TextRule.Lines($"{field}[{i}]", items[i]) is { } error)
                return error;
        }
        return null;
    }

    /// <summary>Null lists read as empty, so the stored recipe has one shape.</summary>
    public static ObligationRecipe Normalize(ObligationRecipe recipe) => recipe with
    {
        ScenarioCheck = recipe.ScenarioCheck is { } check
            ? check with { Faults = check.Faults ?? [], Expect = check.Expect ?? [] }
            : null,
        SourcePattern = recipe.SourcePattern is { } source
            ? source with { Repository = (source.Repository ?? "").Trim().ToLowerInvariant() }
            : null,
        Plan = recipe.Plan is { } plan
            ? new TestPlanRecipe(plan.Steps ?? [], plan.Inputs ?? [], plan.ExpectedObservations ?? [],
                plan.ForbiddenObservations ?? [], [.. (plan.ContractIds ?? []).Distinct().Order()],
                plan.EvidenceSource is { } evidence
                    ? evidence with
                    {
                        Repository = (evidence.Repository ?? "").Trim().ToLowerInvariant(),
                        Workflow = (evidence.Workflow ?? "").Trim(),
                        Job = evidence.Job?.Trim(),
                    }
                    : null)
            : null,
    };
}
