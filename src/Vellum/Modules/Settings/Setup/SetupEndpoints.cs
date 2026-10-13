using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Vellum.Modules.Identity;
using Vellum.Modules.Settings.Audit;
using Vellum.Modules.Settings.Document;
using Vellum.Modules.Settings.Entities;
using Vellum.Modules.Workspaces;
using Vellum.Modules.Workspaces.Authorization;
using Vellum.Modules.Workspaces.Entities;
using Vellum.Shared;

namespace Vellum.Modules.Settings.Setup;

public sealed record SetupStatusResponse(bool NeedsSetup, string Profile, bool RequiresSetupToken);
public sealed record SetupRequest(string? Email, string? Password, string? DisplayName, string? WorkspaceName, string? SetupToken);
public sealed record SetupResponse(string UserId, Guid WorkspaceId);

/// <summary>An instance needs setup while it has no user.</summary>
public static class SetupState
{
    public static async Task<bool> NeedsSetupAsync(AppIdentityDbContext identity, CancellationToken ct) =>
        !await identity.Users.AnyAsync(ct);

    /// <summary>
    /// A hosted instance with no user and no setup token would give the admin account to the
    /// first caller, so it does not start.
    /// </summary>
    public static async Task RefuseOpenHostedSetupAsync(IServiceProvider services, CancellationToken ct = default)
    {
        using var scope = services.CreateScope();
        var bootstrap = scope.ServiceProvider.GetRequiredService<Bootstrap>();
        if (bootstrap.Profile != OperatingProfile.Hosted || bootstrap.SetupToken is not null)
            return;

        if (await NeedsSetupAsync(scope.ServiceProvider.GetRequiredService<AppIdentityDbContext>(), ct))
            throw new BootstrapException(
                "The hosted profile has no user yet and no setup token. Set Vellum__SetupToken to at least " +
                $"{Bootstrap.MinimumSetupTokenLength} random characters and give it to the person who runs first-time setup.");
    }
}

public static class GetSetupStatus
{
    public static async Task<IResult> Handle(Bootstrap bootstrap, AppIdentityDbContext identity, CancellationToken ct) =>
        Results.Ok(new SetupStatusResponse(
            await SetupState.NeedsSetupAsync(identity, ct), bootstrap.ProfileName, bootstrap.RequiresSetupToken));
}

public static class RunSetup
{
    // Any constant works; every Vellum instance must use the same one.
    private const long LockKey = 0x56454C5345545550;

