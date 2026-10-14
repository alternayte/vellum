using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Vellum.Modules.Settings.Entities;
using Vellum.Modules.Settings.Secrets;
using Vellum.Shared;

namespace Vellum.Modules.Settings.Document;

/// <summary><paramref name="Version"/> is 0 until the admin saves instance settings for the first time.</summary>
public sealed record InstanceSettings(InstancePolicy Policy, SettingsDocument Defaults, JsonObject DefaultsJson, long Version);

/// <summary>A stored setting that the current instance policy refuses. It does not apply.</summary>
public sealed record PolicyViolation(string Field, string Message);

/// <summary>The answer to "where may this workspace send a request or a credential".</summary>
public abstract record SettingsUrl
{
    private SettingsUrl() { }

    /// <summary>The URL passed the instance policy. <paramref name="Origin"/> is what a credential is bound to.</summary>
    public sealed record Usable(Uri Url, string Origin) : SettingsUrl;

    /// <summary>The workspace has no such URL, or no such provider.</summary>
    public sealed record NotConfigured : SettingsUrl;

    /// <summary>The instance policy refuses the stored URL. No request and no credential may go to it.</summary>
    public sealed record BlockedByPolicy(string Field, string Message) : SettingsUrl;
}

/// <summary>
/// The settings of one workspace as they apply now. <paramref name="Effective"/> holds no
/// value that <paramref name="Policy"/> refuses: a refused provider is disabled, a refused
/// GitHub base URL is empty and a refused webhook URL is null, and each one is listed in
/// <paramref name="PolicyViolations"/>. A caller that makes a request takes the URL from
/// <see cref="GithubApiBaseUrl"/>, <see cref="WebhookUrl"/> or <see cref="ProviderBaseUrl"/>,
/// then passes it to <c>OutboundUrlPolicy.CheckAsync</c> with
/// <c>Policy.AllowPrivateNetworkUrls</c> and <paramref name="RequireHttps"/> at request time.
/// <paramref name="Version"/> is 0 until the workspace saves settings for the first time.
/// </summary>
public sealed record WorkspaceSettings(
    SettingsDocument Effective,
    JsonObject Overrides,
    SettingsDocument Defaults,
    IReadOnlyList<PolicyViolation> PolicyViolations,
    InstancePolicy Policy,
    bool RequireHttps,
    long Version)
{
    public const string GithubField = "github.apiBaseUrl";
    public const string WebhookField = "notifications.webhookUrl";
    public const string AzurePricesField = "cost.azureRetailPricesBaseUrl";

    public SettingsUrl GithubApiBaseUrl => Resolve(GithubField, Effective.Github.ApiBaseUrl);

    public SettingsUrl WebhookUrl => Resolve(WebhookField, Effective.Notifications.WebhookUrl);

    public SettingsUrl AzureRetailPricesBaseUrl => Resolve(AzurePricesField, Effective.Cost.AzureRetailPricesBaseUrl);

    /// <summary>The address of a provider: its base URL, or the address of its kind when the base URL is empty.</summary>
    public SettingsUrl ProviderBaseUrl(Guid providerId)
    {
        for (var i = 0; i < Effective.Ai.Providers.Count; i++)
        {
            var provider = Effective.Ai.Providers[i];
            if (provider.Id != providerId)
                continue;
            if (PolicyViolations.FirstOrDefault(v => v.Field == $"ai.providers[{i}].kind") is { } kind)
                return new SettingsUrl.BlockedByPolicy(kind.Field, kind.Message);
            return Resolve($"ai.providers[{i}].baseUrl",
                provider.BaseUrl.Length > 0 ? provider.BaseUrl : AiProviderKinds.DefaultBaseUrl(provider.Kind));
        }

        return new SettingsUrl.NotConfigured();
    }

    /// <summary>The URL that the named credential goes to.</summary>
    public SettingsUrl DestinationOf(string secretName) => secretName switch
    {
        SecretNames.GithubToken => GithubApiBaseUrl,
        SecretNames.WebhookSecret => WebhookUrl,
        _ when SecretNames.ProviderIdOf(secretName) is { } providerId => ProviderBaseUrl(providerId),
        _ => new SettingsUrl.NotConfigured(),
    };

    private SettingsUrl Resolve(string field, string? url)
    {
        if (PolicyViolations.FirstOrDefault(v => v.Field == field) is { } violation)
            return new SettingsUrl.BlockedByPolicy(violation.Field, violation.Message);
        return Uri.TryCreate(url, UriKind.Absolute, out var uri)
            ? new SettingsUrl.Usable(uri, OutboundUrlPolicy.OriginOf(uri))
            : new SettingsUrl.NotConfigured();
    }
}

