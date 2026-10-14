using Vellum.Shared;

namespace Vellum.Modules.Settings;

/// <summary>Every rule for a complete settings document, checked against the instance policy.</summary>
public sealed class SettingsValidator(OutboundUrlPolicy urls, Bootstrap bootstrap)
{
    public const int MaxProviders = 20;
    public const int MaxExcludeGlobs = 100;
    private const decimal MaxBudgetUsd = 1_000_000m;

    /// <summary>The longest window a stress run accepts (<c>StressLimits.MaxDurationSeconds</c>).</summary>
    public const int MaxSimulationSeconds = 3_600;

    public async Task<List<FieldError>> ValidateAsync(SettingsDocument document, InstancePolicy policy, CancellationToken ct)
    {
        var errors = new List<FieldError>();

        await ValidateAiAsync(document.Ai, policy, errors, ct);

        await RequireUrlAsync("github.apiBaseUrl", document.Github.ApiBaseUrl, policy, errors, ct);
        if (document.Github.DefaultRef.Length > 255 || document.Github.DefaultRef.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
            errors.Add(new("github.defaultRef", "Must be a branch, tag or commit name of at most 255 characters with no spaces."));

        var import = document.Import;
        Range("import.maxFiles", import.MaxFiles, 1, 100_000, errors);
        Range("import.maxFileBytes", import.MaxFileBytes, 1, 16L * 1024 * 1024, errors);
        Range("import.maxTotalBytes", import.MaxTotalBytes, 1, 1024L * 1024 * 1024, errors);
        if (import.MaxTotalBytes < import.MaxFileBytes)
            errors.Add(new("import.maxTotalBytes", "Must be at least import.maxFileBytes."));
        if (import.ExcludeGlobs.Count > MaxExcludeGlobs)
            errors.Add(new("import.excludeGlobs", $"At most {MaxExcludeGlobs} patterns."));
        for (var i = 0; i < import.ExcludeGlobs.Count; i++)
        {
            if (import.ExcludeGlobs[i] is not { Length: >= 1 and <= 200 } glob || glob.Any(char.IsControl))
                errors.Add(new($"import.excludeGlobs[{i}]", "Must be a pattern of 1 to 200 characters."));
        }

        Text("cost.provider", document.Cost.Provider, 100, errors);
        Text("cost.region", document.Cost.Region, 100, errors);
        if (document.Cost.Currency.Length != 3 || !document.Cost.Currency.All(c => c is >= 'A' and <= 'Z'))
            errors.Add(new("cost.currency", "Must be a three-letter currency code in upper case, for example USD."));
        ValidatePriceApiUrl(document.Cost.AzureRetailPricesBaseUrl, policy, errors);
        Range("cost.stalePriceAfterDays", document.Cost.StalePriceAfterDays, 1, 3650, errors);

        Range("simulation.seed", document.Simulation.Seed, 0, int.MaxValue, errors);
        Range("simulation.durationSeconds", document.Simulation.DurationSeconds, 1, MaxSimulationSeconds, errors);
        if (document.Simulation.WarmUpSeconds < 0 || document.Simulation.WarmUpSeconds >= document.Simulation.DurationSeconds)
            errors.Add(new("simulation.warmUpSeconds", "Must be from 0 to less than simulation.durationSeconds."));
        if (document.Simulation.ArrivalRatePerSecond is not (> 0 and <= 100_000))
            errors.Add(new("simulation.arrivalRatePerSecond", "Must be more than 0 and at most 100000."));

        if (document.Privacy.SendPrivateCodeToAi && !policy.AllowSendingPrivateCodeToAi)
            errors.Add(new("privacy.sendPrivateCodeToAi", "The instance policy does not allow private code to be sent to AI providers."));

        Budget("usage.monthlyAiBudgetUsd", document.Usage.MonthlyAiBudgetUsd, errors);
        Range("usage.maxScansPerDay", document.Usage.MaxScansPerDay, 0, 10_000, errors);

        if (!string.IsNullOrEmpty(document.Notifications.WebhookUrl))
            await RequireUrlAsync("notifications.webhookUrl", document.Notifications.WebhookUrl, policy, errors, ct);

        return errors;
    }

    private async Task ValidateAiAsync(AiSettings ai, InstancePolicy policy, List<FieldError> errors, CancellationToken ct)
    {
        if (ai.Providers.Count > MaxProviders)
            errors.Add(new("ai.providers", $"At most {MaxProviders} providers."));

        var seen = new HashSet<Guid>();
        var enabled = new HashSet<Guid>();
        for (var i = 0; i < ai.Providers.Count; i++)
        {
            var field = $"ai.providers[{i}]";
            if (ai.Providers[i] is not { } provider)
            {
                errors.Add(new(field, "Must be a provider."));
                continue;
            }

            if (provider.Id == Guid.Empty || !seen.Add(provider.Id))
                errors.Add(new($"{field}.id", "Must be a GUID that no other provider in the list uses."));

            var kindAllowed = false;
            if (!AiProviderKinds.All.Contains(provider.Kind))
                errors.Add(new($"{field}.kind", $"Must be one of: {string.Join(", ", AiProviderKinds.All)}."));
            else if (!policy.AllowedAiProviderKinds.Contains(provider.Kind))
                errors.Add(new($"{field}.kind", $"The instance policy does not allow the provider kind '{provider.Kind}'."));
            else
                kindAllowed = true;

            if (provider.Name.Trim().Length is 0 or > 100)
                errors.Add(new($"{field}.name", "Must be 1 to 100 characters."));
            if (provider.Model.Length > 200 || (provider.Enabled && provider.Model.Trim().Length == 0))
                errors.Add(new($"{field}.model", "An enabled provider needs a model name of at most 200 characters."));

            // Only an OpenAI-compatible endpoint has no address of its own.
            if (provider.BaseUrl.Length == 0)
            {
                if (provider.Kind == "openai-compatible")
                    errors.Add(new($"{field}.baseUrl", "An OpenAI-compatible provider needs a base URL."));
            }
            else
            {
                await RequireUrlAsync($"{field}.baseUrl", provider.BaseUrl, policy, errors, ct);
            }

            if (provider.Temperature is not (null or (>= 0 and <= 2)))
                errors.Add(new($"{field}.temperature", "Must be empty or from 0 to 2."));
            Range($"{field}.timeoutSeconds", provider.TimeoutSeconds, 1, 600, errors);
            Budget($"{field}.monthlyBudgetUsd", provider.MonthlyBudgetUsd, errors);
            if (provider.RequestsPerMinute is not (null or (>= 1 and <= 100_000)))
                errors.Add(new($"{field}.requestsPerMinute", "Must be empty or from 1 to 100000."));

            if (provider.Enabled && kindAllowed)
                enabled.Add(provider.Id);
        }

        Route("ai.taskRouting.inferArchitecture", ai.TaskRouting.InferArchitecture);
        Route("ai.taskRouting.proposeFlow", ai.TaskRouting.ProposeFlow);
        Route("ai.taskRouting.critique", ai.TaskRouting.Critique);
        Route("ai.taskRouting.explain", ai.TaskRouting.Explain);

        void Route(string field, Guid? providerId)
        {
            if (providerId is { } id && !enabled.Contains(id))
                errors.Add(new(field, "Must be empty or the id of an enabled provider."));
        }
    }

    private async Task RequireUrlAsync(string field, string? url, InstancePolicy policy, List<FieldError> errors, CancellationToken ct)
    {
        if (url is { Length: > 2000 })
        {
            errors.Add(new(field, "Must be at most 2000 characters."));
            return;
        }

        // Every URL in the settings can receive a credential: the GitHub token, a provider key
        // or a request that the webhook secret signs.
        var verdict = await urls.CheckAsync(
            url, policy.AllowPrivateNetworkUrls, requireHttps: bootstrap.Profile == OperatingProfile.Hosted, ct);
        if (!verdict.Allowed)
            errors.Add(new(field, verdict.Reason!));
    }

    /// <summary>
    /// The price API takes no credential, and its default address is part of every settings
    /// document. So a save does not resolve its host name: an instance with no network still
    /// saves its settings. The request resolves the name and checks the addresses.
    /// </summary>
    private void ValidatePriceApiUrl(string url, InstancePolicy policy, List<FieldError> errors)
    {
        if (url.Length == 0)
            return;
        if (url.Length > 2000)
        {
            errors.Add(new(WorkspaceSettings.AzurePricesField, "Must be at most 2000 characters."));
            return;
        }

        var verdict = OutboundUrlPolicy.CheckWithoutResolving(
            url, policy.AllowPrivateNetworkUrls, requireHttps: bootstrap.Profile == OperatingProfile.Hosted);
        if (!verdict.Allowed)
            errors.Add(new(WorkspaceSettings.AzurePricesField, verdict.Reason!));
    }

    private static void Range(string field, long value, long min, long max, List<FieldError> errors)
    {
        if (value < min || value > max)
            errors.Add(new(field, $"Must be from {min} to {max}."));
    }

    private static void Budget(string field, decimal? value, List<FieldError> errors)
    {
        if (value is < 0 or > MaxBudgetUsd)
            errors.Add(new(field, $"Must be empty or from 0 to {MaxBudgetUsd:0}."));
    }

    private static void Text(string field, string value, int max, List<FieldError> errors)
    {
        if (value.Length > max || value.Any(char.IsControl))
            errors.Add(new(field, $"Must be at most {max} characters."));
    }
}
