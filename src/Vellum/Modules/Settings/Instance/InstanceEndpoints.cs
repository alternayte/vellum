using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Vellum.Modules.Settings.Audit;
using Vellum.Modules.Settings.Document;
using Vellum.Modules.Settings.Entities;
using Vellum.Shared;
using static Vellum.Shared.ResultExtensions;

namespace Vellum.Modules.Settings.Instance;

/// <summary>
/// The instance admin manages the instance: policy, defaults, keys and diagnostics. The role
/// gives no access to a workspace; that comes from a workspace membership only.
/// </summary>
public sealed class InstanceAuthorization(AppDbContext db)
{
    public Task<bool> IsAdminAsync(string userId, CancellationToken ct = default) =>
        db.InstanceAdmins.AsNoTracking().AnyAsync(a => a.UserId == userId, ct);

    /// <returns>The user ID of the admin.</returns>
    public async Task<string> RequireAdminAsync(ClaimsPrincipal user, CancellationToken ct = default)
    {
        var userId = user.UserId();
        if (!await IsAdminAsync(userId, ct))
            throw new UnauthorizedAccessException("Requires the instance admin role");
        return userId;
    }
}

/// <summary>The body of <c>PUT /api/instance/settings</c>: exactly these two fields, and every field of the policy.</summary>
public sealed record UpdateInstanceSettingsRequest(InstancePolicy Policy, JsonObject Defaults);

public sealed record InstanceInfoResponse(string Profile, string Version, bool IsInstanceAdmin, bool AllowSelfRegistration);

/// <summary>
/// <paramref name="Version"/> is what the next <c>PUT</c> sends in <c>If-Match</c>.
/// <paramref name="WorkspacesWithPolicyViolations"/> counts the workspaces that have a stored
/// setting that <paramref name="Policy"/> refuses; such a setting does not apply.
/// </summary>
public sealed record InstanceSettingsResponse(
    InstancePolicy Policy, SettingsDocument Defaults, long Version, int WorkspacesWithPolicyViolations);

public static class GetInstance
{
    public static readonly string Version =
        (typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? typeof(Program).Assembly.GetName().Version?.ToString()
            ?? "unknown").Split('+')[0];

    public static async Task<IResult> Handle(
        ClaimsPrincipal user,
        Bootstrap bootstrap,
        InstanceAuthorization auth,
        SettingsResolver settings,
        CancellationToken ct)
    {
        var userId = user.UserId();
        var instance = await settings.GetInstanceAsync(ct);
        return Results.Ok(new InstanceInfoResponse(
            bootstrap.ProfileName, Version, await auth.IsAdminAsync(userId, ct), instance.Policy.AllowSelfRegistration));
    }
}

public static class GetInstanceSettings
{
    public static async Task<IResult> Handle(
        HttpResponse response, ClaimsPrincipal user, InstanceAuthorization auth, SettingsResolver settings, CancellationToken ct)
    {
        await auth.RequireAdminAsync(user, ct);
        var instance = await settings.GetInstanceAsync(ct);
        return SettingsVersion.Ok(response, instance.Version, new InstanceSettingsResponse(
            instance.Policy, instance.Defaults, instance.Version, await settings.CountWorkspacesWithViolationsAsync(instance, ct)));
    }
}

public static class UpdateInstanceSettings
{
    /// <summary>The defaults document and the policy beside it.</summary>
    public const int MaxBodyBytes = SettingsJson.MaxBodyBytes + 4 * 1024;

    private const string PolicyField = "policy";
    private const string DefaultsField = "defaults";

