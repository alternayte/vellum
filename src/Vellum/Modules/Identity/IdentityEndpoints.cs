using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Vellum.Modules.Identity.AgentTokens;
using Vellum.Modules.Settings.Setup;
using Vellum.Shared;
using Vellum.Shared.Telemetry;

namespace Vellum.Modules.Identity;

public sealed record RegisterRequest(string Email, string Password, string? DisplayName);
public sealed record LoginRequest(string Email, string Password);
public sealed record UserInfoResponse(string Id, string Email, string? DisplayName);

/// <summary>
/// What the sign-in screen of this instance offers. <paramref name="Providers"/> holds the
/// names of the external providers that the instance has configured, as the path of
/// <c>/api/auth/external/{provider}</c> takes them. <paramref name="RegistrationOpen"/> is
/// false while the instance needs setup or the instance admin has closed registration.
/// </summary>
public sealed record SignInMethodsResponse(bool Password, IReadOnlyList<string> Providers, bool RegistrationOpen);

public static class IdentityEndpoints
{
    /// <summary>An email address and a password.</summary>
    public const int MaxLoginBodyBytes = 4 * 1024;

    /// <summary>Where a provider's handler sends the browser after it signed the external identity in.</summary>
    public const string ExternalCompletePath = "/api/auth/external/complete";

    public static WebApplication MapIdentityEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/auth").WithTags("Auth");

        group.MapPost("/register", async (
            RegisterRequest request,
            UserManager<ApplicationUser> userManager,
            SignInManager<ApplicationUser> signInManager,
            RegistrationPolicy registration,
            CancellationToken ct) =>
        {
            if (await registration.RefusalAsync(ct) is { } refusal)
                return Results.Json(refusal, statusCode: StatusCodes.Status403Forbidden);

            var user = new ApplicationUser
            {
                UserName = request.Email,
                Email = request.Email,
                DisplayName = request.DisplayName
            };

            var result = await userManager.CreateAsync(user, request.Password);
            if (!result.Succeeded)
            {
                var errors = result.Errors
                    .Select(e => new FieldError(e.Code, e.Description))
                    .ToList();
                return Results.BadRequest(new ErrorResponse("validation_error", "Registration failed", Errors: errors));
            }

            await signInManager.SignInAsync(user, isPersistent: true);
            return Results.Created($"/api/auth/me", new UserInfoResponse(user.Id, user.Email!, user.DisplayName));
        })
        .WithBodyLimit(MaxLoginBodyBytes)
        .RequireRateLimiting(RequestLimits.RegistrationPolicy);

        // The sign-in screen asks this before anyone has signed in. It says nothing that the
        // other anonymous endpoints do not: a provider that is not configured answers 404,
        // and a closed registration answers 403.
        group.MapGet("/methods", async (
            SignInManager<ApplicationUser> signInManager,
            RegistrationPolicy registration,
            CancellationToken ct) =>
        {
            var providers = (await signInManager.GetExternalAuthenticationSchemesAsync())
                .Select(s => s.Name)
                .Order(StringComparer.Ordinal)
                .ToList();
            return Results.Ok(new SignInMethodsResponse(true, providers, await registration.RefusalAsync(ct) is null));
        }).Produces<SignInMethodsResponse>();

        group.MapPost("/login", async (
            LoginRequest request,
            SignInManager<ApplicationUser> signInManager) =>
        {
            // A wrong password, an unknown account and a locked account get the same answer.
            var result = await signInManager.PasswordSignInAsync(
                request.Email, request.Password, isPersistent: true, lockoutOnFailure: true);

            if (!result.Succeeded)
            {
                VellumMetrics.LoginFailed(result.IsLockedOut ? "locked_out" : "invalid_credentials");
                return Results.Unauthorized();
            }

            return Results.Ok();
        })
        .WithBodyLimit(MaxLoginBodyBytes)
        .RequireRateLimiting(RequestLimits.CredentialPolicy);

        group.MapPost("/logout", async (SignInManager<ApplicationUser> signInManager) =>
        {
            await signInManager.SignOutAsync();
            return Results.Ok();
        }).RequireAuthorization();

