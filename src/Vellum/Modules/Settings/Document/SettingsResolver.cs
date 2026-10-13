using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Vellum.Modules.Settings.Entities;

namespace Vellum.Modules.Settings.Document;

public sealed record InstanceSettings(InstancePolicy Policy, SettingsDocument Defaults, JsonObject DefaultsJson);

public sealed record WorkspaceSettings(SettingsDocument Effective, JsonObject Overrides, SettingsDocument Defaults);

/// <summary>
/// Reads settings from the database on every call, so a change applies to the next request
/// with no restart. Built-in defaults lie under the instance defaults, which lie under a
/// workspace's overrides.
/// </summary>
public sealed class SettingsResolver(SettingsDbContext db, Bootstrap bootstrap)
{
    // Stored by this class only, so a field that a later version adds is absent, not wrong.
    private sealed record StoredPolicy(
        bool? AllowSelfRegistration,
        bool? AllowPrivateNetworkUrls,
        IReadOnlyList<string>? AllowedAiProviderKinds,
        bool? AllowSendingPrivateCodeToAi);

    public static string Serialize(InstancePolicy policy) => JsonSerializer.Serialize(
        new StoredPolicy(policy.AllowSelfRegistration, policy.AllowPrivateNetworkUrls, policy.AllowedAiProviderKinds, policy.AllowSendingPrivateCodeToAi),
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
                stored.AllowSendingPrivateCodeToAi ?? policy.AllowSendingPrivateCodeToAi);
        }

        var defaultsJson = SettingsJson.ToJson(SettingsDefaults.BuiltIn);
        if (row?.Defaults is { } storedDefaults)
            defaultsJson = SettingsJson.Merge(defaultsJson, JsonNode.Parse(storedDefaults)!.AsObject());

        return new InstanceSettings(policy, ReadStored(defaultsJson, "instance defaults"), defaultsJson);
    }

    public async Task<WorkspaceSettings> GetWorkspaceAsync(Guid workspaceId, CancellationToken ct)
    {
        var instance = await GetInstanceAsync(ct);
        var stored = await db.WorkspaceSettings.AsNoTracking()
            .Where(s => s.WorkspaceId == workspaceId)
            .Select(s => s.Overrides)
            .FirstOrDefaultAsync(ct);

        var overrides = stored is null ? new JsonObject() : JsonNode.Parse(stored)!.AsObject();
        var effective = ReadStored(SettingsJson.Merge(instance.DefaultsJson, overrides), $"settings of workspace {workspaceId}");
        return new WorkspaceSettings(ApplyPolicy(effective, instance.Policy), overrides, instance.Defaults);
    }

    /// <summary>
    /// The instance policy wins over a stored choice. A policy can become stricter after a
    /// workspace saved its overrides; the effective document then drops what is no longer
    /// allowed, and the stored overrides stay as the workspace wrote them.
    /// </summary>
    public static SettingsDocument ApplyPolicy(SettingsDocument document, InstancePolicy policy)
    {
        var providers = document.Ai.Providers
            .Select(p => p.Enabled && !policy.AllowedAiProviderKinds.Contains(p.Kind) ? p with { Enabled = false } : p)
            .ToList();
        var enabled = providers.Where(p => p.Enabled).Select(p => p.Id).ToHashSet();
        Guid? Route(Guid? id) => id is { } value && enabled.Contains(value) ? id : null;
        var routing = document.Ai.TaskRouting;

        return document with
        {
            Ai = new AiSettings(providers,
                new TaskRouting(Route(routing.InferArchitecture), Route(routing.ProposeFlow), Route(routing.Critique), Route(routing.Explain))),
            Privacy = new PrivacySettings(document.Privacy.SendPrivateCodeToAi && policy.AllowSendingPrivateCodeToAi),
        };
    }

    private static SettingsDocument ReadStored(JsonObject json, string what) =>
        SettingsJson.Read(json, out var error)
        ?? throw new InvalidOperationException($"The stored {what} cannot be read at '{error!.Field}'.");
}