/// <summary>
/// Reads settings from the database on every call, so a change applies to the next request
/// with no restart. Built-in defaults lie under the instance defaults, which lie under a
/// workspace's overrides. The instance policy is applied on every read.
/// </summary>
public sealed class SettingsResolver(AppDbContext db, Bootstrap bootstrap)
{
    // Stored by this class only, so a field that a later version adds is absent, not wrong.
    private sealed record StoredPolicy(
        bool? AllowSelfRegistration,
        bool? AllowPrivateNetworkUrls,
        IReadOnlyList<string>? AllowedAiProviderKinds,
        bool? AllowSendingPrivateCodeToAi,
        int? MaxConcurrentStressRuns = null);

    /// <summary>
    /// A hosted instance is open to other networks, so a URL that receives a credential
    /// uses https there. Plain http passes only for a private address, and only when the
    /// policy allows private network URLs.
    /// </summary>
    public bool RequireHttps => bootstrap.Profile == OperatingProfile.Hosted;

    public static string Serialize(InstancePolicy policy) => JsonSerializer.Serialize(
        new StoredPolicy(
            policy.AllowSelfRegistration, policy.AllowPrivateNetworkUrls, policy.AllowedAiProviderKinds, policy.AllowSendingPrivateCodeToAi,
            policy.MaxConcurrentStressRuns),
        JsonSerializerOptions.Web);

    public async Task<InstanceSettings> GetInstanceAsync(CancellationToken ct)
    {
        var row = await db.InstanceSettings.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == InstanceSettingsEntity.SingletonId, ct);

        var policy = SettingsDefaults.Policy(bootstrap.Profile);
        if (row?.Policy is { } storedJson)
        {
            var stored = JsonSerializer.Deserialize<StoredPolicy>(storedJson, JsonSerializerOptions.Web)!;
            policy = new InstancePolicy(
                stored.AllowSelfRegistration ?? policy.AllowSelfRegistration,
                stored.AllowPrivateNetworkUrls ?? policy.AllowPrivateNetworkUrls,
                stored.AllowedAiProviderKinds ?? policy.AllowedAiProviderKinds,
                stored.AllowSendingPrivateCodeToAi ?? policy.AllowSendingPrivateCodeToAi,
                stored.MaxConcurrentStressRuns ?? policy.MaxConcurrentStressRuns);
        }

        var defaultsJson = SettingsJson.ToJson(SettingsDefaults.BuiltIn);
        if (row?.Defaults is { } storedDefaults)
            defaultsJson = SettingsJson.Merge(defaultsJson, JsonNode.Parse(storedDefaults)!.AsObject());