    public static async Task<IResult> Handle(
        SetupRequest request,
        Bootstrap bootstrap,
        IConfiguration configuration,
        AppIdentityDbContext identity,
        WorkspacesDbContext workspaces,
        SettingsDbContext settings,
        UserManager<ApplicationUser> users,
        SignInManager<ApplicationUser> signIn,
        CancellationToken ct)
    {
        if (!await SetupState.NeedsSetupAsync(identity, ct))
            return AlreadySetUp();

        if (bootstrap.RequiresSetupToken && !TokenMatches(bootstrap.SetupToken, request.SetupToken))
            return Results.Json(
                new ErrorResponse("invalid_setup_token", "The setup token is missing or wrong",
                    "Use the value of Vellum__SetupToken from the environment of this instance."),
                statusCode: StatusCodes.Status403Forbidden);

        var errors = new List<FieldError>();
        if (string.IsNullOrWhiteSpace(request.Email) || request.Email.Length > 256)
            errors.Add(new("email", "Must be an email address of at most 256 characters."));
        if (string.IsNullOrEmpty(request.Password) || request.Password.Length > 256)
            errors.Add(new("password", "Must be 1 to 256 characters."));
        if (string.IsNullOrWhiteSpace(request.DisplayName) || request.DisplayName.Length > 100)
            errors.Add(new("displayName", "Must be 1 to 100 characters."));
        if (string.IsNullOrWhiteSpace(request.WorkspaceName) || request.WorkspaceName.Length > 100)
            errors.Add(new("workspaceName", "Must be 1 to 100 characters."));
        if (errors.Count > 0)
            return Results.BadRequest(new ErrorResponse("validation_error", "Validation failed", Errors: errors));

        // The user, the workspace and the admin grant commit together or not at all. The
        // advisory lock makes two callers take turns; the second one then finds a user.
        await using var connection = new NpgsqlConnection(configuration.GetConnectionString("DefaultConnection"));
        await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using (var take = new NpgsqlCommand("SELECT pg_advisory_xact_lock(@key)", connection, transaction))
        {
            take.Parameters.AddWithValue("key", LockKey);
            await take.ExecuteNonQueryAsync(ct);
        }

        DbContext[] contexts = [identity, workspaces, settings];
        foreach (var context in contexts)
        {
            context.Database.SetDbConnection(connection);
            await context.Database.UseTransactionAsync(transaction, ct);
        }

        if (!await SetupState.NeedsSetupAsync(identity, ct))
            return AlreadySetUp();

        var user = new ApplicationUser
        {
            UserName = request.Email!.Trim(),
            Email = request.Email.Trim(),
            DisplayName = request.DisplayName!.Trim(),
        };
        var created = await users.CreateAsync(user, request.Password!);
        if (!created.Succeeded)
            return Results.BadRequest(new ErrorResponse("validation_error", "Setup failed",
                Errors: created.Errors.Select(e => new FieldError(e.Code, e.Description)).ToList()));

        var workspaceId = Guid.NewGuid();
        workspaces.Workspaces.Add(new WorkspaceEntity { Id = workspaceId, Name = request.WorkspaceName!.Trim(), CreatedBy = user.Id });
        workspaces.Memberships.Add(new MembershipEntity
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            UserId = user.Id,
            Role = nameof(WorkspaceRole.Owner),
        });
        await workspaces.SaveChangesAsync(ct);

        settings.InstanceAdmins.Add(new InstanceAdminEntity { UserId = user.Id });
        await settings.SaveChangesAsync(ct);
        await AuditLog.AppendAsync(settings, null, user.Id, AuditLog.InstanceSetup, $"user:{user.Id}", ct);
        await AuditLog.AppendAsync(settings, workspaceId, user.Id, AuditLog.MemberAdded, $"user:{user.Id} role:owner", ct);

        await transaction.CommitAsync(ct);
        foreach (var context in contexts)
            await context.Database.UseTransactionAsync(null, ct);

        await signIn.SignInAsync(user, isPersistent: true);
        return Results.Created("/api/auth/me", new SetupResponse(user.Id, workspaceId));
    }

    private static IResult AlreadySetUp() =>
        Results.Conflict(new ErrorResponse("already_set_up", "This instance is already set up", "Sign in with an existing account."));

    // Both sides are hashed first, so the comparison takes the same time for any input length.
    private static bool TokenMatches(string? expected, string? supplied)
    {
        if (expected is null || string.IsNullOrEmpty(supplied))
            return false;
        return CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(expected)),
            SHA256.HashData(Encoding.UTF8.GetBytes(supplied)));
    }
}

/// <summary>Decides whether a new account may be created outside first-run setup.</summary>
public sealed class RegistrationPolicy(AppIdentityDbContext identity, SettingsResolver settings)
{
    /// <summary>Null when registration is open; otherwise the 403 body.</summary>
    public async Task<ErrorResponse?> RefusalAsync(CancellationToken ct)
    {
        // The first account is the instance admin, and only setup creates it.
        if (await SetupState.NeedsSetupAsync(identity, ct))
            return new ErrorResponse("setup_required", "This instance is not set up",
                "Complete first-run setup before you create accounts.");

        var instance = await settings.GetInstanceAsync(ct);
        return instance.Policy.AllowSelfRegistration
            ? null
            : new ErrorResponse("registration_closed", "Registration is closed",
                "The instance admin has not opened self-registration.");
    }
}