        group.MapGet("/me", async (
            ClaimsPrincipal principal,
            UserManager<ApplicationUser> userManager) =>
        {
            var user = await userManager.GetUserAsync(principal);
            if (user is null) return Results.Unauthorized();
            return Results.Ok(new UserInfoResponse(user.Id, user.Email!, user.DisplayName));
        }).RequireAuthorization();

        // Starts a sign-in with a provider. The provider sends the browser back to the
        // callback path of its handler (IdentityModule), which the handler answers itself; the
        // handler then sends the browser to ExternalCompletePath below. The two paths differ:
        // a handler that owns the path of the endpoint would take its own redirect for a
        // second answer of the provider and fail.
        group.MapGet("/external/{provider}", async (
            string provider,
            SignInManager<ApplicationUser> signInManager) =>
        {
            // Only a provider that this instance has configured. Any other name is no page.
            var configured = (await signInManager.GetExternalAuthenticationSchemesAsync())
                .FirstOrDefault(s => string.Equals(s.Name, provider, StringComparison.OrdinalIgnoreCase));
            if (configured is null)
                return Results.NotFound(new ErrorResponse("not_found", "This sign-in provider is not configured"));

            var properties = signInManager.ConfigureExternalAuthenticationProperties(configured.Name, ExternalCompletePath);
            return Results.Challenge(properties, [configured.Name]);
        }).RequireRateLimiting(RequestLimits.CredentialPolicy);

        group.MapGet("/external/complete", async (
            SignInManager<ApplicationUser> signInManager,
            UserManager<ApplicationUser> userManager,
            RegistrationPolicy registration,
            CancellationToken ct) =>
        {
            var info = await signInManager.GetExternalLoginInfoAsync();
            if (info is null)
                return Results.Unauthorized();

            var signInResult = await signInManager.ExternalLoginSignInAsync(
                info.LoginProvider, info.ProviderKey, isPersistent: true);

            if (signInResult.Succeeded)
                return Results.Redirect("/");

            // The provider's handler adds an email claim only for an address the provider verified.
            var email = info.Principal.FindFirstValue(ClaimTypes.Email);
            if (email is null)
                return Results.BadRequest(new ErrorResponse("validation_error", "The provider gave no verified email address",
                    $"Verify a primary email address at {info.LoginProvider} and sign in again."));

            // An email address from a provider does not prove who owns the account here. A
            // provider that lets a person claim any address would otherwise hand over that
            // account, and the first account is the instance admin. Linking a provider to an
            // existing account needs a signed-in session of that account, which this version
            // does not offer.
            if (await userManager.FindByEmailAsync(email) is not null)
                return Results.Conflict(new ErrorResponse("account_exists", "An account with this email address already exists",
                    $"Sign in with the password of that account. This instance does not link {info.LoginProvider} to an existing account."));

            // A first sign-in with an external provider creates an account, so it obeys the
            // same policy as registration. It never creates the first account: only setup does.
            if (await registration.RefusalAsync(ct) is { } refusal)
                return Results.Json(refusal, statusCode: StatusCodes.Status403Forbidden);

            var user = new ApplicationUser
            {
                UserName = email,
                Email = email,
                DisplayName = info.Principal.FindFirstValue(ClaimTypes.Name)
            };
            var createResult = await userManager.CreateAsync(user);
            if (!createResult.Succeeded)
                return Results.Problem(
                    detail: string.Join("; ", createResult.Errors.Select(e => e.Description)),
                    statusCode: 500,
                    title: "Failed to create user account");

            var linkResult = await userManager.AddLoginAsync(user, info);
            if (!linkResult.Succeeded)
                return Results.Problem(
                    detail: string.Join("; ", linkResult.Errors.Select(e => e.Description)),
                    statusCode: 500,
                    title: "Failed to link the external account");
            await signInManager.SignInAsync(user, isPersistent: true);
            return Results.Redirect("/");
        }).RequireRateLimiting(RequestLimits.CredentialPolicy);

        app.MapAgentTokenEndpoints();
        return app;
    }
}