        return new InstanceSettings(policy, ReadStored(defaultsJson, "instance defaults"), defaultsJson, row?.Version ?? 0);
    }

    public async Task<WorkspaceSettings> GetWorkspaceAsync(Guid workspaceId, CancellationToken ct)
    {
        var instance = await GetInstanceAsync(ct);
        var stored = await db.WorkspaceSettings.AsNoTracking()
            .Where(s => s.WorkspaceId == workspaceId)
            .Select(s => new { s.Overrides, s.Version })
            .FirstOrDefaultAsync(ct);

        var overrides = stored is null ? new JsonObject() : JsonNode.Parse(stored.Overrides)!.AsObject();
        var merged = ReadStored(SettingsJson.Merge(instance.DefaultsJson, overrides), $"settings of workspace {workspaceId}");
        return Build(merged, overrides, instance, stored?.Version ?? 0);
    }

    /// <summary>The settings that apply when <paramref name="merged"/> is the defaults with the overrides laid over them.</summary>
    public WorkspaceSettings Build(SettingsDocument merged, JsonObject overrides, InstanceSettings instance, long version)
    {
        var (effective, violations) = ApplyPolicy(merged, instance.Policy, RequireHttps);
        return new WorkspaceSettings(effective, overrides, instance.Defaults, violations, instance.Policy, RequireHttps, version);
    }

    /// <summary>How many workspaces have a stored setting that <paramref name="instance"/> refuses.</summary>
    public async Task<int> CountWorkspacesWithViolationsAsync(InstanceSettings instance, CancellationToken ct)
    {
        var count = 0;
        await foreach (var stored in db.WorkspaceSettings.AsNoTracking().Select(s => s.Overrides).AsAsyncEnumerable().WithCancellation(ct))
        {
            var merged = SettingsJson.Read(SettingsJson.Merge(instance.DefaultsJson, JsonNode.Parse(stored)!.AsObject()), out _);
            // Overrides that no longer read with the new defaults do not apply at all.
            if (merged is null || ApplyPolicy(merged, instance.Policy, RequireHttps).Violations.Count > 0)
                count++;
        }

        return count;
    }

    /// <summary>
    /// The instance policy wins over a stored choice. A policy can become stricter after a
    /// workspace saved its overrides; the effective document then drops what is no longer
    /// allowed and the violations say what was dropped. The stored overrides stay as the
    /// workspace wrote them. URLs are checked with no DNS lookup here: a literal address and
    /// a local name are refused at once, and the caller that makes the request resolves names.
    /// </summary>
    public static (SettingsDocument Effective, IReadOnlyList<PolicyViolation> Violations) ApplyPolicy(
        SettingsDocument document, InstancePolicy policy, bool requireHttps)
    {
        var violations = new List<PolicyViolation>();

        bool UrlRefused(string field, string? url)
        {
            if (string.IsNullOrEmpty(url))
                return false;
            var verdict = OutboundUrlPolicy.CheckWithoutResolving(url, policy.AllowPrivateNetworkUrls, requireHttps);
            if (!verdict.Allowed)
                violations.Add(new PolicyViolation(field, verdict.Reason!));
            return !verdict.Allowed;
        }

        var providers = new List<AiProviderSettings>(document.Ai.Providers.Count);
        for (var i = 0; i < document.Ai.Providers.Count; i++)
        {
            var provider = document.Ai.Providers[i];
            var refused = UrlRefused($"ai.providers[{i}].baseUrl", provider.BaseUrl);
            if (!policy.AllowedAiProviderKinds.Contains(provider.Kind))
            {
                violations.Add(new PolicyViolation($"ai.providers[{i}].kind",
                    $"The instance policy does not allow the provider kind '{provider.Kind}'."));
                refused = true;
            }

            providers.Add(refused ? provider with { Enabled = false } : provider);
        }

        var enabled = providers.Where(p => p.Enabled).Select(p => p.Id).ToHashSet();
        Guid? Route(Guid? id) => id is { } value && enabled.Contains(value) ? id : null;
        var routing = document.Ai.TaskRouting;

        var github = UrlRefused(WorkspaceSettings.GithubField, document.Github.ApiBaseUrl)
            ? document.Github with { ApiBaseUrl = "" }
            : document.Github;
        var notifications = UrlRefused(WorkspaceSettings.WebhookField, document.Notifications.WebhookUrl)
            ? new NotificationSettings(null)
            : document.Notifications;

        var cost = UrlRefused(WorkspaceSettings.AzurePricesField, document.Cost.AzureRetailPricesBaseUrl)
            ? document.Cost with { AzureRetailPricesBaseUrl = "" }
            : document.Cost;

        var forwardsCode = document.Privacy.SendPrivateCodeToAi;
        if (forwardsCode && !policy.AllowSendingPrivateCodeToAi)
        {
            violations.Add(new PolicyViolation("privacy.sendPrivateCodeToAi",
                "The instance policy does not allow private code to be sent to AI providers."));
            forwardsCode = false;
        }

        var effective = document with
        {
            Ai = new AiSettings(providers,
                new TaskRouting(Route(routing.InferArchitecture), Route(routing.ProposeFlow), Route(routing.Critique), Route(routing.Explain))),
            Github = github,
            Cost = cost,
            Notifications = notifications,
            Privacy = new PrivacySettings(forwardsCode),
        };
        return (effective, violations);
    }

    private static SettingsDocument ReadStored(JsonObject json, string what) =>
        SettingsJson.Read(json, out var error)
        ?? throw new InvalidOperationException($"The stored {what} cannot be read at '{error!.Field}'.");
}
