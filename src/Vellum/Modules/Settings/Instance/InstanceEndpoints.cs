using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Vellum.Modules.Settings.Audit;
using Vellum.Modules.Settings.Document;
using Vellum.Modules.Settings.Entities;
using Vellum.Shared;

namespace Vellum.Modules.Settings.Instance;

/// <summary>
/// The instance admin manages the instance: policy, defaults, keys and diagnostics. The role
/// gives no access to a workspace; that comes from a workspace membership only.
/// </summary>
public sealed class InstanceAuthorization(SettingsDbContext db)
{
    public Task<bool> IsAdminAsync(string userId, CancellationToken ct = default) =>
        db.InstanceAdmins.AsNoTracking().AnyAsync(a => a.UserId == userId, ct);

    /// <returns>The user ID of the admin.</returns>
    public async Task<string> RequireAdminAsync(ClaimsPrincipal user, CancellationToken ct = default)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier)!;
        if (!await IsAdminAsync(userId, ct))
            throw new UnauthorizedAccessException("Requires the instance admin role");
        return userId;
    }
}

public sealed record InstanceInfoResponse(string Profile, string Version, bool IsInstanceAdmin, bool AllowSelfRegistration);
public sealed record InstanceSettingsResponse(InstancePolicy Policy, SettingsDocument Defaults);
public sealed record UpdateInstanceSettingsRequest(InstancePolicy? Policy, JsonElement Defaults);

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
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var instance = await settings.GetInstanceAsync(ct);
        return Results.Ok(new InstanceInfoResponse(
            bootstrap.ProfileName, Version, await auth.IsAdminAsync(userId, ct), instance.Policy.AllowSelfRegistration));
    }
}

public static class GetInstanceSettings
{
    public static async Task<IResult> Handle(
        ClaimsPrincipal user, InstanceAuthorization auth, SettingsResolver settings, CancellationToken ct)
    {
        await auth.RequireAdminAsync(user, ct);
        var instance = await settings.GetInstanceAsync(ct);
        return Results.Ok(new InstanceSettingsResponse(instance.Policy, instance.Defaults));
    }
}

public static class UpdateInstanceSettings
{
    public static async Task<IResult> Handle(
        UpdateInstanceSettingsRequest request,
        ClaimsPrincipal user,
        InstanceAuthorization auth,
        SettingsDbContext db,
        SettingsValidator validator,
        CancellationToken ct)
    {
        var userId = await auth.RequireAdminAsync(user, ct);

        var errors = new List<FieldError>();
        var policy = request.Policy;
        if (policy is null)
        {
            errors.Add(new("policy", "Required."));
        }
        else if (policy.AllowedAiProviderKinds is not { } kinds
                 || kinds.Any(k => !AiProviderKinds.All.Contains(k))
                 || kinds.Distinct().Count() != kinds.Count)
        {
            errors.Add(new("policy.allowedAiProviderKinds",
                $"Must be a list with no repeats of: {string.Join(", ", AiProviderKinds.All)}."));
        }

        var requested = SettingsJson.ToDocument(request.Defaults);
        if (requested is null)
            errors.Add(new("defaults", SettingsJson.NotADocument));
        if (errors.Count > 0)
            return Invalid(errors);

        // A field that the admin leaves out takes the built-in default.
        var defaultsJson = SettingsJson.Merge(
            SettingsJson.ToJson(SettingsDefaults.BuiltIn), requested!);
        var defaults = SettingsJson.Read(defaultsJson, out var readError);
        if (defaults is null)
            return Invalid([readError! with { Field = Prefix(readError.Field) }]);

        errors = await validator.ValidateAsync(defaults, policy!, ct);
        if (errors.Count > 0)
            return Invalid(errors.Select(e => e with { Field = Prefix(e.Field) }).ToList());

        var policyJson = SettingsResolver.Serialize(policy!);
        var storedDefaults = defaultsJson.ToJsonString();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO settings.instance_settings (id, policy, defaults, updated_at, updated_by)
            VALUES ({InstanceSettingsEntity.SingletonId}, {policyJson}::jsonb, {storedDefaults}::jsonb, now(), {userId})
            ON CONFLICT (id) DO UPDATE
            SET policy = excluded.policy, defaults = excluded.defaults, updated_at = excluded.updated_at, updated_by = excluded.updated_by
            """, ct);
        await AuditLog.AppendAsync(db, null, userId, AuditLog.InstanceSettingsUpdated, "instance-settings", ct);
        await transaction.CommitAsync(ct);

        return Results.Ok(new InstanceSettingsResponse(policy!, defaults));
    }

    private static string Prefix(string field) => field.Length == 0 ? "defaults" : $"defaults.{field}";

    private static IResult Invalid(IReadOnlyList<FieldError> errors) =>
        Results.BadRequest(new ErrorResponse("validation_error", "Validation failed", Errors: errors));
}