    /// <summary>
    /// The body is an object with exactly <c>policy</c> and <c>defaults</c>. The policy states
    /// every one of its fields. A missing or unknown field is refused: a field that is left
    /// out must not turn a permission off without notice.
    /// </summary>
    public static async Task<IResult> Handle(
        JsonElement body,
        HttpContext http,
        ClaimsPrincipal user,
        InstanceAuthorization auth,
        AppDbContext db,
        SettingsResolver settings,
        SettingsValidator validator,
        CancellationToken ct)
    {
        var userId = await auth.RequireAdminAsync(user, ct);

        if (SettingsVersion.ReadIfMatch(http.Request, out var refusal) is not { } expected)
            return refusal!;
        if (SettingsJson.ToDocument(body) is not { } request)
            return Invalid([new FieldError("", "Must be an object with the fields policy and defaults, in which no field repeats.")]);

        var errors = new List<FieldError>();
        foreach (var (name, _) in request)
        {
            if (name is not (PolicyField or DefaultsField))
                errors.Add(new(name, "Is not a known field."));
        }

        var policy = ReadPolicy(request, errors);
        var requested = request[DefaultsField] as JsonObject;
        if (requested is null)
            errors.Add(new(DefaultsField, request.ContainsKey(DefaultsField) ? SettingsJson.NotADocument : "Required."));
        else if (SettingsJson.FindControlCharacter(requested) is { } path)
            errors.Add(new(Prefix(path), SettingsJson.HasControlCharacter));
        if (errors.Count > 0)
            return Invalid(errors);

        // A field that the admin leaves out of the defaults takes the built-in default.
        var defaultsJson = SettingsJson.Merge(SettingsJson.ToJson(SettingsDefaults.BuiltIn), requested!);
        var defaults = SettingsJson.Read(defaultsJson, out var readError);
        if (defaults is null)
            return Invalid([readError! with { Field = Prefix(readError.Field) }]);

        errors = await validator.ValidateAsync(defaults, policy!, ct);
        if (errors.Count > 0)
            return Invalid(errors.Select(e => e with { Field = Prefix(e.Field) }).ToList());

        var policyJson = SettingsResolver.Serialize(policy!);
        var storedDefaults = defaultsJson.ToJsonString();

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        // No workspace save commits against the old policy after this point.
        await SettingsLocks.InstanceExclusiveAsync(db, ct);
        var before = await settings.GetInstanceAsync(ct);
        if (before.Version != expected)
            return SettingsVersion.Conflict(before.Version);

        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO settings.instance_settings (id, policy, defaults, version, updated_at, updated_by)
            VALUES ({InstanceSettingsEntity.SingletonId}, {policyJson}::jsonb, {storedDefaults}::jsonb, 1, now(), {userId})
            ON CONFLICT (id) DO UPDATE
            SET policy = excluded.policy, defaults = excluded.defaults, version = settings.instance_settings.version + 1,
                updated_at = excluded.updated_at, updated_by = excluded.updated_by
            """, ct);

        var changed = SettingsJson.ChangedPaths(JsonSerializer.SerializeToNode(before.Policy, SettingsJson.Options), request[PolicyField], PolicyField)
            .Concat(SettingsJson.ChangedPaths(before.DefaultsJson, defaultsJson, DefaultsField))
            .ToList();
        await AuditLog.AppendAsync(db, null, userId, AuditLog.InstanceSettingsUpdated,
            AuditLog.ChangeTarget("instance-settings", changed), ct);

        var after = new InstanceSettings(policy!, defaults, defaultsJson, before.Version + 1);
        var violations = await settings.CountWorkspacesWithViolationsAsync(after, ct);
        await transaction.CommitAsync(ct);

        return SettingsVersion.Ok(http.Response, after.Version,
            new InstanceSettingsResponse(after.Policy, after.Defaults, after.Version, violations));
    }

    private static InstancePolicy? ReadPolicy(JsonObject request, List<FieldError> errors)
    {
        if (request[PolicyField] is not JsonObject policyJson)
        {
            errors.Add(new(PolicyField, request.ContainsKey(PolicyField) ? "Must be an object." : "Required."));
            return null;
        }

        InstancePolicy policy;
        try
        {
            policy = policyJson.Deserialize<InstancePolicy>(SettingsJson.Options)!;
        }
        catch (JsonException ex)
        {
            // The message of a JsonException can quote the input, so it is not passed on.
            var path = ex.Path is { Length: > 2 } p && p.StartsWith("$.", StringComparison.Ordinal) ? $"{PolicyField}.{p[2..]}" : PolicyField;
            errors.Add(new(path,
                "The policy must state allowSelfRegistration, allowPrivateNetworkUrls, allowedAiProviderKinds and allowSendingPrivateCodeToAi, each with the right type. " +
                "It may state maxConcurrentStressRuns as a whole number. It holds no other field."));
            return null;
        }

        if (policy.AllowedAiProviderKinds.Any(k => !AiProviderKinds.All.Contains(k))
            || policy.AllowedAiProviderKinds.Distinct().Count() != policy.AllowedAiProviderKinds.Count)
        {
            errors.Add(new("policy.allowedAiProviderKinds",
                $"Must be a list with no repeats of: {string.Join(", ", AiProviderKinds.All)}."));
            return null;
        }

        if (policy.MaxConcurrentStressRuns is < InstancePolicy.MinConcurrentStressRuns or > InstancePolicy.MaxConcurrentStressRunsLimit)
        {
            errors.Add(new("policy.maxConcurrentStressRuns",
                $"Must be from {InstancePolicy.MinConcurrentStressRuns} to {InstancePolicy.MaxConcurrentStressRunsLimit}."));
            return null;
        }

        return policy;
    }

    private static string Prefix(string field) => field.Length == 0 ? DefaultsField : $"{DefaultsField}.{field}";

}
