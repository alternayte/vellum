using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Vellum.Modules.Settings.Setup;
using Vellum.Shared;

namespace Vellum.Modules.Identity;

public sealed record RegisterRequest(string Email, string Password, string? DisplayName);
public sealed record LoginRequest(string Email, string Password);
public sealed record UserInfoResponse(string Id, string Email, string? DisplayName);

public static class IdentityEndpoints
{
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
        });

        group.MapPost("/login", async (
            LoginRequest request,
            SignInManager<ApplicationUser> signInManager) =>
        {
            var result = await signInManager.PasswordSignInAsync(
                request.Email, request.Password, isPersistent: true, lockoutOnFailure: false);

            if (!result.Succeeded)
                return Results.Unauthorized();

            return Results.Ok();
        });

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

        group.MapGet("/external/{provider}", (
            string provider,
            SignInManager<ApplicationUser> signInManager) =>
        {
            var properties = signInManager.ConfigureExternalAuthenticationProperties(
                provider, "/api/auth/external/callback");
            return Results.Challenge(properties, [provider]);
        });

        group.MapGet("/external/callback", async (
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

            var email = info.Principal.FindFirstValue(ClaimTypes.Email);
            if (email is null)
                return Results.BadRequest(new ErrorResponse("validation_error", "Email not provided by external provider"));

            var user = await userManager.FindByEmailAsync(email);
            if (user is null)
            {
                // A first sign-in with an external provider creates an account, so it obeys
                // the same policy as registration.
                if (await registration.RefusalAsync(ct) is { } refusal)
                    return Results.Json(refusal, statusCode: StatusCodes.Status403Forbidden);

                user = new ApplicationUser
                {
                    UserName = email,
                    Email = email,
                    DisplayName = info.Principal.FindFirstValue(ClaimTypes.Name)
                };
                var createResult = await userManager.CreateAsync(user);
                if (!createResult.Succeeded)
                {
                    var errors = createResult.Errors
                        .Select(e => new FieldError(e.Code, e.Description))
                        .ToList();
                    return Results.Problem(
                        detail: string.Join("; ", createResult.Errors.Select(e => e.Description)),
                        statusCode: 500,
                        title: "Failed to create user account");
                }
            }

            await userManager.AddLoginAsync(user, info);
            await signInManager.SignInAsync(user, isPersistent: true);
            return Results.Redirect("/");
        });

        return app;
    }
}
